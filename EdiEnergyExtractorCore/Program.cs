using System;
using System.Configuration;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
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
        
        static void Main(string[] args)
        {
            try
            {
                _log.Debug("EdiEnergyExtractor started.");
                InnerMain(args);
            }
            catch (Exception ex)
            {
                _log.Fatal(ex, "Unhandled exception, program execution aborted!");

                if (Debugger.IsAttached)
                {
                    Debugger.Break();
                }
            }
            LogManager.Shutdown();
        }

        private static void InnerMain(string[] args)
        {
            _log.Trace("Process called with {arguments}", args);

            var dataExtractor = new DataExtractor();
            if (args.Any())
            {
                _log.Warn("Using file as ressource '{filename}'", args[0]);
                dataExtractor.LoadFromFile(args[0]);
            }
            else
            {
                //request data from actual web page
                _log.Info("using web as actual ressource");
                dataExtractor.LoadFromWeb();
            }

            _log.Trace("Initializing RavenDB DocumentStore");
            var store = new DocumentStore()
            {
                Urls = new[] { ConfigurationManager.AppSettings["RavenDBUrl"] },
                Database = ConfigurationManager.AppSettings["RavenDBDatabase"]
            }.Initialize();
            _log.Trace("Creating RavenDB indexe");
            IndexCreation.CreateIndexes(Assembly.GetExecutingAssembly(), store);

            _log.Debug("Initialized RavenDB DocumentStore");

            using (var session = store.OpenSession())
            {
                _log.Trace("AnalyzeResult started");
                dataExtractor.AnalyzeResult(session);
                _log.Trace("StoreOrUpdateInRavenDb started");
                DataExtractor.StoreOrUpdateInRavenDb(session, dataExtractor.Documents);

                _log.Debug("starting final save changes(1)");
                session.SaveChanges();
            }

            bool thereIsMore;
            do
            {
                using (var session = store.OpenSession())
                {
                    _log.Debug("UpdateExistingEdiDocument!");
                    bool saveChangesRequired;
                    (thereIsMore, saveChangesRequired) = DataExtractor.UpdateExistingEdiDocuments(session);
                    if (saveChangesRequired)
                    {
                        _log.Debug("starting final save changes(2)");
                        session.SaveChanges();
                        _log.Debug("final save changes(2) done");
                    }
                    _log.Debug("thereIsMore={thereIsMore}", thereIsMore);
                }
            }
            while (thereIsMore);


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
    }
}
