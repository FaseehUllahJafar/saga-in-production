using Payments;
using Payments.Data;
using ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.AddPaymentsService();

var app = builder.Build();
await app.MigrateDatabaseAsync<PaymentsDbContext>();

app.MapDefaultEndpoints();
app.Run();
