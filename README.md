edi-energy-extractor
====================

A tool to semi-automatic extract all edi documents from the [edi-energy.de][edienergy] website.

See also the [edienergyviewer][edienergyviewer] app which is a web frontend to browse the extracted data.

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

[edienergy]: https://www.edi-energy.de
[edienergyviewer]: https://github.com/fabsenet/edi-energy-viewer
[dotnet user-secrets]: https://learn.microsoft.com/en-us/aspnet/core/security/app-secrets
