using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using ServiceDefaults;

namespace Notifications.Data;

public sealed class SentNotification
{
    public Guid OrderId { get; set; }
    public string Kind { get; set; } = "";
    public DateTimeOffset SentUtc { get; set; }
}

public sealed class NotificationsDbContext(DbContextOptions<NotificationsDbContext> options) : DbContext(options)
{
    public DbSet<SentNotification> Sent => Set<SentNotification>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("notifications");
        modelBuilder.Entity<SentNotification>(b =>
        {
            b.HasKey(x => new { x.OrderId, x.Kind });
            b.Property(x => x.Kind).HasMaxLength(32);
        });
    }
}

public sealed class NotificationsDesignTimeFactory : IDesignTimeDbContextFactory<NotificationsDbContext>
{
    public NotificationsDbContext CreateDbContext(string[] args) => DesignTime.Create<NotificationsDbContext>(o => new(o));
}
