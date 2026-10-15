using System.Text.Json;

namespace Czechia.Cli;

public static class CliApp
{
    public const string Help = """
        CZECHIA DNS CLI

        dns list <domain>                              Výpis celé DNS zóny jako JSON.
        dns export <domain> --output <file>             Export zóny, existující soubor nepřepíše.
        dns plan <manifest.json> [--zone <export.json>]  Náhled změn; --zone funguje offline.
        dns apply <manifest.json> [--yes]               Bez --yes pouze náhled.
        dns publish <domain> --yes                     Publikace celé uložené zóny.

        apply/publish: --backup-dir <directory> (výchozí .czechia/backups)
        Přihlášení: CZECHIA_API_TOKEN nebo CZECHIA_API_TOKEN_FILE,
        jinak soubor ~/.config/czechia/token. Token se nepředává jako argument.
        Zápis podporuje A/AAAA/CNAME/TXT. Nezadané záznamy se nemění ani nemažou.
        Publikace zveřejní celou zónu včetně případných dříve odložených změn.
        """;

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, Func<CzechiaClient> clientFactory, CancellationToken cancellationToken)
    {
        try
        {
            if (args.Length == 0 || args is ["--help"] or ["-h"] or ["help"] or ["dns", "--help"])
            {
                output.WriteLine(Help);
                return 0;
            }
            if (args.Length < 3 || args[0] != "dns" || args[2].StartsWith("--", StringComparison.Ordinal))
                throw new CliException("Očekává se dns <list|export|plan|apply|publish> <doména|manifest>. Použijte --help.");
            var verb = args[1];
            var allowed = verb switch
            {
                "list" => Array.Empty<string>(),
                "export" => ["--output"],
                "plan" => ["--zone"],
                "apply" or "publish" => ["--yes", "--backup-dir"],
                _ => throw new CliException("Neznámý příkaz. Použijte --help.")
            };
            var options = ParseOptions(args[3..], allowed);
            var yes = options.ContainsKey("--yes");
            var backups = options.GetValueOrDefault("--backup-dir") ?? Path.Combine(".czechia", "backups");

            if (verb is "list" or "export" or "publish")
            {
                var domain = Names.Domain(args[2]);
                if (verb == "export" && !options.ContainsKey("--output")) throw new CliException("Export vyžaduje --output <file>.");
                if (verb == "publish" && !yes) throw new CliException("Publikace celé uložené zóny vyžaduje --yes.");
                var client = clientFactory();
                var zone = await client.GetZoneAsync(domain, cancellationToken).ConfigureAwait(false);
                if (verb == "list") output.WriteLine(JsonSerializer.Serialize(zone.Json, DnsManifest.JsonOptions));
                else if (verb == "export")
                {
                    await using var stream = new FileStream(options["--output"]!, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    await JsonSerializer.SerializeAsync(stream, zone.Json, DnsManifest.JsonOptions, cancellationToken).ConfigureAwait(false);
                    output.WriteLine("DNS zóna exportována.");
                }
                else
                {
                    output.WriteLine("Publikace celé uložené zóny včetně dříve odložených změn.");
                    var backup = await DnsApply.BackupAsync(zone, domain, backups, cancellationToken).ConfigureAwait(false);
                    output.WriteLine($"Záloha: {backup}");
                    await client.PublishAsync(domain, cancellationToken).ConfigureAwait(false);
                    output.WriteLine("Zóna publikována. Změny ve veřejném DNS mohou být ještě v cache.");
                }
                return 0;
            }

            var manifest = await ReadManifestAsync(args[2], cancellationToken).ConfigureAwait(false);
            CzechiaClient? liveClient = options.ContainsKey("--zone") ? null : clientFactory();
            var snapshot = liveClient is null
                ? DnsZone.Parse(await ReadJsonAsync(options["--zone"]!, cancellationToken).ConfigureAwait(false), manifest.Domain)
                : await liveClient.GetZoneAsync(manifest.Domain, cancellationToken).ConfigureAwait(false);
            var changes = DnsPlanner.Plan(manifest, snapshot);
            output.WriteLine(JsonSerializer.Serialize(new { manifest.Domain, Changes = changes }, DnsManifest.JsonOptions));
            if (verb == "plan" || !yes)
            {
                output.WriteLine("Pouze náhled; nebylo nic zapsáno ani publikováno. Pro zápis použijte dns apply <manifest> --yes.");
                return 0;
            }
            if (changes.Count == 0)
            {
                output.WriteLine("Žádné změny. Pokud zbyly nepublikované změny po dřívější chybě, použijte dns publish <domain> --yes.");
                return 0;
            }
            output.WriteLine($"Záloha bude uložena do {Path.GetFullPath(backups)}. Po zápisu se publikuje celá uložená zóna.");
            try
            {
                await DnsApply.ApplyAsync(liveClient!, manifest, snapshot, changes, backups, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is CliException or HttpRequestException or OperationCanceledException)
            {
                error.WriteLine("Zápis nebyl dokončen nebo potvrzen. V API mohou zůstat dílčí změny; načtěte nový plán a ověřte DNS. Automatická obnova se neprovádí.");
                throw;
            }
            output.WriteLine("Změny uloženy a zóna publikována. Ověřte veřejné DNS po zohlednění TTL.");
            return 0;
        }
        catch (CliException ex) { error.WriteLine(ex.Message); return 1; }
        catch (JsonException) { error.WriteLine("Neplatný JSON nebo neočekávaný formát. Obsah se z bezpečnostních důvodů nevypisuje."); return 1; }
        catch (HttpRequestException) { error.WriteLine("Spojení s API selhalo. Ověřte síť, HTTPS a povolenou zdrojovou IP."); return 1; }
        catch (OperationCanceledException) { error.WriteLine("Operace byla zrušena nebo vypršel časový limit; výsledek případného zápisu ověřte."); return 130; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { error.WriteLine("Nelze načíst či zapsat soubor nebo je neplatná konfigurace. Ověřte cesty, oprávnění a vstupy."); return 1; }
    }

    private static Dictionary<string, string?> ParseOptions(string[] args, string[] allowed)
    {
        var options = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            var key = args[i];
            if (!allowed.Contains(key, StringComparer.Ordinal) || options.ContainsKey(key))
                throw new CliException("Neznámý nebo opakovaný přepínač. Použijte --help.");
            if (key == "--yes") options.Add(key, null);
            else
            {
                if (++i == args.Length || args[i].StartsWith("--", StringComparison.Ordinal)) throw new CliException("Přepínači chybí hodnota.");
                options.Add(key, args[i]);
            }
        }
        return options;
    }

    private static async Task<DnsManifest> ReadManifestAsync(string path, CancellationToken cancellationToken)
    {
        var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return (JsonSerializer.Deserialize<DnsManifest>(json, DnsManifest.JsonOptions)
            ?? throw new CliException("Manifest nesmí být null.")).Normalize();
    }

    private static async Task<JsonElement> ReadJsonAsync(string path, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false));
        return document.RootElement.Clone();
    }
}
