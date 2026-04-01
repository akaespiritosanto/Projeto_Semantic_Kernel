using Microsoft.EntityFrameworkCore;
using semantic_kernel.Models;

namespace semantic_kernel.Services;

public sealed class LocationsDbContext : DbContext
{
    public LocationsDbContext(DbContextOptions<LocationsDbContext> options) : base(options) { }

    public DbSet<Location> Locations => Set<Location>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Location>(entity =>
        {
            entity.ToTable("Locations");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).IsRequired();
            entity.Property(x => x.Weather).IsRequired();
            entity.Property(x => x.Temperature);
        });
    }
}
