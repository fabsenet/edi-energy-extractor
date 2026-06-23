using System.Globalization;
using System.Threading;
using EdiEnergyExtractor.Commands;
using NLog;
using Spectre.Console.Cli;

namespace EdiEnergyExtractor;

static class Program
{
    private static readonly Logger _log = LogManager.GetCurrentClassLogger();

    static int Main(string[] args)
    {
        Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
        _log.Debug("EdiEnergyExtractor started.");

        var app = new CommandApp();
        app.Configure(config =>
        {
            config.SetApplicationName("EdiEnergyExtractor");

            config.AddCommand<ExtractCommand>("extract")
                .WithDescription("Scrape bdew-mako.de, download new documents and store them in RavenDB.");

            config.AddCommand<AnalyzeCommand>("analyze")
                .WithDescription("Read existing data from RavenDB and print the check identifiers of future AHBs.");

#if DEBUG
            config.PropagateExceptions();
#else
            config.SetExceptionHandler((ex, _) =>
            {
                _log.Fatal(ex, "Unhandled exception, program execution aborted!");

                if (System.Diagnostics.Debugger.IsAttached)
                {
                    System.Diagnostics.Debugger.Break();
                }

                return -1;
            });
#endif
        });

        try
        {
            return app.Run(args);
        }
        finally
        {
            LogManager.Shutdown();
        }
    }
}
