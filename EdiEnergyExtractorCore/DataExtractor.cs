﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using HtmlAgilityPack;
using iTextSharp.text.pdf;
using iTextSharp.text.pdf.parser;
using NLog;
using NLog.LayoutRenderers;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using Path = System.IO.Path;

namespace EdiEnergyExtractorCore
{
    public class OnlineDocument
    {
        public string DocumentNameRaw { get; set; }
        public DateTime? ValidFrom { get; set; }
        public DateTime? ValidTo { get; set; }
        public string DocumentUri { get; set; }
    }

    public class OnlineAndExistingMatchedDocument
    {
        public OnlineDocument Online { get; set; }
        public EdiDocument Existing { get; set; }
    }

    internal class DataExtractor
    {
        private IDocumentStore Store { get; }
        private static readonly ILogger _log = LogManager.GetCurrentClassLogger();

        private List<string> _rootHtml;
        private readonly Uri _baseUri = new Uri("https://www.edi-energy.de");

        private readonly CacheForcableHttpClient _httpClient;

        public DataExtractor(CacheForcableHttpClient httpClient, IDocumentStore store)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            Store = store ?? throw new ArgumentNullException(nameof(store));
        }

        private readonly string[] _webUris =
        {
            "https://www.edi-energy.de/index.php?id=38&tx_bdew_bdew%5Bview%5D=now&tx_bdew_bdew%5Baction%5D=list&tx_bdew_bdew%5Bcontroller%5D=Dokument",
            "https://www.edi-energy.de/index.php?id=38&tx_bdew_bdew%5Bview%5D=future&tx_bdew_bdew%5Baction%5D=list&tx_bdew_bdew%5Bcontroller%5D=Dokument"
        };

        public async Task LoadFromWeb()
        {
            _log.Debug("Loading source data from web.");

            Directory.CreateDirectory("SampleInput");

            //_rootHtml = (await Task.WhenAll(_webUris.Select(async (uri, index) =>
            //{
            //    var (content, filename) = await _httpClient.GetAsync(uri);

            //    _log.Debug("Receive response with {length} bytes for uri {uri}.", content.Length, uri);
            //    return new StreamReader(content).ReadToEnd();

            //}))).ToList();

            _rootHtml = new List<string>(_webUris.Length);
            foreach (var uri in _webUris)
            {
                var (content, filename) = await _httpClient.GetAsync(uri);

                _log.Debug("Receive response with {length} bytes for uri {uri}.", content.Length, uri);
                _rootHtml.Add(new StreamReader(content).ReadToEnd());
            }


        }

        private List<EdiDocument> FetchExistingEdiDocuments(IDocumentSession session)
        {
            var docs = session
                    .Query<EdiDocument>()
                    .ToList();
            return docs;
        }

        public async Task AnalyzeResult()
        {
            var htmlDocs = _rootHtml.Select(html =>
                {
                    var htmlDoc = new HtmlDocument();
                    htmlDoc.LoadHtml(html);

                    return htmlDoc.DocumentNode;
                })
                .ToList();


            {
                int newEdiDocumentCount = 0;
                List<EdiDocument> existingDocuments;

                List<OnlineAndExistingMatchedDocument> matchedDocs;
                using (var session = Store.OpenSession())
                {
                    existingDocuments = FetchExistingEdiDocuments(session);

                    //select all data rows from table
                    var onlineDocs = htmlDocs
                        .SelectMany(d => d.SelectNodes("//table[1]//tr[.//a[@href]]"))
                        .Select(tr => new OnlineDocument
                        {
                            DocumentNameRaw = Regex.Replace(tr.SelectSingleNode(".//td[1]").InnerText.Trim(), "[ ]+", " "), ValidFrom = ConvertToDateTime(tr.SelectSingleNode(".//td[2]")), ValidTo = ConvertToDateTime(tr.SelectSingleNode(".//td[3]")),
                            DocumentUri = BuildDocumentUri(tr)
                        })
                        .Where(tr => !tr.DocumentNameRaw.Contains("EDIFACT Utilities"));


                    matchedDocs = onlineDocs
                        .Select(tr => new OnlineAndExistingMatchedDocument
                        { Online = tr,
                            Existing = existingDocuments.FirstOrDefault(d => d.DocumentNameRaw == tr.DocumentNameRaw)})
                        .ToList();

                    _log.Info($"Extracted {matchedDocs.Count} online listed documents.");

                    foreach (var updateMatch in matchedDocs.Where(d => d.Existing != null))
                    {
                        updateMatch.Existing.ValidTo = updateMatch.Online.ValidTo;
                        updateMatch.Existing.ValidFrom = updateMatch.Online.ValidFrom;
                    }

                    session.SaveChanges();
                }

                var tasks = matchedDocs
                    .Where(d => d.Existing == null)
                    .Select(async doc =>
                    {
                        var newEdiDocument = new EdiDocument(doc.Online.DocumentNameRaw, doc.Online.DocumentUri, doc.Online.ValidFrom, doc.Online.ValidTo);
                        var pdfStream = await CreateMirrorAndAnalyzePdfContent(newEdiDocument);

                        using (var session = Store.OpenSession())
                        {
                            session.Store(newEdiDocument);
                            session.Advanced.Attachments.Store(newEdiDocument, "pdf", pdfStream);
                            ++newEdiDocumentCount;
                            _log.Info($"saving session changes after {newEdiDocumentCount} new documents.");
                            session.SaveChanges();
                        }

                    });

                foreach (var task in tasks)
                {
                    await task;
                }
            }

            using (var session = Store.OpenSession())
            {
                //refetch
                var ediDocuments = FetchExistingEdiDocuments(session).                    Where(d => d.DocumentNameRaw.Contains("Regelun")).ToList();

                //reset current latest document
                ediDocuments.ForEach(doc => doc.IsLatestVersion = false);

                //determine what the latest document version is again
                var ediDocumentGroups = from doc in ediDocuments
                                        where !doc.IsGeneralDocument
                                        group doc by new
                                        {
                                            doc.BdewProcess,
                                            doc.ValidFrom,
                                            doc.ValidTo,
                                            doc.IsAhb,
                                            doc.IsMig,
                                            doc.IsGeneralDocument,
                                            ContainedMessageTypesString =
                                                doc.ContainedMessageTypes != null && doc.ContainedMessageTypes.Any()
                                                    ? doc.ContainedMessageTypes.Aggregate((m1, m2) => m1 + ", " + m2)
                                                    : null
                                        }
                                        into g
                                        select g;

                var newestEdiDocumentsInEachGroup = ediDocumentGroups
                    .Select(g => g.OrderByDescending(doc => doc.DocumentDate).First())
                    .ToList();

                newestEdiDocumentsInEachGroup.ForEach(doc => doc.IsLatestVersion = true);


                var generalDocumentGroups = from doc in ediDocuments
                    where doc.IsGeneralDocument
                    group doc by new
                    {
                        doc.ValidFrom,
                        doc.ValidTo,
                        doc.DocumentName
                    }
                    into g
                    select g;

                var newestGeneralDocumentsInEachGroup = generalDocumentGroups
                    .Select(g => g.OrderByDescending(doc => doc.DocumentDate).First())
                    .ToList();

                newestGeneralDocumentsInEachGroup.ForEach(doc => doc.IsLatestVersion = true);

                session.SaveChanges();
            }
        }

        private string BuildDocumentUri(HtmlNode tr)
        {
            var rawHref = tr.SelectSingleNode(".//td[4]//a[@href]").GetAttributeValue("href", null) ?? throw new Exception("the href is null!");
            var href = rawHref.Replace("&amp;", "&");
            href = Regex.Replace(href, @"&cHash=[a-zA-Z0-9]+", "");
            return new Uri(_baseUri, href).AbsoluteUri;
        }

        private readonly CultureInfo _germanCulture = new CultureInfo("de-DE");

        private DateTime? ConvertToDateTime(HtmlNode htmlNode)
        {
            if (htmlNode == null)
            {
                return null;
            }
            var text = htmlNode.InnerText.Trim();

            if (DateTime.TryParse(text, _germanCulture, DateTimeStyles.None, out var dt))
            {
                return dt;
            }
            return null;
        }


        private async Task<Stream> CreateMirrorAndAnalyzePdfContent(EdiDocument ediDocument)
        {
            if (ediDocument.Filename != null) return null;

            var pdfStream = await DownloadAndCreateMirror(ediDocument);

            var documentRequiresTextAnlyzing = Path.GetExtension(ediDocument.Filename) == ".pdf";
            if (documentRequiresTextAnlyzing)
            {
                _log.Trace($"Analyzing pdf text content for {ediDocument.Filename} ({pdfStream.Length} bytes)");
                using (var reader = new PdfReader(((MemoryStream) pdfStream).ToArray()))
                {
                    ediDocument.BuildCheckIdentifierList(Enumerable.Range(1, reader.NumberOfPages)
                        .Select(pageNumber => PdfTextExtractor.GetTextFromPage(reader, pageNumber)));
                }
            }

            return pdfStream;
        }

        private async Task<Stream> DownloadAndCreateMirror(EdiDocument ediDocument)
        {
            _log.Debug("Downloading copy of ressource {DocumentUri}", ediDocument.DocumentUri);
            var (stream, filename) = await _httpClient.GetAsync(ediDocument.DocumentUri);

            ediDocument.Filename = filename;

            _log.Debug("Stored copy of ressource {DocumentUri}", ediDocument.DocumentUri);

            return stream;
        }
    }
}