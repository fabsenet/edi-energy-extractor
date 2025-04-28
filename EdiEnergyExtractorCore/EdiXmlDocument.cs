using System;

namespace EdiEnergyExtractor;
internal record EdiXmlDocument
{
    public required string Id { get; internal set; }

    public required string OriginalTitle { get; internal set; }
    public required string CleanedTitle { get; internal set; }

    public required string MessageType { get; internal set; }
    public required string MessageVersion { get; internal set; }
    public required bool IsAHB { get; internal set; }
    public required bool IsMIG { get; internal set; }

    public required int FileId { get; internal set; }
    public required DateTime ValidFrom { get; internal set; }
    public required DateTime? ValidTo { get; internal set; }
    public string? AttachmentFilename { get; internal set; }
}
