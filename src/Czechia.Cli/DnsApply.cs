using System.Text.Json;

namespace Czechia.Cli;

public static class DnsApply
{
    public static async Task ApplyAsync(CzechiaClient client, DnsManifest manifest, DnsZone snapshot, IReadOnlyList<DnsChange> changes, string backupDirectory, CancellationToken cancellationToken)
    {
        if (changes.Count == 0) return;
        var current = await client.GetZoneAsync(manifest.Domain, cancellationToken).ConfigureAwait(false);
        if (Canonical(current.Json) != Canonical(snapshot.Json))
            throw new CliException("Zóna se od načtení plánu změnila. Znovu spusťte plan; nic nebylo zapsáno.");
        await BackupAsync(snapshot, manifest.Domain, backupDirectory, cancellationToken).ConfigureAwait(false);
        foreach (var change in changes)
            await client.ChangeAsync(manifest.Domain, change, cancellationToken).ConfigureAwait(false);
        await client.PublishAsync(manifest.Domain, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<string> BackupAsync(DnsZone zone, string domain, string directory, CancellationToken cancellationToken)
    {
        domain = Names.Domain(domain);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{domain}-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}.json");
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        await JsonSerializer.SerializeAsync(stream, zone.Json, DnsManifest.JsonOptions, cancellationToken).ConfigureAwait(false);
        return path;
    }

    private static string Canonical(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(",", element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => JsonSerializer.Serialize(p.Name) + ":" + Canonical(p.Value))) + "}",
        JsonValueKind.Array => "[" + string.Join(",", element.EnumerateArray().Select(Canonical).Order(StringComparer.Ordinal)) + "]",
        _ => element.GetRawText()
    };
}
