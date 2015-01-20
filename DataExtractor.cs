using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using HtmlAgilityPack;
using Newtonsoft.Json;

namespace Fabsenet.EdiEnergy
{
    internal class DataExtractor
    {
        string _rootHtml;
        readonly Uri _baseUri = new Uri("http://edi-energy.de");

        public void LoadFromFile(string rootPath)
        {
            if (!File.Exists(rootPath)) { throw new FileNotFoundException("Root file not found. (Expected location: '" + rootPath + "')"); }
            _rootHtml = File.ReadAllText(rootPath);
        }

        public void LoadFromWeb()
        {
            var client = new HttpClient();
            var responseMessage = client.GetAsync("http://edi-energy.de").Result;
            responseMessage.EnsureSuccessStatusCode();


            //sure, why not use windows encoding without telling anybody in http- or html- header?
            //what could possibly go wrong? :D
            //UTF8 (default): Erg�nzende Beschreibung
            //ANSII: Erg?nzende Beschreibung
            //1252 (Windows): Ergänzende Beschreibung
            var responseBytes = responseMessage.Content.ReadAsByteArrayAsync().Result;
            _rootHtml = Encoding.GetEncoding(1252).GetString(responseBytes);
        }

        public void AnalyzeResult()
        {
            var htmlDoc = new HtmlDocument();
            htmlDoc.LoadHtml(_rootHtml);

            //select all data rows from table
            _documents = htmlDoc
                .DocumentNode
                .SelectNodes("//table[1]//tr[position()>2 and .//a[@href]]")
                .Select(
                    tr =>
                        new EdiDocument
                        {
                            IsCurrent = tr.SelectSingleNode(".//td[1]").InnerText.Contains("*"),
                            DocumentNameRaw = tr.SelectSingleNode(".//td[2]").InnerText.Trim(),
                            DocumentUri = new Uri(_baseUri, tr.SelectSingleNode(".//td[2]//a[@href]").GetAttributeValue("href", "")),
                            ValidFrom = ConvertToDateTime(tr.SelectSingleNode(".//td[3]")),
                            ValidTo = ConvertToDateTime(tr.SelectSingleNode(".//td[4]"))
                        })
                .ToList();

            //determine what the latest document version is
            var documentGroups = from doc in _documents
                group doc by new
                {
                    doc.BdewProcess, 
                    doc.ValidFrom, 
                    doc.ValidTo, 
                    doc.IsAhb,
                    doc.IsMig,
                    doc.IsGeneralDocument,
                    ContainedMessageTypesString = doc.ContainedMessageTypes!= null && doc.ContainedMessageTypes.Any() ? doc.ContainedMessageTypes.Aggregate((m1,m2)=>m1+", "+m2) : null
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

        public string GetResultAsJson()
        {
            return JsonConvert.SerializeObject(_documents
                .OrderBy(d => !d.IsGeneralDocument)
                .ThenBy(d => d.ContainedMessageTypes==null ? null : d.ContainedMessageTypes[0])
                .ThenBy(d => d.DocumentDate)
            , 
            
#if DEBUG
            Formatting.Indented, 
#else
            Formatting.None, 
#endif
 new JsonSerializerSettings()
            {
                NullValueHandling = NullValueHandling.Ignore
            });
        }

        public List<EdiDocument> Documents
        {
            get { return _documents; }
        }
    }
}