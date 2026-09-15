var builder = DistributedApplication.CreateBuilder(args);

// One SQL Server container, one database per service. Each service owns its schema and
// its Wolverine envelope tables; nobody reads anybody else's database. In production
// these would be separate instances (see Limits in the README).
// Session lifetime and no data volume on purpose: every `aspire run` starts from empty
// databases and the services migrate them on startup, so a fresh clone behaves exactly
// like the tenth run.
var sql = builder.AddSqlServer("sql");

var ordersDb = sql.AddDatabase("orders-db");
var paymentsDb = sql.AddDatabase("payments-db");
var inventoryDb = sql.AddDatabase("inventory-db");
var shippingDb = sql.AddDatabase("shipping-db");
var notificationsDb = sql.AddDatabase("notifications-db");
var fakepayDb = sql.AddDatabase("fakepay-db");

var messaging = builder.AddRabbitMQ("messaging")
    .WithImageTag("4-management")
    .WithManagementPlugin();

var fakepay = builder.AddProject<Projects.FakePay>("fakepay")
    .WithReference(fakepayDb).WaitFor(fakepayDb);

builder.AddProject<Projects.Payments>("payments")
    .WithReference(paymentsDb).WaitFor(paymentsDb)
    .WithReference(messaging).WaitFor(messaging)
    .WithReference(fakepay).WaitFor(fakepay);

builder.AddProject<Projects.Inventory>("inventory")
    .WithReference(inventoryDb).WaitFor(inventoryDb)
    .WithReference(messaging).WaitFor(messaging);

builder.AddProject<Projects.Shipping>("shipping")
    .WithReference(shippingDb).WaitFor(shippingDb)
    .WithReference(messaging).WaitFor(messaging);

builder.AddProject<Projects.Notifications>("notifications")
    .WithReference(notificationsDb).WaitFor(notificationsDb)
    .WithReference(messaging).WaitFor(messaging);

builder.AddProject<Projects.Orders>("orders")
    .WithReference(ordersDb).WaitFor(ordersDb)
    .WithReference(messaging).WaitFor(messaging)
    .WithExternalHttpEndpoints();

builder.Build().Run();
