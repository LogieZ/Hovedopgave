using VideoArchiveManager.Data;
using VideoArchiveManager.Interfaces;

namespace VideoArchiveManager.Services;

public sealed class ArchiveDbContextFactory : IArchiveDbContextFactory
{
    private readonly IAppSettings _settings;

    public ArchiveDbContextFactory(IAppSettings settings)
    {
        _settings = settings;
    }

    public ArchiveDbContext CreateDbContext()
    {
        return new ArchiveDbContext(_settings.ConnectionString);
    }
}
