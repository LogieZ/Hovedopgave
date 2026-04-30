using Microsoft.EntityFrameworkCore;
using VideoArchiveManager.Models;

namespace VideoArchiveManager.Data;

public sealed class ArchiveDbContext : DbContext
{
    private readonly string _connectionString;

    public ArchiveDbContext(string connectionString)
    {
        _connectionString = connectionString;
    }

    public DbSet<VideoEntry> VideoEntries { get; set; } = null!;

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.UseNpgsql(_connectionString);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<VideoEntry>()
            .HasIndex(e => e.YoutubeId)
            .IsUnique();
        
        modelBuilder.Entity<VideoEntry>()
            .HasIndex(e => e.Status);
    }
}