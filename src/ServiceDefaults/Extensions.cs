using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Microsoft.Extensions.Hosting;

public static class ServiceDefaultsExtensions
{
    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder, params string[] extraMeters)
        where TBuilder : IHostApplicationBuilder
    {
        builder.ConfigureOpenTelemetry(extraMeters);

        builder.Services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);

        builder.Services.AddServiceDiscovery();
        builder.Services.ConfigureHttpClientDefaults(http => http.AddServiceDiscovery());

        return builder;
    }

    private static void ConfigureOpenTelemetry(this IHostApplicationBuilder builder, string[] extraMeters)
    {
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddMeter("Wolverine*")
                .AddMeter(ServiceDefaults.DeadLetterMonitor.MeterName)
                .AddMeter(extraMeters)
                .AddPrometheusExporter()
                .AddPrometheusPush(builder.Configuration["Monitoring:PrometheusOtlpEndpoint"]))
            .WithTracing(tracing => tracing
                .AddSource(builder.Environment.ApplicationName)
                .AddSource("Wolverine")
                .AddAspNetCoreInstrumentation(o => o.Filter = ctx =>
                    !ctx.Request.Path.StartsWithSegments("/health") &&
                    !ctx.Request.Path.StartsWithSegments("/alive") &&
                    !ctx.Request.Path.StartsWithSegments("/metrics"))
                // The provider's span and ours carry the same saga.id, so the hop across
                // the process boundary is one filter away in the dashboard.
                .AddHttpClientInstrumentation(o => o.EnrichWithHttpRequestMessage = (activity, request) =>
                {
                    if (request.Headers.TryGetValues(ServiceDefaults.SagaTracing.Header, out var ids))
                    {
                        activity.SetTag(ServiceDefaults.SagaTracing.TagName, ids.First());
                    }
                }));

        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
        }
    }

    // `aspire run -- --monitoring` hands every service Prometheus's OTLP receiver, and the
    // metrics are pushed there as well as to the Aspire dashboard. Its own reader, not a
    // second AddOtlpExporter: the OTel SDK refuses to combine that with UseOtlpExporter.
    private static MeterProviderBuilder AddPrometheusPush(this MeterProviderBuilder metrics, string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return metrics;

        var exporter = new OtlpMetricExporter(new OtlpExporterOptions
        {
            Endpoint = new Uri(endpoint),
            Protocol = OtlpExportProtocol.HttpProtobuf,
        });
        return metrics.AddReader(new PeriodicExportingMetricReader(exporter, exportIntervalMilliseconds: 15_000));
    }

    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        app.MapHealthChecks("/health");
        app.MapHealthChecks("/alive", new HealthCheckOptions { Predicate = r => r.Tags.Contains("live") });
        app.MapPrometheusScrapingEndpoint();
        return app;
    }
}
