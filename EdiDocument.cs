using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using JetBrains.Annotations;
using Newtonsoft.Json;
using NLog;
using Raven.Abstractions.Extensions;
using ServiceStack.Text;

namespace Fabsenet.EdiEnergy
{
    public class EdiDocument
    {
        private static readonly Logger _log = LogManager.GetCurrentClassLogger();


        public EdiDocument()
        {
            
        }

        public EdiDocument(string documentNameRaw, Uri documentUri, DateTime? validFrom, DateTime? validTo) : this()
        {
            DocumentNameRaw = Regex.Replace(documentNameRaw, "[ ]+", " ");
            DocumentUri = documentUri;
            ValidFrom = validFrom;
            ValidTo = validTo;
            
            var containedMessageTypes = EdiConstants.MessageTypes.Where(mt => DocumentNameRaw.Contains(mt)).ToArray();
            if (!containedMessageTypes.Any() && DocumentNameRaw.Contains("Herkunftsnachweisregister"))
            {
                //hknr files do not have the message types in the file name
                containedMessageTypes = new[] {"ORDERS", "ORDRSP", "UTILMD", "MSCONS"};
            }
            ContainedMessageTypes = containedMessageTypes.Any() ? containedMessageTypes : null;

            Id = $"EdiDocuments/{Path.GetFileNameWithoutExtension(DocumentUri.AbsolutePath)}";

            DocumentName = DocumentNameRaw.Split('\n', '\r').First();
            IsMig = DocumentNameRaw.Contains("MIG");

            if (IsMig)
            {
                BdewProcess = null;
            }
            else
            {
                BdewProcess = EdiConstants
                    .EdiProcesses
                    .Where(p => p.Value.Any(v => DocumentNameRaw.Contains(v)))
                    .Select(p => p.Key)
                    .SingleOrDefault();
            }

            IsAhb = DocumentNameRaw.Contains("AHB") || BdewProcess != null;
            DocumentDate = GuessDocumentDateFromDocumentNameRawOrFilename();


            IsGeneralDocument = !IsMig && !IsAhb;

            MessageTypeVersion = GetMessageTypeVersion();
        }


        public Uri DocumentUri { get; set; }
        public Uri MirrorUri { get; set; }
        public DateTime? ValidFrom { get; set; }
        public DateTime? ValidTo { get; set; }   
        public string DocumentName { get; private set; }
        public string Id { get; private set; }
        public bool IsMig { get; private set; }
        public bool IsAhb { get; private set; }
        public string[] ContainedMessageTypes { get; private set; }

        public bool IsGeneralDocument { get; private set; }

        public string MessageTypeVersion { get; private set; }

        private string GetMessageTypeVersion()
        {
            if (IsGeneralDocument) return null;

            var regex = new Regex(@"(\d\.\d[a-z]{0,1})");
            var match = regex.Match(DocumentNameRaw);
            if (!match.Success)
            {
                return null;
            }

            return match.Groups[1].Value;
        }

        private static readonly CultureInfo _germanCulture = new CultureInfo("de-DE");

        public DateTime DocumentDate { get; private set; }

        private DateTime GuessDocumentDateFromDocumentNameRawOrFilename()
        {
            DateTime date;
            if (DocumentNameRaw.Contains("Stand:"))
            {
                //"UTILMD AHB GPKE GeLi Gas 6.0a\r\n Konsolidierte Lesefassung mit Fehlerkorrekturen\r\n Stand: 29. August 2014"
                var dateString = DocumentNameRaw.Split(new[] {"Stand:"}, StringSplitOptions.None)[1].Trim();

                date = DateTime.Parse(dateString, _germanCulture);
            }
            else
            {
                //alternatively try parsing the date from the file name
                var filename = Path.GetFileNameWithoutExtension(DocumentUri.AbsoluteUri);

                //could be like "APERAK_MIG_2_1a_2014_04_01"
                if (Regex.IsMatch(filename, @"_\d{4}_\d{2}_\d{2}$"))
                {
                    date = DateTime.ParseExact(filename.Substring(filename.Length - 10), "yyyy_MM_dd", _germanCulture);
                }

                //could be like "CONTRL-APERAK_AHB_2_3a_20141001"
                else if (Regex.IsMatch(filename, @"_\d{8}$"))
                {
                    date = DateTime.ParseExact(filename.Substring(filename.Length - 8), "yyyyMMdd", _germanCulture);
                }
                else
                {
                    throw new Exception("Could not guess the document date for '" + DocumentUri + "'");
                }
            }
            return date;
        }

        public string BdewProcess { get; private set; }

        public string DocumentNameRaw { get; private set; }

        public bool IsLatestVersion { get; set; }


        public string[] TextContentPerPage
        {
            get { return _textContentPerPage; }
            set
            {
                _textContentPerPage = value;
                if (value != null)
                {
                    BuildCheckIdentifierList();
                }
            }
        }
        private string[] _textContentPerPage;

        public Dictionary<int, List<int>> CheckIdentifier { get; private set; }

        private static readonly Dictionary<string, string> _checkIdentifierPatternPerMessageType = new Dictionary<string, string>()
        {
            {"UTILMD", "11"},
            {"MSCONS", "13"},
            {"QUOTES", "15"},
            {"ORDERS", "17"},
            {"ORDRSP", "19"},
            {"IFTSTA", "21"},
            {"INSRPT", "23"},
            {"INVOIC", "31"},
            {"REMADV", "33"},
            {"REQOTE", "35"},
        };

        private void BuildCheckIdentifierList()
        {
            _log.Trace("BuildCheckIdentifierList() called.");

            if (!IsAhb)
            {
                _log.Debug("this is not an AHB document, so there should not be any checkIdentifier. Exiting BuildCheckIdentifierList()");
                return;
            }

            if (ContainedMessageTypes == null || !ContainedMessageTypes.Any() || !ContainedMessageTypes.Any(msgType => _checkIdentifierPatternPerMessageType.ContainsKey(msgType)))
            {
                CheckIdentifier = null;
                return;
            }
            
            _log.Trace("Building stronger pattern based on ContainedMessageTypes: {0}", ContainedMessageTypes.Dump());
            //    (?:11\d{3})

            var pattern = "(" + 
                ContainedMessageTypes
                .Where(msg => _checkIdentifierPatternPerMessageType.ContainsKey(msg))
                .Select(msg => _checkIdentifierPatternPerMessageType[msg])
                .Select(part => @"(?:" + part + @"\d{3})")
                .Aggregate((p1, p2) => p1 + "|" + p2) 
                + ")";

            _log.Debug("refined checkidentifier regex pattern to {0} based on contained messagetypes {1}", pattern, ContainedMessageTypes.Dump());
            var result = new Dictionary<int, List<int>>();

            var pagenum = 0;
            foreach (var text in TextContentPerPage)
            {
                pagenum++;

                var ids = Regex.Matches(text, pattern)
                    .OfType<Match>()
                    .Select(match => match.Value)
                    .Where(id => ContainedMessageTypes == null || ContainedMessageTypes
                        .Select(msgType => _checkIdentifierPatternPerMessageType.GetOrDefault(msgType))
                        .Where(prefix => prefix != null)
                        .Any(prefix => id.StartsWith(prefix))
                    )
                    .Select(id => Convert.ToInt32(id))
                    .Distinct()
                    .OrderBy(id => id)
                    .ToList();

                if (ids.Any())
                {
                    foreach (var id in ids)
                    {
                        result.GetOrAdd(id).Add(pagenum);
                    }
                }
            }
            if (result.Any())
            {
                _log.Debug("Extracted checkIdentifiers: {0}", result.Dump());
                CheckIdentifier = result;
            }
            else
            {
                _log.Debug("Extracted no checkIdentifiers");
            }
        }
    }
}