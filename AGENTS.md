# AGENT Instructions

## Build & Run

```powershell
# Build
dotnet build EdiEnergyExtractor.sln

# Run the default `extract` command (requires RavenDB running at http://127.0.0.1:8080)
dotnet run --project EdiEnergyExtractorCore

# Run with options
dotnet run --project EdiEnergyExtractorCore -- --prefercache   # use local cache, no web requests
dotnet run --project EdiEnergyExtractorCore -- --dryrun        # download only, don't write to DB
dotnet run --project EdiEnergyExtractorCore -- --username x --password y
dotnet run --project EdiEnergyExtractorCore -- --environment Development

# Run the `analyze` command (reads existing DB, prints check identifiers of future AHBs)
dotnet run --project EdiEnergyExtractorCore -- analyze
dotnet run --project EdiEnergyExtractorCore -- analyze --environment Production
```

The CLI is built on **Spectre.Console.Cli**. `extract` is the default command, so running
without a command name behaves like before. Both commands prompt for any parameter that was
not supplied (environment, credentials, prefercache, dryrun) and then show a summary table with
a yes/no confirmation before doing any work. Long-form options only — there are no short aliases.
`--environment` accepts only `Development` or `Production`; if omitted it falls back to the
`DOTNET_ENVIRONMENT` env var and otherwise prompts.

There are no automated tests in this solution.

## Architecture

Single-project console app (`.NET 10 / Windows`) that scrapes the [BDEW MAKO API](https://bdew-mako.de/api/documents), downloads PDF/XML EDI specification documents, extracts metadata, and stores everything in a local **RavenDB** instance.

**CLI entry point (`Program.cs`):** builds a Spectre.Console.Cli `CommandApp<ExtractCommand>` with two commands (`extract` default, `analyze`). Sets `de-DE` culture, wires NLog Fatal logging + debugger break (Release) / `PropagateExceptions` (Debug). Shared logic (environment resolution, config building, parameter prompting, summary/confirmation, `GetDocumentStore`) lives in `Commands/CliHelper.cs`.

**`extract` command flow (`Commands/ExtractCommand.cs`):**

1. Resolve environment → build `appsettings.json` + `appsettings.{env}.json` + env vars + user secrets
2. Resolve/prompt credentials, prefercache, dryrun → confirm summary
3. Connect to RavenDB, create indexes (unless `--dryrun`)
4. `RemoveDuplicatesFromStore()` – deduplicates by `DocumentUri`
5. `DataExtractor.LoadFromWeb()` – fetches document list from BDEW API
6. `DataExtractor.AnalyzeResult()` – core processing (see below)
7. Save `ExportRunStatistics`
8. `DownloadMigFilesLocally()` – writes latest MIG PDFs to dated folders on disk

**`analyze` command (`Commands/AnalyzeCommand.cs`):** resolves environment, confirms, connects to RavenDB and runs `ReadExistingDataForAnalysis()` – prints the check identifiers of future AHBs.

**`AnalyzeResult()` phases:**

- Parse JSON → filter (exclude old, consolidation, informational; only free PDFs)
- Match online docs against existing DB entries by `DocumentUri`; update validity dates or create new `EdiDocument` / `EdiXmlDocument` records
- Download new PDFs via `CacheForcableHttpClient`, extract text with iTextSharp, parse check identifiers
- Download new XMLs via mapper `OnlineJsonDocument2EdiXmlDocumentMapper`
- Normalization: `FixMessageVersions`, `HideWrongDocuments`, `UpdateValidToValues*`, `UpdateIsLatestVersionOnDocuments`

**Storage:**

- `EdiDocument` – PDF metadata + `"pdf"` attachment
- `EdiXmlDocument` – XML metadata + `"xml"` attachment
- `ExportRunStatistics/1` – singleton with last-run timestamp
- Index `EdiDocuments_DocumentUri` enables fast URI lookups

## Key Conventions

**Credentials are never committed.** Supply them via `dotnet user-secrets`, environment variables (`EdiEnergy_Username`, `EdiEnergy_Password`), or CLI args. See README for details.

**HTTP caching.** `CacheForcableHttpClient` caches responses under `cache/` using SHA512(URI) as the filename. Pass `--prefercache` to serve all requests from cache. Useful for development without network access.

**Date parsing in `EdiDocument`.** `GuessDocumentDateFromDocumentNameRawOrFilename()` has 20+ regex patterns plus 30+ hardcoded special cases for problematic filenames. When a document date can't be parsed, add a new entry there rather than changing the general patterns.

**Version prefixes.** Message type versions follow the pattern `S2.1` (Strom) / `G6.0a` (Gas). The normalization step in `FixMessageVersions()` re-derives these from the document name; `OnlineJsonDocument2EdiXmlDocumentMapper` also fixes known missing prefixes (e.g., UTILMD MIG Strom missing `S`).

**`GeneratedRegex` attributes** are used throughout for compile-time regex generation — add new patterns as `[GeneratedRegex(...)] private static partial Regex ...` methods, not inline `new Regex(...)`.

**RavenDB sessions** are opened per logical operation (not shared). The document store is a singleton injected into `DataExtractor`.

**Global culture** is set to `de-DE` in `Program.cs` for German date/number formatting consistency.

**CLI parameter handling.** Use Spectre.Console.Cli command/settings classes. Options are long-form only (no short aliases). Any parameter not supplied on the command line (or via env/secrets/config) is interactively prompted, then a summary table is shown with a yes/no confirmation before any work runs. Add new commands under `Commands/` and put shared resolution/prompt logic in `CliHelper`.
