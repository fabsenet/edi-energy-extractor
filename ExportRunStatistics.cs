using System;

namespace Fabsenet.EdiEnergy
{
    public class ExportRunStatistics
    {
        public const string DefaultId = "ExportRunStatistics/1";

        public string Id { get; set; }
        public DateTime RunFinishedUtc { get; set; }
    }
}