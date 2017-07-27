using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using HtmlAgilityPack;
using iTextSharp.text.pdf;
using iTextSharp.text.pdf.parser;
using Raven.Client;
using Serilog;
using Path = System.IO.Path;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace Fabsenet.EdiEnergy
{
    internal class DataExtractor
    {
        private static readonly ILogger _log = Log.ForContext<DataExtractor>();

        private string _rootHtml;
        private readonly Uri _baseUri = new Uri("http://edi-energy.de");

        public void LoadFromFile(string rootPath)
        {
            _log.Warning("Loading source data from file: {rootPath}", rootPath);
            if (!File.Exists(rootPath))
            {
                _log.Error("the source file does not exist. Its expected path was: {rootPath}", rootPath);
                throw new FileNotFoundException("Root file not found. (Expected location: '" + rootPath + "')");
            }
            _rootHtml = File.ReadAllText(rootPath);
        }

        public void LoadFromWeb()
        {
            _log.Debug("Loading source data from web.");
            var client = new HttpClient();
            var responseMessage = client.GetAsync("http://edi-energy.de").Result;
            _log.Debug("HTTP Status Code is {StatusCode}", responseMessage.StatusCode);
            responseMessage.EnsureSuccessStatusCode();


            //sure, why not use windows encoding without telling anybody in http- or html- header?
            //what could possibly go wrong? :D
            //UTF8 (default): Erg�nzende Beschreibung
            //ANSII: Erg?nzende Beschreibung
            //1252 (Windows): Ergänzende Beschreibung
            var responseBytes = responseMessage.Content.ReadAsByteArrayAsync().Result;
            _log.Debug("Receive response with {ResponseLength} bytes.", responseBytes.Length);
            _log.Warning("Assuming Windows encoding 1252 to parse text.");
            _rootHtml = Encoding.GetEncoding(1252).GetString(responseBytes);
        }

        private IList<EdiDocument> FetchExistingEdiDocuments(IDocumentSession session)
        {
            var docs = session
                    .Query<EdiDocument>()
                    .ToList();
            return docs;
        }

        public void AnalyzeResult(IDocumentSession session)
        {
            var htmlDoc = new HtmlDocument();
            htmlDoc.LoadHtml(_rootHtml);

            var existingDocuments = FetchExistingEdiDocuments(session);
            //select all data rows from table
            _documents = htmlDoc
                .DocumentNode
                .SelectNodes("//table[1]//tr[position()>2 and .//a[@href]]")
                .Select(tr => new
                {
                    DocumentNameRaw = tr.SelectSingleNode(".//td[2]").InnerText.Trim(),
                    DocumentUri = new Uri(_baseUri, tr.SelectSingleNode(".//td[2]//a[@href]").GetAttributeValue("href", "")).AbsoluteUri,
                    ValidFrom = ConvertToDateTime(tr.SelectSingleNode(".//td[3]")),
                    ValidTo = ConvertToDateTime(tr.SelectSingleNode(".//td[4]"))
                })
                .Select(tr =>
                {
                    var fetchedDocument = existingDocuments.FirstOrDefault(d => d.DocumentUri == tr.DocumentUri);
                    if (fetchedDocument != null)
                    {
                        fetchedDocument.ValidTo = tr.ValidTo;
                        fetchedDocument.ValidFrom = tr.ValidFrom;
                        return fetchedDocument;
                    }
                    var newEdiDocument = new EdiDocument(tr.DocumentNameRaw, tr.DocumentUri, tr.ValidFrom, tr.ValidTo);
                    session.Store(newEdiDocument);
                    return newEdiDocument;
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

        public static void StoreOrUpdateInRavenDb(IDocumentSession session, List<EdiDocument> ediDocuments)
        {
            _log.Debug("Saving {DocumentCount} documents to ravendb", ediDocuments.Count);

            IQueryable<EdiDocument> allExtraEdiDocs = session.Query<EdiDocument, EdiDocuments_DocumentUri>();
            foreach (var document in ediDocuments)
            {
                var document1 = document;
                allExtraEdiDocs = allExtraEdiDocs.Where(doc => doc.DocumentUri != document1.DocumentUri);
            }

            var extraDocs = allExtraEdiDocs.ToList();
            foreach (var extraDoc in extraDocs)
            {
                //this document was on the ediEnergy page some time ago but it is not there anymore
                extraDoc.IsLatestVersion = false;
                session.Store(extraDoc);
            }

            foreach (var ediDocument in ediDocuments)
            {
                session.Store(ediDocument);
            }
        }

        private static void CreateMirrorAndAnalyzePdfContent(IDocumentSession session, EdiDocument ediDocument)
        {
            if (ediDocument.MirrorUri!=null && (ediDocument.TextContentPerPage != null || ediDocument.DocumentUri.EndsWith(".zip"))) return;

            bool documentRequiresTextAnlyzing = Path.GetExtension(ediDocument.DocumentUri) == ".pdf";
            var pdfStream = DownloadAndCreateMirror(session, ediDocument, documentRequiresTextAnlyzing);
            ediDocument.MirrorUri = new Uri(ediDocument.Id + Path.GetExtension(ediDocument.DocumentUri), UriKind.Relative);

            if (documentRequiresTextAnlyzing)
            {
                if (ediDocument.TextContentPerPage == null)
                {
                    _log.Verbose("Analyzing pdf text content for {ediDocumentId}", ediDocument.Id);
                    ediDocument.TextContentPerPage = AnalyzePdfContent(pdfStream);
                    pdfStream.Dispose();
                }
                else
                {
                    _log.Verbose("Skipping analyze of pdf text content for {ediDocumentId}", ediDocument.Id);                    
                }
                _log.Verbose("identified checkidentifiers are {CheckIdentifier}", ediDocument.CheckIdentifier);
            }
        }

        private static string[] AnalyzePdfContent(Stream pdfStream)
        {
            string[] result;

            using (var streamCopy = new MemoryStream())
            {
                pdfStream.CopyTo(streamCopy);
                pdfStream.Position = 0;
                streamCopy.Position = 0;
                using (var reader = new PdfReader(streamCopy))
                {
                    result = Enumerable.Range(1, reader.NumberOfPages)
                        .Select(pageNumber => PdfTextExtractor.GetTextFromPage(reader, pageNumber))
                        .ToArray();
                }
            }

            return result;
        }

        private static Stream DownloadAndCreateMirror(IDocumentSession session, EdiDocument ediDocument, bool returnValueRequired)
        {
            _log.Debug("testing mirror file availability for {ediDocumentId}", ediDocument.Id);

            var pdfAttachmentExists = session.Advanced.AttachmentExists(ediDocument.Id, "pdf");

            _log.Debug(pdfAttachmentExists ? "the file is mirrored" : "The file does not exist");

            if (!pdfAttachmentExists)
            {
                _log.Debug("Downloading copy of ressource {DocumentUri}", ediDocument.DocumentUri);
                Stream originalDataStream = GetFilestream(ediDocument.DocumentUri);
                
                var streamForAnalyzing = new MemoryStream((int)originalDataStream.Length);
                originalDataStream.CopyTo(streamForAnalyzing);
                originalDataStream.Position = 0;
                streamForAnalyzing.Position = 0;

                var hash = BuildHash(streamForAnalyzing);
                streamForAnalyzing.Position = 0;

                session.Advanced.GetMetadataFor(ediDocument)["pdf-hash"] = hash;
                session.Advanced.StoreAttachment(ediDocument, "pdf", originalDataStream);

                _log.Debug("Stored copy of ressource {DocumentUri}", ediDocument.DocumentUri);

                if (!returnValueRequired)
                {
                    streamForAnalyzing.Dispose();
                    return null;
                }

                return streamForAnalyzing;
            }
            else
            {
                if (!returnValueRequired) return null;

                var pdfAttachment = session.Advanced.GetAttachment(ediDocument, "pdf");
                var stream = pdfAttachment.Stream;
                var ms = new MemoryStream();
                stream.CopyTo(ms);
                ms.Position = 0;
                return ms;
            }
        }

        private static Stream GetFilestream(string documentUri)
        {
            var uriHash = BitConverter.ToString(SHA256.Create().ComputeHash(Encoding.UTF8.GetBytes(documentUri)));
            var tempFolder = Environment.GetFolderPath(Environment.SpecialFolder.InternetCache);

            var tempFileName = Path.Combine(tempFolder, uriHash + Path.GetExtension(documentUri));
            if (!File.Exists(tempFileName))
            {
                _log.Verbose("Needing to download {documentUri}", documentUri);
                var client = new HttpClient();
                var responseMessage = client.GetAsync(documentUri).Result;
                responseMessage.EnsureSuccessStatusCode();
                var bytes = responseMessage.Content.ReadAsByteArrayAsync().Result;
                File.WriteAllBytes(tempFileName, bytes);
            }
            else
            {
                _log.Information("serving from cache: {documentUri}", documentUri);
            }
            return File.OpenRead(tempFileName);
        }

        private static string BuildHash(Stream stream)
        {
            _log.Verbose("Starting hash computation.");
            var hashBytes = SHA512.Create().ComputeHash(stream);
            stream.Position = 0;
            _log.Debug("Computed hash is {hash}", hashBytes);
            return Convert.ToBase64String(hashBytes);
        }

        public static (bool thereIsMore,bool saveChangesRequired) UpdateExistingEdiDocuments(IDocumentSession session)
        {
            const int batchSize = 3;

            var notMirroredDocuments = session
                    .Query<EdiDocument, EdiDocuments_MirrorUri>()
                    .Statistics(out var stats)
                    .Customize(c => c.WaitForNonStaleResultsAsOfNow())
                    .Where(doc => doc.MirrorUri == null)
                    .Take(batchSize)
                    .ToList();

            _log.Debug("Found {notMirroredDocumentsCount} documents which are not mirrored!", notMirroredDocuments.Count);


            if (notMirroredDocuments.Count == 0) return (false, false);

            foreach (var ediDocument in notMirroredDocuments.Take(batchSize))
            {
                CreateMirrorAndAnalyzePdfContent(session, ediDocument);
            }
            return ( thereIsMore: stats.TotalResults > batchSize
                , saveChangesRequired: notMirroredDocuments.Count > 0);
        }
    }
}