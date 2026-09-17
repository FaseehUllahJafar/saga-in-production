using FakePay.Data;
using Microsoft.EntityFrameworkCore;

namespace FakePay;

public sealed class FakePayOptions
{
    // How long a tok_slow authorize waits after committing, before answering.
    public TimeSpan SlowResponseDelay { get; set; } = TimeSpan.FromSeconds(20);
}

public static class FakePaySetup
{
    public const string DatabaseName = "fakepay-db";

    public static WebApplicationBuilder AddFakePay(this WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton(builder.Configuration.GetSection("FakePay").Get<FakePayOptions>() ?? new FakePayOptions());
        builder.Services.AddDbContext<FakePayDbContext>(o => o.UseSqlServer(builder.Configuration.GetConnectionString(DatabaseName)));
        return builder;
    }
}
