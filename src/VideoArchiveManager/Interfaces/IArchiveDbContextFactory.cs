using VideoArchiveManager.Data;

namespace VideoArchiveManager.Interfaces;

public interface IArchiveDbContextFactory
{
    ArchiveDbContext CreateDbContext();
}
