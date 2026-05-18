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
        BatchSize = 100,
        MaxDownloadsPerRun = 5,
        MaxDownloadRetries = 3,
        RetryBaseDelaySeconds = 10,
        InterDownloadDelaySeconds = 20
    };

    var serviceCollection = new ServiceCollection();
    serviceCollection.AddSingleton(settings);
    serviceCollection.AddSingleton<IAppSettings>(settings);
    serviceCollection.AddScoped<IArchiveDbContextFactory, ArchiveDbContextFactory>();
    serviceCollection.AddScoped<IVideoTitleMatcher, VideoTitleMatcher>();
    serviceCollection.AddScoped<IDatabaseService, DatabaseService>();
    serviceCollection.AddScoped<IYoutubeService, YoutubeService>();
    serviceCollection.AddScoped<IDownloadRetryPolicy, LinearBackoffDownloadRetryPolicy>();
    serviceCollection.AddScoped<IVideoDownloadCoordinator, VideoDownloadCoordinator>();

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
    var downloadCoordinator = serviceProvider.GetRequiredService<IVideoDownloadCoordinator>();
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
        .Take(settings.MaxDownloadsPerRun)
        .ToList();

    if (missingVideos.Any())
    {
        Log.Information("There are {Count} videos missing locally. Starting download of up to {MaxCount}...", unlinkedVideos.Count, settings.MaxDownloadsPerRun);
        await downloadCoordinator.DownloadMissingAsync(missingVideos, cts.Token);
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