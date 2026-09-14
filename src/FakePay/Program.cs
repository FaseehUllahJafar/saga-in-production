using FakePay;
using FakePay.Data;
using Microsoft.EntityFrameworkCore;
using ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddDbContext<FakePayDbContext>(o => o.UseSqlServer(builder.Configuration.GetConnectionString("fakepay-db")));

var app = builder.Build();
await app.MigrateDatabaseAsync<FakePayDbContext>();

app.MapDefaultEndpoints();
app.MapFakePayApi();
app.Run();

public partial class Program;
