using FakePay;
using FakePay.Data;
using ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.AddFakePay();

var app = builder.Build();
await app.MigrateDatabaseAsync<FakePayDbContext>();

app.MapDefaultEndpoints();
app.MapFakePayApi();
app.Run();

public partial class Program;
