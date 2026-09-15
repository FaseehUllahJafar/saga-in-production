using Notifications;
using Notifications.Data;
using ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults(NotificationMetrics.MeterName);
builder.AddNotificationsService();

var app = builder.Build();
await app.MigrateDatabaseAsync<NotificationsDbContext>();

app.MapDefaultEndpoints();
app.Run();
