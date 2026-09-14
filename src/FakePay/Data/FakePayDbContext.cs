using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using ServiceDefaults;

namespace FakePay.Data;

public enum AuthorizationStatus
{
    Authorized,
    Voided,
    Captured
}

public sealed class Authorization
{
    public string Id { get; set; } = "";
    public decimal Amount { get; set; }
    public AuthorizationStatus Status { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset ExpiresUtc { get; set; }
    public string? CaptureId { get; set; }
    public int CaptureAttempts { get; set; }
}

// Stripe-style idempotency record: the first response for a key is stored and replayed
// for every retry with the same key and the same request. Same key + different request
// is a client bug and gets 409. Keys stop being honoured after 24 hours.
public sealed class IdempotencyRecord
{
    public string Key { get; set; } = "";
    public string Operation { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public int StatusCode { get; set; }
    public string ResponseJson { get; set; } = "";
    public string? AuthorizationId { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
}

public sealed class FakePayDbContext(DbContextOptions<FakePayDbContext> options) : DbContext(options)
{
    public DbSet<Authorization> Authorizations => Set<Authorization>();
    public DbSet<IdempotencyRecord> IdempotencyKeys => Set<IdempotencyRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("fakepay");

        modelBuilder.Entity<Authorization>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasMaxLength(64);
            b.Property(x => x.CaptureId).HasMaxLength(64);
            b.Property(x => x.Amount).HasPrecision(18, 2);
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
        });

        modelBuilder.Entity<IdempotencyRecord>(b =>
        {
            b.HasKey(x => new { x.Key, x.Operation });
            b.Property(x => x.Key).HasMaxLength(64);
            b.Property(x => x.Operation).HasMaxLength(16);
            b.Property(x => x.RequestHash).HasMaxLength(64);
            b.Property(x => x.AuthorizationId).HasMaxLength(64);
        });
    }
}

public sealed class FakePayDesignTimeFactory : IDesignTimeDbContextFactory<FakePayDbContext>
{
    public FakePayDbContext CreateDbContext(string[] args) => DesignTime.Create<FakePayDbContext>(o => new(o));
}
