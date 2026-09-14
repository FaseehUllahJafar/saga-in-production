using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using ServiceDefaults;

namespace Payments.Data;

public enum PaymentStepStatus
{
    // Row written BEFORE calling FakePay. If the process dies or the response is lost,
    // this is what's left: "we may have authorized, ask FakePay by idempotency key".
    Pending,
    Authorized,
    Declined,
    // Tombstone: the saga cancelled this step before the forward command ran, or we
    // checked with FakePay and nothing was there. A late authorize is rejected.
    Cancelled,
    // Cancel requested while the authorize outcome was unknown; still a tombstone, but
    // FakePay has not yet confirmed there is nothing to void. Retries resume from here.
    Voiding,
    Voided,
    Captured,
    CaptureFailed
}

// The business idempotency record: one row per (SagaId, Step). This is the second of the
// two idempotency layers (ADR 0002). Wolverine's inbox dedupes the transport MessageId;
// this table dedupes the CommandId, which survives retries that get a new MessageId.
public sealed class PaymentStep
{
    public Guid SagaId { get; set; }
    public string Step { get; set; } = "";
    public Guid CommandId { get; set; }
    public PaymentStepStatus Status { get; set; }
    public string? ProviderRef { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public sealed class PaymentsDbContext(DbContextOptions<PaymentsDbContext> options) : DbContext(options)
{
    public DbSet<PaymentStep> Steps => Set<PaymentStep>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("payments");
        modelBuilder.Entity<PaymentStep>(b =>
        {
            b.HasKey(x => new { x.SagaId, x.Step });
            b.Property(x => x.Step).HasMaxLength(64);
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            b.Property(x => x.ProviderRef).HasMaxLength(64);
            b.Property(x => x.Reason).HasMaxLength(512);
            // The Pending -> Authorized write and the Pending -> Cancelled tombstone race
            // each other. The rowversion makes exactly one of them win.
            b.Property(x => x.RowVersion).IsRowVersion();
        });
    }
}

public sealed class PaymentsDesignTimeFactory : IDesignTimeDbContextFactory<PaymentsDbContext>
{
    public PaymentsDbContext CreateDbContext(string[] args) => DesignTime.Create<PaymentsDbContext>(o => new(o));
}
