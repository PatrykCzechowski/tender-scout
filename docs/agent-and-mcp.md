# MCP server, agent and hosted chat

## MCP server

`TenderScout.Mcp` serves three read-only tools over the [Model Context Protocol](https://modelcontextprotocol.io), so
an MCP client can search the stored notices and tender documents. The decisions behind it are in
[ADR-0005](adr/0005-mcp-server-shared-tools.md).

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
[Authentication](api-and-auth.md#authentication). The server offers no OAuth discovery, so a client is given the token as a header.

## Agent

`POST /agent` answers a question that one search cannot answer, for example about the documents of a tender that is
named only by its title. A chat model first calls the three tools of the [MCP server](#mcp-server), in process, to
gather notices and document passages. A second call then answers only from those sources and names the ones it used.
A model that calls no tool, or finds nothing, gets a refusal instead of an answer from its own knowledge. The
decisions and the model trial behind it are in [ADR-0006](adr/0006-agent-two-phases-limits.md).

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

## Chat through OpenRouter

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
token. Nothing in the API checks that the data are invented; so far only invented data have been sent, and the
service must not be pointed at real tender documents with this setting until that is decided. The evaluation command
refuses to answer its golden set with this setting; see [`evals/README.md`](../evals/README.md#running).
