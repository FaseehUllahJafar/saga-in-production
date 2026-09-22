using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Networks;
using FakePay;
using FakePay.Data;
using Inventory;
using Inventory.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Notifications;
using Notifications.Data;
using Orders;
using Orders.Data;
using Payments;
using Payments.Data;
using Shipping;
using Shipping.Data;
using Testcontainers.MsSql;
using Testcontainers.RabbitMq;
using Testcontainers.Toxiproxy;
using Wolverine;

[assembly: AssemblyFixture(typeof(Saga.IntegrationTests.Infrastructure.SagaCluster))]
[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]

namespace Saga.IntegrationTests.Infrastructure;

public enum Service
{
    Orders,
    Payments,
    Inventory,
    Shipping,
    Notifications
}

// One real cluster for the whole run: SQL Server, RabbitMQ and Toxiproxy in containers,
// FakePay on Kestrel, and every service as an in-process host built from the SAME
// Add*Service() extension its Program.cs uses. Only connection strings and timings
// differ from production.
public sealed class SagaCluster : IAsyncLifetime
{
    public const string FakePayProxy = "fakepay";
    public const string RabbitProxy = "rabbit";
    private const int FakePayProxyPort = 8666;
    private const int RabbitProxyPort = 8667;

    // Short enough to keep the suite fast, long enough that a healthy participant on a
    // loaded CI runner answers well inside one attempt.
    public static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan ProviderTimeout = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan CompensationTimeout = TimeSpan.FromSeconds(4);

    private readonly INetwork _network = new NetworkBuilder().Build();
    private readonly MsSqlContainer _sql;
    private readonly RabbitMqContainer _rabbit;
    private readonly ToxiproxyContainer _toxiproxy;
    private readonly int _fakePayPort = FreePort();
    private readonly Dictionary<Service, IHost> _hosts = [];
    private WebApplication? _fakePay;

    public SagaCluster()
    {
        _sql = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest")
            .WithNetwork(_network)
            .Build();
        _rabbit = new RabbitMqBuilder("rabbitmq:4-management")
            .WithNetwork(_network)
            .WithNetworkAliases("rabbitmq")
            .WithUsername("guest")
            .WithPassword("guest")
            .WithPortBinding(15672, true)
            // Queue depths in the management API are only as fresh as the stats
            // interval (5 s by default). Quiesce reads them, so a message sitting in a
            // queue could look like an empty queue for seconds.
            .WithEnvironment("RABBITMQ_SERVER_ADDITIONAL_ERL_ARGS", "-rabbit collect_statistics_interval 100")
            .Build();
        _toxiproxy = new ToxiproxyBuilder("ghcr.io/shopify/toxiproxy:2.12.0")
            .WithNetwork(_network)
            // How Toxiproxy, inside Docker, reaches FakePay on this machine. host-gateway
            // works on Docker Desktop and on Linux CI runners alike.
            .WithExtraHost("host.docker.internal", "host-gateway")
            .WithPortBinding(FakePayProxyPort, true)
            .WithPortBinding(RabbitProxyPort, true)
            .Build();
    }

    public Toxiproxy Toxiproxy { get; private set; } = null!;
    public string SqlConnectionString(string database) =>
        new SqlConnectionStringBuilder(_sql.GetConnectionString()) { InitialCatalog = database }.ConnectionString;

    public IHost Host(Service service) => _hosts[service];

    public async ValueTask InitializeAsync()
    {
        await _network.CreateAsync();
        await Task.WhenAll(_sql.StartAsync(), _rabbit.StartAsync(), _toxiproxy.StartAsync());

        Toxiproxy = new Toxiproxy(new HttpClient
        {
            BaseAddress = new Uri($"http://{_toxiproxy.Hostname}:{_toxiproxy.GetMappedPublicPort(8474)}")
        });
        await Toxiproxy.CreateProxy(FakePayProxy, $"0.0.0.0:{FakePayProxyPort}", $"host.docker.internal:{_fakePayPort}");
        await Toxiproxy.CreateProxy(RabbitProxy, $"0.0.0.0:{RabbitProxyPort}", "rabbitmq:5672");

        await CreateAndMigrateDatabases();
        await StartFakePay();
        foreach (var service in Enum.GetValues<Service>())
        {
            await Start(service);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var host in _hosts.Values)
        {
            await host.StopAsync();
            host.Dispose();
        }
        if (_fakePay is not null) await _fakePay.DisposeAsync();
        await Task.WhenAll(_toxiproxy.DisposeAsync().AsTask(), _rabbit.DisposeAsync().AsTask(), _sql.DisposeAsync().AsTask());
        await _network.DisposeAsync();
    }

    private IHost? _secondOrdersNode;

    // A second Orders node on the same database and queues, as in a scaled-out deploy.
    public async Task<IHost> StartSecondOrdersNode()
    {
        _secondOrdersNode ??= await Build(Service.Orders);
        return _secondOrdersNode;
    }

    public async Task StopSecondOrdersNode()
    {
        if (_secondOrdersNode is null) return;
        await _secondOrdersNode.StopAsync();
        _secondOrdersNode.Dispose();
        _secondOrdersNode = null;
    }

    public async Task Start(Service service)
    {
        if (_hosts.ContainsKey(service)) return;
        _hosts[service] = await Build(service);
    }

    private async Task<IHost> Build(Service service)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:messaging"] = $"amqp://guest:guest@{_toxiproxy.Hostname}:{_toxiproxy.GetMappedPublicPort(RabbitProxyPort)}",
            [$"ConnectionStrings:{OrdersSetup.DatabaseName}"] = SqlConnectionString(OrdersSetup.DatabaseName),
            [$"ConnectionStrings:{PaymentsSetup.DatabaseName}"] = SqlConnectionString(PaymentsSetup.DatabaseName),
            [$"ConnectionStrings:{InventorySetup.DatabaseName}"] = SqlConnectionString(InventorySetup.DatabaseName),
            [$"ConnectionStrings:{ShippingSetup.DatabaseName}"] = SqlConnectionString(ShippingSetup.DatabaseName),
            [$"ConnectionStrings:{NotificationsSetup.DatabaseName}"] = SqlConnectionString(NotificationsSetup.DatabaseName),
            ["FakePay:BaseUrl"] = $"http://{_toxiproxy.Hostname}:{_toxiproxy.GetMappedPublicPort(FakePayProxyPort)}",
            ["FakePay:TimeoutSeconds"] = ProviderTimeout.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            // Must outlast FakePay's slowest scenario (2 s) with room for a loaded runner.
            ["FakePay:SettleWindowSeconds"] = "6",
            ["Carrier:LatencyMs"] = "20",
        });
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        switch (service)
        {
            case Service.Orders:
                builder.AddOrdersService(t =>
                {
                    t.ForwardAttemptTimeouts = [AttemptTimeout, AttemptTimeout, AttemptTimeout];
                    t.InquiryTimeout = AttemptTimeout;
                    // Longer than the settle window plus its first retries, as in
                    // production (30 s window vs a 15 min compensation budget).
                    t.CompensationAttemptTimeouts = [CompensationTimeout, CompensationTimeout, CompensationTimeout, CompensationTimeout];
                });
                break;
            case Service.Payments: builder.AddPaymentsService(); break;
            case Service.Inventory: builder.AddInventoryService(); break;
            case Service.Shipping: builder.AddShippingService(); break;
            case Service.Notifications: builder.AddNotificationsService(); break;
        }

        builder.Services.ConfigureWolverine(opts =>
        {
            // Production polls scheduled messages every few seconds; the tests would
            // spend most of their time waiting for that.
            opts.Durability.ScheduledJobFirstExecution = TimeSpan.Zero;
            opts.Durability.ScheduledJobPollingTime = TimeSpan.FromMilliseconds(250);
            opts.Durability.NodeReassignmentPollingTime = TimeSpan.FromSeconds(1);
            opts.Durability.HealthCheckPollingTime = TimeSpan.FromSeconds(1);
        });

        builder.Logging.AddProvider(new ScopeRecorder());
        builder.Logging.AddFilter<ScopeRecorder>(null, LogLevel.None);
        builder.Logging.AddFilter<ScopeRecorder>("System.Net.Http.HttpClient", LogLevel.Information);

        if (service == Service.Orders)
        {
            // Lets the concurrency test hold a saga's commit open so two messages for the
            // same saga really overlap. Does nothing unless a test arms it.
            builder.Services.ConfigureDbContext<OrdersDbContext>(o => o.AddInterceptors(SlowSagaCommits.Instance, FailingSagaCommits.Instance));
            builder.Logging.AddProvider(SagaConflictRecorder.Instance);
            builder.Logging.AddFilter<SagaConflictRecorder>(null, LogLevel.Trace);
        }

        var host = builder.Build();
        await host.StartAsync();
        return host;
    }

    // Graceful stop: Wolverine releases ownership of this node's persisted envelopes,
    // so whichever node starts next picks up the scheduled timeouts and the inbox.
    public async Task Stop(Service service)
    {
        if (!_hosts.Remove(service, out var host)) return;
        await host.StopAsync();
        host.Dispose();
    }

    public async Task EnsureAllRunning()
    {
        await Toxiproxy.Reset();
        SlowSagaCommits.Instance.Disarm();
        FailingSagaCommits.Instance.Disarm();
        await StopSecondOrdersNode();
        foreach (var service in Enum.GetValues<Service>())
        {
            await Start(service);
        }
        await Quiesce();
    }

    // Waits until no service has work in flight: nothing incoming, nothing outgoing,
    // and no scheduled message due soon (a leftover StepTimeout from the previous test).
    // Long retry schedules (minutes away) are ignored; they can't fire inside a test.
    public async Task Quiesce(TimeSpan? deadline = null)
    {
        var until = DateTime.UtcNow + (deadline ?? TimeSpan.FromSeconds(45));
        const string pending = """
            SELECT 'incoming ' + status + ' ' + message_type + ' owner ' + CAST(owner_id AS varchar(10)) FROM wolverine.wolverine_incoming_envelopes
            WHERE status = 'Incoming'
               OR (status = 'Scheduled' AND execution_time < DATEADD(second, 30, SYSDATETIMEOFFSET()))
            UNION ALL
            SELECT 'outgoing ' + message_type + ' to ' + destination FROM wolverine.wolverine_outgoing_envelopes
            """;
        // Quiet twice in a row, further apart than the broker's stats interval: one
        // quiet reading can be a stale queue depth, or a message between the broker's
        // hand-off and the receiver's inbox write.
        var quietReadings = 0;
        while (true)
        {
            var busy = new List<string>();
            foreach (var db in new[] { OrdersSetup.DatabaseName, PaymentsSetup.DatabaseName, InventorySetup.DatabaseName, ShippingSetup.DatabaseName, NotificationsSetup.DatabaseName })
            {
                await using var conn = new SqlConnection(SqlConnectionString(db));
                await conn.OpenAsync();
                await using var cmd = new SqlCommand(pending, conn);
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync()) busy.Add($"{db}: {reader.GetString(0)}");
            }
            busy.AddRange(await BrokerBacklog());
            quietReadings = busy.Count == 0 ? quietReadings + 1 : 0;
            if (quietReadings == 2) return;
            if (DateTime.UtcNow > until) throw new TimeoutException($"cluster not quiet: {string.Join("; ", busy.Take(10))}");
            await Task.Delay(250);
        }
    }

    // Messages still sitting in RabbitMQ (ready or delivered-but-unacked) are work the
    // SQL tables can't see yet, e.g. a backlog for a service that was just restarted.
    private async Task<List<string>> BrokerBacklog()
    {
        using var http = new HttpClient
        {
            BaseAddress = new Uri($"http://{_rabbit.Hostname}:{_rabbit.GetMappedPublicPort(15672)}")
        };
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String("guest:guest"u8.ToArray()));
        var queues = await http.GetFromJsonAsync<List<QueueDepth>>("/api/queues?columns=name,messages");
        return (queues ?? []).Where(q => q.Messages > 0).Select(q => $"rabbitmq queue {q.Name}: {q.Messages}").ToList();
    }

    private sealed record QueueDepth(string Name, int Messages);

    private async Task StartFakePay()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"ConnectionStrings:{FakePaySetup.DatabaseName}"] = SqlConnectionString(FakePaySetup.DatabaseName),
            // Past the Payments client timeout, well inside one saga attempt.
            ["FakePay:SlowResponseDelay"] = "00:00:02",
        });
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.UseUrls($"http://0.0.0.0:{_fakePayPort}");
        builder.AddFakePay();

        _fakePay = builder.Build();
        _fakePay.MapFakePayApi();
        await _fakePay.StartAsync();
    }

    private async Task CreateAndMigrateDatabases()
    {
        await using (var master = new SqlConnection(_sql.GetConnectionString()))
        {
            await master.OpenAsync();
            foreach (var db in new[] { OrdersSetup.DatabaseName, PaymentsSetup.DatabaseName, InventorySetup.DatabaseName, ShippingSetup.DatabaseName, NotificationsSetup.DatabaseName, FakePaySetup.DatabaseName })
            {
                await using var cmd = new SqlCommand($"CREATE DATABASE [{db}]", master);
                await cmd.ExecuteNonQueryAsync();
            }
        }

        await Migrate<OrdersDbContext>(OrdersSetup.DatabaseName, o => new(o));
        await Migrate<PaymentsDbContext>(PaymentsSetup.DatabaseName, o => new(o));
        await Migrate<InventoryDbContext>(InventorySetup.DatabaseName, o => new(o));
        await Migrate<ShippingDbContext>(ShippingSetup.DatabaseName, o => new(o));
        await Migrate<NotificationsDbContext>(NotificationsSetup.DatabaseName, o => new(o));
        await Migrate<FakePayDbContext>(FakePaySetup.DatabaseName, o => new(o));
    }

    private async Task Migrate<TContext>(string database, Func<DbContextOptions<TContext>, TContext> create) where TContext : DbContext
    {
        await using var db = create(new DbContextOptionsBuilder<TContext>().UseSqlServer(SqlConnectionString(database)).Options);
        await db.Database.MigrateAsync();
    }

    public TContext Db<TContext>(string database, Func<DbContextOptions<TContext>, TContext> create) where TContext : DbContext =>
        create(new DbContextOptionsBuilder<TContext>().UseSqlServer(SqlConnectionString(database)).Options);

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
