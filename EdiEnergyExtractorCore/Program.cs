using System;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using CommandLine;
using CommandLine.Attributes;
using Microsoft.Extensions.Configuration;
using NLog;
using NLog.Config;
using NLog.Fluent;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;

namespace EdiEnergyExtractorCore;

record Options
{
    [OptionalArgument(false, "prefercache", "Set to true to prefer cache over actual web access.")]
    public bool PreferCache { get; set; }

    [OptionalArgument(false, "dryrun", "Set to true to not opperate on the database at all. This will only download the latest data from edi-energy.de")]
    public bool DryRun { get; set; }
}

static class Program
{
    private static readonly ILogger _log = LogManager.GetCurrentClassLogger();

    static async Task Main(string[] args)
    {
        Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
#if !DEBUG
        try
        {
#endif
        if (!Parser.TryParse(args, out Options options)) return;

        _log.Debug("EdiEnergyExtractor started.");
        await InnerMain(options);
#if !DEBUG
        }
        catch (Exception ex)
        {
            _log.Fatal(ex, "Unhandled exception, program execution aborted!");

            if (Debugger.IsAttached)
            {
                Debugger.Break();
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

        var store = options.DryRun ? null : GetDocumentStore(config);

        //ReadExistingDataForAnalysis(store);

        var dataExtractor = new DataExtractor(new CacheForcableHttpClient(options.PreferCache), store);

        //request data from web page
        await dataExtractor.LoadFromWeb();

        _log.Trace("AnalyzeResult started");
        await dataExtractor.AnalyzeResult();

        if (options.DryRun)
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
        SaveMigFiles(store);
        _log.Debug("Done");
    }

    private static void SaveMigFiles(IDocumentStore store)
    {
        using var session = store.OpenSession();

        var docsToSave = session.Query<EdiDocument>()
            .Where(d => d.IsLatestVersion)
            .Where(d => d.IsMig)
            .ToList();

        foreach (var doc in docsToSave)
        {
            string raw = doc.DocumentNameRaw.Replace("\n", " ");
            var filename = $"{string.Join("_", (doc.ContainedMessageTypes ?? new string[] { "unknown" }).OrderBy(m => m))}_MIG_{doc.MessageTypeVersion}_{doc.DocumentDate.Value.ToString("yyyy-MM-dd")}{Path.GetExtension(doc.Filename)}";

            var foldername = $"{doc.DocumentDate?.ToString("yyyy-MM-dd") ?? "unknown"} MIG";
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

    private static void ReadExistingDataForAnalysis(IDocumentStore store)
    {
        using var session = store.OpenSession();
        //select future AHBs
        var docs = session.Query<EdiDocument>()
            .Where(d => d.IsAhb)
            .Where(d => d.ValidTo == null)
            .Where(d => d.IsLatestVersion)
            .ToList();

        var existingPIs = docs
            .Where(d => d.CheckIdentifier?.Any() == true)
            .SelectMany(d => d.CheckIdentifier.Keys)
            .Distinct()
            .OrderBy(p => p)
            .ToList();

        foreach (var pi in existingPIs)
        {
            Console.WriteLine($"{pi},");
        }
    }

    private static IDocumentStore GetDocumentStore(IConfigurationRoot config)
    {
        _log.Trace("Initializing RavenDB DocumentStore");
        var store = new DocumentStore()
        {
            Urls = new[] { config["DatabaseUrl"] },
            Database = config["DatabaseName"]
        };
        string databaseCertificate = config["DatabaseCertificate"];
        if (!string.IsNullOrEmpty(databaseCertificate))
        {
            // powershell: [Environment]::SetEnvironmentVariable("EdiDocuments_DatabaseUrl", "https://sazesla11442:10204/", "User")
            // powershell: [Environment]::SetEnvironmentVariable("EdiDocuments_DatabaseName", "EdiDocuments", "User")
            // powershell: [Environment]::SetEnvironmentVariable("EdiDocuments_DatabaseCertificate", "C:\....pfx", "User")
            if (!File.Exists(databaseCertificate)) throw new Exception($"certificate file does not exist: {databaseCertificate}");
            store.Certificate = new X509Certificate2(databaseCertificate);
        }

        store.Initialize();
        _log.Trace("Creating RavenDB indexe");
        IndexCreation.CreateIndexes(Assembly.GetExecutingAssembly(), store);

        _log.Debug("Initialized RavenDB DocumentStore");
        return store;
    }
}
