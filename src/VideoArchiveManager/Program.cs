using Serilog;
using Microsoft.Extensions.DependencyInjection;
using VideoArchiveManager.Interfaces;
using VideoArchiveManager.Configuration;
using VideoArchiveManager.Services;
using VideoArchiveManager.Models;
using Microsoft.Extensions.Configuration;
using System.Diagnostics;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .CreateLogger();

try
{
    Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal;
    var config = new ConfigurationBuilder()
        .AddJsonFile("appsettings.json", optional: false)
        .Build();

    var settings = new AppSettings
    {
        ArchiveRootPath = config["ArchiveRootPath"] ?? throw new InvalidOperationException("Missing required setting: ArchiveRootPath"),
        ConnectionString = config["ConnectionString"] ?? throw new InvalidOperationException("Missing required setting: ConnectionString"),
        ChannelUrl = config["ChannelUrl"] ?? throw new InvalidOperationException("Missing required setting: ChannelUrl"),
        BatchSize = int.TryParse(config["BatchSize"], out var batchSize) ? batchSize : 100,
        MetadataEnrichmentBatchSize = int.TryParse(config["MetadataEnrichmentBatchSize"], out var metadataBatchSize) ? metadataBatchSize : 50,
        YtDlpPath = config["YtDlpPath"] ?? "yt-dlp",
        LogPath = config["LogPath"] ?? "logs/archive-.txt",
        DownloadEnabled = bool.TryParse(config["DownloadEnabled"], out var downloadEnabled) ? downloadEnabled : true,
        DownloadBatchSize = int.TryParse(config["DownloadBatchSize"], out var downloadBatchSize) ? downloadBatchSize : 5,
        DownloadRetryCount = int.TryParse(config["DownloadRetryCount"], out var retryCount) ? retryCount : 3,
        DownloadRetryBaseDelayMs = int.TryParse(config["DownloadRetryBaseDelayMs"], out var retryDelayMs) ? retryDelayMs : 10000,
        DownloadCooldownMs = int.TryParse(config["DownloadCooldownMs"], out var cooldownMs) ? cooldownMs : 20000
    };

    Log.Logger = new LoggerConfiguration()
        .MinimumLevel.Information()
        .WriteTo.Console()
        .WriteTo.File(settings.LogPath, rollingInterval: RollingInterval.Day)
        .CreateLogger();

    Log.Information("=== Video Archive Manager Starter ===");

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

    await PhasePerformanceLogger.MeasureAsync(
        "Import channel videos",
        () => youtubeService.ImportChannelVideosAsync(settings.ChannelUrl));

    if (settings.MetadataEnrichmentBatchSize > 0)
    {
        var missingUploadedDateBefore = await dbService.CountEntriesMissingUploadedDateAsync();

        if (missingUploadedDateBefore > 0)
        {
            Log.Information(
                "Starting metadata enrichment for up to {Limit} entries missing UploadedDate (currently missing: {MissingBefore})...",
                settings.MetadataEnrichmentBatchSize,
                missingUploadedDateBefore);

            var enrichedCount = await PhasePerformanceLogger.MeasureAsync(
                "Metadata enrichment",
                () => youtubeService.EnrichMissingMetadataAsync(settings.MetadataEnrichmentBatchSize, cts.Token));
            var missingUploadedDateAfter = await dbService.CountEntriesMissingUploadedDateAsync();

            Log.Information(
                "Metadata enrichment complete: updated {Count} entries. Missing UploadedDate now: {MissingAfter}.",
                enrichedCount,
                missingUploadedDateAfter);
        }
        else
        {
            Log.Information("Skipping metadata enrichment: all entries already have UploadedDate.");
        }
    }

    // Recover any videos that were left in Downloading state from a previous crashed run.
    var stuckCount = await PhasePerformanceLogger.MeasureAsync(
        "Reset stuck downloads",
        () => dbService.ResetStuckDownloadsAsync());
    if (stuckCount > 0)
    {
        Log.Warning("Recovery: Reset {Count} video(s) stuck in Downloading state back to Unlinked.", stuckCount);
    }

    Log.Information("Starting scan and match of archive: {Path}", settings.ArchiveRootPath);

    var report = await PhasePerformanceLogger.MeasureAsync(
        "Scan and link archive",
        () => linker.LinkAsync(scanner, cts.Token));

    Log.Information("Checking linked entries for files missing on disk...");

    var brokenLinks = await PhasePerformanceLogger.MeasureAsync(
        "Verify linked files on disk",
        async () =>
        {
            var linkedEntries = dbService.StreamLinkedButMissingOnDisk();
            int missingLinkCount = 0;

            foreach (var entry in linkedEntries)
            {
                if (cts.Token.IsCancellationRequested) break;
                if (string.IsNullOrWhiteSpace(entry.LinkedFilePath)) continue;

                if (!fileSystem.FileExists(entry.LinkedFilePath))
                {
                    await dbService.UpdateLink(entry.YoutubeId, null, null, LinkStatus.Missing);
                    missingLinkCount++;
                }
            }

            return missingLinkCount;
        });

    if (brokenLinks > 0)
    {
        Log.Warning("Marked {Count} linked entries as Missing because files were not found on disk.", brokenLinks);
    }

    Log.Information("Checking if any missing videos need to be downloaded...");

    var totalMissingCount = await dbService.CountDownloadCandidatesAsync();
    var effectiveDownloadBatchSize = settings.DownloadEnabled ? settings.DownloadBatchSize : 0;
    var missingVideos = await dbService.GetDownloadCandidatesAsync(effectiveDownloadBatchSize);

    if (!settings.DownloadEnabled)
    {
        Log.Information("Download step is disabled by configuration (DownloadEnabled=false).");
    }

    await PhasePerformanceLogger.MeasureAsync(
        "Download missing videos",
        async () =>
        {
            if (missingVideos.Any())
            {
                Log.Information(
                    "There are {TotalMissing} videos missing locally. Starting download of {BatchCount} video(s) this run (max {ConfiguredBatch}).",
                    totalMissingCount,
                    missingVideos.Count,
                    effectiveDownloadBatchSize);

                foreach (var video in missingVideos)
                {
                    if (cts.Token.IsCancellationRequested) break;

                    int maxRetries = Math.Max(1, settings.DownloadRetryCount);
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

                                Log.Information("Waiting {Seconds} seconds before the next download...", settings.DownloadCooldownMs / 1000);
                                await Task.Delay(settings.DownloadCooldownMs, cts.Token);

                                break;
                            }

                            throw new Exception("Download returned false (yt-dlp failed)");
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
                                int backoffDelay = (int)(Math.Pow(2, attempt - 1) * settings.DownloadRetryBaseDelayMs);
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
        });

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