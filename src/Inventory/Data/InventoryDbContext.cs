using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using ServiceDefaults;

namespace Inventory.Data;

public sealed class StockItem
{
    public string Sku { get; set; } = "";
    public int Available { get; set; }
}

public sealed class Reservation
{
    public Guid SagaId { get; set; }
    public Guid OrderLineId { get; set; }
    public string Sku { get; set; } = "";
    public int Quantity { get; set; }
}

public enum InventoryStepStatus
{
    Reserved,
    Rejected,
    // Tombstone: release arrived before reserve.
    Cancelled,
    Released
}

public sealed class InventoryStep
{
    public Guid SagaId { get; set; }
    public string Step { get; set; } = "";
    public InventoryStepStatus Status { get; set; }
    public string? RejectedSku { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
}

public sealed class InventoryDbContext(DbContextOptions<InventoryDbContext> options) : DbContext(options)
{
    public DbSet<StockItem> Stock => Set<StockItem>();
    public DbSet<Reservation> Reservations => Set<Reservation>();
    public DbSet<InventoryStep> Steps => Set<InventoryStep>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("inventory");

        modelBuilder.Entity<StockItem>(b =>
        {
            b.HasKey(x => x.Sku);
            b.Property(x => x.Sku).HasMaxLength(64);
            b.ToTable(t => t.HasCheckConstraint("CK_Stock_Available_NonNegative", "[Available] >= 0"));
            b.HasData(
                new StockItem { Sku = "BOOK-DDD", Available = 1000 },
                new StockItem { Sku = "BOOK-EIP", Available = 1000 },
                new StockItem { Sku = "MUG-SAGA", Available = 50 },
                new StockItem { Sku = "LIMITED-01", Available = 1 });
        });

        // One reservation per order line, enforced by the database: a duplicate reserve
        // can't hold the same line twice even if every other guard failed.
        modelBuilder.Entity<Reservation>(b =>
        {
            b.HasKey(x => new { x.SagaId, x.OrderLineId });
            b.Property(x => x.Sku).HasMaxLength(64);
        });

        modelBuilder.Entity<InventoryStep>(b =>
        {
            b.HasKey(x => new { x.SagaId, x.Step });
            b.Property(x => x.Step).HasMaxLength(64);
            b.Property(x => x.RejectedSku).HasMaxLength(64);
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
        });
    }
}

public sealed class InventoryDesignTimeFactory : IDesignTimeDbContextFactory<InventoryDbContext>
{
    public InventoryDbContext CreateDbContext(string[] args) => DesignTime.Create<InventoryDbContext>(o => new(o));
}
