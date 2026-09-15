using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using ServiceDefaults;

namespace Shipping.Data;

public enum ShipmentStatus
{
    // Written before calling the carrier; see Payments for the same pattern.
    Pending,
    Booked,
    Rejected,
    // Tombstone: cancel arrived before (or while) the booking ran.
    Cancelled
}

public sealed class Shipment
{
    public Guid SagaId { get; set; }
    public Guid CommandId { get; set; }
    public ShipmentStatus Status { get; set; }
    public string? TrackingNumber { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public sealed class ShippingDbContext(DbContextOptions<ShippingDbContext> options) : DbContext(options)
{
    public DbSet<Shipment> Shipments => Set<Shipment>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("shipping");
        modelBuilder.Entity<Shipment>(b =>
        {
            // One shipment per saga: the business idempotency key is the SagaId itself.
            b.HasKey(x => x.SagaId);
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            b.Property(x => x.TrackingNumber).HasMaxLength(64);
            b.Property(x => x.Reason).HasMaxLength(512);
            b.Property(x => x.RowVersion).IsRowVersion();
        });
    }
}

public sealed class ShippingDesignTimeFactory : IDesignTimeDbContextFactory<ShippingDbContext>
{
    public ShippingDbContext CreateDbContext(string[] args) => DesignTime.Create<ShippingDbContext>(o => new(o));
}
