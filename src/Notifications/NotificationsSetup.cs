using Contracts;
using Microsoft.EntityFrameworkCore;
using Notifications.Data;
using ServiceDefaults;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.RabbitMQ;

namespace Notifications;

public static class NotificationsSetup
{
    public const string DatabaseName = "notifications-db";

    public static IHostApplicationBuilder AddNotificationsService(this IHostApplicationBuilder builder)
    {
        var sql = builder.Configuration.GetConnectionString(DatabaseName)
            ?? throw new InvalidOperationException($"Connection string '{DatabaseName}' is missing");
        var rabbit = builder.Configuration.GetConnectionString("messaging")
            ?? throw new InvalidOperationException("Connection string 'messaging' is missing");

        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton(builder.Configuration.GetSection("Email").Get<EmailOptions>() ?? new EmailOptions());
        builder.Services.AddSingleton<EmailSender>();
        builder.Services.AddSingleton<NotificationMetrics>();
        builder.Services.AddDbContextWithWolverineIntegration<NotificationsDbContext>(o => o.UseSqlServer(sql));

        builder.UseWolverine(opts =>
        {
            SagaMessaging.ApplyConventions(opts, "notifications", sql, rabbit);
            // Handler discovery from this assembly, not the entry assembly: under test every
            // service runs in one process whose entry assembly is the test project.
            opts.ApplicationAssembly = typeof(NotificationsSetup).Assembly;
            opts.ListenToRabbitQueue(Queues.Notifications, q => q.BindExchange(Queues.OrderEvents));
        });

        return builder;
    }
}
