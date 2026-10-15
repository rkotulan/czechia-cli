using System.Text.Json;

namespace Czechia.Cli;

public sealed record DnsZone(JsonElement Json, IReadOnlyList<DnsRecord> Records, IReadOnlyList<(string Type, string Name)> Owners)
{
    public static DnsZone Parse(JsonElement json, string domain)
    {
        string[] supportedArrays = ["aRecords", "aaaaRecords", "cnameRecords", "txtRecords", "mxRecords", "srvRecords", "caaRecords", "tlsaRecords"];
        if (json.ValueKind != JsonValueKind.Object || !supportedArrays.Any(p => json.TryGetProperty(p, out _)))
            throw new CliException("API nevrátilo očekávaný formát DNS zóny.");

        var records = new List<DnsRecord>();
        var owners = new List<(string Type, string Name)>();
        foreach (var (property, type, ownerField, valueField) in new[]
        {
            ("aRecords", "A", "hostName", "iPv4"),
            ("aaaaRecords", "AAAA", "hostName", "iPv6"),
            ("cnameRecords", "CNAME", "hostName", "target"),
            ("txtRecords", "TXT", "hostName", "text"),
            ("mxRecords", "MX", "hostName", ""),
            ("srvRecords", "SRV", "name", ""),
            ("caaRecords", "CAA", "name", ""),
            ("tlsaRecords", "TLSA", "name", "")
        })
        {
            if (!json.TryGetProperty(property, out var array) || array.ValueKind == JsonValueKind.Null) continue;
            foreach (var element in array.EnumerateArray())
            {
                var name = Names.Owner(element.GetProperty(ownerField).GetString()!, domain);
                owners.Add((type, name));
                if (valueField.Length > 0)
                    records.Add(new DnsRecord(type, name, element.GetProperty(valueField).GetString()!, element.GetProperty("ttl").GetInt32()).Normalize(domain));
            }
        }
        return new(json.Clone(), records, owners);
    }
}

public sealed record DnsChange(string Action, DnsRecord? Before, DnsRecord After);

public static class DnsPlanner
{
    public static IReadOnlyList<DnsChange> Plan(DnsManifest manifest, DnsZone zone)
    {
        var desired = manifest.Normalize();
        var changes = new List<DnsChange>();
        foreach (var record in desired.Records)
        {
            if (zone.Owners.Any(o => o.Name == record.Name && o.Type != record.Type && (o.Type == "CNAME" || record.Type == "CNAME")))
                throw new CliException($"Konflikt CNAME u jména {record.Name}; záznamy jiného typu se automaticky nemažou.");

            var existing = zone.Records.Where(r => r.Type == record.Type && r.Name == record.Name).ToArray();
            if (existing.Length > 1)
                throw new CliException($"Jméno {record.Name} má více záznamů {record.Type}; CLI odmítá nejednoznačnou změnu.");
            if (existing.Length == 0) changes.Add(new("create", null, record));
            else if (existing[0] != record) changes.Add(new("update", existing[0], record));
        }
        return changes;
    }
}
