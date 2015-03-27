using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using HtmlAgilityPack;
using iTextSharp.text.pdf;
using iTextSharp.text.pdf.parser;
using NLog;
using Raven.Abstractions.Extensions;
using Raven.Abstractions.FileSystem;
using Raven.Client;
using Raven.Client.Document;
using Raven.Client.FileSystem;
using Raven.Client.Linq;
using Raven.Json.Linq;
using ServiceStack.Text;

namespace Fabsenet.EdiEnergy
{
    internal class DataExtractor
    {
        private static readonly Logger _log = LogManager.GetCurrentClassLogger();

        private string _rootHtml;
        private readonly Uri _baseUri = new Uri("http://edi-energy.de");

        public void LoadFromFile(string rootPath)
        {
            _log.Warn("Loading source data from file: {0}", rootPath);
            if (!File.Exists(rootPath))
            {
                _log.Error("the source file does not exist. Its expected path was: {0}", rootPath);
                throw new FileNotFoundException("Root file not found. (Expected location: '" + rootPath + "')");
            }
            _rootHtml = File.ReadAllText(rootPath);
        }

        public void LoadFromWeb()
        {
            _log.Debug("Loading source data from web.");
            var client = new HttpClient();
            var responseMessage = client.GetAsync("http://edi-energy.de").Result;
            _log.Debug("HTTP Status Code is {0}", responseMessage.StatusCode);
            responseMessage.EnsureSuccessStatusCode();


            //sure, why not use windows encoding without telling anybody in http- or html- header?
            //what could possibly go wrong? :D
            //UTF8 (default): Erg�nzende Beschreibung
            //ANSII: Erg?nzende Beschreibung
            //1252 (Windows): Ergänzende Beschreibung
            var responseBytes = responseMessage.Content.ReadAsByteArrayAsync().Result;
            _log.Debug("Receive response with {0} bytes.", responseBytes.Length);
            _log.Warn("Assuming Windows encoding 1252 to parse text.");
            _rootHtml = Encoding.GetEncoding(1252).GetString(responseBytes);
        }

        private async Task<EdiDocument> FetchExistingEdiDocument(Uri documentUri)
        {
            using (var session = _ravenDb.Value.OpenAsyncSession())
            {
                var doc = await Queryable.Where(session
                        .Query<EdiDocument>(), d => d.DocumentUri == documentUri)
                    .FirstOrDefaultAsync();

                return doc;
            } 
        }

        public void AnalyzeResult()
        {
            var htmlDoc = new HtmlDocument();
            htmlDoc.LoadHtml(_rootHtml);

            //select all data rows from table
            _documents = htmlDoc
                .DocumentNode
                .SelectNodes("//table[1]//tr[position()>2 and .//a[@href]]")
                .Select(tr => new
                {
                    DocumentNameRaw = tr.SelectSingleNode(".//td[2]").InnerText.Trim(),
                    DocumentUri = new Uri(_baseUri, tr.SelectSingleNode(".//td[2]//a[@href]").GetAttributeValue("href", "")),
                    ValidFrom = ConvertToDateTime(tr.SelectSingleNode(".//td[3]")),
                    ValidTo = ConvertToDateTime(tr.SelectSingleNode(".//td[4]"))
                })
                .Select(tr =>
                {
                    var fetchedDocument = FetchExistingEdiDocument(tr.DocumentUri).Result;
                    if (fetchedDocument != null)
                    {
                        fetchedDocument.ValidTo = tr.ValidTo;
                        fetchedDocument.ValidFrom = tr.ValidFrom;
                    }
                    return fetchedDocument ?? new EdiDocument(tr.DocumentNameRaw, tr.DocumentUri, tr.ValidFrom, tr.ValidTo);
                })
                .ToList();

            //reset current latest document
            _documents.ForEach(doc => doc.IsLatestVersion = false);

            //determine what the latest document version is again
            var documentGroups = from doc in _documents
                group doc by new
                {
                    doc.BdewProcess,
                    doc.ValidFrom,
                    doc.ValidTo,
                    doc.IsAhb,
                    doc.IsMig,
                    doc.IsGeneralDocument,
                    ContainedMessageTypesString =
                        doc.ContainedMessageTypes != null && doc.ContainedMessageTypes.Any() ? doc.ContainedMessageTypes.Aggregate((m1, m2) => m1 + ", " + m2) : null
                }
                into g
                select g;

            var newestDocumentsInEachGroup = documentGroups.Select(g => g.OrderByDescending(doc => doc.DocumentDate).First()).ToList();

            newestDocumentsInEachGroup.ForEach(doc => doc.IsLatestVersion = true);
        }

        private readonly CultureInfo _germanCulture = new CultureInfo("de-DE");
        private List<EdiDocument> _documents;

        private static readonly Lazy<IFilesStore> _ravenFs = new Lazy<IFilesStore>(() =>
        {
            _log.Trace("Initializing RavenFS FilesStore");
            var store = new FilesStore()
            {
                ConnectionStringName = "RavenFS"
            }.Initialize(ensureFileSystemExists: Debugger.IsAttached);
            _log.Debug("Initialized RavenFS FilesStore");
            return store;
        }
            );


        private static readonly Lazy<IDocumentStore> _ravenDb = new Lazy<IDocumentStore>(() =>
        {
            _log.Trace("Initializing RavenDB DocumentStore");
            var store = new DocumentStore()
            {
                ConnectionStringName = "RavenDB"
            }.Initialize(ensureDatabaseExists: Debugger.IsAttached);
            _log.Debug("Initialized RavenDB DocumentStore");
            return store;
        }
            );

        private DateTime? ConvertToDateTime(HtmlNode htmlNode)
        {
            if (htmlNode == null)
            {
                return null;
            }
            var text = htmlNode.InnerText.Trim();

            DateTime dt;
            if (DateTime.TryParse(text, _germanCulture, DateTimeStyles.None, out dt))
            {
                return dt;
            }
            return null;
        }

        public List<EdiDocument> Documents
        {
            get { return _documents; }
        }

        public static async Task StoreOrUpdateInRavenDb(List<EdiDocument> ediDocuments)
        {
            _log.Debug("Saving {0} documents to ravendb", ediDocuments.Count);

            using (var session = _ravenDb.Value.OpenAsyncSession())
            {
                IQueryable<EdiDocument> allExtraEdiDocs = session.Query<EdiDocument>();
                foreach (var document in ediDocuments)
                {
                    var document1 = document;
                    allExtraEdiDocs = allExtraEdiDocs.Where(doc => doc.DocumentUri != document1.DocumentUri);
                }

                var extraDocs = await allExtraEdiDocs.ToListAsync();
                foreach (var extraDoc in extraDocs)
                {
                    //this document was on the ediEnergy page some time ago but it is not there anymore
                    extraDoc.IsLatestVersion = false;
                    await session.StoreAsync(extraDoc);
                }

                foreach (var ediDocument in ediDocuments)
                {
                    await CreateMirrorAndAnalyzePdfContent(ediDocument);

                    await session.StoreAsync(ediDocument);
                }
                await session.SaveChangesAsync();
            }
        }

        private static async Task CreateMirrorAndAnalyzePdfContent(EdiDocument ediDocument)
        {
            if (ediDocument.MirrorUri!=null && (ediDocument.TextContentPerPage != null || ediDocument.DocumentUri.ToString().EndsWith(".zip"))) return;

            var pdfStream = await DownloadAndCreateMirror(ediDocument);
            ediDocument.MirrorUri = new Uri(ediDocument.Id + Path.GetExtension(ediDocument.DocumentUri.ToString()), UriKind.Relative);

            if (Path.GetExtension(ediDocument.DocumentUri.ToString()) == ".pdf")
            {
                if (ediDocument.TextContentPerPage == null)
                {
                    _log.Trace("Analyzing pdf text content for {0}", ediDocument.Id);
                    ediDocument.TextContentPerPage = AnalyzePdfContent(pdfStream);
                }
                else
                {
                    _log.Debug("Skipping analyze of pdf text content for {0}", ediDocument.Id);                    
                }
                _log.Trace("identified checkidentifiers are {0}", ediDocument.CheckIdentifier.Dump());
            }
        }

        private static string[] AnalyzePdfContent(Stream pdfStream)
        {
            string[] result;

            using (var reader = new PdfReader(pdfStream))
            {
                result = Enumerable.Range(1, reader.NumberOfPages)
                    .Select(pageNumber => PdfTextExtractor.GetTextFromPage(reader, pageNumber))
                    .ToArray();
            }

            return result;
        }

        private static async Task<Stream> DownloadAndCreateMirror(EdiDocument ediDocument)
        {
            _log.Debug("testing mirror file availability for {0}", ediDocument.Id);
            using (var session = _ravenFs.Value.OpenAsyncSession())
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
                    var pdfStream = await responseMessage.Content.ReadAsStreamAsync();
                    var ms = new MemoryStream();
                    await pdfStream.CopyToAsync(ms);
                    ms.Position = 0;
                    var hash = BuildHash(ms);

                    session.RegisterUpload(new FileHeader(ediDocument.Id + Path.GetExtension(ediDocument.DocumentUri.ToString()), new RavenJObject()
                    {
                        {
                            "OriginalUri", new RavenJValue(ediDocument.DocumentUri.ToString())
                        },
                        {
                            "Hash", new RavenJValue(hash)
                        }
                    }), ms);

                    await session.SaveChangesAsync();
                    _log.Debug("Stored copy of ressource '{0}'", ediDocument.DocumentUri);

                    ms.Position = 0;
                    return ms;
                }
                else
                {
                    var stream = await session.DownloadAsync(file);
                    var ms = new MemoryStream();
                    await stream.CopyToAsync(ms);
                    ms.Position = 0;
                    return ms;
                }
            }
        }

        private static string BuildHash(Stream stream)
        {
            _log.Trace("Starting hash computation.");
            var hashBytes = SHA512.Create().ComputeHash(stream);
            stream.Position = 0;
            _log.Debug("Computed hash is {0}", hashBytes.Dump());
            return Convert.ToBase64String(hashBytes);
        }

        public static async Task UpdateExistingEdiDocuments()
        {
            using (var session = _ravenDb.Value.OpenAsyncSession())
            {
                var notMirroredDocuments = await Queryable.Where(session
                        .Query<EdiDocument>()
                        .Customize(c => c.WaitForNonStaleResults()), doc => doc.MirrorUri == null)
                    .ToListAsync();

                _log.Debug("Found {0} documents which are not mirrored!", notMirroredDocuments.Count);

                foreach (var ediDocument in notMirroredDocuments)
                {
                    await CreateMirrorAndAnalyzePdfContent(ediDocument);
                }

                await session.SaveChangesAsync();
            }
        }
    }
}