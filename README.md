edi-energy-extractor
====================

A tool to semi-automatic extract all edi documents from the [edi-energy.de][edienergy] website.

See also the [edienergyviewer][edienergyviewer] app which is a web frontend to browse the extracted data.

## Local setup

The tool requires credentials for edi-energy.de. These are **never stored in git** – instead use [dotnet user-secrets]:

```powershell
dotnet user-secrets set "Username" "your@email.de"
dotnet user-secrets set "Password" "yourPassword"
```

Alternatively in Visual Studio: right-click the project → **Manage User Secrets**.

The credentials are stored in `%APPDATA%\Microsoft\UserSecrets\` and are never committed to git.

Credentials can also be supplied via environment variables (`EdiEnergy_Username`, `EdiEnergy_Password`) or directly as command-line arguments (`-username`, `-password`). Command-line arguments take highest priority.

[edienergy]: https://www.edi-energy.de
[edienergyviewer]: https://github.com/fabsenet/edi-energy-viewer
[dotnet user-secrets]: https://learn.microsoft.com/en-us/aspnet/core/security/app-secrets
