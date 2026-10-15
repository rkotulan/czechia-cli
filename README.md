# czechia-cli

Neoficiální nástroj pro správu DNS přes REST API [CZECHIA](https://www.czechia.com/podminky-rest-api).
Samostatný .NET 10 CLI nástroj pro Windows, Linux a macOS, bez externích runtime knihoven.
Projekt není produktem ani oficiálním klientem společnosti ZONER.

## Instalace z NuGet.org

Balíček se jmenuje `Czechia.Cli`, nainstalovaný příkaz je `czechia`.
Vyžaduje .NET 10 runtime a pro instalaci příkazu `dotnet tool` také .NET SDK.
První vydání 0.1.0 připravuje workflow popsaný níže; dokud neproběhne úspěšná publikace,
použijte instalaci ze zdrojů.

```powershell
dotnet tool install --global Czechia.Cli
czechia --help
```

Aktualizace:

```powershell
dotnet tool update --global Czechia.Cli
```

Odinstalace: `dotnet tool uninstall --global Czechia.Cli`.
Publikovaná vydání: [NuGet.org](https://www.nuget.org/packages/Czechia.Cli).

## Instalace ze zdrojů

Vyžaduje .NET SDK 10 (verze podle `global.json` nebo novější kompatibilní feature band).

```powershell
git clone https://github.com/rkotulan/czechia-cli.git
cd czechia-cli
dotnet pack src/Czechia.Cli --configuration Release --output artifacts
dotnet tool install --global Czechia.Cli --version 0.1.0 --add-source ./artifacts
czechia --help
```

Nainstalovaný příkaz `czechia` lze používat z libovolného projektu. Pro aktualizaci po novém
sestavení použijte `dotnet tool update --global Czechia.Cli --add-source ./artifacts`.
Bez instalace lze použít `dotnet run --project src/Czechia.Cli -- <argumenty>`.

## Přihlášení

V administraci poskytovatele aktivujte REST API a povolte svoji veřejnou zdrojovou IP.
Token uložte jako samotnou hodnotu do `~/.config/czechia/token`
(Windows: `%USERPROFILE%\.config\czechia\token`), mimo jakýkoli repozitář.
Přístup k souboru omezte na svůj účet.

Pořadí načítání tokenu:

1. Proměnná `CZECHIA_API_TOKEN`.
2. Soubor určený proměnnou `CZECHIA_API_TOKEN_FILE`.
3. Výchozí soubor `~/.config/czechia/token`.

Token se nepředává jako argument a CLI jej nevypisuje. Používá HTTPS a hlavičku
`authorizationToken`; nepřesměrovává autentizované HTTP požadavky na jiné adresy.
API nemá sandbox: zápisy mění skutečnou DNS zónu.

## Příkazy

```text
czechia dns list <domain>
czechia dns export <domain> --output <file>
czechia dns plan <manifest.json> [--zone <export.json>]
czechia dns apply <manifest.json> [--yes] [--backup-dir <directory>]
czechia dns publish <domain> --yes [--backup-dir <directory>]
```

`list` a `export` vrací všechny typy záznamů dostupné v API. Export nepřepisuje existující soubor.
Zápis podporuje A/AAAA/CNAME/TXT. Nezadané záznamy zachovává a žádné záznamy nemaže.

Bezpečná offline ukázka, která nepotřebuje token ani připojení k API:

```powershell
czechia dns plan examples/dns.json --zone examples/empty-zone.json
```

Příklady používají rezervovanou doménu `example.com` a dokumentační IP adresy.
`empty-zone.json` je simulovaná prázdná zóna. Před použitím živého API vytvořte vlastní
manifest se skutečnou doménou a hodnotami.

```json
{
  "domain": "example.com",
  "records": [
    { "type": "A", "name": "app", "value": "192.0.2.10", "ttl": 300 }
  ]
}
```

`name` je relativní jméno, `@` označuje kořen; lze zadat i plné jméno v dané zóně.
TTL musí být 120–172800 sekund podle API. CLI odmítne více záznamů stejného typu a jména
i konflikt CNAME s jiným typem. Pro vícehodnotové sady, wildcardy, mazání nebo zápis
MX/SRV/CAA/TLSA použijte administraci poskytovatele.

## Zápis a publikace

Nejdřív spusťte `dns plan` se svým manifestem. `dns apply` bez `--yes` také pouze zobrazí plán.
Potvrzený zápis:

```powershell
czechia dns apply my-domain.json --yes
```

Před první změnou nástroj znovu načte zónu, porovná ji s načteným plánem a uloží zálohu do
`.czechia/backups` v aktuálním adresáři. Při souběžné změně operaci přeruší.
Mezi kontrolou a zápisem zůstává krátké souběžné okno; neupravujte zónu současně jiným nástrojem.
Záznamy uloží s `publishZone=0` a po úspěchu všech změn jednou publikuje celou zónu.
Publikace zahrnuje také případné dřívější nepublikované změny jiných administrátorů.

Při chybě nejsou zápisy automaticky opakovány ani vraceny zpět. Mohou zůstat dílčí odložené změny;
při výpadku spojení nemusí být znám výsledek posledního požadavku.
Znovu načtěte zónu a plán, porovnejte se zálohou a ověřte veřejné DNS. Je-li uložený stav správný,
publikujte ho explicitně příkazem `dns publish <domain> --yes`.
Nulový plán sám publikaci nevyvolá. Záloha slouží pro ruční obnovu, automatický import není dostupný.

Exporty a zálohy nepatří do Gitu: TXT mohou obsahovat ověřovací údaje.
Návratové kódy: 0 úspěch/náhled, 1 chyba, 130 přerušení/timeout.

## Vývoj a ověření

```powershell
dotnet build Czechia.Cli.slnx --configuration Release
dotnet test --solution Czechia.Cli.slnx --configuration Release
```

Testy používají simulované HTTP odpovědi a neprovádějí živé změny DNS.
CI ověřuje Windows a Linux. Skutečné přihlašovací údaje nepatří do zdrojových souborů ani testů.
Podporovaný kontrakt vychází z [OpenAPI poskytovatele](https://api.czechia.com/swagger/index.html).

## Vydávání

V GitHub repozitáři musí být secret `NUGET_API_KEY` s NuGet.org API klíčem,
který dovoluje publikaci balíčku `Czechia.Cli`. Klíč nepatří do souborů repozitáře.

1. Nastavte novou verzi v `src/Czechia.Cli/Czechia.Cli.csproj` a změny commitněte a pushněte.
2. Vytvořte a pushněte odpovídající tag, například `git tag v0.1.0` a `git push origin v0.1.0`.
3. Workflow `Publish to NuGet.org` nejdříve ověří build a testy na Windows i Linuxu.
   Pak ověří shodu tagu s verzí projektu, vytvoří balíček, vyzkouší instalaci a offline plán
   a nahraje balíček na NuGet.org.

Každá verze je neměnná; již publikované číslo nelze přepsat.
Při selhání ověřte nejdřív stav balíčku na NuGet.org a až potom opakujte běh nebo vydejte novou verzi.
Po úspěšné publikaci může chvíli trvat indexace a dostupnost pro instalaci.

Licence: [MIT](LICENSE).
