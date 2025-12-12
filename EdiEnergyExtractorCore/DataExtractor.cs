using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using EdiEnergyExtractor;
using iTextSharp.text.pdf;
using iTextSharp.text.pdf.parser;
using NLog;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using Path = System.IO.Path;

namespace EdiEnergyExtractorCore;

internal record OnlineDocument
{
    public required string DocumentNameRaw { get; init; }
    public DateTime? ValidFrom { get; set; }
    public DateTime? ValidTo { get; set; }
    public required Uri DocumentUri { get; init; }
}

internal record OnlineAndExistingMatchedDocument
{
    public required OnlineDocument Online { get; init; }
    public EdiDocument? Existing { get; set; }
}

internal partial class DataExtractor(CacheForcableHttpClient httpClient, IDocumentStore? store)
{
    private static readonly Logger _log = LogManager.GetCurrentClassLogger();

    private readonly List<string> _rootHtml = [];
    private readonly Uri _baseUri = new("https://bdew-mako.de");
    private readonly Uri[] _webUris =
    {
        new ("https://bdew-mako.de/api/documents"),
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
    /// <summary>
    /// Sample JSON:
    /// {"userId":0,"id":5486,"fileId":9314,"title":"IFTSTA MIG 2.0d - konsolidierte Lesefassung mit Fehlerkorrekturen Stand: 06.12.2021","version":null,"topicId":116,"topicGroupId":17,"isFree":true,"publicationDate":null,"validFrom":"2022-04-01T00:00:00","validTo":"2022-01-30T00:00:00","isConsolidatedReadingVersion":false,"isExtraordinaryPublication":false,"isErrorCorrection":false,"correctionDate":null,"isInformationalReadingVersion":false,"fileType":"application/pdf","topicGroupSortNr":1,"topicSortNr":1}
    /// </summary>

    internal record OnlineJsonDocument
    {
        public required string Title { get; set; }
        public int Id { get; set; }
        public int FileId { get; set; }
        public int TopicId { get; set; }
        public int TopicGroupId { get; set; }
        public bool IsFree { get; set; }
        public required DateTime ValidFrom { get; set; }
        public DateTime? ValidTo { get; set; }
        public bool IsConsolidatedReadingVersion { get; set; }
        public bool IsExtraordinaryPublication { get; set; }
        public bool IsErrorCorrection { get; set; }
        public bool IsInformationalReadingVersion { get; set; }
        public string? FileType { get; set; }
        public int TopicGroupSortNr { get; set; }
        public int TopicSortNr { get; set; }
    }

    internal record BdewMakoApiDocumentsResponse
    {
        public List<OnlineJsonDocument>? Data { get; set; }
    }

    public async Task AnalyzeResult()
    {
        var jsonSerializerOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            RespectRequiredConstructorParameters = true
        };

        var onlineJsonDocs = _rootHtml
                        .SelectMany(json => JsonSerializer.Deserialize<BdewMakoApiDocumentsResponse>(json, jsonSerializerOptions)?.Data ?? [])
                        .Where(d => d.ValidTo == null || d.ValidTo.Value.Year >= 2023) //do not mess with old docs
                        .Where(d => d.Id != 7791) // hide a broken (and duplicate) document "UTILTS AHB 1.0 - konsolidierte Lesefassung mit Fehlerkorrekturen Stand: 18.02.2025"
                        .OrderBy(d => d.Title)
                        .ThenByDescending(d => d.ValidFrom)
                        .ToList();

        foreach (var jsonDoc in onlineJsonDocs)
        {
            jsonDoc.Title = jsonDoc.Title.Trim();
        }

        var pdfOnlineDocs = onlineJsonDocs.Where(d => d.FileType == "application/pdf" && d.IsFree).ToList();

        int newEdiDocumentCount = 0;
        List<EdiDocument> existingDocuments;

        List<OnlineAndExistingMatchedDocument> matchedDocs;
        using (var session = store?.OpenSession())
        {
            existingDocuments = session != null ? FetchExistingEdiDocuments(session) : [];

            //select all data rows from table
            var onlineDocs = pdfOnlineDocs
                .Select(jsonDoc => new OnlineDocument
                {
                    DocumentNameRaw = jsonDoc.Title,
                    ValidFrom = jsonDoc.ValidFrom,
                    ValidTo = jsonDoc.ValidTo,
                    DocumentUri = new Uri(_baseUri, $"api/downloadFile/{jsonDoc.FileId}")
                })
                .Where(tr => !tr.DocumentNameRaw.Contains("EDIFACT Utilities", StringComparison.OrdinalIgnoreCase))
                .Where(d => !d.DocumentNameRaw.Contains("informatorische Lesefassung", StringComparison.OrdinalIgnoreCase))
                .ToList();

            matchedDocs = onlineDocs
                .Select(onlineDoc => new OnlineAndExistingMatchedDocument
                {
                    Online = onlineDoc,
                    Existing = existingDocuments.FirstOrDefault(d => d.DocumentUri == onlineDoc.DocumentUri)
                })
                .ToList();

            var newDocumentCount = matchedDocs.Count(d => d.Existing == null);


            _log.Info($"Extracted {matchedDocs.Count} online listed documents. {newDocumentCount} are new!");

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
        }

        if (newDocumentNames.Count != 0) _log.Warn($"New documents:\n{string.Join("\n", newDocumentNames)}");

        //now the same for XML documents
        {
            var xmlOnlineDocs = onlineJsonDocs.Where(d => d.FileType == "text/xml").Select(OnlineJsonDocument2EdiXmlDocumentMapper.Map).ToList();
            using var session = store?.OpenSession();
            {
                var existingXmlDocuments = session != null ? session.Query<EdiXmlDocument>().ToList() : [];
                foreach (var xmlDoc in xmlOnlineDocs)
                {
                    var existingDoc = existingXmlDocuments.FirstOrDefault(d => d.FileId == xmlDoc.FileId);
                    if (existingDoc != null)
                    {
                        //copy over values that might have changed
                        existingDoc.ValidFrom = xmlDoc.ValidFrom;
                        existingDoc.ValidTo = xmlDoc.ValidTo;
                    }
                    else
                    {
                        //store new document
                        session?.Store(xmlDoc);
                        var (xmlStream, attachmentFilename) = await DownloadXmlContentAsync(xmlDoc).ConfigureAwait(false);
                        xmlDoc.AttachmentFilename = attachmentFilename;
                        session?.Advanced.Attachments.Store(xmlDoc, "xml", xmlStream);
                        _log.Info($"Saved new XML document {xmlDoc}");
                    }
                }
                session?.SaveChanges();
            }
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

        //ediDocuments = ediDocuments.Where(e => e.ContainedMessageTypes?.Contains("UTILMD") == true && e.IsAhb && e.IsStrom).ToList();

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

            var ediDocumentGroupOrdered = ediDocumentGroup
                .OrderByDescending(d => d.MessageTypeVersion)
                .ThenByDescending(d => d.DocumentDate > d.ValidFrom ? d.DocumentDate : d.ValidFrom)
                .ThenByDescending(d => d.Id)
                .ToList();

            foreach (var ediDocument in ediDocumentGroupOrdered)
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
            .Select(g => g.OrderByDescending(doc => doc.DocumentDate)
                            .ThenByDescending(doc => doc.DocumentUri.ToString())//it happened that the same document was uploaded twice and this selects the newer one
                            .First()
                            )
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
            var future = generalDocumentGroup.Where(d => !d.ValidTo.HasValue || d.ValidTo > DateTime.Now).OrderByDescending(d => d.DocumentDate).FirstOrDefault();

            if (past != null) past.IsLatestVersion = true;
            if (current != null) current.IsLatestVersion = true;
            if (future != null) future.IsLatestVersion = true;
        }

        session.SaveChanges();
    }


    private readonly CultureInfo _germanCulture = new("de-DE");

    private DateTime? ConvertToDateTime(string? textValue)
    {
        if (DateTime.TryParse(textValue, _germanCulture, DateTimeStyles.None, out var dt))
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

    private async Task<(MemoryStream content, string filename)> DownloadXmlContentAsync(EdiXmlDocument xmlDoc)
    {
        var (stream, filename) = await httpClient.GetAsync(new Uri(_baseUri, $"api/downloadFile/{xmlDoc.FileId}")).ConfigureAwait(false);

        return (stream, filename);
    }

    [GeneratedRegex(" {2,}")]
    private static partial Regex MultiWhitespaceRegex();

    [GeneratedRegex(@"&cHash=[a-zA-Z0-9]+")]
    private static partial Regex CHashRegex();
}
