using System;
using System.Linq;
using System.Threading;
using EdiEnergyExtractorCore;
using NLog;
using Raven.Client.Documents;
using Spectre.Console.Cli;

namespace EdiEnergyExtractor.Commands;

internal sealed class AnalyzeCommand : Command<AnalyzeSettings>
{
    private static readonly Logger _log = LogManager.GetCurrentClassLogger();

    protected override int Execute(CommandContext context, AnalyzeSettings settings, CancellationToken cancellationToken)
    {
        var environmentName = CliHelper.ResolveEnvironment(settings.Environment);
        var config = CliHelper.BuildConfig(environmentName);

        if (!CliHelper.ConfirmAnalyze(environmentName, config))
        {
            _log.Info("Aborted by user.");
            return 1;
        }

        _log.Info("Environment: {environment}", environmentName);
        _log.Info("Database URL: {DatabaseUrl}, Database Name: {DatabaseName}, Has Certificate? {HasCertificate}", config["DatabaseUrl"], config["DatabaseName"], !string.IsNullOrEmpty(config["DatabaseCertificate"]));

        using var store = CliHelper.GetDocumentStore(config);
        ReadExistingDataForAnalysis(store);
        return 0;
    }

    private static void ReadExistingDataForAnalysis(DocumentStore store)
    {
        using var session = store.OpenSession();

        //select future AHBs
        var docs = session.Query<EdiDocument>()
            .Where(d => d.IsAhb)
            .Where(d => d.ValidTo == null)
            .Where(d => d.IsLatestVersion)
            .ToList();

        var existingPIs = docs
            .Where(d => d.CheckIdentifier != null && d.CheckIdentifier.Count != 0)
            .SelectMany(d => d.CheckIdentifier.Keys)
            .Distinct()
            .OrderBy(p => p)
            .ToList();

        foreach (var pi in existingPIs)
        {
            Console.WriteLine($"{pi},");
        }
    }
}
