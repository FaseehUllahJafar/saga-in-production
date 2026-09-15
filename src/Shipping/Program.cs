using Shipping;
using Shipping.Data;
using ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.AddShippingService();

var app = builder.Build();
await app.MigrateDatabaseAsync<ShippingDbContext>();

app.MapDefaultEndpoints();
app.Run();
