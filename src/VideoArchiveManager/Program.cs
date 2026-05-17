using Serilog;
using Serilog.Events;
using Microsoft.Extensions.DependencyInjection;
using VideoArchiveManager.Interfaces;
using VideoArchiveManager.Configuration;
using VideoArchiveManager.Services;
using VideoArchiveManager.Models;

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
        ArchiveRootPath = @"F:\DKCTV Filer",
        ConnectionString = "Host=localhost;Database=video_archive;Username=postgres;Password=1234",
        BatchSize = 100
    };

    var serviceCollection = new ServiceCollection();
    serviceCollection.AddSingleton(settings);
    serviceCollection.AddScoped<IDatabaseService, DatabaseService>();
    serviceCollection.AddScoped<IYoutubeService, YoutubeService>();

    if (OperatingSystem.IsWindows())
    {
        Log.Information("Runs on Windows. Injecting WindowsFileSystem");
        serviceCollection.AddScoped<IFileSystem, WindowsFileSystem>();
    }
    else
    {
        Log.Information("Runs on Linux. Injecting LinuxFileSystem");
        serviceCollection.AddScoped<IFileSystem, LinuxFileSystem>();
    }

    serviceCollection.AddScoped<ArchiveScanner>();
    serviceCollection.AddScoped<LinkerService>();

    var serviceProvider = serviceCollection.BuildServiceProvider();

    var dbService = serviceProvider.GetRequiredService<IDatabaseService>();
    var youtubeService = serviceProvider.GetRequiredService<IYoutubeService>();
    var scanner = serviceProvider.GetRequiredService<ArchiveScanner>();
    var linker = serviceProvider.GetRequiredService<LinkerService>();
    
    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (s, e) => {
        e.Cancel = true;
        cts.Cancel();
        Log.Warning("Stop signal received. Exiting gracefully...");
    };

    await youtubeService.ImportChannelVideosAsync(settings.ChannelUrl);

    Log.Information("Starting scan and match of archive: {Path}", settings.ArchiveRootPath);

    var report = await linker.LinkAsync(scanner, cts.Token);

    Log.Information("Checking if any missing videos need to be downloaded...");

    var unlinkedVideos = await dbService.GetAllUnlinkedEntriesAsync();

    var missingVideos = unlinkedVideos
        .Take(5) // We take 5 at a time to be safe
        .ToList();

    if (missingVideos.Any())
    {
        Log.Information("There are {Count} videos missing locally. Starting download of the first 5...", unlinkedVideos.Count);

        foreach (var video in missingVideos)
        {
            if (cts.Token.IsCancellationRequested) break;

            // Save them in ArchiveRootPath
            bool success = await youtubeService.DownloadVideoAsync(video, settings.ArchiveRootPath);

            if (success)
            {
                Log.Information("Waiting 20 seconds before the next download..."); // To avoid YouTube becoming suspicious
                await Task.Delay(20000, cts.Token);
            }
        }
    }
    else
    {
        Log.Information("No videos are missing. Everything is archived.");
    }

    Log.Information("=== Process Complete ===");
    Log.Information("Result: {Report}", report.ToString());

}
catch (OperationCanceledException)
{
    Log.Warning("Scanning was canceled by the user.");
}
catch (Exception ex)
{
    Log.Fatal(ex, "The program stopped due to a critical error");
}
finally
{
    Log.Information("Cleaning up and closing...");
    Log.CloseAndFlush();
}