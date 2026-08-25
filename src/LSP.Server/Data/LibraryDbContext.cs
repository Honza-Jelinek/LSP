using Microsoft.EntityFrameworkCore;

namespace LSP.Server.Data;

public sealed class LibraryDbContext(DbContextOptions<LibraryDbContext> options) : DbContext(options)
{
    public DbSet<Setting> Settings => Set<Setting>();
    public DbSet<LibraryFolder> LibraryFolders => Set<LibraryFolder>();
    public DbSet<MediaFile> MediaFiles => Set<MediaFile>();
    public DbSet<Movie> Movies => Set<Movie>();
    public DbSet<Show> Shows => Set<Show>();
    public DbSet<Episode> Episodes => Set<Episode>();
    public DbSet<PlaybackProgress> PlaybackProgress => Set<PlaybackProgress>();
    public DbSet<ParseCache> ParseCaches => Set<ParseCache>();
    public DbSet<TmdbCache> TmdbCaches => Set<TmdbCache>();
    public DbSet<ManualMatch> ManualMatches => Set<ManualMatch>();
    public DbSet<MatchAlias> MatchAliases => Set<MatchAlias>();
    public DbSet<TmdbSeasonCache> TmdbSeasonCaches => Set<TmdbSeasonCache>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Setting>(e =>
        {
            e.HasKey(x => x.Key);
        });

        b.Entity<ParseCache>(e =>
        {
            e.HasIndex(x => x.Path).IsUnique();
        });

        b.Entity<TmdbCache>(e =>
        {
            e.HasIndex(x => x.QueryKey).IsUnique();
        });

        b.Entity<TmdbSeasonCache>(e =>
        {
            e.HasIndex(x => new { x.TmdbId, x.Season }).IsUnique();
        });

        b.Entity<MatchAlias>(e =>
        {
            e.HasIndex(x => x.Key).IsUnique();
        });

        b.Entity<ManualMatch>(e =>
        {
            e.HasIndex(x => x.Key).IsUnique();
        });

        b.Entity<LibraryFolder>(e =>
        {
            e.HasIndex(x => x.Path).IsUnique();
        });

        b.Entity<MediaFile>(e =>
        {
            e.HasIndex(x => x.Path).IsUnique();
            e.HasOne(x => x.Movie).WithOne(m => m.MediaFile)
                .HasForeignKey<Movie>(m => m.MediaFileId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Episode).WithOne(ep => ep.MediaFile)
                .HasForeignKey<Episode>(ep => ep.MediaFileId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Show>(e =>
        {
            e.HasIndex(x => x.Title).IsUnique();
        });

        b.Entity<Episode>(e =>
        {
            e.HasIndex(x => new { x.ShowId, x.Season, x.Number });
        });

        b.Entity<PlaybackProgress>(e =>
        {
            e.HasIndex(x => x.Path).IsUnique();
            e.HasIndex(x => x.UpdatedAt);
        });
    }
}
