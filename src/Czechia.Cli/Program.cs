using Czechia.Cli;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
using var handler = new HttpClientHandler { AllowAutoRedirect = false };
using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };

return await CliApp.RunAsync(args, Console.Out, Console.Error, () => new CzechiaClient(http, ReadToken()), cancellation.Token).ConfigureAwait(false);

static string ReadToken()
{
    var token = Environment.GetEnvironmentVariable("CZECHIA_API_TOKEN");
    if (string.IsNullOrWhiteSpace(token))
    {
        var path = Environment.GetEnvironmentVariable("CZECHIA_API_TOKEN_FILE")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "czechia", "token");
        if (!File.Exists(path)) throw new CliException("Chybí API token. Nastavte CZECHIA_API_TOKEN nebo CZECHIA_API_TOKEN_FILE; výchozí soubor je ~/.config/czechia/token.");
        token = File.ReadAllText(path).Trim();
    }
    return token;
}
