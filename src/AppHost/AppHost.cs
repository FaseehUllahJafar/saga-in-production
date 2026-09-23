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

var payments = builder.AddProject<Projects.Payments>("payments")
    .WithReference(paymentsDb).WaitFor(paymentsDb)
    .WithReference(messaging).WaitFor(messaging)
    .WithReference(fakepay).WaitFor(fakepay);

var inventory = builder.AddProject<Projects.Inventory>("inventory")
    .WithReference(inventoryDb).WaitFor(inventoryDb)
    .WithReference(messaging).WaitFor(messaging);

var shipping = builder.AddProject<Projects.Shipping>("shipping")
    .WithReference(shippingDb).WaitFor(shippingDb)
    .WithReference(messaging).WaitFor(messaging);

var notifications = builder.AddProject<Projects.Notifications>("notifications")
    .WithReference(notificationsDb).WaitFor(notificationsDb)
    .WithReference(messaging).WaitFor(messaging);

var orders = builder.AddProject<Projects.Orders>("orders")
    .WithReference(ordersDb).WaitFor(ordersDb)
    .WithReference(messaging).WaitFor(messaging)
    .WithExternalHttpEndpoints();

// `aspire run -- --monitoring`: Prometheus, Alertmanager and Grafana alongside the
// dashboard. The Aspire dashboard is for "what is happening to this one saga"; this is
// for "is anything wrong, and who needs to know". See monitoring/ and docs/runbook.md.
if (args.Contains("--monitoring"))
{
    // Explicit rather than trusting the image's defaults: the scrape config depends on it.
    messaging.WithBindMount("../../monitoring/rabbitmq/enabled_plugins", "/etc/rabbitmq/enabled_plugins", isReadOnly: true);

    var alertLog = builder.AddContainer("alert-log", "mendhak/http-https-echo", "42")
        .WithEnvironment("HTTP_PORT", "8080")
        .WithEnvironment("LOG_WITHOUT_NEWLINE", "true")
        .WithHttpEndpoint(targetPort: 8080);

    var alertmanager = builder.AddContainer("alertmanager", "prom/alertmanager", "v0.34.1")
        .WithBindMount("../../monitoring/alertmanager.yml", "/etc/alertmanager/alertmanager.yml", isReadOnly: true)
        .WithHttpEndpoint(targetPort: 9093)
        .WaitFor(alertLog);

    var prometheus = builder.AddContainer("prometheus", "prom/prometheus", "v3.13.3")
        .WithBindMount("../../monitoring/prometheus.yml", "/etc/prometheus/prometheus.yml", isReadOnly: true)
        .WithBindMount("../../monitoring/alert-rules.yml", "/etc/prometheus/alert-rules.yml", isReadOnly: true)
        .WithArgs("--config.file=/etc/prometheus/prometheus.yml", "--web.enable-otlp-receiver")
        .WithHttpEndpoint(targetPort: 9090)
        .WaitFor(alertmanager)
        .WaitFor(messaging);

    builder.AddContainer("grafana", "grafana/grafana-oss", "13.0.2")
        .WithBindMount("../../monitoring/grafana", "/etc/grafana/provisioning", isReadOnly: true)
        .WithEnvironment("GF_AUTH_ANONYMOUS_ENABLED", "true")
        .WithEnvironment("GF_AUTH_ANONYMOUS_ORG_ROLE", "Admin")
        .WithEnvironment("GF_AUTH_DISABLE_LOGIN_FORM", "true")
        .WithHttpEndpoint(targetPort: 3000)
        .WaitFor(prometheus);

    var otlpMetrics = ReferenceExpression.Create($"{prometheus.GetEndpoint("http")}/api/v1/otlp/v1/metrics");
    foreach (var service in new[] { orders, payments, inventory, shipping, notifications })
    {
        service.WithEnvironment("Monitoring__PrometheusOtlpEndpoint", otlpMetrics).WaitFor(prometheus);
    }
}

builder.Build().Run();
