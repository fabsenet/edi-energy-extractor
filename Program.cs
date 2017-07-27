using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Raven.Client.Documents;
using Serilog;
using Serilog.Events;
using System.Configuration;
using Raven.Client.Documents.Indexes;
using System.Reflection;

namespace Fabsenet.EdiEnergy
{
    static class Program
    {
        private static ILogger _log;

        static void Main(string[] args)
        {
            _log = SetupLogging().ForContext(typeof(Program));
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
        }

        private static ILogger SetupLogging()
        {
            var logger = new LoggerConfiguration()
                    .ReadFrom.AppSettings()
                    .CreateLogger();

            Log.Logger = logger;

            return logger;
        }

        private static void InnerMain(string[] args)
        {
            _log.Verbose("Process called with {arguments}", args);

            var dataExtractor = new DataExtractor();
            if (args.Any())
            {
                _log.Warning("Using file as ressource '{filename}'", args[0]);
                dataExtractor.LoadFromFile(args[0]);
            }
            else
            {
                //request data from actual web page
                _log.Information("using web as actual ressource");
                dataExtractor.LoadFromWeb();
            }

            _log.Verbose("Initializing RavenDB DocumentStore");
            var store = new DocumentStore()
            {
                Urls = new[] { ConfigurationManager.AppSettings["RavenDBUrl"] },
                Database = ConfigurationManager.AppSettings["RavenDBDatabase"]
            }.Initialize();
            _log.Verbose("Creating RavenDB indexe");
            IndexCreation.CreateIndexes(Assembly.GetExecutingAssembly(), store);

            _log.Debug("Initialized RavenDB DocumentStore");

            using (var session = store.OpenSession())
            {
                _log.Verbose("AnalyzeResult started");
                dataExtractor.AnalyzeResult(session);
                _log.Verbose("StoreOrUpdateInRavenDb started");
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
                    thereIsMore = DataExtractor.UpdateExistingEdiDocuments(session);
                    _log.Debug("starting final save changes(2) and thereIsMore={thereIsMore}", thereIsMore);
                    session.SaveChanges();
                }
            }
            while (thereIsMore);
            _log.Debug("Done");
        }
    }
}
