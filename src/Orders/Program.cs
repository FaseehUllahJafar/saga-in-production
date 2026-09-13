using Orders;
using Orders.Api;
using Orders.Data;
using Orders.Saga;
using ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults(SagaMetrics.MeterName);
builder.AddOrdersService();

var app = builder.Build();
await app.MigrateDatabaseAsync<OrdersDbContext>();

app.MapDefaultEndpoints();
app.MapOrdersApi();
app.Run();
