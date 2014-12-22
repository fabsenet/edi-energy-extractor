using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using HtmlAgilityPack;
using Newtonsoft.Json;

namespace EdiEnergyExtractor
{
    internal class DataExtractor
    {
        string rootHtml;
        Uri baseUri = new Uri("http://edi-energy.de");

        public void LoadFromFile(string rootPath)
        {
            if (!File.Exists(rootPath)) { throw new FileNotFoundException("Root file not found. (Expected location: '" + rootPath + "')"); }
            rootHtml = File.ReadAllText(rootPath);
        }

        public void LoadFromWeb()
        {
            var client = new HttpClient();
            var responseMessage = client.GetAsync("http://edi-energy.de").Result;
            responseMessage.EnsureSuccessStatusCode();
            rootHtml = responseMessage.Content.ReadAsStringAsync().Result;
        }

        public void AnalyzeResult()
        {
            var htmlDoc = new HtmlDocument();
            htmlDoc.LoadHtml(rootHtml);

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
                            DocumentUri = new Uri(baseUri, tr.SelectSingleNode(".//td[2]//a[@href]").GetAttributeValue("href", "")),
                            ValidFrom = ConvertToDateTime(tr.SelectSingleNode(".//td[3]")),
                            ValidTo = ConvertToDateTime(tr.SelectSingleNode(".//td[4]"))
                        })
                .ToList();
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
    }
}