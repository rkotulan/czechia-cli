using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;

namespace Czechia.Cli;

public sealed class CzechiaClient(HttpClient http, string token)
{
    private static readonly Uri ApiBase = new("https://api.czechia.com/api/DNS/");

    public async Task<DnsZone> GetZoneAsync(string domain, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, domain, null, null, cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), cancellationToken: cancellationToken).ConfigureAwait(false);
        try { return DnsZone.Parse(document.RootElement, domain); }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException or FormatException)
        { throw new CliException("API nevrátilo očekávaný formát DNS zóny."); }
    }

    public async Task ChangeAsync(string domain, DnsChange change, CancellationToken cancellationToken)
    {
        var record = change.After;
        var valueField = record.Type switch
        {
            "A" => "iPv4", "AAAA" => "iPv6", "CNAME" => "target", "TXT" => "text",
            _ => throw new CliException("Nepodporovaný typ DNS záznamu.")
        };
        var body = new Dictionary<string, object>
        {
            ["hostName"] = record.Name,
            ["publishZone"] = 0
        };
        if (change.Before is { } before)
        {
            body[valueField] = before.Value;
            body[record.Type switch { "A" => "newIPv4", "AAAA" => "newIPv6", "CNAME" => "newTarget", _ => "newText" }] = record.Value;
            body["newTTL"] = record.Ttl;
        }
        else
        {
            body[valueField] = record.Value;
            body["ttl"] = record.Ttl;
        }
        using var response = await SendAsync(change.Before is null ? HttpMethod.Post : HttpMethod.Put, domain, record.Type, body, cancellationToken).ConfigureAwait(false);
    }

    public async Task PublishAsync(string domain, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Post, domain, "Publish", null, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string domain, string? suffix, object? body, CancellationToken cancellationToken)
    {
        domain = Names.Domain(domain);
        if (string.IsNullOrWhiteSpace(token) || token.Any(char.IsControl)) throw new CliException("API token je prázdný nebo neplatný.");
        var uri = new Uri(ApiBase, domain + (suffix is null ? "" : "/" + suffix));
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Add("authorizationToken", token);
        request.Headers.Accept.ParseAdd("application/json");
        if (body is not null) request.Content = JsonContent.Create(body);
        var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode) return response;
        var status = ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture);
        response.Dispose();
        // Tělo odpovědi se nevypisuje: poskytovatel v něm může vrátit přihlašovací údaje.
        throw new CliException($"API vrátilo HTTP {status}. U 401/403 ověřte token, oprávnění a povolenou zdrojovou IP. Požadavek se automaticky neopakuje.");
    }
}
