# ADR-0005: MCP server over Streamable HTTP with tools defined once

## Status

Accepted — 2026-11-13

## Context

Notices and tender documents were reachable only through `POST /ask` and command-line
tools. A model that should decide by itself what to look up needs tools: search the
notices, read one notice, search the documents. The Model Context Protocol (MCP) lets
any MCP client use such tools, and the agent planned for `POST /agent` needs the same
three.

Constraints: the tools only read, CI has no Ollama, authentication comes later, and
the same tool must not exist in two versions for the MCP server and for the agent.

## Options considered

| Question | **Chosen** | Rejected |
| --- | --- | --- |
| Transport | Streamable HTTP at `/mcp` | stdio: the server would have to be started by each client and could not run as an AppHost resource. Legacy SSE: superseded |
| Sessions | Stateless, the SDK's default | Sessions: needed only for sampling, elicitation or server-to-client requests, which these tools do not use |
| Where the tools live | One class in `TenderScout.Infrastructure`, which every host references | A project only for the tools; definitions in the MCP host, which the API would then have to reference or copy |
| How the agent calls them | In process, the same methods | As an MCP client of its own server: one more thing that must run, and a network hop for a call inside one solution |
| Result form | Structured content with an output schema, and the same JSON as text | Text only: every client and the agent would parse it |
| Not found | A normal result with `found: false` | An error result: a model reads it as a failed call |

## Decision

- **`TenderScout.Mcp` is a host and nothing else.** It registers the database, the
  embedding client and the MCP server from `ModelContextProtocol.AspNetCore`, and maps
  `/mcp` next to `/health` and `/alive`. No stdio and no legacy SSE endpoint.
- **`TenderTools` defines the three tools once**: `search_notices`, `get_notice` and
  `search_tender_documents`. Name, description, parameters, validation and result of
  each are there and nowhere else. The class carries the SDK's tool attributes, so
  `TenderScout.Infrastructure` references `ModelContextProtocol.Core`, the SDK's
  package without hosting.
- **The agent will call the same methods in process.** They are ordinary methods with
  described parameters, so `AIFunctionFactory` can turn them into functions for an
  `IChatClient` under the same names. This was tried on a throwaway program, not built.
- **Notice search is a query class, not a tool detail.** `NoticeQueries` filters by
  words in the title or buyer name (`ILIKE`, part of a word), CPV code prefix and
  deadline range, with no migration and no index. A list endpoint can use it later.
- **Document search is the existing hybrid search**, with the existing embedding
  client and without the refusal threshold of `/ask`: the tool returns passages and
  the caller judges them.
- **Results.** A successful call returns an object as structured content, described
  by an output schema, and the same object as JSON text. Every field is always
  present: a missing value is `null`, an empty list is `[]`. Only the number of items
  is limited: 25 notices, 10 passages.
- **Errors.** Invalid input and an unreachable embedding model give an error result
  (`isError: true`) whose text names the parameter and the rule, so a model can
  correct the call; they are thrown as `McpException`, the one exception whose
  message the SDK passes on. Any other exception gives the SDK's generic message with
  no internal detail and is logged on the server. An unknown tool is a protocol error.
- **Limits, defaults and the path are constants.** The tools add no retries, time
  limits or cache.
- **No authentication yet.** The server is for the development machine until
  authentication and scopes per tool are added.

## Consequences

### Easier

- Any MCP client, including MCP Inspector, can list and call the tools; nothing is
  specific to one client.
- A contract test compares the listed tools with a hand-written file, so a renamed
  tool, a changed parameter, default or description fails the build.
- Tests run the host in memory and call it through an MCP client over HTTP, with a
  fake embedding generator and no Ollama.
- Stateless requests need no session store and work behind any load balancer.

### Costs and risks

- **`TenderScout.Infrastructure` depends on an MCP package** for three attributes and
  one exception type. Moving the tools to the host would remove it, at the price of a
  second definition for the agent.
- **Notice search scans the table.** `ILIKE '%word%'` cannot use the existing indexes.
  Fine for thousands of notices; a trigram index is the next step if it gets slow.
- **A number where text is expected is rejected with the generic message.** A model
  that sends `cpvPrefix` as the number 72 instead of the text "72" is not told why.
  The same holds for a missing required argument and for `null` as `limit`.
- **The SDK logs every tool exception as an error**, also invalid input. The log shows
  bad calls of clients next to real failures.
- **Document search needs the embedding model.** With Ollama off it returns an error
  result; the two notice tools keep working.
- **Descriptions are part of the behaviour.** A model chooses a tool and fills its
  arguments from them, so a change is a change of the contract and must be evaluated.
