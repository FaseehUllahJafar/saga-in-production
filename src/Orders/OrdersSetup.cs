using Contracts;
using Microsoft.EntityFrameworkCore;
using Orders.Data;
using Orders.Saga;
using ServiceDefaults;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.RabbitMQ;

namespace Orders;

public static class OrdersSetup
{
    public const string DatabaseName = "orders-db";

    public static IHostApplicationBuilder AddOrdersService(this IHostApplicationBuilder builder, Action<SagaTimings>? configureTimings = null)
    {
        var sql = builder.Configuration.GetConnectionString(DatabaseName)
            ?? throw new InvalidOperationException($"Connection string '{DatabaseName}' is missing");
        var rabbit = builder.Configuration.GetConnectionString("messaging")
            ?? throw new InvalidOperationException("Connection string 'messaging' is missing");

        var timings = builder.Configuration.GetSection("Saga:Timings").Get<SagaTimings>() ?? new SagaTimings();
        configureTimings?.Invoke(timings);

        builder.Services.AddSingleton(timings);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<SagaMetrics>();
        builder.Services.AddSingleton<SagaRuntime>();
        builder.Services.AddDbContextWithWolverineIntegration<OrdersDbContext>(o => o.UseSqlServer(sql));

        builder.UseWolverine(opts =>
        {
            SagaMessaging.ApplyConventions(opts, "orders", sql, rabbit);
            // Handler discovery from this assembly, not the entry assembly: under test every
            // service runs in one process whose entry assembly is the test project.
            opts.ApplicationAssembly = typeof(OrdersSetup).Assembly;

            opts.ListenToRabbitQueue(Queues.Orders);

            opts.PublishMessage<AuthorizePayment>().ToRabbitQueue(Queues.Payments);
            opts.PublishMessage<VoidPayment>().ToRabbitQueue(Queues.Payments);
            opts.PublishMessage<CapturePayment>().ToRabbitQueue(Queues.Payments);
            opts.PublishMessage<ReserveStock>().ToRabbitQueue(Queues.Inventory);
            opts.PublishMessage<ReleaseStock>().ToRabbitQueue(Queues.Inventory);
            opts.PublishMessage<BookShipment>().ToRabbitQueue(Queues.Shipping);
            opts.PublishMessage<CancelShipment>().ToRabbitQueue(Queues.Shipping);

            // CheckStepStatus is addressed per step with ToEndpoint(), so every
            // participant queue must exist as a named sending endpoint here.
            opts.PublishMessage<CheckStepStatus>().ToRabbitQueue(Queues.Payments);
            // Integration events leave the saga through a fanout exchange. Whoever cares
            // binds a queue to it; the saga neither knows nor waits.
            opts.PublishMessage<OrderCompleted>().ToRabbitExchange(Queues.OrderEvents);
            opts.PublishMessage<OrderCancelled>().ToRabbitExchange(Queues.OrderEvents);
        });

        return builder;
    }
}
