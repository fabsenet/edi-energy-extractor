using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NLog;

namespace EdiEnergyExtractorCore;

[DebuggerDisplay("EdiDocument {ValidFrom}-{ValidTo} {DocumentNameRaw} ({Id})")]
public partial class EdiDocument
{
    private static readonly Logger _log = LogManager.GetCurrentClassLogger();
    private static readonly CultureInfo _germanCulture = new("de-DE");

    public EdiDocument()
    {

    }

    public EdiDocument(string documentNameRaw, Uri documentUri, DateTime? validFrom, DateTime? validTo) : this()
    {
        DocumentNameRaw = documentNameRaw;
        DocumentUri = documentUri;
        ValidFrom = validFrom;
        ValidTo = validTo;

        var containedMessageTypes = EdiConstants.MessageTypes.Where(mt => DocumentNameRaw.Contains(mt, StringComparison.Ordinal)).ToArray();
        if (containedMessageTypes.Length == 0 && DocumentNameRaw.Contains("Herkunftsnachweisregister", StringComparison.Ordinal))
        {
            //hknr files do not have the message types in the file name
            containedMessageTypes = ["ORDERS", "ORDRSP", "UTILMD", "MSCONS"];
        }
        ContainedMessageTypes = containedMessageTypes.Length != 0 ? containedMessageTypes : null;

        string saveFilename = Uri.UnescapeDataString(Path.GetFileNameWithoutExtension(DocumentUri.AbsoluteUri))
            .Replace(" ", "_", StringComparison.Ordinal)
            ;

        //Id = $"EdiDocuments/{saveFilename}";

        DocumentName = DocumentNameRaw.Split('\n', '\r').First();
        IsMig = DocumentNameRaw.Contains("MIG", StringComparison.Ordinal);

        if (IsMig)
        {
            BdewProcess = null;
        }
        else
        {
            BdewProcess = EdiConstants
                .EdiProcesses
                .Where(p => p.Value.Any(v => DocumentNameRaw.Contains(v, StringComparison.Ordinal)))
                .Select(p => p.Key)
                .SingleOrDefault();
        }

        IsAhb = DocumentNameRaw.Contains("AHB", StringComparison.Ordinal) || BdewProcess != null;

        IsGeneralDocument = !IsMig && !IsAhb;

        MessageTypeVersion = GetRawMessageTypeVersion();
    }

    public string Id { get; set; }
    public bool IsMig { get; set; }
    public bool IsAhb { get; set; }
    public bool IsGeneralDocument { get; set; }

    public bool IsGas => MessageTypeVersion?.StartsWith('G') == true || DocumentNameRaw?.Contains("gas", StringComparison.OrdinalIgnoreCase) == true;
    public bool IsStrom => MessageTypeVersion?.StartsWith('S') == true || DocumentNameRaw?.Contains("strom", StringComparison.OrdinalIgnoreCase) == true || DocumentNameRaw?.Contains("gpke", StringComparison.OrdinalIgnoreCase) == true;

    public bool IsStromUndOderGas => !IsGas && !IsStrom;

    public bool IsLatestVersion { get; set; }

    public DateTime? DocumentDate { get; set; }
    public DateTime? ValidFrom { get; set; }
    public DateTime? ValidTo { get; set; }
    public string DocumentName { get; set; }
    public string DocumentNameRaw { get; set; }
    public Uri DocumentUri { get; set; }
    public IReadOnlyCollection<string>? ContainedMessageTypes { get; set; }
    public string? MessageTypeVersion { get; set; }
    public string? BdewProcess { get; set; }
    public Dictionary<int, List<int>> CheckIdentifier { get; } = [];

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

    [GeneratedRegex("([sSgG]?\\d\\.\\d[a-z]?)")]
    private static partial Regex MessageTypeVersionRegex();

    public string? GetRawMessageTypeVersion()
    {
        if (IsGeneralDocument) return null;
        if (DocumentNameRaw == null) return null;

        var regex = MessageTypeVersionRegex();

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
        if (DocumentNameRaw.Contains("Stand:", StringComparison.Ordinal))
        {
            //"UTILMD AHB GPKE GeLi Gas 6.0a\r\n Konsolidierte Lesefassung mit Fehlerkorrekturen\r\n Stand: 29. August 2014"
            var dateString = DocumentNameRaw.Split("Stand:", StringSplitOptions.None)[1].Trim();

            date = DateTime.Parse(dateString, _germanCulture);
        }
        else
        {
            //alternatively try parsing the date from the file name
            var filename = Path.GetFileNameWithoutExtension(Filename)
            .Replace("_202112120_", "_20211220_", StringComparison.Ordinal); //fix typo in filename='Ã?nderungshistorie XML-Datenformate_202112120_Onlineversion'

            if (filename.EndsWith("_end", StringComparison.Ordinal))
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
                var year = int.Parse(match.Groups["year"].Value, _germanCulture);
                var month = int.Parse(match.Groups["month"].Value, _germanCulture);
                var day = int.Parse(match.Groups["day"].Value, _germanCulture);
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
                var year = int.Parse(match.Groups["year"].Value, _germanCulture);
                var month = int.Parse(match.Groups["month"].Value, _germanCulture);
                var day = int.Parse(match.Groups["day"].Value, _germanCulture);
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
            else if (filename == "Kostenblatt FB_1.0c_202404")
            {
                return new DateTime(2024, 4, 2);
            }
            else if (Regex.IsMatch(filename, @"^Regelungen_zum_.{1,2}bertragungsweg_AS4_final$")) //schei? encoding
            {
                return new DateTime(2022, 9, 1);
            }
            else if (Regex.IsMatch(filename, @"^Regelungen_zum_.{1,2}bertragungsweg_AS4_2_1$")) //schei? encoding
            {
                return new DateTime(2023, 10, 4);
            }
            else if (Regex.IsMatch(filename, @"^Regelungen_zum_.{1,2}bertragungsweg_AS4_2\.2$")) //schei? encoding
            {
                return new DateTime(2024, 4, 2);
            }
            else if (Regex.IsMatch(filename, @"^EDI_Energy_AWH_Einf.{1,2}hrungsszenario_AS4_final$")) //schei? encoding
            {
                return new DateTime(2022, 9, 1);
            }
            else if (Regex.IsMatch(filename, @"^Regelungen_zum_.{1,2}bertragungsweg_1\.7$")) //schei? encoding
            {
                return new DateTime(2023, 12, 13);
            }
            else if (Regex.IsMatch(filename, @"^Regelungen_zum_.{1,2}bertragungsweg_1\.8$")) //schei? encoding
            {
                return new DateTime(2024, 4, 2);
            }
            else if (Regex.IsMatch(filename, @"^AWH_Einf.{1,2}hrungsszenario_BK6-20-160_Version_1\.8$")) //schei? encoding
            {
                return new DateTime(2022, 9, 29);
            }
            else if (Regex.IsMatch(filename, @"^AWH_Einf.{1,2}hrungsszenario_Redispatch 2\.0_Unavailability_MarketDocument _V1\.0$")) //schei? encoding
            {
                return new DateTime(2023, 12, 11);
            }
            else if (Regex.IsMatch(filename, @"^AWH_Einf.{1,2}hrungsszenario_Redispatch 2\.0_Unavailability_MarketDocument _V1\.1$")) //schei? encoding
            {
                return new DateTime(2024, 1, 25);
            }
            else if (Regex.IsMatch(filename, @"^Rz._API_Webdienste_1_0_v05_final$")) //schei? encoding
            {
                return new DateTime(2024, 7, 3);
            }
            else if (Regex.IsMatch(filename, @"^EDI_Energy_AWH_Einf.{1,2}hrungsszenario_AS4_Gas$")) //schei? encoding
            {
                return new DateTime(2024, 4, 2);
            }
            //could be like "Ã?nderungshistorie XML-Datenformate_20211206_Onlineversion"
            else if (Regex.IsMatch(filename, @"_20\d{6}_"))
            {
                var match = Regex.Match(filename, @"_(?<year>\d{4})(?<month>\d{2})(?<day>\d{2})_", RegexOptions.ExplicitCapture);
                var year = int.Parse(match.Groups["year"].Value, _germanCulture);
                var month = int.Parse(match.Groups["month"].Value, _germanCulture);
                var day = int.Parse(match.Groups["day"].Value, _germanCulture);
                date = new DateTime(year, month, day);
            }
            else
            {
                throw new NotImplementedException($"cannot guess date for document. (DocumentNameRaw='{DocumentNameRaw}', filename='{filename}'). source = {DocumentUri}");
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
        ArgumentNullException.ThrowIfNull(textContentPerPage, nameof(textContentPerPage));

        _log.Trace("BuildCheckIdentifierList() called.");

        if (!IsAhb)
        {
            _log.Debug("this is not an AHB document, so there should not be any checkIdentifier. Exiting BuildCheckIdentifierList()");
            return;
        }

        if (ContainedMessageTypes == null || ContainedMessageTypes.Count == 0 || !ContainedMessageTypes.Any(msgType => _checkIdentifierPatternPerMessageType.ContainsKey(msgType)))
        {
            CheckIdentifier.Clear();
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
        var checkIdentifiers = new Dictionary<int, List<int>>();

        var pagenum = 0;
        foreach (var text in textContentPerPage)
        {
            pagenum++;

            var ids = Regex.Matches(text, pattern)
                .Select(match => match.Value)
                .Select(id => Convert.ToInt32(id, _germanCulture))
                .Distinct()
                .OrderBy(id => id)
                .ToList();

            if (ids.Count != 0)
            {
                foreach (var id in ids)
                {
                    if (!checkIdentifiers.TryGetValue(id, out var value))
                    {
                        value = [];
                        checkIdentifiers[id] = value;
                    }

                    value.Add(pagenum);
                }
            }
        }
        if (checkIdentifiers.Count != 0)
        {
            _log.Debug("Extracted checkIdentifiers: {result}", checkIdentifiers);
            CheckIdentifier.Clear();
            foreach (var checkIdentifier in checkIdentifiers)
            {
                CheckIdentifier.Add(checkIdentifier.Key, checkIdentifier.Value);
            }
        }
        else
        {
            _log.Debug("Extracted no checkIdentifiers");
        }
    }

}
