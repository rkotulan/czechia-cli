using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Czechia.Cli;

public sealed record DnsManifest(string Domain, IReadOnlyList<DnsRecord> Records)
{
    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public DnsManifest Normalize()
    {
        var domain = Names.Domain(Domain);
        if (Records is null || Records.Count == 0)
            throw new CliException("Manifest musí obsahovat alespoň jeden DNS záznam.");

        var records = Records.Select(r => r is null
            ? throw new CliException("Záznam v manifestu nesmí být null.")
            : r.Normalize(domain)).ToArray();
        if (records.GroupBy(r => (r.Type, r.Name)).Any(g => g.Count() > 1))
            throw new CliException("Manifest smí obsahovat jen jeden záznam daného typu a jména.");
        if (records.GroupBy(r => r.Name).Any(g => g.Any(r => r.Type == "CNAME") && g.Count() > 1))
            throw new CliException("CNAME nesmí mít stejné jméno jako jiný záznam.");
        return new(domain, records);
    }
}

public sealed record DnsRecord(string Type, string Name, string Value, int Ttl)
{
    public DnsRecord Normalize(string domain)
    {
        var type = Type?.ToUpperInvariant();
        if (type is not ("A" or "AAAA" or "CNAME" or "TXT"))
            throw new CliException("Zápis podporuje pouze A, AAAA, CNAME a TXT.");
        if (Ttl is < 120 or > 172800)
            throw new CliException("TTL musí být mezi 120 a 172800 sekundami podle API CZECHIA.");
        if (string.IsNullOrWhiteSpace(Value) || Value.Any(char.IsControl))
            throw new CliException("Hodnota záznamu musí být neprázdná a bez řídicích znaků.");

        var name = Names.Owner(Name, domain);
        var value = Value;
        if (type is "A" or "AAAA")
        {
            var family = type == "A" ? AddressFamily.InterNetwork : AddressFamily.InterNetworkV6;
            if (!IPAddress.TryParse(value, out var ip) || ip.AddressFamily != family
                || (ip.AddressFamily == AddressFamily.InterNetworkV6 && ip.ScopeId != 0))
                throw new CliException("A/AAAA záznam vyžaduje platnou IP adresu odpovídajícího typu.");
            value = ip.ToString();
        }
        if (type == "CNAME")
        {
            if (name == "@") throw new CliException("CNAME na kořeni zóny není podporované.");
            if (IPAddress.TryParse(value, out _)) throw new CliException("CNAME musí cílit na doménové jméno.");
            value = Names.Domain(value) + ".";
        }
        return new(type, name, value, Ttl);
    }
}

public static partial class Names
{
    public static string Domain(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new CliException("Doména musí být vyplněná.");
        string ascii;
        try { ascii = new IdnMapping().GetAscii(value.TrimEnd('.')).ToLowerInvariant(); }
        catch (ArgumentException) { throw new CliException("Neplatné doménové jméno."); }
        if (ascii.Length > 253 || !ascii.Contains('.', StringComparison.Ordinal)
            || ascii.Split('.').Any(l => !DomainLabel().IsMatch(l)))
            throw new CliException("Neplatné doménové jméno.");
        return ascii;
    }

    public static string Owner(string value, string domain)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new CliException("Jméno záznamu musí být vyplněné; kořen zóny je @.");
        var name = value.ToLowerInvariant().TrimEnd('.');
        if (name == domain || name == "@") return "@";
        if (name.EndsWith("." + domain, StringComparison.Ordinal)) name = name[..^(domain.Length + 1)];
        else if (value.EndsWith('.')) throw new CliException("Absolutní jméno záznamu musí patřit do zvolené zóny.");
        if (name.Length + domain.Length + 1 > 253 || name.Split('.').Any(l => !OwnerLabel().IsMatch(l)))
            throw new CliException("Neplatné jméno DNS záznamu.");
        return name;
    }

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$")]
    private static partial Regex DomainLabel();

    [GeneratedRegex("^[a-z0-9_](?:[a-z0-9_-]{0,61}[a-z0-9_])?$")]
    private static partial Regex OwnerLabel();
}

public sealed class CliException(string message) : Exception(message);
