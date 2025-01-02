using System;

namespace EdiEnergyExtractor;

public class ExportRunStatistics
{
    public const string DefaultId = "ExportRunStatistics/1";

    public string Id { get; set; }
    public DateTime RunFinishedUtc { get; set; }
}
