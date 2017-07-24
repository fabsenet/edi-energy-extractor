using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Raven.Client.Documents;
using Serilog;
using Serilog.Events;
using System.Configuration;

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
                AsyncMain(args);
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
                .MinimumLevel.Verbose()
                .WriteTo.Trace()
                .WriteTo.ColoredConsole()
                .Enrich.WithProperty("ExtractionRunGuid", Guid.NewGuid().ToString("N"))
                .CreateLogger()
                .ForContext("App", "EdiEnergyExtractor");

            Log.Logger = logger;

            return logger;
        }

        private static void AsyncMain(string[] args)
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

            _log.Debug("Initialized RavenDB DocumentStore");

            using (var session = store.OpenSession())
            {
                session.Advanced.MaxNumberOfRequestsPerSession = 400;

                dataExtractor.AnalyzeResult(session);
                DataExtractor.StoreOrUpdateInRavenDb(session, dataExtractor.Documents);

                _log.Debug("starting final save changes(1)");
                session.SaveChanges();
            }

            using (var session = store.OpenSession())
            {
                session.Advanced.MaxNumberOfRequestsPerSession = 400;

                _log.Debug("UpdateExistingEdiDocument!");
                DataExtractor.UpdateExistingEdiDocuments(session);
                _log.Debug("starting final save changes(2)");
                session.SaveChanges();
            }
            _log.Debug("Done");
        }
    }
}
