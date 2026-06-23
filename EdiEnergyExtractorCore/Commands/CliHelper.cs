using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Configuration;
using NLog;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Spectre.Console;

namespace EdiEnergyExtractor.Commands;

internal static class CliHelper
{
    private static readonly Logger _log = LogManager.GetCurrentClassLogger();

    public static string ResolveEnvironment(string? provided)
    {
        if (TryNormalizeEnvironment(provided, out var normalized))
        {
            return normalized;
        }

        var fromEnvVar = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
        if (TryNormalizeEnvironment(fromEnvVar, out normalized))
        {
            return normalized;
        }

        return AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Select the target [green]environment[/]:")
                .AddChoices("Development", "Production"));
    }

    private static bool TryNormalizeEnvironment(string? value, out string normalized)
    {
        if (string.Equals(value, "Development", StringComparison.OrdinalIgnoreCase))
        {
            normalized = "Development";
            return true;
        }

        if (string.Equals(value, "Production", StringComparison.OrdinalIgnoreCase))
        {
            normalized = "Production";
            return true;
        }

        normalized = string.Empty;
        return false;
    }

    public static IConfigurationRoot BuildConfig(string environmentName) =>
        new ConfigurationBuilder()
            .AddJsonFile("appsettings.json")
            .AddUserSecrets(Assembly.GetExecutingAssembly(), optional: true)
            .AddEnvironmentVariables("EdiDocuments_")
            .AddEnvironmentVariables("EdiEnergy_")
            .AddJsonFile($"appsettings.{environmentName}.json", true)
            .Build();

    public static (string? Username, string? Password) ResolveCredentials(ExtractSettings settings, IConfigurationRoot config)
    {
        var username = settings.Username ?? config["Username"];
        var password = settings.Password ?? config["Password"];

        if (string.IsNullOrEmpty(username))
        {
            username = AnsiConsole.Prompt(
                new TextPrompt<string>("BDEW [green]username[/] (leave empty to run without credentials):")
                    .AllowEmpty());
        }

        if (!string.IsNullOrEmpty(username) && string.IsNullOrEmpty(password))
        {
            password = AnsiConsole.Prompt(
                new TextPrompt<string>("BDEW [green]password[/]:")
                    .Secret()
                    .AllowEmpty());
        }

        return (
            string.IsNullOrEmpty(username) ? null : username,
            string.IsNullOrEmpty(password) ? null : password);
    }

    public static bool ResolveFlag(bool provided, string prompt)
    {
        if (provided)
        {
            return true;
        }

        return AnsiConsole.Confirm(prompt, defaultValue: false);
    }

    public static bool ConfirmExtract(string environmentName, IConfigurationRoot config, string? username, string? password, bool preferCache, bool dryRun)
    {
        var table = NewSummaryTable("extract", environmentName, config);
        table.AddRow("Prefer cache", preferCache.ToString());
        table.AddRow("Dry run", dryRun.ToString());
        table.AddRow("Username", string.IsNullOrEmpty(username) ? "(none)" : username);
        table.AddRow("Password", string.IsNullOrEmpty(password) ? "(none)" : "***");

        AnsiConsole.Write(table);
        return AnsiConsole.Confirm("Proceed with these parameters?");
    }

    public static bool ConfirmAnalyze(string environmentName, IConfigurationRoot config)
    {
        var table = NewSummaryTable("analyze", environmentName, config);

        AnsiConsole.Write(table);
        return AnsiConsole.Confirm("Proceed with these parameters?");
    }

    private static Table NewSummaryTable(string command, string environmentName, IConfigurationRoot config)
    {
        var table = new Table()
            .Title("[yellow]Parameters[/]")
            .Border(TableBorder.Rounded);
        table.AddColumn("Parameter");
        table.AddColumn("Value");

        table.AddRow("Command", command);
        table.AddRow("Environment", environmentName);
        table.AddRow("Database URL", config["DatabaseUrl"] ?? "(none)");
        table.AddRow("Database Name", config["DatabaseName"] ?? "(none)");
        table.AddRow("Has Certificate", (!string.IsNullOrEmpty(config["DatabaseCertificate"])).ToString());

        return table;
    }

    public static DocumentStore GetDocumentStore(IConfigurationRoot config)
    {
        _log.Trace("Initializing RavenDB DocumentStore");
        var store = new DocumentStore()
        {
            Urls = [config["DatabaseUrl"]],
            Database = config["DatabaseName"]
        };

        var certPath = config["DatabaseCertificate"];
        if (!string.IsNullOrEmpty(certPath))
        {
            if (!File.Exists(certPath)) throw new Exception($"certificate files does not exist: {certPath}");

            var limits = new Pkcs12LoaderLimits { PreserveStorageProvider = true };
            store.Certificate = X509CertificateLoader.LoadPkcs12(File.ReadAllBytes(certPath), null, X509KeyStorageFlags.MachineKeySet, limits);
        }

        store.Initialize();
        _log.Trace("Creating RavenDB indexe");
        IndexCreation.CreateIndexes(Assembly.GetExecutingAssembly(), store);

        _log.Debug("Initialized RavenDB DocumentStore");
        return store;
    }
}
