using System.ComponentModel;
using Spectre.Console.Cli;

namespace EdiEnergyExtractor.Commands;

internal sealed class ExtractSettings : BaseSettings
{
    [CommandOption("--username")]
    [Description("Username for bdew-mako.de.")]
    public string? Username { get; set; }

    [CommandOption("--password")]
    [Description("Password for bdew-mako.de.")]
    public string? Password { get; set; }

    [CommandOption("--prefercache")]
    [Description("Prefer the local cache over actual web access.")]
    public bool PreferCache { get; set; }

    [CommandOption("--dryrun")]
    [Description("Download only; do not operate on the database at all.")]
    public bool DryRun { get; set; }
}
