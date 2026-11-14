# ADR-0007: One hand-written Dockerfile with a shared build stage and four runtime images

## Status

Accepted — 2026-11-14

## Context

The system ran only through the Aspire AppHost on a development machine. A deployment
to Azure Container Apps needs container images of the four services that run there:
the API, the MCP server, the worker and the database migrations.

Constraints: the agent needs the time zone `Europe/Warsaw`, the services must start
without a model, no secret or local setting may get into an image, and CI must show
on every pull request that the images build and start, without pushing them.

## Options considered

| Question | **Chosen** | Rejected |
| --- | --- | --- |
| How images are made | A hand-written multi-stage `Dockerfile` | `dotnet publish /t:PublishContainer`: less to write, but the base image, the user and the layers are then decided by the SDK and cannot be read in one file |
| Layout | One file: a shared build stage, then one named stage for each service | A build argument for the project: the entry point then needs a shell. One file per service: four near-copies and four restores |
| Runtime base image | `mcr.microsoft.com/dotnet/aspnet:10.0` for all four | Alpine and the chiseled images: no ICU and no time zone data unless the `extra` variant is used; chiseled has no shell, which is awkward for a first deployment. The plain runtime image: the shared service defaults reference ASP.NET Core, also in the worker and the migrations |
| Migrations | A container of their own that runs once and exits | Migrating when a service starts: two replicas would migrate at the same time, and a web service would need the rights to change the schema |
| What CI does with the images | Builds them and runs one check script; pushes nothing | A registry now: it needs credentials and a decision about tags, which belong to the deployment. Tests in `dotnet test` that build images: every local test run would wait for Docker builds |

## Decision

- **Restore is a layer of its own.** The project files and the three files that decide
  the SDK and the packages are copied first, then `dotnet restore` runs, then the
  source is copied and published. A change in a source file reuses the restore layer.
- **Framework-dependent publish without a runtime identifier,** so the same file builds
  on amd64 and arm64. A deployment needs amd64; CI builds that on a GitHub-hosted
  runner.
- **The runtime images hold the published output only.** No SDK, source or project
  file. Time zone data and ICU come from the base image: nothing is installed,
  invariant globalization is not switched on and no `TZ` is set.
- **Every image runs as the user `app` of the base image.** The API and the MCP server
  listen on HTTP port 8080; TLS is the platform's job.
- **Configuration is environment variables.** Each image needs the connection string
  of the database; the worker also needs the broker. Model addresses are optional.
- **The build context is an allow-list.** `.dockerignore` leaves everything out and
  lets in `src/` and three root files, without build output and without settings of
  one machine or one environment.
- **`scripts/check-images.sh` is the check,** for CI and for a developer: it builds
  the images and checks their content, the migrations, `/health` and that the agent
  resolves its time zone. It needs no model and removes its containers.
- **No images** for the document import, the evaluation, the document search and the
  AppHost: they are developer commands. No `HEALTHCHECK` instruction: the image has no
  HTTP tool, and Container Apps has probes of its own.

## Consequences

### Easier

- One file shows how every service is built and run; a base image change is one word
  in four lines.
- A private file in a working copy cannot enter an image, because nothing outside the
  allow-list reaches the build.
- A broken image fails the pull request, and the same check runs on a laptop.
- Moving to the chiseled `extra` image later is a change of the four `FROM` lines,
  checked by the same script.

### Costs and risks

- **The images are large,** about 105 MB each compressed and 405 MB unpacked, of which
  the base image is 94 MB and 377 MB. They share that base, so four images do not cost
  four times the space.
- **The image tag `10.0` moves.** A rebuild picks up the monthly fixes, and two builds
  of the same commit can differ. Pinned digests need tooling to keep them fresh.
- **On arm64 a crashed process does not end by itself.** A .NET process that is the
  first process of a container keeps running after an unhandled exception; on amd64 it
  ends, which the check shows on every pull request. `docker run --init` avoids it, and
  on arm64 the check starts the worker that way. The deployment target is amd64.
- **`/health` takes about a minute to turn unhealthy** when the database is gone,
  because the database check goes through the retry policy. Probes of a deployment
  must allow for that.
- **The worker image cannot process documents by itself.** It reads them from a path
  on the machine that ran the import. Where documents live after a deployment is a
  decision of the deployment.
- **The check does not start the worker with a broker,** because it would then call
  the TED API. Only its start without settings is checked.
- **Not checked:** an image built on amd64 by hand, an image scan, the size after
  switching to a smaller base.
