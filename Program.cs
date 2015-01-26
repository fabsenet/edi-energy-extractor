using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using NLog;
using Raven.Abstractions.FileSystem;
using Raven.Client.Document;
using Raven.Client.FileSystem;
using Raven.Json.Linq;

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
            await StoreOrUpdateInRavenDb(dataExtractor.Documents);

            _log.Debug("Done");
        }

        private static async Task StoreOrUpdateInRavenDb(List<EdiDocument> ediDocuments)
        {
            _log.Debug("Saving " + ediDocuments.Count + " documents to ravendb");

            var database = new DocumentStore()
            {
                ConnectionStringName = "RavenDB"
            }.Initialize(true);

            using (var session = database.OpenAsyncSession())
            {
                foreach (var ediDocument in ediDocuments)
                {
                    await DownloadMirror(ediDocument);
                    await session.StoreAsync(ediDocument);
                }
                await session.SaveChangesAsync();
            }
        }

        private static async Task DownloadMirror(EdiDocument ediDocument)
        {
            _log.Debug("testing mirror file availability for {0}", ediDocument.Id);
            var filesystem = new FilesStore() {ConnectionStringName = "RavenFS"}.Initialize(true);
            using (var session = filesystem.OpenAsyncSession())
            {
                var file = await session.Query()
                    .WhereEquals("OriginalUri", ediDocument.DocumentUri.ToString())
                    .FirstOrDefaultAsync();

                _log.Debug(file == null ? "The file does not exist" : "the file is mirrored");

                if (file == null)
                {
                    _log.Debug("Downloading copy of ressource '{0}'", ediDocument.DocumentUri);
                    var client = new HttpClient();
                    var responseMessage = await client.GetAsync(ediDocument.DocumentUri);
                    responseMessage.EnsureSuccessStatusCode();

                    session.RegisterUpload(new FileHeader(ediDocument.Id, new RavenJObject()
                    {
                        {"OriginalUri", new RavenJValue(ediDocument.DocumentUri.ToString())}
                    }), await responseMessage.Content.ReadAsStreamAsync());

                    await session.SaveChangesAsync();
                    _log.Debug("Stored copy of ressource '{0}'", ediDocument.DocumentUri);
                }
            }
        }
    }
}
