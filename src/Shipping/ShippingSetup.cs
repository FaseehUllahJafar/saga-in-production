using Contracts;
using Microsoft.EntityFrameworkCore;
using ServiceDefaults;
using Shipping.Data;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.RabbitMQ;

namespace Shipping;

public static class ShippingSetup
{
    public const string DatabaseName = "shipping-db";

    public static IHostApplicationBuilder AddShippingService(this IHostApplicationBuilder builder)
    {
        var sql = builder.Configuration.GetConnectionString(DatabaseName)
            ?? throw new InvalidOperationException($"Connection string '{DatabaseName}' is missing");
        var rabbit = builder.Configuration.GetConnectionString("messaging")
            ?? throw new InvalidOperationException("Connection string 'messaging' is missing");

        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton(builder.Configuration.GetSection("Carrier").Get<CarrierOptions>() ?? new CarrierOptions());
        builder.Services.AddSingleton<CarrierClient>();
        builder.Services.AddDbContextWithWolverineIntegration<ShippingDbContext>(o => o.UseSqlServer(sql));

        builder.UseWolverine(opts =>
        {
            SagaMessaging.ApplyConventions(opts, "shipping", sql, rabbit);
            // Handler discovery from this assembly, not the entry assembly: under test every
            // service runs in one process whose entry assembly is the test project.
            opts.ApplicationAssembly = typeof(ShippingSetup).Assembly;

            opts.ListenToRabbitQueue(Queues.Shipping);
            opts.PublishAllMessages().ToRabbitQueue(Queues.Orders);
        });

        return builder;
    }
}
