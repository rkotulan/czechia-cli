using System.Net;
using System.Text.Json;
using Czechia.Cli;
using Xunit;

namespace Czechia.Cli.Tests;

public sealed class DnsTests
{
    private const string Empty = """{"aRecords":[],"mxRecords":[{"hostName":"@","mailServerName":"mx.example.net.","priority":1,"ttl":3600}]}""";
    private static DnsZone Zone(string json = Empty) => DnsZone.Parse(JsonSerializer.Deserialize<JsonElement>(json), "example.com");
    private static DnsManifest Manifest(params DnsRecord[] records) => new("example.com", records);
    private static DnsRecord Record(string name = "app", string value = "192.0.2.10") => new("A", name, value, 300);

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"unknownRecords\":[]}")]
    [InlineData("[]")]
    public void Unexpected_zone_format_cannot_be_treated_as_empty(string json)
    {
        Assert.Throws<CliException>(() => Zone(json));
    }

    [Fact]
    public async Task Offline_plan_never_reads_credentials_or_calls_api()
    {
        var manifestFile = Path.GetTempFileName();
        var zoneFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(manifestFile, JsonSerializer.Serialize(Manifest(Record()), DnsManifest.JsonOptions), TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(zoneFile, Empty, TestContext.Current.CancellationToken);
            using var output = new StringWriter();
            using var error = new StringWriter();
            Assert.Equal(0, await CliApp.RunAsync(["dns", "plan", manifestFile, "--zone", zoneFile], output, error,
                () => throw new InvalidOperationException("Must remain offline"), TestContext.Current.CancellationToken));
            Assert.Contains("create", output.ToString());
        }
        finally { File.Delete(manifestFile); File.Delete(zoneFile); }
    }

    [Fact]
    public void Plan_is_idempotent_and_preserves_mail_records()
    {
        var zone = Zone("""{"aRecords":[{"hostName":"app","iPv4":"192.0.2.10","ttl":300}],"mxRecords":[{"hostName":"@"}]}""");
        Assert.Empty(DnsPlanner.Plan(Manifest(Record()), zone));
        Assert.Equal("MX", zone.Owners.Single(o => o.Type == "MX").Type);
    }

    [Fact]
    public void Ambiguous_existing_records_are_rejected()
    {
        var zone = Zone("""{"aRecords":[{"hostName":"app","iPv4":"192.0.2.1","ttl":300},{"hostName":"app","iPv4":"192.0.2.2","ttl":300}]}""");
        Assert.Throws<CliException>(() => DnsPlanner.Plan(Manifest(Record()), zone));
    }

    [Fact]
    public void Cname_cannot_replace_existing_mail_owner()
    {
        Assert.Throws<CliException>(() => DnsPlanner.Plan(Manifest(new DnsRecord("CNAME", "mail", "example.com", 300)),
            Zone("""{"mxRecords":[{"hostName":"mail"}]}""")));
    }

    [Theory]
    [InlineData("A", "app", "::1", 300)]
    [InlineData("AAAA", "app", "192.0.2.1", 300)]
    [InlineData("A", "app", "192.0.2.1", 119)]
    [InlineData("CNAME", "@", "example.com", 300)]
    [InlineData("TXT", "foreign.cz.", "test", 300)]
    public void Invalid_manifest_records_are_rejected(string type, string name, string value, int ttl)
    {
        Assert.Throws<CliException>(() => Manifest(new DnsRecord(type, name, value, ttl)).Normalize());
    }

    [Fact]
    public void Duplicate_normalized_owners_are_rejected()
    {
        Assert.Throws<CliException>(() => Manifest(Record(), Record("APP.example.com.")).Normalize());
    }

    [Theory]
    [InlineData("A", "192.0.2.1", "192.0.2.2", "iPv4", "newIPv4")]
    [InlineData("AAAA", "2001:db8::1", "2001:db8::2", "iPv6", "newIPv6")]
    [InlineData("CNAME", "old.example.com.", "new.example.com.", "target", "newTarget")]
    [InlineData("TXT", "old", "new", "text", "newText")]
    public async Task Update_uses_original_value_and_stages_without_publish(string type, string oldValue, string newValue, string originalField, string newField)
    {
        using var handler = new Handler(async request =>
        {
            Assert.Equal(HttpMethod.Put, request.Method);
            Assert.Equal($"https://api.czechia.com/api/DNS/example.com/{type}", request.RequestUri!.AbsoluteUri);
            Assert.Equal("test-token", request.Headers.GetValues("authorizationToken").Single());
            var body = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.Equal(oldValue, body.GetProperty(originalField).GetString());
            Assert.Equal(newValue, body.GetProperty(newField).GetString());
            Assert.Equal(300, body.GetProperty("newTTL").GetInt32());
            Assert.Equal(0, body.GetProperty("publishZone").GetInt32());
            return new(HttpStatusCode.OK);
        });
        using var http = new HttpClient(handler);
        await new CzechiaClient(http, "test-token").ChangeAsync("example.com", new("update", new(type, "app", oldValue, 3600), new(type, "app", newValue, 300)), TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Apply_backs_up_before_writing_and_only_publishes_after_success(bool failWrite)
    {
        var directory = Path.Combine(Path.GetTempPath(), "czechia-cli-tests-" + Guid.NewGuid().ToString("N"));
        var calls = new List<string>();
        using var handler = new Handler(async request =>
        {
            calls.Add(request.Method + " " + request.RequestUri!.AbsolutePath);
            if (request.Method == HttpMethod.Get) return Response(Empty);
            Assert.Single(Directory.GetFiles(directory));
            Assert.Contains("mx.example.net", await File.ReadAllTextAsync(Directory.GetFiles(directory).Single(), TestContext.Current.CancellationToken));
            if (request.RequestUri.AbsolutePath.EndsWith("/A", StringComparison.Ordinal))
            {
                var body = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(TestContext.Current.CancellationToken));
                Assert.Equal(0, body.GetProperty("publishZone").GetInt32());
                return new(failWrite ? HttpStatusCode.BadRequest : HttpStatusCode.OK);
            }
            return new(HttpStatusCode.OK);
        });
        using var http = new HttpClient(handler);
        var manifest = Manifest(Record());
        var snapshot = Zone();
        try
        {
            var task = DnsApply.ApplyAsync(new(http, "test-token"), manifest, snapshot, DnsPlanner.Plan(manifest, snapshot), directory, TestContext.Current.CancellationToken);
            if (failWrite) await Assert.ThrowsAsync<CliException>(() => task);
            else await task;
            Assert.Equal(failWrite ? 2 : 3, calls.Count);
            Assert.Equal(failWrite ? "POST /api/DNS/example.com/A" : "POST /api/DNS/example.com/Publish", calls[^1]);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Concurrent_change_aborts_before_backup_or_write()
    {
        using var handler = new Handler(_ => Task.FromResult(Response("""{"aRecords":[{"hostName":"other","iPv4":"192.0.2.9","ttl":300}]}""")));
        using var http = new HttpClient(handler);
        var manifest = Manifest(Record());
        var directory = Path.Combine(Path.GetTempPath(), "czechia-cli-tests-" + Guid.NewGuid().ToString("N"));
        await Assert.ThrowsAsync<CliException>(() => DnsApply.ApplyAsync(new(http, "test-token"), manifest, Zone(), DnsPlanner.Plan(manifest, Zone()), directory, TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(directory));
        Assert.Equal(1, handler.Count);
    }

    [Fact]
    public async Task Apply_without_confirmation_only_reads()
    {
        var file = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(file, JsonSerializer.Serialize(Manifest(Record()), DnsManifest.JsonOptions), TestContext.Current.CancellationToken);
            using var handler = new Handler(request =>
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                return Task.FromResult(Response(Empty));
            });
            using var http = new HttpClient(handler);
            using var output = new StringWriter();
            using var error = new StringWriter();
            Assert.Equal(0, await CliApp.RunAsync(["dns", "apply", file], output, error, () => new(http, "test-token"), TestContext.Current.CancellationToken));
            Assert.Equal(1, handler.Count);
            Assert.Contains("Pouze náhled", output.ToString());
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task Publish_requires_confirmation_before_client_creation()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(1, await CliApp.RunAsync(["dns", "publish", "example.com"], output, error, () => throw new InvalidOperationException("Must not access token"), TestContext.Current.CancellationToken));
        Assert.Contains("--yes", error.ToString());
    }

    [Fact]
    public async Task Api_error_does_not_disclose_response_body()
    {
        using var handler = new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("secret-token") }));
        using var http = new HttpClient(handler);
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(1, await CliApp.RunAsync(["dns", "list", "example.com"], output, error, () => new(http, "secret-token"), TestContext.Current.CancellationToken));
        Assert.Contains("403", error.ToString());
        Assert.DoesNotContain("secret-token", error.ToString());
        Assert.Equal(1, handler.Count);
    }

    private static HttpResponseMessage Response(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json) };

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public int Count { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Count++;
            return respond(request);
        }
    }
}
