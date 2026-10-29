using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace DispatchFlow.Api.Observability;

public static class ObservabilityExtensions {
    public static WebApplicationBuilder AddObservability(this WebApplicationBuilder builder) {
        var telemetry = builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService("DispatchFlow.Api"))
            .WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation()
                .AddNpgsql())
            .WithMetrics(metrics => metrics.AddAspNetCoreInstrumentation())
            .WithLogging(configureBuilder: null, configureOptions: logging => logging.IncludeFormattedMessage = true);

        builder.Logging.AddFilter<OpenTelemetryLoggerProvider>("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.None);
        builder.Logging.AddFilter<OpenTelemetryLoggerProvider>("Npgsql.Command", LogLevel.None);

        string? endpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
        if (!string.IsNullOrWhiteSpace(endpoint)) {
            telemetry.UseOtlpExporter(OtlpExportProtocol.HttpProtobuf, new Uri(endpoint));
        }

        return builder;
    }
}
