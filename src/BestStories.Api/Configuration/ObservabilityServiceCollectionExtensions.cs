using Azure.Monitor.OpenTelemetry.AspNetCore;
using BestStories.Api.Services;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace BestStories.Api.Configuration;

public static class ObservabilityServiceCollectionExtensions
{
    public static IServiceCollection AddBestStoriesObservability(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var serviceVersion = typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown";
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
            configuration["ApplicationInsights:ConnectionString"];
        if (string.IsNullOrWhiteSpace(applicationInsightsConnectionString))
        {
            applicationInsightsConnectionString =
                Environment.GetEnvironmentVariable("APPLICATIONINSIGHTS_CONNECTION_STRING");
        }

        if (!string.IsNullOrWhiteSpace(applicationInsightsConnectionString))
        {
            openTelemetry.UseAzureMonitor(options =>
                options.ConnectionString = applicationInsightsConnectionString);
        }

        var otlpEndpointValue = configuration["OpenTelemetry:Otlp:Endpoint"];
        if (string.IsNullOrWhiteSpace(otlpEndpointValue))
        {
            otlpEndpointValue = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
        }

        if (!string.IsNullOrWhiteSpace(otlpEndpointValue))
        {
            if (!Uri.TryCreate(otlpEndpointValue, UriKind.Absolute, out var otlpEndpoint) ||
                (otlpEndpoint.Scheme != Uri.UriSchemeHttp &&
                 otlpEndpoint.Scheme != Uri.UriSchemeHttps))
            {
                throw new InvalidOperationException(
                    "The OpenTelemetry OTLP endpoint must be an absolute HTTP or HTTPS URL.");
            }

            openTelemetry.WithTracing(tracing =>
                tracing.AddOtlpExporter(options => options.Endpoint = otlpEndpoint));
            openTelemetry.WithMetrics(metrics =>
                metrics.AddOtlpExporter(options => options.Endpoint = otlpEndpoint));
            services.AddLogging(logging => logging.AddOpenTelemetry(options =>
            {
                options.IncludeFormattedMessage = true;
                options.IncludeScopes = true;
                options.AddOtlpExporter(exporter => exporter.Endpoint = otlpEndpoint);
            }));
        }

        return services;
    }
}
