# Run the demo

The whole system runs with Docker Compose on invented data: ten tenders and the passages of five of their documents.
No .NET SDK and no Aspire AppHost are needed, only Docker with Compose v2 (`docker compose`). It was checked with
Compose 5.1.2 on macOS and with the Compose of GitHub's `ubuntu-latest` runner in CI.

```sh
docker compose up --build
```

The first start builds the four images of the [`Dockerfile`](../Dockerfile) and pulls PostgreSQL with pgvector, RabbitMQ
and Keycloak, which takes a few minutes. Then open <http://localhost:15150>, choose "Zaloguj się" and sign in as
`tester` with the password `local-only-password`.

| Address | What |
| --- | --- |
| <http://localhost:15150> | The page and the API |
| <http://localhost:15151/mcp> | The MCP server |
| <http://localhost:18180> | Keycloak, which signs in the user and the machine client |

The ports are bound to `127.0.0.1` only. PostgreSQL and RabbitMQ publish no port: only the containers of the project
reach them, so the database trusts every connection and the broker takes its built-in user, and no password is set.
The user, the password and the machine client's secret are the local-only values of the realm file in
[`keycloak`](../keycloak); they are public and must never be used outside this machine.

**What starts, in this order.** PostgreSQL, RabbitMQ and Keycloak, each until its health check passes. Then two
one-off jobs from the migrations image: `migrations` applies the schema, and `demo-data` loads the demo data. Each ends
with code 0; only then do the API, the MCP server and the worker start. When a job fails, Compose says which one and
the services stay unstarted. The worker has no HTTP endpoint and no health check.

**Stop, start again, start over.**

```sh
docker compose stop            # stops; the data stay
docker compose up              # starts again; both jobs run again and change nothing
docker compose down            # removes the containers; the data stay in the volume
docker compose down --volumes  # removes the data too; the next start loads the demo data again
docker compose run --rm demo-data   # loads the demo data alone, into an empty database
```

The project is named `tender-scout-demo` in [`docker-compose.yml`](../docker-compose.yml), whatever the folder of the
checkout, so these commands touch only its own containers, network and volume.

## What the demo holds

`demo-data` loads the invented tenders of [`evals/agent/notices.jsonl`](../evals/agent/notices.jsonl) and the passages of
[`synthetic-passages.jsonl`](../tests/TenderScout.IntegrationTests/Evaluation/synthetic-passages.jsonl): 10 tenders,
5 documents and 30 passages with their page numbers and their `bge-m3` vectors, the same data the evaluation uses. It
needs no model and no network, and loads all of it at once or nothing. Into a database that already holds exactly
these data it loads nothing and ends with code 0; into a database with other data it stops with code 1 and changes
nothing, and the way out is `docker compose down --volumes`.

The deadlines are fixed, from 11 December 2026 to 26 February 2027, and the clock is not moved. The calendar on the
start page shows the deadlines of this week and the next four, so it shows the demo's first deadlines from mid-November
2026; the inbox lists a tender until its deadline has passed. The documents are passages only: there is no original
PDF to open, and a citation names the document, the page and the stored passage.

## Models

The page, the inbox and the MCP notice tools work without a model. A question needs two: `bge-m3` to embed the
question and a chat model to answer it. Compose installs neither and pulls nothing; they run on
[Ollama](https://ollama.com/download), which needs its own disk space and memory:

```sh
ollama pull bge-m3
ollama pull SpeakLeash/bielik-4.5b-v3.0-instruct:Q8_0    # answers of the assistant, POST /ask
ollama pull SpeakLeash/bielik-11b-v3.0-instruct:Q4_K_M   # the agent, POST /agent; it calls tools
```

The containers reach Ollama at `http://host.docker.internal:11434`, which is the machine Docker runs on. Ollama
listens on `127.0.0.1` by default. Docker Desktop passes such connections on to the machine; on Linux the container
comes from the Docker network, so start Ollama there with `OLLAMA_HOST=0.0.0.0` and keep port 11434 closed to other
machines, or point the demo at another machine. Only the second way was tried for this guide, with Ollama on another
machine of the local network. Without a reachable model a question answers 503, and the page says so.

Every setting with a default can be changed in the shell or in a file `.env` next to `docker-compose.yml`, which git
ignores; [`.env.example`](../.env.example) lists them.

| Variable | Default | What |
| --- | --- | --- |
| `OLLAMA_ENDPOINT` | `http://host.docker.internal:11434` | Ollama for the embeddings, and for the chat unless `CHAT_ENDPOINT` is set |
| `CHAT_PROVIDER`, `CHAT_ENDPOINT` | `Ollama`, the Ollama address | The chat model's provider; `OpenRouter` for hosted chat |
| `CHAT_MODEL` | `SpeakLeash/bielik-4.5b-v3.0-instruct:Q8_0` | The model of `POST /ask` and the page's assistant |
| `AGENT_MODEL` | `SpeakLeash/bielik-11b-v3.0-instruct:Q4_K_M` | The model of `POST /agent`, which must call tools |
| `OPENROUTER_API_KEY` | empty | The key for hosted chat; only the API gets it |
| `INGESTION_ENABLED` | `false` | `true` fetches real notices from TED |
| `API_PORT`, `MCP_PORT`, `KEYCLOAK_PORT` | `15150`, `15151`, `18180` | The ports on `127.0.0.1` |

For example, with Ollama on another machine: `OLLAMA_ENDPOINT=http://<ollama-host>:11434 docker compose up`.
The embedding model stays `bge-m3`: the stored vectors were made with it, and another model would not match them.

**Hosted chat.** For [OpenRouter](agent-and-mcp.md#chat-through-openrouter), put these lines into `.env`; the embeddings stay on
Ollama. Without the key the API stops at start and names `OPENROUTER_API_KEY`. Every question then costs money.

```sh
CHAT_PROVIDER=OpenRouter
CHAT_ENDPOINT=https://openrouter.ai/api/v1
CHAT_MODEL=openai/gpt-4.1-mini
AGENT_MODEL=openai/gpt-4.1-mini
OPENROUTER_API_KEY=
```

**TED.** The worker does not fetch notices in the demo. With `INGESTION_ENABLED=true` it fetches real notices from
TED at start and every hour, and they mix with the invented ones. Such a database is no longer the demo: do not send
it to a hosted model or take screenshots of it, and run `docker compose down --volumes` to get the demo back. Once
real notices are stored, the next `docker compose up` stops at `demo-data`, which refuses a database with other data,
and the services do not start until `docker compose down --volumes` resets it.

## A question to try

In "Asystent", type `Jaka jest wysokość wadium?` and put `100101-2026` into the field of the tender's number. With the
two Bielik models above the answer was `Wysokość wadium wynosi 9 000,00 zł.`, citing page 10 of the document `syn-01`:
"11.1. Zamawiający żąda wniesienia wadium w wysokości 9 000,00 zł". The number may also be written in the question,
`Jaka jest wysokość wadium w 100101-2026?`, with the field empty; the answer then names the tender as taken from the
question. Without a number in either place the search runs over all five documents. The agent, `POST /agent`, finds
the tender by itself:

```sh
curl -X POST http://localhost:15150/agent -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{"question": "Jakiej wysokości wadium jest wymagane w postępowaniu 100101-2026?"}'
```

A token of the machine client comes from Keycloak, as in [Authentication](api-and-auth.md#authentication), at
`http://localhost:18180/realms/tender-scout/protocol/openid-connect/token`.

## Sign-in between the browser and the containers

The issuer in every token is the address the browser uses, `http://localhost:18180/realms/tender-scout`, and the API
and the MCP server check that address as `Authentication__Schemes__Bearer__Authority`. Inside a container `localhost`
is the container itself, so they fetch Keycloak's metadata and signing keys from
`http://keycloak:8080/...` (`MetadataAddress`) instead. Keycloak advertises the browser's address as the issuer
(`KC_HOSTNAME`) and the address a request came in on for the keys (`KC_HOSTNAME_BACKCHANNEL_DYNAMIC`). The checks of
the token are the same as everywhere else: signature, issuer, audience, lifetime and scopes.

The realm accepts the page at `http://localhost:15150/` and `http://localhost:25150/` besides the two addresses of a
local run. With another `API_PORT` the API starts, but sign-in on the page fails with an invalid redirect. With
another `KEYCLOAK_PORT` the issuer changes with it.

## When it does not work

- **A port is in use:** the start stops with "address already in use"; set `API_PORT`, `MCP_PORT` or `KEYCLOAK_PORT`.
- **Keycloak says "Invalid parameter: redirect_uri":** the page is open at a port the realm does not accept; use 15150
  or 25150.
- **401 with a token:** the token comes from another address than `http://localhost:<KEYCLOAK_PORT>`; ask for it at
  that address.
- **503 or "model unavailable":** Ollama is not reachable from the containers or lacks a model; see Models.
- **`demo-data` ended with code 1:** the database holds other data; `docker compose down --volumes` starts over.
- **Logs:** `docker compose logs api`, or any other service name.

## The check of CI

```sh
scripts/check-compose.sh
```

starts the same `docker-compose.yml` as a project of its own on the ports 25150, 25151 and 28180, without a model and
without TED, and checks: that the file is valid, the start order and both jobs, the demo data and their vectors, the
worker on the broker with polling off, `/health`, the page, a token of the machine client from the browser's issuer
accepted by the API and the MCP server, 401 without a token, 503 for questions without a model, and that a second load,
a stop and a start change no stored row. It removes its project with its volume at the end, also when a check fails;
it uses the images that are there and builds the missing ones. CI runs it after `scripts/check-images.sh`.

## Screenshots and the GIF

The showcase is taken from the running demo with a reachable model, on the invented data only, in a project of its
own so a running demo stays untouched:

```sh
API_PORT=25150 MCP_PORT=25151 KEYCLOAK_PORT=28180 docker compose -p tender-scout-shots up --build
```

The page is then at <http://localhost:25150>. The files go to `docs/images/demo/`: three screenshots (inbox,
calendar, an answer with citations) as PNG or WebP of at most 500 KiB each, and one GIF of asking the question above,
10–20 seconds, at most 1000 px wide, 10 frames per second and 5 MiB. Note in the pull request which model gave the
answer. `docker compose -p tender-scout-shots down --volumes` removes the project afterwards.
