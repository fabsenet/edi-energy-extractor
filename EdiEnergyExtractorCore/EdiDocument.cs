using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NLog;

namespace EdiEnergyExtractorCore
{

    [DebuggerDisplay("EdiDocument {ValidFrom}-{ValidTo} {DocumentNameRaw} ({Id})")]
    public class EdiDocument
    {
        private static readonly ILogger _log = LogManager.GetCurrentClassLogger();
        private static readonly CultureInfo _germanCulture = new CultureInfo("de-DE");


        public EdiDocument()
        {

        }

        public EdiDocument(string documentNameRaw, string documentUri, DateTime? validFrom, DateTime? validTo) : this()
        {
            DocumentNameRaw = documentNameRaw;
            DocumentUri = documentUri;
            ValidFrom = validFrom;
            ValidTo = validTo;

            var containedMessageTypes = EdiConstants.MessageTypes.Where(mt => DocumentNameRaw.Contains(mt)).ToArray();
            if (!containedMessageTypes.Any() && DocumentNameRaw.Contains("Herkunftsnachweisregister"))
            {
                //hknr files do not have the message types in the file name
                containedMessageTypes = new[] { "ORDERS", "ORDRSP", "UTILMD", "MSCONS" };
            }
            ContainedMessageTypes = containedMessageTypes.Any() ? containedMessageTypes : null;

            string saveFilename = Uri.UnescapeDataString(Path.GetFileNameWithoutExtension(DocumentUri))
                .Replace(" ", "_")
                ;

            //Id = $"EdiDocuments/{saveFilename}";

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


            IsGeneralDocument = !IsMig && !IsAhb;

            MessageTypeVersion = GetMessageTypeVersion();
        }


        public string Id { get; set; }
        public bool IsMig { get; set; }
        public bool IsAhb { get; set; }
        public bool IsGeneralDocument { get; set; }

        public bool IsGas => DocumentNameRaw?.Contains("gas", StringComparison.OrdinalIgnoreCase) == true;
        public bool IsStrom => DocumentNameRaw?.Contains("strom", StringComparison.OrdinalIgnoreCase) == true || DocumentNameRaw?.Contains("gpke", StringComparison.OrdinalIgnoreCase) == true;

        public bool IsStromUndOderGas => !IsGas && !IsStrom;

        public bool IsLatestVersion { get; set; }

        public DateTime? DocumentDate { get; set; }
        public DateTime? ValidFrom { get; set; }
        public DateTime? ValidTo { get; set; }
        public string DocumentName { get; set; }
        public string DocumentNameRaw { get; set; }
        public string DocumentUri { get; set; }
        public string[] ContainedMessageTypes { get; set; }
        public string MessageTypeVersion { get; set; }
        public string BdewProcess { get; set; }
        public IDictionary<int, List<int>> CheckIdentifier { get; set; }

        private string _filename;
        public string Filename
        {
            get => _filename;
            set
            {
                _filename = value;
                DocumentDate = GuessDocumentDateFromDocumentNameRawOrFilename();
            }
        }


        private string GetMessageTypeVersion()
        {
            if (IsGeneralDocument) return null;

            var regex = new Regex(@"(\d\.\d[a-z]{0,1})");

            var isUtiltsErrorUpstream = DocumentNameRaw == "UTILTS MIG\n 1.1e" && ValidFrom == new DateTime(2022, 10, 1);
            if (isUtiltsErrorUpstream) return "1.1a";

            var match = regex.Match(DocumentNameRaw);
            if (!match.Success)
            {
                return null;
            }

            return match.Groups[1].Value;
        }

        private DateTime? GuessDocumentDateFromDocumentNameRawOrFilename()
        {
            DateTime date;
            if (DocumentNameRaw.Contains("Stand:"))
            {
                //"UTILMD AHB GPKE GeLi Gas 6.0a\r\n Konsolidierte Lesefassung mit Fehlerkorrekturen\r\n Stand: 29. August 2014"
                var dateString = DocumentNameRaw.Split(new[] { "Stand:" }, StringSplitOptions.None)[1].Trim();

                date = DateTime.Parse(dateString, _germanCulture);
            }
            else
            {
                //alternatively try parsing the date from the file name
                var filename = Path.GetFileNameWithoutExtension(Filename)
                .Replace("_202112120_", "_20211220_"); //fix typo in filename='Ã?nderungshistorie XML-Datenformate_202112120_Onlineversion'

                if (filename.EndsWith("_end"))
                {
                    filename = filename.Substring(0, filename.Length - "_end".Length);
                }

                //could be like "APERAK_MIG_2_1a_2014_04_01"
                if (Regex.IsMatch(filename, @"_\d{4}_\d{2}_\d{2}$"))
                {
                    date = DateTime.ParseExact(filename.Substring(filename.Length - 10), "yyyy_MM_dd", _germanCulture);
                }

                //could be like "BK6-13-200_Beschluss_2014_04_16_Anlage_5"
                //could be like "EDI_Energy_AWH_MaKo2020_2020.02.18_Lieferschein_final_V1.1"
                else if (Regex.IsMatch(filename, @"_\d{4}(?:_|\.)\d{2}(?:_|\.)\d{2}_"))
                {
                    var match = Regex.Match(filename, @"_(?<year>\d{4})(?:_|\.)(?<month>\d{2})(?:_|\.)(?<day>\d{2})_", RegexOptions.ExplicitCapture);
                    var year = int.Parse(match.Groups["year"].Value);
                    var month = int.Parse(match.Groups["month"].Value);
                    var day = int.Parse(match.Groups["day"].Value);
                    date = new DateTime(year, month, day);
                }

                //could be like "CONTRL-APERAK_AHB_2_3a_20141001"
                else if (Regex.IsMatch(filename, @"_\d{8}$"))
                {
                    date = DateTime.ParseExact(filename.Substring(filename.Length - 8), "yyyyMMdd", _germanCulture);
                }

                //could be like "CONTRL-APERAK_AHB_2_3a_20141001_v2"
                // "PID_1_3_20200401_V3.pdf"
                //could be like "Codeliste-OBIS-Kennzahlen_2_2h_20190401_2"
                // or "Ã?nderungshistorie XML-Datenformate_202112120_Onlineversion"
                else if (Regex.IsMatch(filename, @"_202[1-3][0-1]\d[0-3]\d_[^_]*$"))
                {
                    var match = Regex.Match(filename, @"_(?<year>\d{4})(?<month>\d{2})(?<day>\d{2})_", RegexOptions.ExplicitCapture);
                    var year = int.Parse(match.Groups["year"].Value);
                    var month = int.Parse(match.Groups["month"].Value);
                    var day = int.Parse(match.Groups["day"].Value);
                    date = new DateTime(year, month, day);
                }
                else if (ValidFrom.HasValue && ValidFrom.Value.Year < 2017 && !ValidTo.HasValue)
                {
                    return ValidFrom.Value;
                }
                else if (filename == "INVOIC_MIG_2_7_2020401")
                {
                    //the source does not provide a date for this file, so we fake it
                    return new DateTime(2020, 4, 1);
                }
                else if (Filename == "Aenderungsantrag_EBD.xlsx")
                {
                    //the source does not provide a date for this file, so we fake it
                    return new DateTime(2019, 11, 11);
                }
                //could be like "APERAK_MIG_2_1a_2014_04_01"
                else if (Regex.IsMatch(filename, @"_\d{4}-\d{2}-\d{2}$"))
                {
                    date = DateTime.ParseExact(filename.Substring(filename.Length - 10), "yyyy-MM-dd", _germanCulture);
                }
                else if (Filename == "Beschaffungsanforderung FB 1.0a.pdf")
                {
                    return new DateTime(2021, 10, 1);
                }
                else if (Filename == "Beschaffungsanforderung AWT 1.0a.pdf")
                {
                    return new DateTime(2021, 10, 1);
                }
                else if (Filename == "Beschaffungsvorbehalt AWT 1.0a.pdf")
                {
                    return new DateTime(2021, 10, 1);
                }
                else if (Filename == "urn-entsoe-eu-local-extension-types XSD 1.0a_informatorische Lesefassung_2021001.xsd")
                {
                    //date in filename is missing a 1 !
                    return new DateTime(2021, 10, 1);
                }
                else if (filename == "AS4-Profil_final")
                {
                    return new DateTime(2022, 9, 1);
                }
                else if (Regex.IsMatch(filename, @"^Regelungen_zum_.{1,2}bertragungsweg_AS4_final$")) //schei? encoding
                {
                    return new DateTime(2022, 9, 1);
                }
                else if (Regex.IsMatch(filename, @"^EDI_Energy_AWH_Einf.{1,2}hrungsszenario_AS4_final$")) //schei? encoding
                {
                    return new DateTime(2022, 9, 1);
                }
                else if (Regex.IsMatch(filename, @"^AWH_Einf.{1,2}hrungsszenario_BK6-20-160_Version_1\.8$")) //schei? encoding
                {
                    return new DateTime(2022, 9, 29);
                }
                //could be like "Ã?nderungshistorie XML-Datenformate_20211206_Onlineversion"
                else if (Regex.IsMatch(filename, @"_20\d{6}_"))
                {
                    var match = Regex.Match(filename, @"_(?<year>\d{4})(?<month>\d{2})(?<day>\d{2})_", RegexOptions.ExplicitCapture);
                    var year = int.Parse(match.Groups["year"].Value);
                    var month = int.Parse(match.Groups["month"].Value);
                    var day = int.Parse(match.Groups["day"].Value);
                    date = new DateTime(year, month, day);
                }
                else
                {
                    throw new NotImplementedException($"cannot guess date for document. (DocumentNameRaw='{DocumentNameRaw}', filename='{filename}')");
                }
            }
            return date;
        }


        /// <summary>
        /// becomes the X in @"(?:X\d{3})" based on the detected messagetype
        /// </summary>
        private static readonly Dictionary<string, string> _checkIdentifierPatternPerMessageType = new Dictionary<string, string>()
        {
            {"UTILMD", "(?:11|44|55)"},
            {"MSCONS", "13"},
            {"QUOTES", "15"},
            {"ORDERS", "17"},
            {"ORDRSP", "19"},
            {"IFTSTA", "21"},
            {"INSRPT", "23"},
            {"PRICAT", "27"},
            {"INVOIC", "31"},
            {"REMADV", "33"},
            {"REQOTE", "35"},
            {"COMDIS", "29"},
            {"UTILTS", "25"},
            {"PARTIN", "37"},
            {"ORDCHG", "39"}
        };

        public void BuildCheckIdentifierList(IEnumerable<string> textContentPerPage)
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

            _log.Trace("Building stronger pattern based on ContainedMessageTypes: {ContainedMessageTypes}", ContainedMessageTypes);
            //    (?:11\d{3})

            var pattern = "(" +
                ContainedMessageTypes
                .Where(msg => _checkIdentifierPatternPerMessageType.ContainsKey(msg))
                .Select(msg => _checkIdentifierPatternPerMessageType[msg])
                .Select(part => @"(?:" + part + @"\d{3})")
                .Aggregate((p1, p2) => p1 + "|" + p2)
                + ")";

            _log.Debug("refined checkidentifier regex pattern to {pattern} based on contained messagetypes {ContainedMessageTypes}", pattern, ContainedMessageTypes);
            var result = new Dictionary<int, List<int>>();

            var pagenum = 0;
            foreach (var text in textContentPerPage)
            {
                pagenum++;

                var ids = Regex.Matches(text, pattern)
                    .Select(match => match.Value)
                    .Where(id => ContainedMessageTypes == null || ContainedMessageTypes
                        .Select(msgType => _checkIdentifierPatternPerMessageType.ContainsKey(msgType) ? _checkIdentifierPatternPerMessageType[msgType] : null)
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
                        if (!result.ContainsKey(id))
                        {
                            result[id] = new List<int>();
                        }
                        result[id].Add(pagenum);
                    }
                }
            }
            if (result.Any())
            {
                _log.Debug("Extracted checkIdentifiers: {result}", result);
                CheckIdentifier = result;
            }
            else
            {
                _log.Debug("Extracted no checkIdentifiers");
            }
        }
    }
}