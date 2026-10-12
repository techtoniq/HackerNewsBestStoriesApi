# Best Stories API

An ASP.NET Core (.NET 10) REST API that returns the best Hacker News stories in descending score order. It is designed to serve a high volume of requests from a cached snapshot while keeping load on the Hacker News API low and bounded.

## Run locally

Install the .NET 10 SDK (the version is pinned in `global.json`), then run from the repository root:

```powershell
dotnet run --project src\BestStories.Api
```

The `http` launch profile listens on `http://localhost:5172` and opens the stories endpoint:

```text
GET http://localhost:5172/api/stories/best?n=10
```

To run the container image instead:

```powershell
docker build --tag beststories-api .
docker run --rm -p 8080:8080 beststories-api
```

## API

`GET /api/stories/best?n={n}`

- `n` is required, must appear exactly once and must be between 1 and 500. Anything else returns `400`.
- The response is a JSON array of `title`, `uri`, `postedBy`, `time` (UTC, for example `2019-10-12T13:43:01+00:00`), `score` and `commentCount`, highest score first; equal scores are ordered by story ID.
- The result can contain fewer than `n` items when fewer eligible stories exist.
- Responses carry `ETag`, `Last-Modified` and `Cache-Control: public, max-age=…`. The max-age is whatever remains of the snapshot's freshness, capped at `ClientCacheMaxAgeSeconds` (60 by default). A request with a matching `If-None-Match` receives `304 Not Modified`.
- `429 Too Many Requests` (with `Retry-After`) when a client exceeds the rate limit.
- `503 Service Unavailable` (with `Retry-After`) when no snapshot within the maximum staleness is available.

The OpenAPI document is served at `/openapi/v1.json`.

### Errors and response headers

- All errors use `application/problem+json` and include a `traceId` that matches the server-side trace and logs. Unexpected exceptions are logged and return a generic `500` without exception details.
- Empty error responses (unknown routes, unsupported methods) are converted to Problem Details.
- Every response sets `X-Content-Type-Options: nosniff`. Responses that do not set their own caching policy (errors, health checks) are sent with `Cache-Control: no-store`. Kestrel's `Server` header is disabled; configure the reverse proxy or load balancer not to add identifying headers of its own.

### Health checks

- `GET /health/live` returns `200` while the process is running.
- `GET /health/ready` returns `200` when the instance can handle requests, otherwise a generic `503`:
  - Automatic refresh: ready once a snapshot is available (this instance's initial load, or one another instance published to Redis).
  - On-demand refresh: ready as soon as the story cache is reachable, because the first request populates it.
  - With Redis configured, Redis availability is part of readiness.
  - A snapshot older than `MaxStalenessSeconds` reports `{"status":"degraded"}` with `200`. Story requests return `503` meanwhile, but the instance stays in rotation: staleness comes from Hacker News and affects every instance alike, so failing readiness would only swap the API's `503` (with Problem Details and `Retry-After`) for the load balancer's and stall rolling deployments. Alert on the `beststories.cache.snapshot.age` metric instead.

Use `/health/ready` as the readiness probe and `/health/live` as the liveness probe.

## How it works

Requests are always served from a score-sorted snapshot of the Hacker News best stories. A refresh fetches the best-story ID list and then each item, with at most `MaxConcurrentRequests` (8) item requests in flight, and publishes the complete snapshot atomically.

### Refresh modes

- **Automatic** (`StoryCache:AutomaticRefresh = true`, the default). A background service loads the snapshot at startup, retrying with exponential backoff (1s doubling up to `InitialLoadMaxRetryDelaySeconds`) until it succeeds, then refreshes every `RefreshIntervalSeconds` (300). Requests never wait for Hacker News. Suited to high-volume or high-availability deployments.
- **On-demand** (`AutomaticRefresh = false`). The first request loads the snapshot. Once it is older than `CacheTtlSeconds` (300), the next request starts a refresh. Suited to low-volume deployments where polling Hacker News continuously is wasteful.

### Staleness and failure handling

- A snapshot is **fresh** for the refresh interval (automatic) or the TTL (on-demand), and **acceptable** up to `MaxStalenessSeconds` (1800).
- Fresh and stale-but-acceptable snapshots are served immediately. In on-demand mode a stale snapshot also starts a refresh in the background (stale-while-revalidate), so requests do not wait for it.
- Beyond the maximum staleness, or with no snapshot, on-demand requests wait for a refresh and automatic-mode requests receive `503`.
- A failed refresh never replaces the last good snapshot.
- After a failed on-demand refresh, requests that need one fail fast with `503` for `FailureCooldownSeconds` (30) instead of retrying Hacker News on every request.
- A refresh that has not finished within `RefreshTimeoutSeconds` (60) is abandoned and counts as a failure, so a Hacker News that is slow rather than failing cannot hold the refresh (and the Redis lease) indefinitely.
- An on-demand request waits at most `OnDemandWaitSeconds` (10) for a refresh, then receives `503`; the refresh carries on in the background and later requests are served from it.

### Protecting Hacker News

- **Resilience pipeline** on the Hacker News client (`Microsoft.Extensions.Http.Resilience`): up to two retries with exponential backoff and jitter for transient failures, a 5-second per-attempt timeout, a 20-second overall timeout, and a circuit breaker that stops calling Hacker News while it is failing.
- **Partial failures**: a refresh publishes when at least `MinimumSuccessRatio` (95%) of item requests succeed; the failed items are left out and counted in telemetry. Once more items have failed than the ratio allows, the remaining requests are cancelled.
- **Upstream anomalies**: non-positive and duplicate IDs are ignored, and a list longer than 500 is truncated, with a warning, rather than failing the refresh.
- **Coalescing**: concurrent requests on an instance share one in-flight refresh. Across instances, see below.
- **Rate limiting**: each client address may make `RateLimiting:PermitLimit` (100) story requests per `WindowSeconds` (10) on each instance. IPv4 addresses mapped into IPv6 are limited as IPv4, and IPv6 clients by their /64 prefix so that rotating addresses within one subscriber's range does not bypass the limit.

### Running behind a proxy or load balancer

`X-Forwarded-For` and `X-Forwarded-Proto` are honoured only from loopback and the proxies listed under `ForwardedHeaders`; from anyone else they are ignored, so clients cannot spoof their address. **Configure this before deploying behind a load balancer or ingress.** Otherwise every client behind it shares the proxy's address and therefore one rate limit.

```powershell
$env:ForwardedHeaders__KnownNetworks__0 = "10.0.0.0/16"   # for example, the ingress subnet
$env:ForwardedHeaders__ForwardLimit = "1"                   # trusted proxies between client and service
```

Deleted and dead stories are excluded. Stories without a URL use their Hacker News item page as `uri`; a missing comment count is returned as zero.

## Distributed cache (Redis)

The default `DistributedCache:Provider` is `Memory`, suitable for development and single-instance deployments. For several instances, set it to `Redis` and supply a connection string through deployment configuration:

```powershell
$env:DistributedCache__Provider = "Redis"
$env:DistributedCache__RedisConnectionString = "localhost:6379"
dotnet run --project src\BestStories.Api
```

Run Redis locally with Docker if needed:

```powershell
docker run --rm --name beststories-redis -p 6379:6379 redis:7-alpine
```

With Redis:

- All instances must use the same Redis deployment and `DistributedCache:InstanceName`.
- The snapshot is shared through `IDistributedCache` under a versioned key (`best-stories:snapshot:v1`). Change the version when the snapshot format changes so that old and new versions do not read each other's snapshots during a rolling deployment.
- Each instance reuses a snapshot it has read for `LocalCacheMilliseconds` (1000), so requests do not each read and deserialize it from Redis.
- A Redis lease (`SET NX` with an owner token) ensures only one instance refreshes at a time. The lease lasts `LockLeaseSeconds` (30) and is renewed by its owner every third of that period while the refresh runs. If the owner crashes, another instance can take over within one lease period. Renewal and release check the owner token, so an expired owner cannot extend or delete another instance's lease.
- If a renewal finds another owner holding the lease, or renewals fail (including Redis timeouts) for a whole lease period, the owner abandons its refresh rather than racing the new owner. Failures to renew or release are logged and never fail a refresh that has already published; an unreleased lease simply expires.
- Scheduled refreshes are skipped when another instance published within the current interval, so a fleet refreshes about once per interval rather than once per instance.
- On-demand instances that find the lease held poll the shared cache every `LockPollMilliseconds` (200) for up to `LockWaitSeconds` (10), then return `503`.
- The connection is made with `AbortOnConnectFail = false`, so the API starts while Redis is unreachable and reconnects in the background; readiness reports the outage meanwhile.

Use a private, authenticated, TLS-enabled Redis service in production and supply its connection string as a secret.

## Configuration reference

Every setting can be overridden with environment variables, for example `StoryCache__AutomaticRefresh=false`. Settings are validated at startup, and invalid values stop the application with a message naming the setting.

| Setting | Default | Purpose |
| --- | --- | --- |
| `StoryCache:AutomaticRefresh` | `true` | Automatic (background) or on-demand refresh. |
| `StoryCache:RefreshIntervalSeconds` | `300` | Automatic refresh interval. |
| `StoryCache:CacheTtlSeconds` | `300` | On-demand freshness window. |
| `StoryCache:MaxStalenessSeconds` | `1800` | Oldest snapshot that is served. Must be at least the freshness window. |
| `StoryCache:MaxConcurrentRequests` | `8` | Concurrent item requests during a refresh (1–64). |
| `StoryCache:MinimumSuccessRatio` | `0.95` | Fraction of item requests that must succeed to publish. |
| `StoryCache:FailureCooldownSeconds` | `30` | Fail-fast period after a failed on-demand refresh; also the `Retry-After` on `503`. |
| `StoryCache:RefreshTimeoutSeconds` | `60` | Longest a single refresh may run before it is abandoned. |
| `StoryCache:OnDemandWaitSeconds` | `10` | Longest an on-demand request waits for a refresh before returning `503`. |
| `StoryCache:InitialLoadMaxRetryDelaySeconds` | `30` | Longest backoff between initial-load attempts. |
| `StoryCache:ClientCacheMaxAgeSeconds` | `60` | Upper bound for the `Cache-Control` max-age. |
| `DistributedCache:Provider` | `Memory` | `Memory` or `Redis`. |
| `DistributedCache:RedisConnectionString` | | Required for Redis. Supply as a secret. |
| `DistributedCache:InstanceName` | `BestStories:` | Key prefix shared by all instances. |
| `DistributedCache:LockLeaseSeconds` | `30` | Refresh-lock lease (minimum 5). |
| `DistributedCache:LockWaitSeconds` | `10` | How long an on-demand request waits for another instance's refresh. |
| `DistributedCache:LockPollMilliseconds` | `200` | Shared-cache polling interval while waiting. |
| `DistributedCache:LocalCacheMilliseconds` | `1000` | Per-instance reuse of the Redis snapshot (0 disables it). |
| `RateLimiting:PermitLimit` | `100` | Requests per client address per window. |
| `RateLimiting:WindowSeconds` | `10` | Rate-limit window. |
| `ForwardedHeaders:KnownProxies` | | Proxy IP addresses whose forwarded headers are trusted (loopback is always trusted). |
| `ForwardedHeaders:KnownNetworks` | | Proxy networks in CIDR notation whose forwarded headers are trusted. |
| `ForwardedHeaders:ForwardLimit` | `1` | Number of trusted proxies between the client and the service. |

## Observability

The API is instrumented with OpenTelemetry: ASP.NET Core and outbound HTTP traces (health checks excluded), runtime, HTTP and API metrics, and structured logs. Custom telemetry covers cache reads (hit, stale or miss), refresh results and duration, refresh timeouts, on-demand wait timeouts, stories per refresh, skipped items, coalesced refreshes, cooldown rejections and the age of the newest snapshot (`beststories.cache.snapshot.age`, in seconds; alert when it exceeds `MaxStalenessSeconds`).

- **Azure Application Insights**: set `APPLICATIONINSIGHTS_CONNECTION_STRING` (or `ApplicationInsights:ConnectionString`) through the platform's secret store.
- **Any OTLP collector**: set the standard `OTEL_EXPORTER_OTLP_ENDPOINT`, plus `OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf` when the collector expects OTLP over HTTP rather than gRPC (the default). Traces, metrics and logs are exported, and the SDK appends the per-signal paths that HTTP/protobuf requires.
- With neither configured, the application is instrumented but exports nothing. Do not commit connection strings or collector credentials.

## Tests

The solution has three test projects:

- **Unit tests**: the snapshot builder, provider, refresh service, client, health check and options validation, with a fake clock (`FakeTimeProvider`) instead of real delays.
- **Integration tests**: the real application hosted in memory with `WebApplicationFactory`, with Hacker News replaced at the HTTP-handler level. The typed client, resilience pipeline, snapshot builder, provider, cache and endpoints all run for real. No network access or Redis is needed.
- **Component tests**: the Redis cache and refresh lock against a real Redis container started by Testcontainers. These need a running Docker engine.

```powershell
dotnet test --project tests\BestStories.Api.UnitTests\BestStories.Api.UnitTests.csproj
dotnet test --project tests\BestStories.Api.IntegrationTests\BestStories.Api.IntegrationTests.csproj
dotnet test --project tests\BestStories.Api.ComponentTests\BestStories.Api.ComponentTests.csproj
```

Run all three with:

```powershell
dotnet test --solution BestStories.slnx
```

All test projects use xUnit v3 on Microsoft Testing Platform. The GitHub Actions workflow (`.github/workflows/ci.yml`) builds the solution with warnings as errors, runs all three suites (component tests use the runner's Docker engine) and builds the container image on every pull request and push to `main`.

## Project structure

```text
src/BestStories.Api
  Domain/            Story, StorySnapshot and limits, independent of the upstream and API formats
  Application/       Snapshot building, refresh coordination, background refresh, readiness, telemetry
  Infrastructure/    In-memory and Redis implementations of the cache and refresh lock
  Integrations/      Hacker News v0 client and its wire DTO
  Endpoints/         HTTP endpoints, the public response contract and pipeline middleware
  Configuration/     Options, validators and service registration
```

Build settings are shared through `Directory.Build.props` (nullable, warnings as errors, .NET analyzers), and package versions are managed centrally in `Directory.Packages.props`.

## Potential enhancements

- **Distributed rate limiting**: limits are enforced per instance; a shared limit across instances would need a Redis-backed limiter or enforcement at the gateway.
- **Incremental refresh**: every refresh fetches every item, because scores change. Hacker News's `/v0/updates.json` lists recently changed items, which would allow refetching only those between periodic full refreshes and cut upstream traffic substantially.
- **Fencing for lost leases**: if a refresh outlives its lease (for example after a long pause that stops renewal), two instances can briefly refresh at once. Both publish complete snapshots, so the effect is extra upstream load rather than inconsistent data; a fencing token on the snapshot write would remove it.
- **Readiness during Redis outages**: readiness currently fails when Redis is unreachable, which takes every instance out of rotation at once. An alternative is to keep instances ready and serve their last local snapshot during a short outage.
- **Load testing**: add a load test (for example with k6 or NBomber) to verify throughput and latency targets and to tune the rate limit and client cache max-age.
- **Deployment**: infrastructure as code for the chosen platform, probes wired to the health endpoints, dashboards, and alerts on refresh failures, staleness, skipped items and cooldown rejections.
- **Supply chain**: dependency update automation (Dependabot or Renovate), container image scanning and an SBOM in CI.
- **Administration**: authenticated endpoints to force a refresh or clear the cache, if operational needs justify them.
