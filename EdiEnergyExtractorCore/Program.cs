using System;
using System.Configuration;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using NLog;
using NLog.Config;
using NLog.Fluent;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;

namespace EdiEnergyExtractorCore
{
    static class Program
    {
        private static readonly ILogger _log = LogManager.GetCurrentClassLogger();
        
        static async Task Main(string[] args)
        {
#if !DEBUG
            try
            {
#endif
                _log.Debug("EdiEnergyExtractor started.");
                await InnerMain(args);
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

        private static async Task InnerMain(string[] args)
        {
            _log.Trace("Process called with {arguments}", args);

            var config = new ConfigurationBuilder()
                .AddJsonFile("appsettings.json")
                .AddEnvironmentVariables("EdiDocuments_")
                .Build();

            var shouldPreferCache = args.Any();
            var store = GetDocumentStore(config);

            ReadExistingDataForAnalysis(store);

            var dataExtractor = new DataExtractor(new CacheForcableHttpClient(shouldPreferCache), store);

            //request data from web page
            await dataExtractor.LoadFromWeb();

            _log.Trace("AnalyzeResult started");
            await dataExtractor.AnalyzeResult();



            _log.Debug("saving final stats");
            using (var session = store.OpenSession())
            {
                var stats = session.Load<ExportRunStatistics>(ExportRunStatistics.DefaultId) ?? new ExportRunStatistics { Id = ExportRunStatistics.DefaultId };
                stats.RunFinishedUtc = DateTime.UtcNow;
                session.Store(stats);
                session.SaveChanges();
            }
            _log.Debug("Done");
        }

        private static void ReadExistingDataForAnalysis(IDocumentStore store)
        {
            using(var session = store.OpenSession())
            {
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
                    .OrderBy(p=>p)
                    .ToList();

                foreach (var pi in existingPIs)
                {
                    Console.WriteLine($"{pi},");
                }
            }
        }

        private static IDocumentStore GetDocumentStore(IConfigurationRoot config)
        {
            _log.Trace("Initializing RavenDB DocumentStore");
            var store = new DocumentStore()
            {
                Urls = new[] {config["DatabaseUrl"]},
                Database = config["DatabaseName"]
            }.Initialize();
            _log.Trace("Creating RavenDB indexe");
            IndexCreation.CreateIndexes(Assembly.GetExecutingAssembly(), store);

            _log.Debug("Initialized RavenDB DocumentStore");
            return store;
        }
    }
}
