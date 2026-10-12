using System.Reflection;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using BestStories.Api.Application;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace BestStories.Api.Configuration;

public static class ObservabilityServiceCollectionExtensions
{
    public const string OtlpEndpointKey = "OTEL_EXPORTER_OTLP_ENDPOINT";

    public static IServiceCollection AddBestStoriesObservability(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var serviceVersion = typeof(Program).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "unknown";

        var openTelemetry = services.AddOpenTelemetry()
            .ConfigureResource(resource =>
                resource.AddService("BestStories.Api", serviceVersion: serviceVersion))
            .WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation(options =>
                    options.Filter = context => !context.Request.Path.StartsWithSegments("/health"))
                .AddHttpClientInstrumentation()
                .AddSource(StoryTelemetry.ActivitySourceName))
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddMeter("System.Runtime")
                .AddMeter(StoryTelemetry.MeterName));

        var applicationInsightsConnectionString =
            configuration["ApplicationInsights:ConnectionString"] is { Length: > 0 } configured
                ? configured
                : configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];

        if (!string.IsNullOrWhiteSpace(applicationInsightsConnectionString))
        {
            openTelemetry.UseAzureMonitor(options =>
                options.ConnectionString = applicationInsightsConnectionString);
        }

        var otlpEndpoint = configuration[OtlpEndpointKey];
        if (!string.IsNullOrWhiteSpace(otlpEndpoint))
        {
            if (!Uri.TryCreate(otlpEndpoint, UriKind.Absolute, out var endpoint) ||
                (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
            {
                throw new InvalidOperationException(
                    $"{OtlpEndpointKey} must be an absolute HTTP or HTTPS URL.");
            }

            // The exporters read the endpoint and protocol from the standard OTEL_EXPORTER_OTLP_*
            // settings themselves. Setting the endpoint in code would stop the SDK from appending
            // the per-signal paths (/v1/traces, /v1/metrics, /v1/logs) that http/protobuf needs.
            openTelemetry
                .WithTracing(tracing => tracing.AddOtlpExporter())
                .WithMetrics(metrics => metrics.AddOtlpExporter())
                .WithLogging(
                    logging => logging.AddOtlpExporter(),
                    logOptions =>
                    {
                        logOptions.IncludeFormattedMessage = true;
                        logOptions.IncludeScopes = true;
                    });
        }

        return services;
    }
}
