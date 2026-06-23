using System;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using EdiEnergyExtractorCore;
using NLog;
using Raven.Client.Documents;
using Spectre.Console.Cli;

namespace EdiEnergyExtractor.Commands;

internal sealed class ExtractCommand : AsyncCommand<ExtractSettings>
{
    private static readonly Logger _log = LogManager.GetCurrentClassLogger();
    private static readonly CultureInfo _germanCulture = new("de-DE");

    protected override async Task<int> ExecuteAsync(CommandContext context, ExtractSettings settings, CancellationToken cancellationToken)
    {
        var environmentName = CliHelper.ResolveEnvironment(settings.Environment);
        var config = CliHelper.BuildConfig(environmentName);

        var (username, password) = CliHelper.ResolveCredentials(settings, config);
        var preferCache = CliHelper.ResolveFlag(settings.PreferCache, "Prefer the local cache over actual web access?");
        var dryRun = CliHelper.ResolveFlag(settings.DryRun, "Dry run (download only, do not write to the database)?");

        if (!CliHelper.ConfirmExtract(environmentName, config, username, password, preferCache, dryRun))
        {
            _log.Info("Aborted by user.");
            return 1;
        }

        _log.Info("Environment: {environment}", environmentName);
        _log.Info("BDEW Username: {Username}, Has Password? {HasPassword}", username, !string.IsNullOrEmpty(password));
        _log.Info("Database URL: {DatabaseUrl}, Database Name: {DatabaseName}, Has Certificate? {HasCertificate}", config["DatabaseUrl"], config["DatabaseName"], !string.IsNullOrEmpty(config["DatabaseCertificate"]));

        using var store = dryRun ? null : CliHelper.GetDocumentStore(config);

        if (store != null) RemoveDuplicatesFromStore(store);

        var dataExtractor = new DataExtractor(new CacheForcableHttpClient(preferCache, username, password), store);

        await dataExtractor.LoadFromWeb().ConfigureAwait(false);

        _log.Trace("AnalyzeResult started");
        await dataExtractor.AnalyzeResult().ConfigureAwait(false);

        if (dryRun || store == null)
        {
            _log.Info("dry run done!");
            return 0;
        }

        _log.Debug("saving final stats");
        using (var session = store.OpenSession())
        {
            var stats = session.Load<ExportRunStatistics>(ExportRunStatistics.DefaultId) ?? new ExportRunStatistics { Id = ExportRunStatistics.DefaultId };
            stats.RunFinishedUtc = DateTime.UtcNow;
            session.Store(stats);
            session.SaveChanges();
        }

        _log.Debug("saving local version of MIG files");
        DownloadMigFilesLocally(store);
        _log.Debug("Done");
        return 0;
    }

    private static void RemoveDuplicatesFromStore(DocumentStore store)
    {
        using var session = store.OpenSession();

        //delete duplicates
        var duplicates = session.Query<EdiDocument>().ToList()
            .GroupBy(d => d.DocumentUri)
            .Where(g => g.Count() > 1)
            .ToList();
        foreach (var group in duplicates)
        {
            foreach (var doc in group.Skip(1))
            {
                session.Delete(doc);
            }
        }
        session.SaveChanges();
    }

    private static void DownloadMigFilesLocally(DocumentStore store)
    {
        using var session = store.OpenSession();

        var docsToSave = session.Query<EdiDocument>()
            .Where(d => d.IsLatestVersion)
            .Where(d => d.IsMig)
            .ToList();

        foreach (var doc in docsToSave)
        {
            var raw = doc.DocumentNameRaw.Replace("\n", " ", StringComparison.Ordinal);
            var filename = $"{string.Join("_", (doc.ContainedMessageTypes ?? ["unknown"]).OrderBy(m => m))}_MIG_{doc.MessageTypeVersion}_{doc.DocumentDate?.ToString("yyyy-MM-dd", _germanCulture) ?? "???"}{Path.GetExtension(doc.Filename)}";

            var foldername = $"{doc.DocumentDate?.ToString("yyyy-MM-dd", _germanCulture) ?? "unknown"} MIG";
            Directory.CreateDirectory(foldername);

            Console.WriteLine(filename);
            Console.WriteLine(raw);

            var fullPath = Path.Combine(foldername, filename);

            if (File.Exists(fullPath)) continue;

            using var file = File.OpenWrite(fullPath);
            session.Advanced.Attachments.Get(doc, "pdf").Stream.CopyTo(file);
            file.Close();
        }
    }
}
