using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using HtmlAgilityPack;
using iTextSharp.text.pdf;
using iTextSharp.text.pdf.parser;
using NLog;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using Path = System.IO.Path;

namespace EdiEnergyExtractorCore;

public record OnlineDocument
{
    public required string DocumentNameRaw { get; init; }
    public DateTime? ValidFrom { get; set; }
    public DateTime? ValidTo { get; set; }
    public required Uri DocumentUri { get; init; }
}

public record OnlineAndExistingMatchedDocument
{
    public required OnlineDocument Online { get; init; }
    public EdiDocument? Existing { get; set; }
}

internal partial class DataExtractor(CacheForcableHttpClient httpClient, IDocumentStore? store)
{
    private static readonly Logger _log = LogManager.GetCurrentClassLogger();

    private readonly List<string> _rootHtml = [];
    private readonly Uri _baseUri = new("https://www.edi-energy.de");
    private readonly Uri[] _webUris =
    {
        new ("https://www.edi-energy.de/index.php?id=38&tx_bdew_bdew%5Bview%5D=now&tx_bdew_bdew%5Baction%5D=list&tx_bdew_bdew%5Bcontroller%5D=Dokument&cHash=5d1142e54d8f3a1913af8e4cc56c71b2"),
        new ("https://www.edi-energy.de/index.php?id=38&tx_bdew_bdew%5Bview%5D=future&tx_bdew_bdew%5Baction%5D=list&tx_bdew_bdew%5Bcontroller%5D=Dokument&cHash=325de212fe24061e83e018a2223e6185")
    };

    public async Task LoadFromWeb()
    {
        _log.Debug("Loading source data from web.");

        Directory.CreateDirectory("SampleInput");

        foreach (var uri in _webUris)
        {
            var (content, filename) = await httpClient.GetAsync(uri).ConfigureAwait(false);

            _log.Debug("Receive response with {length} bytes for uri {uri}.", content.Length, uri);
            using var sr = new StreamReader(content);
            _rootHtml.Add(await sr.ReadToEndAsync().ConfigureAwait(false));
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
            using (var session = store?.OpenSession())
            {
                existingDocuments = session != null ? FetchExistingEdiDocuments(session) : [];

                //select all data rows from table
                var onlineDocs = htmlDocs
                    .SelectMany(d => d.SelectNodes("//table[1]//tr[.//a[@href]]"))
                    .Select(tr => new OnlineDocument
                    {
                        DocumentNameRaw = MultiWhitespaceRegex().Replace(tr.SelectSingleNode(".//td[1]").InnerText.Trim(), " "),
                        ValidFrom = ConvertToDateTime(tr.SelectSingleNode(".//td[2]")),
                        ValidTo = ConvertToDateTime(tr.SelectSingleNode(".//td[3]")),
                        DocumentUri = BuildDocumentUri(tr)
                    })
                    .Where(tr => !tr.DocumentNameRaw.Contains("EDIFACT Utilities", StringComparison.OrdinalIgnoreCase))
                    .Where(d => !d.DocumentNameRaw.Contains("informatorische Lesefassung", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                matchedDocs = onlineDocs
                    .Select(tr => new OnlineAndExistingMatchedDocument
                    {
                        Online = tr,
                        Existing = existingDocuments.FirstOrDefault(d => d.DocumentNameRaw == tr.DocumentNameRaw)
                    })
                    .ToList();

                _log.Info($"Extracted {matchedDocs.Count} online listed documents.");

                foreach (var updateMatch in matchedDocs.Where(d => d.Existing != null))
                {
                    updateMatch.Existing!.ValidTo = updateMatch.Online.ValidTo;
                    updateMatch.Existing.ValidFrom = updateMatch.Online.ValidFrom;
                }

                session?.SaveChanges();
            }

            var newDocumentNames = new List<string>();
            foreach (var doc in matchedDocs.Where(d => d.Existing == null).OrderByDescending(d => d.Online.ValidFrom))
            {
                var newEdiDocument = new EdiDocument(doc.Online.DocumentNameRaw, doc.Online.DocumentUri, doc.Online.ValidFrom, doc.Online.ValidTo);
                _log.Warn($"Working on new document {newEdiDocument.DocumentName} {newEdiDocument.MessageTypeVersion}");//date is guessed from filename and only available after download of the actual file!

                var pdfStream = await CreateMirrorAndAnalyzePdfContent(newEdiDocument).ConfigureAwait(false);
                newDocumentNames.Add($"{newEdiDocument.DocumentName} {newEdiDocument.MessageTypeVersion}, {newEdiDocument.DocumentDate?.ToString("d", _germanCulture) ?? "no date"}");

                if (store != null)
                {
                    using var session = store.OpenSession();
                    session.Store(newEdiDocument);
                    session.Advanced.Attachments.Store(newEdiDocument, "pdf", pdfStream);
                    ++newEdiDocumentCount;
                    _log.Info($"saving session changes after {newEdiDocumentCount} new documents.");
                    session.SaveChanges();
                }
            };

            if (newDocumentNames.Count != 0) _log.Warn($"New documents:\n{string.Join("\n", newDocumentNames)}");
        }

        if (store == null)
        {
            _log.Info($"The dryrun ends here!");
            return;
        }

        FixMessageVersions();
        UpdateValidToValuesOnGeneralDocuments();
        UpdateValidToValuesOnEdiDocuments();
        UpdateIsLatestVersionOnDocuments();
    }

    private void FixMessageVersions()
    {
        if (store == null) throw new InvalidOperationException("store is null");
        using var session = store.OpenSession();

        //refetch
        var ediDocuments = session.Query<EdiDocument>()
            .Where(e => !e.IsGeneralDocument)
            .Where(e => e.DocumentDate.HasValue && e.DocumentDate.Value.Year >= 2022) //do not mess with old docs
            .ToList();

        foreach (var doc in ediDocuments)
        {
            doc.MessageTypeVersion = doc.GetRawMessageTypeVersion();
        }

        session.SaveChanges();
    }

    private void UpdateValidToValuesOnGeneralDocuments()
    {
        if (store == null) throw new InvalidOperationException("store is null");
        using var session = store.OpenSession();

        //refetch
        var ediDocuments = session.Query<EdiDocument>()
            .Where(e => e.IsGeneralDocument)
            .ToList();

        var generalDocumentsGroups = from doc in ediDocuments
                                     orderby doc.ValidFrom descending, doc.DocumentDate descending

                                     group doc by new
                                     {
                                         doc.DocumentName,
                                         ContainedMessageTypesString =
                                             doc.ContainedMessageTypes != null && doc.ContainedMessageTypes.Count != 0
                                                 ? doc.ContainedMessageTypes.OrderBy(m => m).Aggregate((m1, m2) => m1 + ", " + m2)
                                                 : null
                                     }
                                into g
                                     where g.Count(e => !e.ValidTo.HasValue) > 1
                                     select g;

        foreach (var generalDocumentsGroup in generalDocumentsGroups)
        {
            EdiDocument? lastDocument = null;

            foreach (var ediDocument in generalDocumentsGroup)
            {
                if (lastDocument != null && !ediDocument.ValidTo.HasValue)
                {
                    //this document needs a new validto!
                    if (ediDocument.ValidFrom == lastDocument.ValidFrom)
                    {
                        ediDocument.ValidTo = lastDocument.DocumentDate!.Value;
                    }
                    else
                    {
                        Debug.Assert(lastDocument.ValidFrom.HasValue, "lastDocument.ValidFrom.HasValue");
                        ediDocument.ValidTo = lastDocument.ValidFrom.Value.Date.Subtract(TimeSpan.FromDays(1));
                    }
                }
                lastDocument = ediDocument;
            }
        }

        session.SaveChanges();
    }

    private void UpdateValidToValuesOnEdiDocuments()
    {
        if (store == null) throw new InvalidOperationException("store is null");
        using var session = store.OpenSession();

        //refetch
        var ediDocuments = session.Query<EdiDocument>()
            .Where(e => e.IsAhb || e.IsMig)
            .Where(e => !e.IsGeneralDocument)
            .Where(e => e.ContainedMessageTypes.Count != 0)
            .ToList();

        //ediDocuments = ediDocuments.Where(e => e.ContainedMessageTypes?.Contains("UTILMD") == true && e.ContainedMessageTypes.Count()>1).ToList();

        //determine what the latest document version is again
        var ediDocumentGroups = from doc in ediDocuments
                                where !doc.IsGeneralDocument
                                orderby doc.ValidFrom descending, doc.MessageTypeVersion descending, doc.DocumentDate descending

                                group doc by new
                                {
                                    doc.BdewProcess,
                                    doc.IsAhb,
                                    doc.IsMig,
                                    doc.IsGas,
                                    doc.IsStrom,
                                    doc.IsGeneralDocument,
                                    ContainedMessageTypesString =
                                        doc.ContainedMessageTypes != null && doc.ContainedMessageTypes.Count != 0
                                            ? doc.ContainedMessageTypes.OrderBy(m => m).Aggregate((m1, m2) => m1 + ", " + m2)
                                            : null
                                }
                                into g
                                where g.Count(e => !e.ValidTo.HasValue) > 1
                                select g;

        foreach (var ediDocumentGroup in ediDocumentGroups)
        {
            EdiDocument? lastDocument = null;

            foreach (var ediDocument in ediDocumentGroup)
            {
                if (lastDocument != null && !ediDocument.ValidTo.HasValue)
                {
                    //this document needs a new validto!
                    if (ediDocument.ValidFrom == lastDocument.ValidFrom)
                    {
                        if (ediDocument.MessageTypeVersion == lastDocument.MessageTypeVersion)
                        {
                            ediDocument.ValidTo = lastDocument.ValidTo;
                        }
                        else
                        {
                            //same validfrom date, same messagetype but different messagetypeversion?
                            if (ediDocument.ValidFrom == new DateTime(2021, 4, 1))
                            {
                                //they had released old messageversions at some point, we "fix" them by artificially updating there version number
                                ediDocument.MessageTypeVersion = lastDocument.MessageTypeVersion;
                            }
                            ediDocument.ValidTo = lastDocument.DocumentDate!.Value;
                        }

                    }
                    else
                    {
                        Debug.Assert(lastDocument.ValidFrom.HasValue, "lastDocument.ValidFrom.HasValue");
                        ediDocument.ValidTo = lastDocument.ValidFrom.Value.Date.Subtract(TimeSpan.FromDays(1));
                    }
                }
                lastDocument = ediDocument;
            }
        }

        session.SaveChanges();
    }

    private void UpdateIsLatestVersionOnDocuments()
    {
        if (store == null) throw new InvalidOperationException("store is null");
        using var session = store.OpenSession();
        //refetch
        var ediDocuments = FetchExistingEdiDocuments(session);

        //reset current latest document
        ediDocuments.ForEach(doc => doc.IsLatestVersion = false);

        //determine what the latest document version is again
        var ediDocumentGroups = from doc in ediDocuments
                                where !doc.IsGeneralDocument
                                group doc by new
                                {
                                    doc.BdewProcess,
                                    doc.MessageTypeVersion,
                                    doc.IsAhb,
                                    doc.IsMig,
                                    doc.IsGas,
                                    doc.IsStrom,
                                    doc.IsGeneralDocument,
                                    ContainedMessageTypesString =
                                        doc.ContainedMessageTypes != null && doc.ContainedMessageTypes.Count != 0
                                            ? doc.ContainedMessageTypes.OrderBy(m => m).Aggregate((m1, m2) => m1 + ", " + m2)
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
                                    orderby doc.DocumentDate descending
                                    group doc by new
                                    {
                                        doc.DocumentName
                                    }
                                    into g
                                    select g;

        foreach (var generalDocumentGroup in generalDocumentGroups)
        {
            var past = generalDocumentGroup.Where(d => d.ValidTo.HasValue && d.ValidTo < DateTime.Now).OrderByDescending(d => d.DocumentDate).FirstOrDefault();
            var current = generalDocumentGroup.Where(d => d.ValidFrom < DateTime.Now && (!d.ValidTo.HasValue || d.ValidTo > DateTime.Now)).OrderByDescending(d => d.DocumentDate).FirstOrDefault();
            var future = generalDocumentGroup.Where(d => (!d.ValidTo.HasValue || d.ValidTo > DateTime.Now)).OrderByDescending(d => d.DocumentDate).FirstOrDefault();

            if (past != null) past.IsLatestVersion = true;
            if (current != null) current.IsLatestVersion = true;
            if (future != null) future.IsLatestVersion = true;
        }

        session.SaveChanges();
    }

    private Uri BuildDocumentUri(HtmlNode tr)
    {
        var rawHref = tr.SelectSingleNode(".//td[4]//a[@href]").GetAttributeValue("href", null) ?? throw new ArgumentException("the href is null!");
        var href = rawHref.Replace("&amp;", "&", StringComparison.OrdinalIgnoreCase);
        href = CHashRegex().Replace(href, "");
        return new Uri(_baseUri, href);
    }

    private readonly CultureInfo _germanCulture = new("de-DE");

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

    private async Task<Stream?> CreateMirrorAndAnalyzePdfContent(EdiDocument ediDocument)
    {
        if (ediDocument.Filename != null) return null;

        var pdfStream = await DownloadAndCreateMirror(ediDocument).ConfigureAwait(false);

        var documentRequiresTextAnlyzing = Path.GetExtension(ediDocument.Filename) == ".pdf";
        if (documentRequiresTextAnlyzing)
        {
            _log.Trace($"Analyzing pdf text content for {ediDocument.Filename} ({pdfStream.Length} bytes)");
            var pdfStreamCopy = new MemoryStream();
            await pdfStream.CopyToAsync(pdfStreamCopy).ConfigureAwait(false);
            pdfStreamCopy.Position = 0;
            pdfStream.Position = 0;

            using var reader = new PdfReader(pdfStreamCopy);
            ediDocument.BuildCheckIdentifierList(
                                    Enumerable.Range(1, reader.NumberOfPages)
                                    .Select(pageNumber => PdfTextExtractor.GetTextFromPage(reader, pageNumber))
                                );
        }

        return pdfStream;
    }

    private async Task<MemoryStream> DownloadAndCreateMirror(EdiDocument ediDocument)
    {
        _log.Trace(CultureInfo.InvariantCulture, "Downloading copy of ressource {DocumentUri}", ediDocument.DocumentUri);
        var (stream, filename) = await httpClient.GetAsync(ediDocument.DocumentUri).ConfigureAwait(false);

        ediDocument.Filename = filename;

        _log.Trace(CultureInfo.InvariantCulture, "Stored copy of ressource {DocumentUri}", ediDocument.DocumentUri);

        return stream;
    }

    [GeneratedRegex(" {2,}")]
    private static partial Regex MultiWhitespaceRegex();

    [GeneratedRegex(@"&cHash=[a-zA-Z0-9]+")]
    private static partial Regex CHashRegex();
}
