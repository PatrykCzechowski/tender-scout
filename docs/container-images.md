# Container images

The four services that get deployed build into container images from one `Dockerfile`: the API, the MCP server, the
worker and the database migrations. Why it looks the way it does is in
[ADR-0007](adr/0007-container-images.md). Build each image from the repository root by naming its stage:

```sh
docker build --target api --tag tender-scout-api .
docker build --target mcp --tag tender-scout-mcp .
docker build --target worker --tag tender-scout-worker .
docker build --target migrations --tag tender-scout-migrations .
```

The images are based on `mcr.microsoft.com/dotnet/aspnet:10.0`, which brings the time zone data and ICU. They hold
the published output only, run as the user `app` and not as root, and the API and the MCP server listen on HTTP port
8080. Settings are environment variables, with `__` in place of `:`; no setting is stored in an image.

| Image | Must be set | May be set |
| --- | --- | --- |
| `tender-scout-migrations` | `ConnectionStrings__tenderscout`, for a role that may create the `vector` extension | |
| `tender-scout-api` | `ConnectionStrings__tenderscout`, `Authentication__Schemes__Bearer__Authority`, `Authentication__Schemes__Bearer__ValidAudiences__0` | `WebSignIn__ClientId`, `Embedding__Endpoint`, `Chat__Provider`, `Chat__Endpoint`, `Chat__Model`, `Agent__Model`, `OPENROUTER_API_KEY`, `OTEL_EXPORTER_OTLP_ENDPOINT` |
| `tender-scout-mcp` | `ConnectionStrings__tenderscout`, `Authentication__Schemes__Bearer__Authority`, `Authentication__Schemes__Bearer__ValidAudiences__0` | `Embedding__Endpoint`, `OTEL_EXPORTER_OTLP_ENDPOINT` |
| `tender-scout-worker` | `ConnectionStrings__tenderscout`, `ConnectionStrings__messaging` (the RabbitMQ address as a URI) | `Embedding__Endpoint`, `Ingestion__*`, `OTEL_EXPORTER_OTLP_ENDPOINT` |

Run the migrations first and once. The other three expect the migrated schema and do not wait for it:

```sh
docker run --rm --env "ConnectionStrings__tenderscout=<connection string>" tender-scout-migrations
docker run --init --publish 8080:8080 --env "ConnectionStrings__tenderscout=<connection string>" \
  --env "Authentication__Schemes__Bearer__Authority=<issuer address>" \
  --env "Authentication__Schemes__Bearer__ValidAudiences__0=<audience>" tender-scout-api
```

The API then serves the page at `http://localhost:8080/` and answers `/health`. For hosted chat, add the settings of
[Chat through OpenRouter](agent-and-mcp.md#chat-through-openrouter) with `--env`, and pass the key by name from your shell, as
`--env OPENROUTER_API_KEY`, so that its value is not written in the command. Only the API needs it.

- **The issuer address is the one in the tokens.** A container reaches the issuer under that same address, or the
  tokens are refused; for an issuer over HTTP add `Authentication__Schemes__Bearer__RequireHttpsMetadata=false`.

- **No model is needed to start.** Without a model address the API and the MCP server are healthy, and the page,
  `/notices` and the notice tools work. `/ask` and `/agent` answer 503, and the document search tool reports that the
  model is unavailable.
- **The worker ingests notices from TED as it is,** unless `Ingestion__Enabled` is `false`, as in the demo. It reads
  a document from the path in its message, which is a path on the machine that ran the import, so it processes
  documents only when that folder is mounted at the same path.
- **`--init`.** With Docker Desktop on an Apple Silicon Mac, a worker started without its broker setting printed
  the unhandled exception and then kept running. With `docker run --init`, which puts a small init process in
  front, it ended. On the amd64 runner of CI it ends by itself. Other arm64 machines, such as a Raspberry Pi, were
  not checked, and the cause is not established.
- **`/health` follows the database slowly.** It connects to the database, and when the database is gone it answers
  503 only after about a minute, because the check goes through the retry policy of the connection.

`scripts/check-images.sh` builds the four images and checks them against a throwaway PostgreSQL and a Keycloak with
the realm file of the repository, without a model: the user, that no SDK, source or local settings file is inside,
the time zone data, the migrations on an empty database and a second time, `/health`, the page, 401 without a token,
`/notices` and the MCP server with a token of that realm, and that `POST /agent` resolves `Europe/Warsaw`. It needs
Docker and curl, takes about three minutes and removes its containers afterwards; the images stay. CI runs it on
every pull request and pushes nothing.

The images also run together, with the demo data, as described in [Run the demo](running.md). Not there yet: a
registry and a deployment.
