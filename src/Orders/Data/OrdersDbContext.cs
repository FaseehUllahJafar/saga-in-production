using Microsoft.EntityFrameworkCore;
using Orders.Saga;

namespace Orders.Data;

public sealed class OrdersDbContext(DbContextOptions<OrdersDbContext> options) : DbContext(options)
{
    public DbSet<CheckoutSaga> Sagas => Set<CheckoutSaga>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("orders");

        // Wolverine persists the saga through this mapping, so the saga is an ordinary
        // table we own: every Part 5 column is queryable in plain SQL, no framework blob.
        modelBuilder.Entity<CheckoutSaga>(b =>
        {
            b.ToTable("CheckoutSagas");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            b.Property(x => x.CurrentStep).HasMaxLength(64);
            b.Property(x => x.CustomerEmail).HasMaxLength(256);
            b.Property(x => x.CardToken).HasMaxLength(64);
            b.Property(x => x.ShippingAddress).HasMaxLength(512);
            b.Property(x => x.FailureReason).HasMaxLength(2048);
            b.Property(x => x.AuthorizationId).HasMaxLength(64);
            b.Property(x => x.TrackingNumber).HasMaxLength(64);
            b.Property(x => x.CaptureId).HasMaxLength(64);
            b.Property(x => x.Amount).HasPrecision(18, 2);

            // Two messages for the same saga (a reply racing its own timeout) can be
            // handled at once on different threads or nodes. The rowversion makes the
            // second commit fail; the retry policy re-runs it against fresh state.
            b.Property(x => x.RowVersion).IsRowVersion();

            // The stuck-saga query's index (see SagaMonitor). Filtered to the in-flight
            // statuses, so it stays as small as today's traffic while the table keeps
            // every order ever placed. Covering, so the monitor never touches the table.
            b.HasIndex(x => x.LastUpdatedUtc, "IX_CheckoutSagas_Active")
                .HasFilter("[Status] IN ('InProgress', 'Compensating')")
                .IncludeProperties(x => new { x.Status, x.CurrentStep, x.AttemptCount });

            // Parked sagas waiting for a human. Rare, and the monitor counts them every
            // minute; without this that count is a scan of the whole table.
            b.HasIndex(x => x.Status, "IX_CheckoutSagas_NeedsAttention")
                .HasFilter("[Status] IN ('CompensationFailed', 'NeedsManualReview')");

            b.OwnsMany(x => x.Journal, j =>
            {
                j.ToJson();
                j.Property(e => e.Outcome).HasConversion<string>();
            });
            b.OwnsMany(x => x.Lines, l =>
            {
                l.ToJson();
                l.Property(x => x.UnitPrice).HasPrecision(18, 2);
            });
        });
    }
}

public sealed class OrdersDesignTimeFactory : Microsoft.EntityFrameworkCore.Design.IDesignTimeDbContextFactory<OrdersDbContext>
{
    public OrdersDbContext CreateDbContext(string[] args) => ServiceDefaults.DesignTime.Create<OrdersDbContext>(o => new(o));
}
