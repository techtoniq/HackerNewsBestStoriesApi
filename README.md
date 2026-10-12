# Best Stories API

An ASP.NET Core .NET 10 REST API that returns Hacker News best stories in descending score order.

## Run locally

Install the .NET 10 SDK, then run from the repository root:

```powershell
dotnet run --project src\BestStories.Api
```

The API listens on the URL printed by ASP.NET Core. For example:

```text
GET http://localhost:5000/api/stories/best?n=10
```

`n` is required and must be between 1 and 500. The response is a JSON array containing `title`, `uri`, `postedBy`, `time`, `score`, and `commentCount`. `time` is UTC. The result can contain fewer than `n` items if Hacker News returns fewer eligible stories.

## Health checks

- `GET /health/live` returns `200` while the API process is running.
- `GET /health/ready` returns a generic `503` Problem Details response until the first story snapshot has been successfully published when automatic refresh is enabled, then returns `200`. With on-demand refresh enabled, it returns `200` immediately because the instance is ready to populate the cache on the first request. Configure the load balancer or orchestrator to use this endpoint as the readiness probe.

## Error handling and response headers

- API errors use `application/problem+json`. Unexpected exceptions are logged server-side and return a generic `500` response without exception details, stack traces, or request identifiers.
- Empty error responses (such as unknown routes or unsupported methods) are converted to generic Problem Details responses. Health responses do not expose individual check names or diagnostic messages.
- The API suppresses common server/platform-identifying response headers, disables Kestrel's `Server` header, marks responses as non-cacheable, and sets `X-Content-Type-Options: nosniff`. Configure the hosting proxy/load balancer similarly because it may add its own headers.

## Observability

The API is instrumented with OpenTelemetry for ASP.NET Core and outbound HTTP traces, runtime/HTTP/API metrics, and structured logs. Custom cache telemetry includes cache hits/misses, refresh success/failure and duration, fetched-story counts, and refreshes coalesced by the single-flight guard. Health-check traffic is excluded from request traces.

- To send telemetry to Azure Application Insights, configure `APPLICATIONINSIGHTS_CONNECTION_STRING` through the deployment platform's secret/configuration store. Azure Monitor exports traces, metrics, and logs.
- To send telemetry to any OTLP-compatible collector, set `OTEL_EXPORTER_OTLP_ENDPOINT` or `OpenTelemetry:Otlp:Endpoint` to an absolute HTTP(S) collector endpoint. Traces, metrics, and logs are exported over OTLP. The exporter uses gRPC by default; set `OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf` when the collector expects OTLP over HTTP/protobuf.
- If neither exporter is configured, the application remains instrumented but does not export telemetry. Do not commit connection strings or collector credentials.

## Distributed cache (Redis)

The default `DistributedCache:Provider` is `Memory`, which is suitable for development and single-instance deployments. For a shared cache and cross-instance refresh coordination, set it to `Redis` and provide a Redis connection string through deployment configuration:

```powershell
$env:DistributedCache__Provider = "Redis"
$env:DistributedCache__RedisConnectionString = "localhost:6379,abortConnect=false"
dotnet run --project src\BestStories.Api
```

Run Redis locally with Docker if needed:

```powershell
docker run --rm --name beststories-redis -p 6379:6379 redis:7-alpine
```

All instances must use the same Redis deployment and `DistributedCache:InstanceName`. Story snapshots are shared through `IDistributedCache`; a Redis `SET NX` lease ensures only one instance refreshes Hacker News at a time. Other on-demand instances poll the shared cache while the lock is held and use the newly populated snapshot, or return `503` after `LockWaitSeconds`. The lock is released with an owner-token check and expires automatically after `LockLeaseSeconds` if its owner exits unexpectedly. The default lease exceeds the worst-case refresh duration implied by the 500-item list, the configured concurrency, and the 15-second upstream request timeout; startup validation rejects a shorter Redis lease. Use a private, authenticated, TLS-enabled Redis service in production and supply its connection string as a secret. Redis availability is part of readiness.

## Run tests

The solution has separate test projects for isolated unit tests, HTTP integration tests, and component tests for the distributed-cache adapter. Each project can be run independently:

```powershell
dotnet test --project tests\BestStories.Api.UnitTests\BestStories.Api.UnitTests.csproj
dotnet test --project tests\BestStories.Api.IntegrationTests\BestStories.Api.IntegrationTests.csproj
dotnet test --project tests\BestStories.Api.ComponentTests\BestStories.Api.ComponentTests.csproj
```

Run all three suites together with:

```powershell
dotnet test --solution BestStories.slnx
```

All test projects use xUnit v3 with Microsoft Testing Platform. HTTP integration tests use `WebApplicationFactory` and ASP.NET Core's in-memory test server to call the actual routes and middleware while replacing the upstream story provider, so they require neither Hacker News access nor Redis. Component tests exercise the distributed-cache adapter through `IDistributedCache` backed by an in-memory provider; they do not require a live Redis server.

## Design and assumptions

- High volume/high availability - `StoryCache:AutomaticRefresh` defaults to `true`. When enabled, the API refreshes the Hacker News best-story IDs and their item details at startup and every `RefreshIntervalSeconds` (300 seconds by default).
- Low volume - when `AutomaticRefresh` is `false`, the background service does not call Hacker News. The first API request loads the cache; subsequent requests use the cached snapshot until `CacheTtlSeconds` (300 seconds by default) elapses, then trigger an on-demand refresh. These settings can be changed in `appsettings.json` or with the `StoryCache__AutomaticRefresh` environment variable.
- On-demand refreshes are coalesced: concurrent requests share one in-flight refresh rather than each starting a separate upstream fetch. Item requests remain bounded by `MaxConcurrentRequests` (8 by default).
- Responsibilities are separated: `StoryProvider` owns retrieval, cache freshness, and single-flight upstream refresh; `StoryRefreshService` only schedules refreshes when automatic refresh is enabled; `HackerNewsV0Client` and its wire DTO are grouped under `Integrations/HackerNews/V0` so the upstream version, routes, and response mapping stay together.
- Requests are served from an in-memory or Redis-backed, score-sorted snapshot. Refreshes publish the complete snapshot atomically; a failed refresh retains the last good snapshot, but the request that required a refresh receives `503 Service Unavailable` rather than silently receiving expired data.
- `n` is capped at 500, matching the Hacker News best-stories list size. Deleted/dead stories are excluded. Stories without a URL use their Hacker News item page as `uri`; missing comment counts are returned as zero.
- A failed or incomplete upstream refresh is logged and does not replace the last good snapshot.

## Potential enhancements

- Add bounded retry and backoff policies for transient Hacker News failures, with tests that verify retry limits and cancellation behavior.
- Add Redis-backed component/integration tests using a real Redis service to verify snapshot sharing and refresh-lock acquisition, expiry, and safe release across instances. The current component test uses `IDistributedCache` backed by in-memory storage; cross-instance refresh tests use an in-memory lock.
- Add deployment-specific Redis monitoring and alerting, including cache availability, lock contention, and refresh failures.
- Add a CI pipeline that restores, builds, and runs all three test projects on each change.
- Deploy the container to the selected hosting platform with health/readiness probes, centralized telemetry, and secrets/configuration supplied through the platform. AWS is one possible target, not a current deployment.
- Add an authentication mechanism, with admin endpoints to clear the cache and change configuration settings on the fly