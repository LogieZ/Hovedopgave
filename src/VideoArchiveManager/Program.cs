using Serilog;
using Serilog.Events;
using VideoArchiveManager.Configuration;
using VideoArchiveManager.Services;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .WriteTo.Console()
    .WriteTo.File("logs/archive-.txt", rollingInterval: RollingInterval.Day)
    .CreateLogger();

try
{
    Log.Information("=== Video Archive Manager Starter ===");

    var settings = new AppSettings
    {
        ArchiveRootPath = @"C:\Users\mttho\Videos", // RET DENNE til din test-mappe
        ConnectionString = "Host=localhost;Database=video_archive;Username=postgres;Password=1234",
        BatchSize = 100
    };

    var dbService = new DatabaseService(settings);
    var scanner = new ArchiveScanner(settings);
    var linker = new LinkerService(dbService, settings);

    Log.Information("Begynder scanning af arkiv: {Path}", settings.ArchiveRootPath);
    
    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (s, e) => {
        e.Cancel = true;
        cts.Cancel();
        Log.Warning("Stop-signal modtaget. Afslutter elegant...");
    };

    var report = await linker.LinkAsync(scanner, cts.Token);

    Log.Information("=== Scanning Færdig ===");
    Log.Information("Resultat: {Report}", report.ToString());

}
catch (OperationCanceledException)
{
    Log.Warning("Scanning blev afbrudt af brugeren.");
}
catch (Exception ex)
{
    Log.Fatal(ex, "Programmet stoppede pga. en kritisk fejl");
}
finally
{
    Log.Information("Rydder op og lukker...");
    Log.CloseAndFlush();
}