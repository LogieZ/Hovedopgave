using Serilog;
using VideoArchiveManager.Interfaces;

namespace VideoArchiveManager.Services;

public sealed class LinearBackoffDownloadRetryPolicy : IDownloadRetryPolicy
{
    private readonly IAppSettings _settings;

    public LinearBackoffDownloadRetryPolicy(IAppSettings settings)
    {
        _settings = settings;
    }

    public async Task<bool> ExecuteAsync(Func<int, CancellationToken, Task<bool>> operation, CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; attempt <= _settings.MaxDownloadRetries; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var succeeded = await operation(attempt, cancellationToken).ConfigureAwait(false);
                if (succeeded)
                {
                    return true;
                }
            }
            catch (Exception ex) when (attempt < _settings.MaxDownloadRetries)
            {
                Log.Warning(ex, "Attempt {Attempt} failed. Preparing retry.", attempt);
            }
            catch
            {
                throw;
            }

            if (attempt < _settings.MaxDownloadRetries)
            {
                var backoffDelay = TimeSpan.FromSeconds(_settings.RetryBaseDelaySeconds * attempt);
                Log.Warning("Waiting {Seconds} seconds before retry attempt {Attempt}.", backoffDelay.TotalSeconds, attempt + 1);
                await Task.Delay(backoffDelay, cancellationToken).ConfigureAwait(false);
            }
        }

        return false;
    }
}
