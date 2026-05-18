using Serilog;
using Serilog.Events;
using Microsoft.Extensions.DependencyInjection;
using VideoArchiveManager.Interfaces;
using VideoArchiveManager.Configuration;
using VideoArchiveManager.Services;
using VideoArchiveManager.Models;
using Microsoft.Extensions.Configuration;
using System.Diagnostics;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .WriteTo.Console()
    .WriteTo.File("logs/archive-.txt", rollingInterval: RollingInterval.Day)
    .CreateLogger();

try
{
    Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal;
    Log.Information("=== Video Archive Manager Starter ===");

    var config = new ConfigurationBuilder()
        .AddJsonFile("appsettings.json", optional: false)
        .Build();
    
    var settings = new AppSettings
    {
        ArchiveRootPath = config["ArchiveRootPath"]!,
        ConnectionString = config["ConnectionString"]!,
        ChannelUrl = config["ChannelUrl"]!,
        BatchSize = int.Parse(config["BatchSize"] ?? "100")
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
    var fileSystem = serviceProvider.GetRequiredService<IFileSystem>();
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

    Log.Information("Checking linked entries for files missing on disk...");

    var linkedEntries = dbService.StreamLinkedButMissingOnDisk();
    int brokenLinks = 0;

    foreach (var entry in linkedEntries)
    {
        if (cts.Token.IsCancellationRequested) break;
        if (string.IsNullOrWhiteSpace(entry.LinkedFilePath)) continue;

        if (!fileSystem.FileExists(entry.LinkedFilePath))
        {
            await dbService.UpdateLink(entry.YoutubeId, null, null, LinkStatus.Missing);
            brokenLinks++;
        }
    }

    if (brokenLinks > 0)
    {
        Log.Warning("Marked {Count} linked entries as Missing because files were not found on disk.", brokenLinks);
    }

    Log.Information("Checking if any missing videos need to be downloaded...");

    var totalMissingCount = await dbService.CountDownloadCandidatesAsync();
    var missingVideos = await dbService.GetDownloadCandidatesAsync(5);

    if (missingVideos.Any())
    {
        Log.Information(
            "There are {TotalMissing} videos missing locally. Starting download of {BatchCount} video(s) this run (max 5).",
            totalMissingCount,
            missingVideos.Count);

        foreach (var video in missingVideos)
        {
            if (cts.Token.IsCancellationRequested) break;

            int maxRetries = 3;
            bool downloadSuccess = false;

            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                if (cts.Token.IsCancellationRequested) break;

                try
                {
                    if (attempt > 1)
                    {
                        Log.Warning("Retry attempt {Attempt} of {MaxRetries} for video {Title}", attempt, maxRetries, video.Title);
                    }

                    downloadSuccess = await youtubeService.DownloadVideoAsync(video, settings.ArchiveRootPath, cts.Token);

                    if (downloadSuccess)
                    {
                        Log.Information("Successfully downloaded video: {Title}", video.Title);

                        Log.Information("Waiting 20 seconds before the next download..."); // To avoid YouTube becoming suspicious
                        await Task.Delay(20000, cts.Token);

                        break;
                    }
                    else
                    {
                        throw new Exception("Download returned false (yt-dlp failed)");
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("Attempt {Attempt} failed for video {Title}. Error: {Message}", attempt, video.Title, ex.Message);

                    if (attempt == maxRetries)
                    {
                        Log.Fatal("All {MaxRetries} attempts failed. Skipping video: {Title}", maxRetries, video.Title);
                        await dbService.UpdateLink(video.YoutubeId, null, null, LinkStatus.DownloadFailed);
                    }
                    else
                    {
                        int backoffDelay = (int)(Math.Pow(2, attempt -1) * 10000); // Exponential backoff: 10s, 20s, 40s
                        Log.Warning("Network or API issue. Waiting {Seconds} seconds before retrying...", backoffDelay / 1000);
                        await Task.Delay(backoffDelay, cts.Token);
                    }
                }
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