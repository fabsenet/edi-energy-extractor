using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using CommandLine;
using CommandLine.Attributes;
using EdiEnergyExtractorCore;
using Microsoft.Extensions.Configuration;
using NLog;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;

namespace EdiEnergyExtractor;

record Options
{
    [OptionalArgument(false, "prefercache", "Set to true to prefer cache over actual web access.")]
    public bool PreferCache { get; set; }

    [OptionalArgument(false, "dryrun", "Set to true to not opperate on the database at all. This will only download the latest data from edi-energy.de")]
    public bool DryRun { get; set; }
}

static class Program
{
    private static readonly Logger _log = LogManager.GetCurrentClassLogger();

    static async Task Main(string[] args)
    {
        Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
#if !DEBUG
        try
        {
#endif
        if (!Parser.TryParse(args, out Options options)) return;

        _log.Debug("EdiEnergyExtractor started.");
        await InnerMain(options).ConfigureAwait(false);
#if !DEBUG
        }
        catch (Exception ex)
        {
            _log.Fatal(ex, "Unhandled exception, program execution aborted!");

            if (System.Diagnostics.Debugger.IsAttached)
            {
                System.Diagnostics.Debugger.Break();
            }
        }
#endif
        LogManager.Shutdown();
    }

    private static async Task InnerMain(Options options)
    {
        _log.Trace("Process called with {arguments}", options);

        var config = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json")
            .AddEnvironmentVariables("EdiDocuments_")
            .Build();

        using var store = options.DryRun ? null : GetDocumentStore(config);

        //ReadExistingDataForAnalysis(store);

        if (store != null) RemoveDuplicatesFromStore(store);

        var dataExtractor = new DataExtractor(new CacheForcableHttpClient(options.PreferCache), store);

        //request data from web page
        await dataExtractor.LoadFromWeb().ConfigureAwait(false);

        _log.Trace("AnalyzeResult started");
        await dataExtractor.AnalyzeResult().ConfigureAwait(false);

        if (options.DryRun || store == null)
        {
            _log.Info("dry run done!");
            return;
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

    private static readonly CultureInfo _germanCulture = new("de-DE");

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

    private static void ReadExistingDataForAnalysis(DocumentStore store)
    {
        using var session = store.OpenSession();

        //select future AHBs
        var docs = session.Query<EdiDocument>()
            .Where(d => d.IsAhb)
            .Where(d => d.ValidTo == null)
            .Where(d => d.IsLatestVersion)
            .ToList();

        var existingPIs = docs
            .Where(d => d.CheckIdentifier != null && d.CheckIdentifier.Count != 0)
            .SelectMany(d => d.CheckIdentifier.Keys)
            .Distinct()
            .OrderBy(p => p)
            .ToList();

        foreach (var pi in existingPIs)
        {
            Console.WriteLine($"{pi},");
        }
    }

    private static DocumentStore GetDocumentStore(IConfigurationRoot config)
    {
        _log.Trace("Initializing RavenDB DocumentStore");
        var store = new DocumentStore()
        {
            Urls = [config["DatabaseUrl"]],
            Database = config["DatabaseName"]
        };

        var certPath = config["DatabaseCertificate"];
        if (!string.IsNullOrEmpty(certPath))
        {
            if (!File.Exists(certPath)) throw new Exception($"certificate files does not exist: {certPath}");

            var limits = new Pkcs12LoaderLimits { PreserveStorageProvider = true };
            store.Certificate = X509CertificateLoader.LoadPkcs12(File.ReadAllBytes(certPath), null, X509KeyStorageFlags.MachineKeySet, limits);
        }

        store.Initialize();
        _log.Trace("Creating RavenDB indexe");
        IndexCreation.CreateIndexes(Assembly.GetExecutingAssembly(), store);

        _log.Debug("Initialized RavenDB DocumentStore");
        return store;
    }
}
