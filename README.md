# edi-energy-extractor

A C# tool to semi-automatic extract all edi documents from the [bdew-mako.de][bdewmako] website/API into a RavenDB.

## See also

### edienergyviewer (viewer for the data)
See also the [edi-energy-viewer][edienergyviewer] app which is a web frontend to browse the extracted data.

### edi-energy-scraper (alternative scraper)
For a Python and file-based tool with a similar purpose see [edi-energy-scraper](https://github.com/Hochfrequenz/edi_energy_scraper).

## Usage

The tool is a [Spectre.Console.Cli](https://spectreconsole.net/cli/) command-line app with two commands:

- `extract` (default) – scrape bdew-mako.de, download new documents and store them in RavenDB.
- `analyze` – read the existing RavenDB data and print the check identifiers of future AHBs.

```powershell
# default extract command
dotnet run --project EdiEnergyExtractorCore

# extract options (long-form only, no short aliases)
dotnet run --project EdiEnergyExtractorCore -- --prefercache            # use local cache, no web requests
dotnet run --project EdiEnergyExtractorCore -- --dryrun                 # download only, don't write to DB
dotnet run --project EdiEnergyExtractorCore -- --environment Development # Development or Production only

# analyze command
dotnet run --project EdiEnergyExtractorCore -- analyze
```

Any parameter you do not pass is prompted interactively at startup. Before doing any work the
tool prints a summary table of the resolved parameters and asks for a yes/no confirmation.

### Environment

The environment selects which `appsettings.{Environment}.json` is loaded. Allowed values are
`Development` and `Production`. Resolution order: the `--environment` option, then the
`DOTNET_ENVIRONMENT` environment variable, otherwise you are prompted to pick one.

## Local setup

The tool supports credentials for edi-energy.de. They are optional — leave them empty to run
without authentication. You can use command line args, environment variables or dotnet user secrets.

### Command Line Args
Pass them directly as command-line arguments (`--username`, `--password`). Command-line arguments take highest priority. If only `--username` is supplied you are prompted for the password.

### Environment Variables
Credentials can also be supplied via environment variables (`EdiEnergy_Username`, `EdiEnergy_Password`) 

### [dotnet user-secrets]

```powershell
dotnet user-secrets set "Username" "your@email.de"
dotnet user-secrets set "Password" "yourPassword"
```

Alternatively in Visual Studio: right-click the project → **Manage User Secrets**.

The credentials are stored in `%APPDATA%\Microsoft\UserSecrets\` and are never committed to git.

[bdewmako]: https://www.bdew-mako.de
[edienergy]: https://www.edi-energy.de
[edienergyviewer]: https://github.com/fabsenet/edi-energy-viewer
[dotnet user-secrets]: https://learn.microsoft.com/en-us/aspnet/core/security/app-secrets
