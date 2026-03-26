using Microsoft.EntityFrameworkCore;
using NetworkGuard.Models;

namespace NetworkGuard.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<DnsQueryLog> DnsQueryLogs => Set<DnsQueryLog>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<Alert> Alerts => Set<Alert>();
    public DbSet<IgnoredDomain> IgnoredDomains => Set<IgnoredDomain>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DnsQueryLog>(entity =>
        {
            entity.HasIndex(q => q.Timestamp);
            entity.HasIndex(q => q.IsFlagged);
            entity.HasIndex(q => q.SourceIp);
        });

        modelBuilder.Entity<Device>(entity =>
        {
            entity.HasIndex(d => d.MacAddress).IsUnique();
        });

        modelBuilder.Entity<Alert>(entity =>
        {
            entity.HasIndex(a => a.IsRead);
            entity.HasIndex(a => a.Timestamp);
        });

        modelBuilder.Entity<IgnoredDomain>(entity =>
        {
            entity.HasIndex(i => new { i.MacAddress, i.Domain }).IsUnique();
        });
    }
}
