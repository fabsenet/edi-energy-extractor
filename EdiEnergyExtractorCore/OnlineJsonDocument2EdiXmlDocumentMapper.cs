using System;
using System.Text.RegularExpressions;
using static EdiEnergyExtractorCore.DataExtractor;

namespace EdiEnergyExtractor;
internal partial class OnlineJsonDocument2EdiXmlDocumentMapper
{
    public static EdiXmlDocument Map(OnlineJsonDocument onlineJsonDocument)
    {
        var ediXmlDocument = new EdiXmlDocument
        {
            Id = $"EdiXmlDocument/{onlineJsonDocument.Id}",
            OriginalTitle = onlineJsonDocument.Title,
            CleanedTitle = CleanTitle(onlineJsonDocument.Title),
            FileId = onlineJsonDocument.FileId,
            ValidFrom = onlineJsonDocument.ValidFrom,
            ValidTo = onlineJsonDocument.ValidTo,
            IsAHB = IsAhbRegex().IsMatch(onlineJsonDocument.Title),
            IsMIG = IsMigRegex().IsMatch(onlineJsonDocument.Title),
            MessageType = ReadMessageType(onlineJsonDocument.Title),
            MessageVersion = ReadMessageVersion(onlineJsonDocument.Title)
        };
        return ediXmlDocument;
    }

    private static string ReadMessageVersion(string title)
    {
        var match = MessageVersionRegex().Match(CleanTitle(title));
        if (!match.Success) throw new Exception($"MessageVersion not found in title: {title}.");

        return match.Groups["msgversion"].Value;
    }

    private static string ReadMessageType(string title)
    {
        var match = MessageTypeRegex().Match(CleanTitle(title));
        if (!match.Success) throw new Exception($"MessageType not found in title: {title}.");

        return match.Groups["msgtype"].Value;
    }

    private static string CleanTitle(string title)
    {
        return title.Trim().Replace("  ", " ", StringComparison.Ordinal);
    }

    [GeneratedRegex(@"\bAHB\b", RegexOptions.IgnoreCase, "en-DE")]
    private static partial Regex IsAhbRegex();

    [GeneratedRegex(@"\bMIG\b", RegexOptions.IgnoreCase, "en-DE")]
    private static partial Regex IsMigRegex();

    [GeneratedRegex(@"\b(?<msgtype>APERAK|CONTRL|UTILMD|INVOIC|MSCONS|ORDCHG|ORDERS|ORDRSP|PRICAT|QUOTES|IFTSTA|INSRPT|REMADV|REQOTE|IDEXGM|REQDOC|COMDIS|UTILTS|PARTIN)\b")]
    private static partial Regex MessageTypeRegex();

    [GeneratedRegex(@"\b(?<msgversion>[GS]?\d\.\d[a-zA-Z]?)\b")]
    private static partial Regex MessageVersionRegex();
}
