# ADR-0001: .NET 10 with .NET Aspire for local orchestration

## Status

Accepted — 2026-11-07

## Context

Tender Scout is a small distributed system: an HTTP API (`TenderScout.Api`) and a
background worker (`TenderScout.Worker`) that share a PostgreSQL database (with the
`pgvector` extension for embeddings) and communicate through RabbitMQ.

We need to decide:

1. **The runtime and language** for the services.
2. **How the whole system is started locally** — services plus their backing
   containers — so that a fresh clone runs with one command and the services get
   connection strings, health checks and telemetry without hand-written glue.

Constraints that shape the decision:

- One developer, so setup and maintenance cost matters more than flexibility.
- Strict builds (`TreatWarningsAsErrors`, central package management, `dotnet format`
  in CI) — the tooling should fit into a single `dotnet` toolchain.
- CI runs on a self-hosted Raspberry Pi (linux-arm64), so the SDK and images must
  support ARM64.
- The likely production target is a managed container platform (Azure Container Apps).

## Options considered

### Runtime

| Option | Notes |
| --- | --- |
| **.NET 10 (LTS)** | Long-term support until late 2028. Mature ASP.NET Core, hosted services for workers, first-class OpenTelemetry, ARM64 SDK. The author's main stack. |
| .NET 9 (STS) | Shorter support window; nothing it offers that 10 lacks. |
| Node.js / Python | Viable for an API + worker, but would mean a second ecosystem with no offsetting benefit for this project. |

### Local orchestration

| Criterion | **.NET Aspire AppHost** | Plain `docker-compose` |
| --- | --- | --- |
| Definition | C# (`AppHost.cs`), type-checked, refactorable, references projects directly (`Projects.TenderScout_Api`) | YAML; services referenced by image or build context |
| How services run | .NET projects run as normal processes (fast rebuilds, debugger attaches as usual); only infrastructure runs in containers | Everything in containers, or a hybrid you wire up yourself |
| Configuration wiring | `WithReference(...)` injects connection strings and endpoints as environment variables; client integrations (`AddNpgsqlDataSource`, `AddRabbitMQClient`) consume them | Connection strings duplicated by hand across `compose.yml`, `appsettings` and `.env` |
| Startup ordering | `WaitFor(...)` waits for resources to be *healthy*, not just started | `depends_on` + hand-written `healthcheck` blocks |
| Observability | Dashboard with logs, traces and metrics out of the box via OTLP; shared `ServiceDefaults` project adds health checks, resilience and telemetry to every service | Requires adding and configuring an OTel collector / Jaeger / Grafana stack |
| Tooling footprint | Only the .NET SDK + a container runtime; `dotnet run --project src/TenderScout.AppHost` | Docker Compose; no .NET-specific knowledge needed |
| Portability | Tied to .NET; non-.NET contributors must learn the AppHost model | Universal, language-agnostic, well understood |
| Stability | Young and fast-moving; frequent releases with API changes | Very stable format |
| Path to deployment | Can publish manifests / deploy to Azure Container Apps (`azd`, Aspire publishers); also emits Docker Compose if needed | Compose is not a production format for ACA/Kubernetes; needs a separate deployment definition |

## Decision

- Build all services on **.NET 10**, pinned via `global.json` (SDK `10.0.x`,
  `rollForward: latestFeature`) and `Directory.Build.props` (`net10.0`).
- Use a **.NET Aspire AppHost** (`TenderScout.AppHost`, Aspire 13.x) as the single
  entry point for local development. It provisions PostgreSQL (`pgvector/pgvector:pg17`)
  and RabbitMQ (with management plugin) as persistent containers with data volumes,
  and starts the API and worker with references and health-based startup ordering.
- Share cross-cutting concerns (OpenTelemetry, health endpoints, HTTP resilience,
  service discovery) through **`TenderScout.ServiceDefaults`**.
- Keep Aspire package versions aligned in `Directory.Packages.props` so the AppHost
  SDK and all `Aspire.*` packages move together.
- Do **not** maintain a separate `docker-compose.yml`. If one is ever needed (e.g. for
  someone without the .NET SDK), generate it from the AppHost rather than hand-writing it.

## Consequences

### Easier

- **One command to run everything:** `dotnet run --project src/TenderScout.AppHost`
  starts the database, broker, API and worker with correct configuration.
- **No duplicated connection strings:** resource names (`tenderscout`, `messaging`)
  are the only contract between the AppHost and the services.
- **Observability from day one:** traces, metrics and structured logs are visible in
  the Aspire dashboard without extra infrastructure.
- **Fast inner loop:** services run as native processes with normal debugging and hot
  reload; only stateful dependencies are containers, and persistent lifetimes keep
  them warm between runs.
- **Single toolchain:** build, test, format and orchestration are all `dotnet`
  commands, which keeps CI (including the Raspberry Pi runner) simple.
- **Integration tests** can reuse the AppHost via `Aspire.Hosting.Testing` instead of
  re-declaring the topology.
- **Deployment path:** the same app model can target Azure Container Apps through
  `azd` / Aspire publishing, rather than translating a Compose file by hand.

### Costs and risks

- **Aspire version churn:** Aspire ships often and has renamed APIs, packages and SDK
  shapes between versions. Upgrades must bump the AppHost SDK and every `Aspire.*`
  package together, and may need code changes. We pin exact versions and upgrade
  deliberately rather than floating.
- **Aspire CLI vs `dotnet run`:** newer Aspire versions steer towards the Aspire CLI;
  we currently run via `dotnet run` and suppress `ASPIRE010`. This may need revisiting
  on future upgrades.
- **Container runtime still required:** Docker or Podman must be installed and
  running locally; Aspire does not remove that dependency.
- **Deployment is not free:** Azure Container Apps support depends on the Aspire
  publishing/`azd` story at the time we deploy; production concerns (managed
  PostgreSQL, secrets, networking, scaling) will still need their own decisions and
  likely their own ADR. Deploying somewhere other than Azure means relying on generic
  publishers (Compose, Kubernetes) that are less mature.
- **.NET-specific knowledge:** contributors unfamiliar with Aspire face a learning
  curve that `docker-compose` would not impose.
- **LTS horizon:** .NET 10 support ends in late 2028; moving to the next LTS (.NET 12)
  will be a planned upgrade.
