namespace VideoArchiveManager.Interfaces;

public interface IDownloadRetryPolicy
{
    Task<bool> ExecuteAsync(Func<int, CancellationToken, Task<bool>> operation, CancellationToken cancellationToken = default);
}
