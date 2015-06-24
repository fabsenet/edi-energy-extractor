using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Raven.Client.Document;
using Serilog;
using Serilog.Events;

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
                AsyncMain(args).Wait();
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
            var logStore = new DocumentStore
            {
                ConnectionStringName = "LogsDB"
            }.Initialize();

            var logger = new LoggerConfiguration()
                .MinimumLevel.Verbose()
                .WriteTo.RavenDB(logStore)
                .WriteTo.Trace()
                .WriteTo.ColoredConsole()
                .Enrich.WithProperty("ExtractionRunGuid", Guid.NewGuid().ToString("N"))
                .CreateLogger()
                .ForContext("App", "EdiEnergyExtractor");

            Log.Logger = logger;

            return logger;
        }

        private static async Task AsyncMain(string[] args)
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

            dataExtractor.AnalyzeResult();
            await DataExtractor.StoreOrUpdateInRavenDb(dataExtractor.Documents);

            await DataExtractor.UpdateExistingEdiDocuments();
            _log.Debug("Done");
        }
    }
}
