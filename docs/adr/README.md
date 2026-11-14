# Architecture Decision Records

Significant technical decisions are recorded here, one file per decision, numbered
in order (`NNNN-short-title.md`). Each record has the sections **Status**,
**Context**, **Options considered**, **Decision** and **Consequences**.

A changed decision gets a new ADR that supersedes the old one, whose status is
updated. Small extensions and clarifications are written into the accepted record
instead, with a dated "amended" note in its status.

| ADR | Title | Status |
| --- | --- | --- |
| [0001](0001-dotnet-and-aspire.md) | .NET 10 with .NET Aspire for local orchestration | Accepted |
| [0002](0002-messaging-masstransit-rabbitmq.md) | MassTransit v8 on RabbitMQ for notice ingestion, with idempotent consumers | Accepted |
| [0003](0003-embeddings-bge-m3-ollama-pgvector.md) | `bge-m3` on Ollama, stored in pgvector with an HNSW cosine index | Accepted |
| [0004](0004-answers-bielik-citations-refusal.md) | Answers from Bielik 4.5B with numbered-passage citations and a refusal rule | Accepted |
| [0005](0005-mcp-server-shared-tools.md) | MCP server over Streamable HTTP with tools defined once | Accepted |
| [0006](0006-agent-two-phases-limits.md) | Agent in two phases: tool calls gather sources, one call answers from them | Accepted |
| [0007](0007-container-images.md) | One hand-written Dockerfile with a shared build stage and four runtime images | Accepted |
| [0008](0008-bearer-tokens-and-scopes.md) | Bearer tokens validated locally, two scopes, Keycloak for local runs only | Accepted |
