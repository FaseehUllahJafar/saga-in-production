using Contracts;
using Microsoft.EntityFrameworkCore;
using Payments.Data;
using ServiceDefaults;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.RabbitMQ;

namespace Payments;

public static class PaymentsSetup
{
    public const string DatabaseName = "payments-db";

    public static IHostApplicationBuilder AddPaymentsService(this IHostApplicationBuilder builder)
    {
        var sql = builder.Configuration.GetConnectionString(DatabaseName)
            ?? throw new InvalidOperationException($"Connection string '{DatabaseName}' is missing");
        var rabbit = builder.Configuration.GetConnectionString("messaging")
            ?? throw new InvalidOperationException("Connection string 'messaging' is missing");

        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddDbContextWithWolverineIntegration<PaymentsDbContext>(o => o.UseSqlServer(sql));
        var fakePaySettings = new FakePaySettings
        {
            Timeout = TimeSpan.FromSeconds(builder.Configuration.GetValue("FakePay:TimeoutSeconds", 5.0)),
            SettleWindow = TimeSpan.FromSeconds(builder.Configuration.GetValue("FakePay:SettleWindowSeconds", 30.0)),
        };
        builder.Services.AddSingleton(fakePaySettings);
        builder.Services.AddSingleton<FakePayClient>();
        builder.Services.AddHttpClient(FakePayClient.HttpClientName, http =>
        {
            http.BaseAddress = new Uri(builder.Configuration["FakePay:BaseUrl"] ?? "https+http://fakepay");
            http.Timeout = fakePaySettings.Timeout;
        });

        builder.UseWolverine(opts =>
        {
            // Lightweight: the Pending row must commit before FakePay is called, and no
            // DB transaction may stay open across an HTTP call.
            SagaMessaging.ApplyConventions(opts, "payments", sql, rabbit);
            // Handler discovery from this assembly, not the entry assembly: under test every
            // service runs in one process whose entry assembly is the test project.
            opts.ApplicationAssembly = typeof(PaymentsSetup).Assembly;

            opts.ListenToRabbitQueue(Queues.Payments);
            opts.PublishAllMessages().ToRabbitQueue(Queues.Orders);
        });

        return builder;
    }
}
