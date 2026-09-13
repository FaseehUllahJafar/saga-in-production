using Microsoft.EntityFrameworkCore;

namespace ServiceDefaults;

// Lets `dotnet ef migrations add` build a context without starting the service.
public static class DesignTime
{
    public static TContext Create<TContext>(Func<DbContextOptions<TContext>, TContext> create) where TContext : DbContext =>
        create(new DbContextOptionsBuilder<TContext>()
            .UseSqlServer("Server=design-time-only;Database=unused;Trusted_Connection=True")
            .Options);
}
