# tender-scout

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

The API needs both models on Ollama, which may run on different machines:

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
AppHost and with `dotnet run`, so sign-in does not work at the address of a container.

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

Run on 2026-11-13 from commit `80c2249`: agent model `SpeakLeash/bielik-11b-v3.0-instruct:Q4_K_M`, embeddings
`bge-m3`, 12 cases, 3 runs each (36 case runs), reference date 2026-12-07. The run was complete and no case run
ended with an error.

| Number | All runs | Per run, least-most |
| --- | ---: | ---: |
| Expected tool chosen | 1.00 (42/42) | 1.00-1.00 |
| Arguments matched | 0.50 (21/42) | 0.50-0.50 |
| Cases passed | 0.58 (21/36) | 0.58-0.58 |
| Steps per case run: mean, least, most | 1.17, 0, 2 | |
| Any gathering limit (case runs) | 0 | |
| Runs at the call cap of 12, at most | 0 | |
| Unexpected calls (of them to tools that do not exist) | 0 (0) | |
| Failed calls | 12 | |
| Most tool calls in a run (cap 12) | 2 | |
| Errored case runs | 0 | |

The data are invented and small, and answers are not judged: the numbers measure this model with this prompt on 12
cases, not models in general.

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
| `Chat:Endpoint` | `http://localhost:11434` | API, evaluation | Address of the Ollama server with the chat model |
| `Chat:Model` | `SpeakLeash/bielik-4.5b-v3.0-instruct:Q8_0` | API, evaluation | Chat model that writes the answers |
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
| `tender-scout-api` | `ConnectionStrings__tenderscout`, `Authentication__Schemes__Bearer__Authority`, `Authentication__Schemes__Bearer__ValidAudiences__0` | `WebSignIn__ClientId`, `Embedding__Endpoint`, `Chat__Endpoint`, `Agent__Model`, `OTEL_EXPORTER_OTLP_ENDPOINT` |
| `tender-scout-mcp` | `ConnectionStrings__tenderscout`, `Authentication__Schemes__Bearer__Authority`, `Authentication__Schemes__Bearer__ValidAudiences__0` | `Embedding__Endpoint`, `OTEL_EXPORTER_OTLP_ENDPOINT` |
| `tender-scout-worker` | `ConnectionStrings__tenderscout`, `ConnectionStrings__messaging` (the RabbitMQ address as a URI) | `Embedding__Endpoint`, `Ingestion__*`, `OTEL_EXPORTER_OTLP_ENDPOINT` |

Run the migrations first and once. The other three expect the migrated schema and do not wait for it:

```sh
docker run --rm --env "ConnectionStrings__tenderscout=<connection string>" tender-scout-migrations
docker run --init --publish 8080:8080 --env "ConnectionStrings__tenderscout=<connection string>" \
  --env "Authentication__Schemes__Bearer__Authority=<issuer address>" \
  --env "Authentication__Schemes__Bearer__ValidAudiences__0=<audience>" tender-scout-api
```

The API then serves the page at `http://localhost:8080/` and answers `/health`.

- **The issuer address is the one in the tokens.** A container reaches the issuer under that same address, or the
  tokens are refused; for an issuer over HTTP add `Authentication__Schemes__Bearer__RequireHttpsMetadata=false`.

- **No model is needed to start.** Without a model address the API and the MCP server are healthy, and the page,
  `/notices` and the notice tools work. `/ask` and `/agent` answer 503, and the document search tool reports that the
  model is unavailable.
- **The worker ingests notices from TED as it is.** It reads a document from the path in its message, which is a path
  on the machine that ran the import, so it processes documents only when that folder is mounted at the same path.
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

Not there yet: a registry, a compose file and a deployment.

## Architecture decisions

Key technical decisions are recorded as ADRs in [`docs/adr`](docs/adr/README.md).
