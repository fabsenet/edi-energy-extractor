using System;
using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;

namespace EdiEnergyExtractor.Commands;

internal class BaseSettings : CommandSettings
{
    [CommandOption("--environment")]
    [Description("Target environment. Allowed values: Development, Production.")]
    public string? Environment { get; set; }

    public override ValidationResult Validate()
    {
        if (Environment is not null
            && !string.Equals(Environment, "Development", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(Environment, "Production", StringComparison.OrdinalIgnoreCase))
        {
            return ValidationResult.Error("--environment must be either 'Development' or 'Production'.");
        }

        return ValidationResult.Success();
    }
}
