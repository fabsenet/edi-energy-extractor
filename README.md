# edi-energy-extractor

A C# tool to semi-automatic extract all edi documents from the [bdew-mako.de][bdewmako] website/API into a RavenDB.

## See also

### edienergyviewer (viewer for the data)
See also the [edi-energy-viewer][edienergyviewer] app which is a web frontend to browse the extracted data.

### edi-energy-scraper (alternative scraper)
For a Python and file-based tool with a similar purpose see [edi-energy-scraper](https://github.com/Hochfrequenz/edi_energy_scraper).

## Local setup

The tool supports credentials for edi-energy.de. You can use command line args, environment variables or dotnet user sercrets.

### Command Line Args
directly as command-line arguments (`-username`, `-password`). Command-line arguments take highest priority.

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
