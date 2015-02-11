using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using NLog;

namespace Fabsenet.EdiEnergy
{
    static class Program
    {
        private static readonly Logger _log = LogManager.GetCurrentClassLogger();

        static void Main(string[] args)
        {
            try
            {
                _log.Debug("EdiEnergyExtractor started.");
                AsyncMain(args).Wait();
            }
            catch (Exception ex)
            {
                _log.Fatal("Unhandled exception! {0}", ex.ToString());

                if (Debugger.IsAttached)
                {
                    Debugger.Break();
                }
            }
        }

        private static async Task AsyncMain(string[] args)
        {
            var dataExtractor = new DataExtractor();
            if (args.Any())
            {
                _log.Warn("Using file as ressource '{0}'", args[0]);
                dataExtractor.LoadFromFile(args[0]);
            }
            else
            {
                //request data from actual web page
                _log.Info("using web as actual ressource");
                dataExtractor.LoadFromWeb();
            }

            dataExtractor.AnalyzeResult();
            await DataExtractor.StoreOrUpdateInRavenDb(dataExtractor.Documents);

            await DataExtractor.UpdateExistingEdiDocuments();
            _log.Debug("Done");
        }
    }
}
