# tender-scout

Tender Scout collects public tenders, stores their documents and answers questions about them with the page and
the passage each answer comes from. A web page shows the open tenders, their deadlines and an assistant; an MCP server
gives the same data to other agents.

## Showcase

<!-- Screenshots and a GIF of the compose demo go here, stored in docs/images/demo/: the inbox, the calendar of
deadlines, one answer with its citations, and a short GIF of asking that question. See "Run the demo" for how they
are taken. -->

_To be added: screenshots of the inbox, the calendar and an answer with citations, and a GIF of asking a question._

## Run the demo

The whole system runs with Docker Compose on invented data: ten tenders and the passages of five of their documents.
No .NET SDK and no Aspire AppHost are needed, only Docker with Compose v2 (`docker compose`). It was checked with
Compose 5.1.2 on macOS and with the Compose of GitHub's `ubuntu-latest` runner in CI.

```sh
docker compose up --build
```

The first start builds the four images of the [`Dockerfile`](Dockerfile) and pulls PostgreSQL with pgvector, RabbitMQ
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
[`keycloak`](keycloak); they are public and must never be used outside this machine.

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

The project is named `tender-scout-demo` in [`docker-compose.yml`](docker-compose.yml), whatever the folder of the
checkout, so these commands touch only its own containers, network and volume.

### What the demo holds

`demo-data` loads the invented tenders of [`evals/agent/notices.jsonl`](evals/agent/notices.jsonl) and the passages of
[`synthetic-passages.jsonl`](tests/TenderScout.IntegrationTests/Evaluation/synthetic-passages.jsonl): 10 tenders,
5 documents and 30 passages with their page numbers and their `bge-m3` vectors, the same data the evaluation uses. It
needs no model and no network, and loads all of it at once or nothing. Into a database that already holds exactly
these data it loads nothing and ends with code 0; into a database with other data it stops with code 1 and changes
nothing, and the way out is `docker compose down --volumes`.

The deadlines are fixed, from 11 December 2026 to 26 February 2027, and the clock is not moved. The calendar on the
start page shows the deadlines of this week and the next four, so it shows the demo's first deadlines from mid-November
2026; the inbox lists a tender until its deadline has passed. The documents are passages only: there is no original
PDF to open, and a citation names the document, the page and the stored passage.

### Models

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
machines, or point the demo at another machine. Only the second way was tried for this README, with Ollama on another
machine of the local network. Without a reachable model a question answers 503, and the page says so.

Every setting with a default can be changed in the shell or in a file `.env` next to `docker-compose.yml`, which git
ignores; [`.env.example`](.env.example) lists them.

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

**Hosted chat.** For [OpenRouter](#chat-through-openrouter), put these lines into `.env`; the embeddings stay on
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
it to a hosted model or take screenshots of it, and run `docker compose down --volumes` to get the demo back.

### A question to try

In "Asystent", type `Jaka jest wysokość wadium?` and put `100101-2026` into the field of the tender's number. With the
two Bielik models above the answer was `Wysokość wadium wynosi 9 000,00 zł.`, citing page 10 of the document `syn-01`:
"11.1. Zamawiający żąda wniesienia wadium w wysokości 9 000,00 zł". Without the number the search runs over all five
documents, and the same question with the number written in its text was once answered from another tender's page.
The agent, `POST /agent`, finds the tender by itself:

```sh
curl -X POST http://localhost:15150/agent -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{"question": "Jakiej wysokości wadium jest wymagane w postępowaniu 100101-2026?"}'
```

A token of the machine client comes from Keycloak, as in [Authentication](#authentication), at
`http://localhost:18180/realms/tender-scout/protocol/openid-connect/token`.

### Sign-in between the browser and the containers

The issuer in every token is the address the browser uses, `http://localhost:18180/realms/tender-scout`, and the API
and the MCP server check that address as `Authentication__Schemes__Bearer__Authority`. Inside a container `localhost`
is the container itself, so they fetch Keycloak's metadata and signing keys from
`http://keycloak:8080/...` (`MetadataAddress`) instead. Keycloak advertises the browser's address as the issuer
(`KC_HOSTNAME`) and the address a request came in on for the keys (`KC_HOSTNAME_BACKCHANNEL_DYNAMIC`). The checks of
the token are the same as everywhere else: signature, issuer, audience, lifetime and scopes.

The realm accepts the page at `http://localhost:15150/` and `http://localhost:25150/` besides the two addresses of a
local run. With another `API_PORT` the API starts, but sign-in on the page fails with an invalid redirect. With
another `KEYCLOAK_PORT` the issuer changes with it.

### When it does not work

- **A port is in use:** the start stops with "address already in use"; set `API_PORT`, `MCP_PORT` or `KEYCLOAK_PORT`.
- **Keycloak says "Invalid parameter: redirect_uri":** the page is open at a port the realm does not accept; use 15150
  or 25150.
- **401 with a token:** the token comes from another address than `http://localhost:<KEYCLOAK_PORT>`; ask for it at
  that address.
- **503 or "model unavailable":** Ollama is not reachable from the containers or lacks a model; see Models.
- **`demo-data` ended with code 1:** the database holds other data; `docker compose down --volumes` starts over.
- **Logs:** `docker compose logs api`, or any other service name.

### The check of CI

```sh
scripts/check-compose.sh
```

starts the same `docker-compose.yml` as a project of its own on the ports 25150, 25151 and 28180, without a model and
without TED, and checks: that the file is valid, the start order and both jobs, the demo data and their vectors, the
worker on the broker with polling off, `/health`, the page, a token of the machine client from the browser's issuer
accepted by the API and the MCP server, 401 without a token, 503 for questions without a model, and that a second load,
a stop and a start change no stored row. It removes its project with its volume at the end, also when a check fails;
it uses the images that are there and builds the missing ones. CI runs it after `scripts/check-images.sh`.

### Screenshots and the GIF

The showcase is taken from the running demo with a reachable model, on the invented data only, in a project of its
own so a running demo stays untouched:

```sh
API_PORT=25150 MCP_PORT=25151 KEYCLOAK_PORT=28180 docker compose -p tender-scout-shots up --build
```

The page is then at <http://localhost:25150>. The files go to `docs/images/demo/`: three screenshots (inbox,
calendar, an answer with citations) as PNG or WebP of at most 500 KiB each, and one GIF of asking the question above,
10–20 seconds, at most 1000 px wide, 10 frames per second and 5 MiB. Note in the pull request which model gave the
answer. `docker compose -p tender-scout-shots down --volumes` removes the project afterwards.

## Document pipeline

Tender documents are listed in a CSV manifest and read from a local folder. They go through RabbitMQ in two steps,
so each step can fail and be retried on its own:

1. `document-import` reads the manifest, checks each file against its SHA-256 and publishes one `DocumentUploaded`
   per valid row. It needs no database and no Ollama.
2. The Worker reads the PDF, splits it into chunks, stores the document with its chunks and publishes
   `ChunksCreated`. Scans and Word files are stored as skipped, with the reason.
3. The Worker embeds the chunks of that document with `bge-m3` and stores the vectors.

Both consumers can receive the same message again without duplicating anything: an unchanged file is not read
again, and only chunks without a vector are embedded. Running the import again is therefore always safe, and it is
also how to recover after a failure.

### Ollama

Chunks are embedded with `bge-m3` on [Ollama](https://ollama.com/download). Ollama is not part of the Aspire
AppHost, so it can run on another machine with a GPU. Tests and CI do not need it.

```sh
ollama pull bge-m3
```

The Worker uses `http://localhost:11434` by default and starts even when Ollama is not running. Set the real address
in user secrets or the environment variable `Embedding__Endpoint`, so it stays out of the repository:

```sh
dotnet user-secrets --project src/TenderScout.Worker set "Embedding:Endpoint" "http://<ollama-host>:11434"
```

#### Ollama on another machine

On the machine that runs Ollama, make it listen beyond localhost by setting this environment variable and
restarting Ollama:

```sh
OLLAMA_HOST=0.0.0.0:11434
```

Check the connection from the development machine before using the address:

```sh
curl http://<ollama-host>:11434/api/tags
```

Ollama has no login: anyone who can reach the port can use it. Keep port 11434 inside the local network, allow
only the machines that need it in the firewall, and do not forward it on the router.

### Running an import

1. Point the import at the documents, with absolute paths. The PDFs stay out of git.

   ```sh
   dotnet user-secrets --project src/TenderScout.DocumentImport set "DocumentImport:DocumentsFolder" "/absolute/path/to/documents"
   dotnet user-secrets --project src/TenderScout.DocumentImport set "DocumentImport:ManifestPath" "/absolute/path/to/manifest.csv"
   ```

2. Start the AppHost and wait until the `worker` resource is running. On a fresh broker the Worker creates the
   queues, so it must start before the first import. Otherwise the broker returns every message and the import
   rejects all rows instead of losing them silently.
3. Start `document-import` from the dashboard. Its log ends with the number of published, ignored and rejected
   rows; any rejected row makes it exit with a non-zero code.
4. The work is done when the `document-uploaded` and `chunks-created` queues are empty in the RabbitMQ management
   UI, which opens from the `messaging` resource in the dashboard.

Messages that still fail after the retries go to `document-uploaded_error` or `chunks-created_error`. A corrupted
or missing file goes there at once. If Ollama was off, start it and run `document-import` again: every document
is published again, and only what is missing is processed. The old messages in the error queues can then be purged.

### Searching

`TenderScout.DocumentSearch` is a development command that embeds a question with the same model and prints the
five best passages, with document, page and score. It combines vector search with full-text search, so it finds
both passages that match by meaning and exact strings such as CPV codes; the score is the fused rank, higher is
better. It runs outside the AppHost, so give it the database
connection string shown for the `tenderscout` resource in the dashboard, and the Ollama address:

```sh
dotnet user-secrets --project src/TenderScout.DocumentSearch set "ConnectionStrings:tenderscout" "<connection string>"
dotnet user-secrets --project src/TenderScout.DocumentSearch set "Embedding:Endpoint" "http://<ollama-host>:11434"
dotnet run --project src/TenderScout.DocumentSearch -- "Jaka jest wysokość wadium?"
```

It exits with a non-zero code when the question is missing or Ollama cannot be reached.

### Authentication

The API and the MCP server serve data only to a request with a JWT bearer token of the configured issuer, and each
endpoint and tool requires one of two scopes. For local runs the AppHost starts [Keycloak](https://www.keycloak.org)
with the realm of `keycloak/tender-scout-realm.json`. The decisions are in
[ADR-0008](docs/adr/0008-bearer-tokens-and-scopes.md).

| Route or tool | Scope |
| --- | --- |
| `GET /notices`; MCP tools `search_notices` and `get_notice` | `notices:read` |
| `POST /ask`, `POST /agent`; MCP tool `search_tender_documents` | `documents:ask` |
| `/mcp` itself | either one |
| `/health`, `/alive`, the files of the web page, `/openapi/v1.json`, `/scalar` (Development), `/sign-in-settings` | none |

A request without a token, or with one that is expired, wrongly signed or made for another issuer or audience, gets
401 with `WWW-Authenticate: Bearer`. A valid token without the scope gets 403. An MCP client with one scope sees and
can call only the tools of that scope.

Get a token for the machine client from the local Keycloak, with one scope or both, and call the API with it:

```sh
TOKEN=$(curl -s http://localhost:8180/realms/tender-scout/protocol/openid-connect/token \
  -d grant_type=client_credentials -d client_id=tender-scout-machine \
  -d client_secret=local-only-machine-secret -d "scope=notices:read documents:ask" \
  | sed 's/.*"access_token":"\([^"]*\)".*/\1/')
curl -H "Authorization: Bearer $TOKEN" http://localhost:5150/notices
```

A token lasts 5 minutes. The examples in the sections below send `$TOKEN`.

- **The realm is for local use only.** It has a public client for the web page (`tender-scout-web`, authorization
  code with PKCE), the machine client above and one test user, `tester` with the password `local-only-password`. The
  user, its password and the secret of the machine client are invented and written in the realm file. They protect
  only a Keycloak on a development machine: do not use them, or this realm file, anywhere else.
- **Keycloak** listens at `http://localhost:8180` and its admin console is at `http://localhost:8180/admin`, user
  `admin`. The admin password is not in the repository: the AppHost generates it on its first start and keeps it in
  its user secrets, under `Parameters:keycloak-admin-password` in the output of
  `dotnet user-secrets list --project src/TenderScout.AppHost`. Keycloak keeps nothing between starts, so a change of
  the realm file takes effect on the next start of the AppHost.
- **The evaluation and the document search command need no token.** They run the search and the agent in process
  and call no endpoint.

The services know nothing about Keycloak. They read the issuer and the audience from settings, so another identity
provider needs other values and no change of the code:

| Key | Meaning |
| --- | --- |
| `Authentication:Schemes:Bearer:Authority` | Address of the issuer, exactly as it is written in the tokens; the signing keys are read from its metadata |
| `Authentication:Schemes:Bearer:ValidAudiences:0` | The audience a token must have |
| `Authentication:Schemes:Bearer:RequireHttpsMetadata` | `false` only for a local issuer that speaks HTTP |
| `WebSignIn:ClientId` | API only: the client the web page signs in with |

The AppHost sets them for local runs. A service without the first two does not start and names the missing setting,
and no setting switches the checks off. The scopes are read from the `scope` claim; a provider that writes them
elsewhere, as Entra ID does with `scp`, needs a mapping at the provider or a change of one line.

### Asking questions

`POST /ask` answers a question from the stored documents, with the document and page of every passage the answer is
based on. It embeds the question with `bge-m3`, takes the five best passages from hybrid search and lets the chat
model answer only from them. When no passage is close enough, or the model cites none, it refuses. The prompt and
the refusal rule are in [ADR-0004](docs/adr/0004-answers-bielik-citations-refusal.md). It needs a token with the scope
`documents:ask`; see [Authentication](#authentication).

The API needs both models on Ollama, which may run on different machines (the chat model may instead be hosted, see
[Chat through OpenRouter](#chat-through-openrouter)):

```sh
ollama pull SpeakLeash/bielik-4.5b-v3.0-instruct:Q8_0
dotnet user-secrets --project src/TenderScout.Api set "Embedding:Endpoint" "http://<ollama-host>:11434"
dotnet user-secrets --project src/TenderScout.Api set "Chat:Endpoint" "http://<ollama-host>:11434"
```

Start the AppHost and use the address of the `api` resource from the dashboard. `publicationNumber` is optional and
limits the search to one tender:

```sh
curl -X POST http://<api-host>/ask -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{"question": "Jaka jest wysokość wadium?", "publicationNumber": "123456-2026"}'
```

```json
{
  "refused": false,
  "answer": "Wadium wynosi 10 000 zł.",
  "citations": [
    { "docId": "swz-001", "publicationNumber": "123456-2026", "page": 13, "passage": "<stored passage text>" }
  ]
}
```

A refusal is also 200, with `"refused": true`, the answer `Nie znalazłem odpowiedzi w dokumentach.` and no
citations. Invalid input gives 400 with problem details naming the field; an unreachable model gives 503. The API
starts with Ollama off. An answer takes several seconds; the example above is invented.

The API describes its endpoints in an OpenAPI document at `/openapi/v1.json`. In the Development environment
`/scalar` shows that document as a page where the endpoints can be tried.

### Listing notices

`GET /notices` lists the stored notices, soonest submission deadline first; notices without a deadline come last. It
takes the filters of the `search_notices` tool, with the same rules, plus a documents filter. All are optional:

| Parameter | Meaning |
| --- | --- |
| `query` | Words that must all occur in the title or the buyer name, 1 to 200 characters |
| `cpvPrefix` | First 2 to 8 digits of the main or any other CPV code, for example `72` |
| `deadlineFrom`, `deadlineTo` | Submission deadline range, `yyyy-MM-dd`, both inclusive |
| `hasDocuments` | `true`: only notices with a stored tender document; `false`: only those without |
| `limit` | 1 to 200; 50 when not given |

```sh
curl -H "Authorization: Bearer $TOKEN" "http://<api-host>/notices?cpvPrefix=72&deadlineFrom=2026-12-01&limit=20"
```

The reply is `{ "total": n, "notices": [...] }`: `total` counts every matching notice whatever the limit, and each
notice has its publication number, title, buyer, main and all CPV codes, publication date, deadline, TED link and
`hasDocuments`. Invalid parameters give 400 with problem details naming each of them. The endpoint only reads.

### Web page

The API also serves a web page at its root: `http://localhost:5150/` when the API runs alone with
`dotnet run --project src/TenderScout.Api`, or the address of the `api` resource in the AppHost dashboard. It has three
views, and the part of the address after `#` says which one is shown:

- **Pulpit** (`/`): counts of open tenders (deadline today or later), of those due within 7 days and of those with
  stored documents, and a calendar of submission deadlines for this week and the next four.
- **Skrzynka** (`#skrzynka`): open tenders by deadline, with a search, five filters (all, with documents, due within 7
  days, CPV 72, CPV 48) and a detail pane with the link to TED. For a tender with stored documents the pane leads to
  the assistant with that tender chosen.
- **Asystent** (`#asystent`): sends each question to `POST /ask`, optionally limited to one tender by its publication
  number, and shows the answer with its sources (document, tender, page and the passage), a refusal, or what went
  wrong. Each question is answered on its own and nothing is saved; the next question can be typed while one waits.

The notice views read `GET /notices`. The assistant needs both models, like `/ask`. The page is plain HTML, CSS and
JavaScript in `src/TenderScout.Api/wwwroot`, with no build step.

**Sign-in.** The page first shows a sign-in screen and calls no protected endpoint. "Zaloguj się" sends the browser to
the issuer from `/sign-in-settings` (the local Keycloak; test user `tester`, password `local-only-password`) with the
authorization code flow and PKCE, asking for both scopes, and every call to the API then carries the access token. The
token is kept in memory only, never in browser storage, so a reload signs the page out; there is no refresh token.
When the API answers 401, for example after the token's five minutes, the page goes to the issuer and back while the
issuer's session lasts, keeping the view and a typed question; a 401 right after signing in shows an error instead of
trying again. 403 shows that the account may not do this. "Wyloguj się" also ends the session at the issuer. The realm
accepts the page only at `http://localhost:5150/` and `https://localhost:7150/`, the addresses of the API in the
AppHost and with `dotnet run`, and at the two ports of the [demo](#run-the-demo), so sign-in does not work at any other
address of a container.

The fonts Geist, Geist Mono and Newsreader are stored with the page under the SIL Open Font License 1.1; their licence
texts and sources are in `src/TenderScout.Api/wwwroot/fonts`. The icons are part of this project.

### MCP server

`TenderScout.Mcp` serves three read-only tools over the [Model Context Protocol](https://modelcontextprotocol.io), so
an MCP client can search the stored notices and tender documents. The decisions behind it are in
[ADR-0005](docs/adr/0005-mcp-server-shared-tools.md).

| Tool | Parameters | Returns |
| --- | --- | --- |
| `search_notices` | `query` (words in the title or buyer name, 1-200 characters), `cpvPrefix` (2-8 digits), `deadlineFrom`, `deadlineTo` (dates as `yyyy-MM-dd`), `limit` (1-25, default 10); all optional | Notices, newest first |
| `get_notice` | `publicationNumber` (for example `123456-2026`), required | One notice with all its CPV codes, or `found: false` |
| `search_tender_documents` | `query` (1-1000 characters, required), `publicationNumber`, `limit` (1-10, default 5) | The best passages from hybrid search, with document, tender and page |

Write the text of a query in Polish, the language of the notices and documents. Invalid input gives an error result
that names the parameter. The document search needs `bge-m3` on Ollama, like the Worker; the two notice tools work
without it.

The server starts with the AppHost as the `mcp` resource, after the migrations. It speaks Streamable HTTP at `/mcp`:
take the address of the resource from the dashboard and add `/mcp`. Started alone with
`dotnet run --project src/TenderScout.Mcp`, it listens on `http://localhost:5160` and needs the connection string
`ConnectionStrings:tenderscout`. Set the Ollama address as for the other hosts:

```sh
dotnet user-secrets --project src/TenderScout.Mcp set "Embedding:Endpoint" "http://<ollama-host>:11434"
```

To try the tools, connect [MCP Inspector](https://modelcontextprotocol.io/docs/tools/inspector), which needs
Node.js 22.19 or newer:

```sh
npx @modelcontextprotocol/inspector --server-url http://<mcp-host>/mcp --transport http \
  --header "Authorization: Bearer $TOKEN"
```

Or call a tool from the shell. A value made of digits must be quoted as JSON, or the Inspector sends it as a number:

```sh
npx @modelcontextprotocol/inspector --cli http://<mcp-host>/mcp --transport http \
  --header "Authorization: Bearer $TOKEN" --method tools/list
npx @modelcontextprotocol/inspector --cli http://<mcp-host>/mcp --transport http \
  --header "Authorization: Bearer $TOKEN" \
  --method tools/call --tool-name search_notices --tool-arg 'cpvPrefix="72"' --tool-arg limit=5
```

Every request to `/mcp` needs a token, and each tool the scope of its data; see
[Authentication](#authentication). The server offers no OAuth discovery, so a client is given the token as a header.

### Agent

`POST /agent` answers a question that one search cannot answer, for example about the documents of a tender that is
named only by its title. A chat model first calls the three tools of the [MCP server](#mcp-server), in process, to
gather notices and document passages. A second call then answers only from those sources and names the ones it used.
A model that calls no tool, or finds nothing, gets a refusal instead of an answer from its own knowledge. The
decisions and the model trial behind it are in [ADR-0006](docs/adr/0006-agent-two-phases-limits.md).

The agent needs a chat model that can call tools, which the model of `/ask` cannot. It is named by its own setting
and uses the Ollama server and time limit of `Chat`:

```sh
ollama pull SpeakLeash/bielik-11b-v3.0-instruct:Q4_K_M
dotnet user-secrets --project src/TenderScout.Api set "Agent:Model" "SpeakLeash/bielik-11b-v3.0-instruct:Q4_K_M"
```

```sh
curl -X POST http://<api-host>/agent -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{"question": "Jakie wadium trzeba wnieść w przetargu na serwery dla urzędu gminy?"}'
```

```json
{
  "refused": false,
  "answer": "Wadium wynosi 10 000 zł.",
  "citations": [
    { "docId": "swz-001", "publicationNumber": "123456-2026", "page": 13, "passage": "<stored passage text>" }
  ],
  "notices": [
    {
      "publicationNumber": "123456-2026", "title": "Dostawa serwerów", "buyerName": "Urząd Gminy",
      "mainCpvCode": "48820000", "publicationDate": "2026-11-02", "submissionDeadline": "2026-12-01", "link": null
    }
  ],
  "limitReached": false,
  "toolCalls": [
    { "name": "search_notices", "arguments": { "query": "serwery urząd gminy" }, "durationMs": 12, "failed": false },
    { "name": "search_tender_documents", "arguments": { "query": "wadium", "publicationNumber": "123456-2026" }, "durationMs": 85, "failed": false }
  ]
}
```

`question` has 1 to 1000 characters. `citations` and `notices` are the sources the answer names, as the tools
returned them. `toolCalls` lists every call the model made, in order, also the failed ones. A refusal is 200 with
`"refused": true`, the answer of `/ask` and no sources. Invalid input gives 400 with problem details; an unreachable
chat or embedding model, or no `Agent:Model`, gives 503. The example above is invented.

The search is limited, and `limitReached` is `true` when it stopped at a limit; the answer then rests on what was
found until then:

| Limit | Value |
| --- | --- |
| Requests to the model while gathering | 4 |
| Tool calls in one run | 12 |
| Time for gathering | 120 seconds |
| Sources given to the answering call | the first 25 notices and the first 12 passages |

With the model above on a GPU with 8 GB of memory a question takes 10 to 45 seconds. The endpoint needs a token with
the scope `documents:ask`, like `/ask`; the agent's own tool calls are not checked against the caller's scopes.

### Chat through OpenRouter

The chat model can also be a hosted one, through [OpenRouter](https://openrouter.ai) and its OpenAI-compatible API.
`Chat:Provider` chooses it for `/ask`, for both phases of `/agent` and for the evaluation command; there is no switch
per endpoint. Ollama stays the default, and the only choice that keeps every question and passage on your machines.
The embeddings stay on Ollama either way: the stored vectors and the query vectors of the search are `bge-m3`, so
document search still needs the Ollama server. Nothing is re-embedded when the provider changes.

The key is read from `OPENROUTER_API_KEY`, a user secret or an environment variable of that exact name; there is no
other place for it. With OpenRouter chosen and no key the API stops before it listens, and the evaluation command
stops before it loads anything, both naming `OPENROUTER_API_KEY`. The key is only sent to OpenRouter, never logged,
and never put in an image; the incoming bearer token of a caller is not passed on.

```sh
dotnet user-secrets --project src/TenderScout.Api set "Chat:Provider" "OpenRouter"
dotnet user-secrets --project src/TenderScout.Api set "Chat:Endpoint" "https://openrouter.ai/api/v1"
dotnet user-secrets --project src/TenderScout.Api set "Chat:Model" "openai/gpt-4.1-mini"
dotnet user-secrets --project src/TenderScout.Api set "Agent:Model" "openai/gpt-4.1-mini"
dotnet user-secrets --project src/TenderScout.Api set "OPENROUTER_API_KEY" "<your OpenRouter key>"
dotnet user-secrets --project src/TenderScout.Api set "Embedding:Endpoint" "http://<ollama-host>:11434"
```

Both model settings are needed: `/ask` uses `Chat:Model` and the agent `Agent:Model`, as with Ollama. A provider that
cannot be reached, a rejected key, no credit or a rate limit gives the same 503 as an unreachable Ollama, with the
HTTP status in the log and nothing of the provider's answer. A failed request is not repeated, because each attempt is
paid. Requests are limited by `Chat:RequestTimeout` as before.

**What leaves the machine with OpenRouter chosen.** OpenRouter, and the company that runs the model behind it, receive
the question, the instructions of the prompts with today's date, the names, descriptions and parameters of the tools,
the model's own calls and the whole conversation of a run, the notices and passages the tools return, and for `/ask`
the five passages it answers from. They do not receive the PDF files, vectors, database credentials or the caller's
token. Nothing checks that the data are invented; so far only invented data have been sent, and the service must
not be pointed at real tender documents with this setting until that is decided.

### Evaluation

`dotnet run --project src/TenderScout.Evaluation` measures search and answers on a private golden set of questions
about the indexed documents and writes `artifacts/eval/<UTC time>.json`; `--retrieval-only` skips the chat models.
The golden set format, the metric definitions and the CI gate are described in [evals/README.md](evals/README.md).

Run on 2026-11-10: 27 documents, 3512 chunks, embeddings `bge-m3`, answers
`SpeakLeash/bielik-4.5b-v3.0-instruct:Q8_0`, judge `SpeakLeash/bielik-11b-v3.0-instruct:Q4_K_M`. Golden set: 40
questions (32 answerable, 12 of them paraphrased; 8 without an answer), labels: 40 auto, 0 verified. Every question
names its tender, and search and `/ask` were limited to it.

The run used the search and answering code as in commit `8c914b2`, where the refusal threshold was 0.55. The results
file records the commit the command was built from, which cannot show whether the working tree was unchanged, so
treat these numbers as a first measurement. They will be re-run from a clean checkout once the labels are verified.

| Search | All (32) recall@5 | All MRR@10 | Paraphrased (12) recall@5 | Literal (20) recall@5 |
| --- | ---: | ---: | ---: | ---: |
| Vector | 0.50 | 0.32 | 0.42 | 0.55 |
| Full-text | 0.41 | 0.13 | 0.00 | 0.65 |
| Hybrid | 0.53 | 0.28 | 0.00 | 0.85 |

| Answers | Value |
| --- | ---: |
| Citation hit rate | 0.46 of 26 answered |
| Citation precision | 0.33 of 36 citations |
| Faithfulness (judge, 0 invalid replies) | 0.73 of 30 answered |
| Correct refusals | 0.50 of 8 |
| False refusals | 0.19 of 32 |

Share of questions whose nearest chunk is below each threshold, by similarity alone (provisional):

| Threshold | False refusals (of 32) | Correct refusals (of 8) |
| ---: | ---: | ---: |
| 0.30-0.45 | 0.00 | 0.00 |
| 0.50 | 0.00 | 0.12 |
| 0.55 | 0.00 | 0.25 |
| 0.60 | 0.28 | 0.75 |

- **The refusal threshold stays 0.45.** By this table 0.55 is the highest threshold with at most 10% false refusals,
  but the run could not confirm it: the number exception applied to all 40 questions, so no threshold refused
  anything, and the answer metrics above do not depend on it. The lowest answerable question scored 0.56, which
  leaves 0.55 almost no margin. The answerable questions' nearest chunks scored 0.56-0.74, those without an answer
  0.49-0.62, so similarity alone separates them poorly. The threshold will be calibrated after the number exception
  is narrowed and the labels are verified.
- **Hybrid search loses the 5 paraphrased questions** that vector search finds: full-text finds none of their
  answers, and fusion ranks chunks found by both searches above an answer found by one. It wins on literal questions
  (0.85 against 0.55).
- **Ranking passages with a number from the question first hurts here.** Every question contains its publication
  number, which ends in the year, so the rule promotes any passage with the year. Without it full-text gets recall@5
  0.53 and MRR@10 0.38, hybrid 0.56 and 0.35.
- **The number exception to the early refusal applied to all 40 questions** for the same reason, so the threshold
  never ran.
- Without the tender filter recall@5 is 0.22 (vector), 0.03 (full-text) and 0.12 (hybrid).
- Of the 10 refusals by the model, 9 had no citation and 1 cited a page number instead of a passage number.
- The judge is from the same family as the answering model.

`dotnet run --project src/TenderScout.Evaluation -- --agent-cases` measures which tools the agent of `POST /agent`
calls: 12 invented cases name the calls a question needs and their key arguments, each case is run 3 times against
the model of `Agent:Model` on an empty database the command fills with invented notices and passages, and the tool
calls are scored. Answers are not judged. The cases, the scoring rules and the results file
`artifacts/eval/agent-<UTC time>.json` are described in [evals/README.md](evals/README.md#agent-tool-choices).

Two runs with the same cases, invented notices and passages, reference date 2026-12-07, agent model
`SpeakLeash/bielik-11b-v3.0-instruct:Q4_K_M`, embeddings `bge-m3`, Ollama version, limits and number of runs: 12
cases, 3 runs each (36 case runs), on an empty database. Both runs were complete and no case run ended with an
error. The first is from 2026-11-13, commit `80c2249`. The second is from 2026-11-14, commit `2736bf6`, after the
instructions of the gathering phase were changed; it is the one measured run made after that change.

| Number | 2026-11-13, `80c2249`: all runs | Per run, least-most | 2026-11-14, `2736bf6`: all runs | Per run, least-most |
| --- | ---: | ---: | ---: | ---: |
| Expected tool chosen | 1.00 (42/42) | 1.00-1.00 | 1.00 (42/42) | 1.00-1.00 |
| Arguments matched | 0.50 (21/42) | 0.50-0.50 | 0.71 (30/42) | 0.71-0.71 |
| Cases passed | 0.58 (21/36) | 0.58-0.58 | 0.67 (24/36) | 0.67-0.67 |
| Steps per case run: mean, least, most | 1.17, 0, 2 | | 1.17, 0, 2 | |
| Any gathering limit (case runs) | 0 | | 0 | |
| Runs at the call cap of 12, at most | 0 | | 0 | |
| Unexpected calls (of them to tools that do not exist) | 0 (0) | | 0 (0) | |
| Failed calls | 12 | | 12 | |
| Most tool calls in a run (cap 12) | 2 | | 2 | |
| Errored case runs | 0 | | 0 | |
| Runs passed, the four cases that need a notice search and then a document search | 0 of 12 | | 0 of 12 | |
| Runs passed, the seven cases that passed in the first run | 21 of 21 | | 21 of 21 | |

The data are invented and small, and answers are not judged: the numbers measure this model with this prompt on 12
cases, not models in general.

The first run:

- **The agent always called the expected tools and no other.** The cases that need the documents of a tender given
  by its number passed in 9 of 9 runs, the cases that need no tool in 6 of 6, the cases that need only notices in 6
  of 9.
- **The four cases that need a notice search and then a document search failed in all 12 runs.** The model asked
  for both calls in one reply, before it had the result of the first, and wrote a placeholder where the publication
  number belongs. The tool rejected those 12 calls, and the model stopped instead of searching again. In two of the
  four cases it also searched the notices by a CPV code where the question names the subject in words.
- **"Next week" was one day too long** in the one case that asks for it, in all three runs.
- **No run came near a limit:** at most 2 tool calls against the cap of 12. The cap did not get in the way on these
  cases, but the runs that should have gone on stopped early, so this does not show that 12 is the right number.
- The three runs of a case scored the same. Their calls were not identical: the order of the arguments, the value
  of the optional `limit` and the wording of the placeholder differed between runs. None of this changed a score.

Between the runs only the instructions of the gathering phase changed. They now state the dates of this week and of
next week, from Monday to Sunday. They tell the model to search notices with one or two distinctive words and to
give a CPV code only when the question gives one, to make a call that needs a value from the result of another call
only after that result has come, and to correct a call that a tool rejected instead of stopping. The cases, their
expected calls and the scoring are the same files.

The second run:

- **The target of the change was missed.** The four cases that need two searches were to pass in at least 9 of
  their 12 runs and passed in 0 of 12, as before. In all 12 runs the document search again carried a placeholder
  where the publication number belongs, so the model wrote it before it had the result of the notice search. The
  tool rejected it, and the model did not call again.
- **The notice search of those four cases had the expected arguments in 12 of 12 runs,** against 6 of 12 in the
  first run: none searched by a CPV code that the question does not give. These runs still do not pass, because
  their document search failed.
- **"Next week" was right in 3 of 3 runs** of the one case that asks for it. It is the only change that one case
  isolates: the model now reads the dates instead of counting them.
- **No case that passed in the first run failed:** 21 of 21 runs.
- **The cap of 12 is still not confirmed:** again at most 2 tool calls in a run, and the runs that should have gone
  on stopped early.
- **The instructions were changed after the first run on these same cases,** so the second column shows what the
  change does on them, not on other questions. Stricter wordings, among them one tool call per reply, were tried on
  other invented questions; the model still sent both calls in one reply, so they were not measured here and are
  not in the code.
- The three runs of a case scored the same in this run too.

**A hosted model on the same cases.** One run on 2026-11-14 from commit `f1a0795`, with chat through OpenRouter and
`openai/gpt-4.1-mini` as `Agent:Model`. Everything else was the same as in the second local run: the 12 cases with
their expected calls, the invented notices and passages with their stored vectors (same SHA-256 sums), reference date
2026-12-07, `bge-m3` on the same Ollama server (0.40.16) for the query vectors, the prompts and tool descriptions as
they are after the change above, temperature 0, at most 512 tokens a reply, the limits of 4 requests, 12 tool calls
and 120 seconds, and 3 runs a case on an empty database. The run was complete and no case run ended with an error.
It is the only measured hosted run.

| Number | Local: 2026-11-14, `2736bf6`, Bielik 11B v3 `Q4_K_M` | Hosted: 2026-11-14, `f1a0795`, `openai/gpt-4.1-mini` |
| --- | ---: | ---: |
| Expected tool chosen | 1.00 (42/42) | 0.93 (39/42) |
| Arguments matched | 0.71 (30/42) | 0.93 (39/42) |
| Cases passed | 0.67 (24/36) | 0.92 (33/36) |
| Cases passed per run, least-most | 0.67-0.67 | 0.92-0.92 |
| Steps per case run: mean, least, most | 1.17, 0, 2 | 1.08, 0, 2 |
| Any gathering limit (case runs) | 0 | 0 |
| Runs at the call cap of 12, at most | 0 | 0 |
| Unexpected calls (of them to tools that do not exist) | 0 (0) | 0 (0) |
| Failed calls | 12 | 0 |
| Most tool calls in a run (cap 12) | 2 | 2 |
| Errored case runs | 0 | 0 |
| Runs passed, the four cases that need a notice search and then a document search | 0 of 12 | 9 of 12 |
| Runs passed, the seven cases that passed in the first run | 21 of 21 | 21 of 21 |
| Runs passed, "next week" | 3 of 3 | 3 of 3 |

The first local run of 2026-11-13 (21 of 36) is above; the column here is the latest local result.

- **The hosted model waited for the notice before it searched the documents.** In 9 of the 12 runs of the two-step
  cases it searched the notices, read the publication number from the result and searched that tender's documents in
  its next reply, without a placeholder and without a failed call. The target of 9 of 12 that the local model missed
  is reached on this model, with no case that passed before failing.
- **The three misses are one case.** In all three runs of that case the model searched the notices with four words of
  the question; a notice search needs every word in the title or the buyer, found nothing, and the agent refused
  without searching the documents. The scoring counts the missing document search as a missed tool and a missed
  argument, which is why "expected tool chosen" is lower than for the local model.
- **The runs of a case scored the same.** In two cases the calls differed between runs (an optional `limit`, the
  words of a notice search); no score changed.
- **What this does not show.** The prompts were tuned on the local model; the cases are 12 invented ones, run 3 times;
  the scores are of the tool calls, not of the answers; the hosted model has a far larger context and another
  tokenizer, and OpenRouter may serve it from more than one upstream provider. It is no ranking of the two models in
  general, and the cap of 12 tool calls is still not confirmed: no run made more than 2.
- **Cost.** The run made about 100 requests to the model and cost about USD 0.04 by OpenRouter's usage of the key;
  the whole change, with the trials, about USD 0.06.

CI runs the gate on 20 invented questions over 30 invented passages with stored vectors, without Ollama. It checks
that the searches still find the answers; [evals/README.md](evals/README.md#ci-gate) lists the tests that guard the
fusion and the stop-word list:

| Search | Recall@5 | Threshold | MRR@10 | Threshold |
| --- | ---: | ---: | ---: | ---: |
| Vector | 0.95 | 0.90 | 0.57 | 0.50 |
| Full-text | 0.60 | 0.55 | 0.43 | 0.35 |
| Hybrid | 0.95 | 0.90 | 0.53 | 0.45 |

### Settings

| Key | Default | Used by | Meaning |
| --- | --- | --- | --- |
| `DocumentImport:DocumentsFolder` | none, required | import | Folder the manifest paths point into |
| `DocumentImport:ManifestPath` | none, required | import | The manifest CSV |
| `DocumentImport:DocKinds` | `swz` | import | Manifest `doc_kind` values to import |
| `DocumentChunking:ChunkSize` | `1200` | Worker | Maximum chunk length in characters |
| `DocumentChunking:ChunkOverlap` | `150` | Worker | Text a chunk repeats from the previous one |
| `Embedding:Endpoint` | `http://localhost:11434` in the Worker, API and MCP server | Worker, search, API, MCP server, evaluation | Address of the Ollama server |
| `Embedding:Model` | `bge-m3` | Worker, search, API, MCP server | Model name, stored with every vector |
| `Embedding:BatchSize` | `32` | Worker | Chunks per request; each batch is saved separately |
| `Embedding:RequestTimeout` | `00:02:00` | Worker, search, API, MCP server | Time limit of one attempt |
| `Chat:Provider` | `Ollama` | API, evaluation | `Ollama` or `OpenRouter`, for `/ask`, `/agent` and the evaluation; see [Chat through OpenRouter](#chat-through-openrouter) |
| `Chat:Endpoint` | `http://localhost:11434` | API, evaluation | Address of the Ollama server with the chat model, or `https://openrouter.ai/api/v1` |
| `Chat:Model` | `SpeakLeash/bielik-4.5b-v3.0-instruct:Q8_0` | API, evaluation | Chat model that writes the answers of `/ask` |
| `OPENROUTER_API_KEY` | none; required with `OpenRouter` | API, evaluation | The OpenRouter key, as a user secret or an environment variable only |
| `Chat:RequestTimeout` | `00:01:30` | API, evaluation | Time limit of one request to the chat model |
| `Agent:Model` | none; `/agent` answers 503 without it | API, evaluation | Chat model of the agent; it must be able to call tools |
| `Authentication:Schemes:Bearer:Authority` | none, required; set by the AppHost | API, MCP server | Address of the token issuer |
| `Authentication:Schemes:Bearer:ValidAudiences:0` | none, required; set by the AppHost | API, MCP server | The audience a token must have |
| `Authentication:Schemes:Bearer:RequireHttpsMetadata` | `true` | API, MCP server | `false` only for a local issuer that speaks HTTP |
| `WebSignIn:ClientId` | none; set by the AppHost | API | The client the web page signs in with |
| `Evaluation:GoldenSetPath` | none, required | evaluation | The golden set JSONL file |
| `Evaluation:JudgeModel` | `SpeakLeash/bielik-11b-v3.0-instruct:Q4_K_M` | evaluation | Model that judges the answers |

### Local result

Run on 2026-11-09 with the manifest of 68 rows, of which the 31 main tender specifications (`swz`) are in scope:

| Outcome | Documents |
| --- | --- |
| Indexed | 27 |
| Skipped: PDF without a text layer (scan) | 1 |
| Skipped: Word file (DOC or DOCX) | 3 |
| Rejected | 0 |

The 27 indexed documents gave 3512 chunks. Starting from empty tables, the queue embedded all 3512 with `bge-m3`.
A second import changed nothing and sent no request to Ollama. The other 37 manifest rows (answers to bidders' questions and amendments) are ignored for now.

## Traces and metrics

Every service exports traces, metrics and logs with OpenTelemetry. Started from the AppHost
(`dotnet run --project src/TenderScout.AppHost`), they go to the Aspire dashboard, whose address the AppHost prints;
its **Traces** tab shows one trace per question and per document, and **Metrics** the model calls. Outside the AppHost
a service exports only when `OTEL_EXPORTER_OTLP_ENDPOINT` is set; without it nothing leaves the process, and a
dashboard that is down does not stop a service. Questions need the models and a token as described above.

A question is one trace, from the HTTP request to the last model call:

```text
POST /ask
├── retrieval                         the question's vector and both searches
│   ├── embeddings bge-m3
│   └── postgresql …
└── chat <Chat:Model>                 only when relevant passages were found

POST /agent
├── orchestrate_tools                 the gathering phase
│   ├── chat <Agent:Model>            one span per request to the model
│   ├── execute_tool search_tender_documents
│   │   └── embeddings bge-m3
│   └── chat <Agent:Model>
└── chat <Agent:Model>                the answer, when sources were found
```

A tool runs after the model turn that asked for it, so its span is a sibling of that turn, not a child. A tool call
that a tool rejects ends with an error status; a call over a limit has no span, because it does not run.

A document is one trace too, across the queue: the import's `DocumentUploaded send` span is the root, and the Worker
continues it from the message's headers (MassTransit's own propagation) through `document-uploaded receive/process`,
`ChunksCreated send`, `chunks-created receive/process` and an `embeddings bge-m3` span per batch.

The model spans come from Microsoft.Extensions.AI 10.11, which follows the GenAI semantic conventions v1.41:
`gen_ai.operation.name`, `gen_ai.request.model`, `gen_ai.response.model`, `gen_ai.provider.name` (`ollama`, or `openai`
for the OpenAI-compatible client of OpenRouter, with `server.address` `openrouter.ai`; it does not say which company
ran the model) and `gen_ai.usage.input_tokens`/`output_tokens` when the provider reports them. Counts that are not
reported are left out, not set to 0; embeddings have input tokens only. The newer drafts of the conventions rename
some of this, for example split token metrics; the names here are those of the installed package.

| Metric | Unit | What it holds |
| --- | --- | --- |
| `gen_ai.client.operation.duration` | s | One value per call to a chat or embedding model, also a failed one |
| `gen_ai.client.token.usage` | {token} | The reported tokens of each call, by `gen_ai.token.type` `input` or `output` |

Both are in the meter `Experimental.Microsoft.Extensions.AI`, by operation, model, provider and server; no question,
document or user is a dimension. The spans' sources are `TenderScout` (the search of `/ask`),
`Experimental.Microsoft.Extensions.AI` and `MassTransit`, besides ASP.NET Core, HttpClient and Npgsql.

**What the telemetry does not hold.** No prompt, question, passage, answer, tool argument or tool result: the
recording of message texts is switched off in code, also when `OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT` is
`true`. A failed span keeps its error status and type but not the text of its exception. One exception remains:
when a Worker consumer fails, MassTransit adds an `exception` event with the exception's message and stack trace,
which cannot be changed after it is created; the message of a failed document may name its path. Every trace is
recorded; sampling for production is not set here.

## Container images

The four services that get deployed build into container images from one `Dockerfile`: the API, the MCP server, the
worker and the database migrations. Why it looks the way it does is in
[ADR-0007](docs/adr/0007-container-images.md). Build each image from the repository root by naming its stage:

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
[Chat through OpenRouter](#chat-through-openrouter) with `--env`, and pass the key by name from your shell, as
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

The images also run together, with the demo data, as described in [Run the demo](#run-the-demo). Not there yet: a
registry and a deployment.

## Architecture decisions

Key technical decisions are recorded as ADRs in [`docs/adr`](docs/adr/README.md).
