using Inventory;
using Inventory.Data;
using ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.AddInventoryService();

var app = builder.Build();
await app.MigrateDatabaseAsync<InventoryDbContext>();

app.MapDefaultEndpoints();
app.Run();
