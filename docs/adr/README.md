# Architecture Decision Records

Significant technical decisions are recorded here, one file per decision, numbered
in order (`NNNN-short-title.md`). Each record has the sections **Status**,
**Context**, **Options considered**, **Decision** and **Consequences**.

A record is not edited once accepted; to change a decision, add a new ADR that
supersedes it and update the old one's status.

| ADR | Title | Status |
| --- | --- | --- |
| [0001](0001-dotnet-and-aspire.md) | .NET 10 with .NET Aspire for local orchestration | Accepted |
| [0002](0002-messaging-masstransit-rabbitmq.md) | MassTransit v8 on RabbitMQ for notice ingestion, with idempotent consumers | Accepted |
| [0003](0003-embeddings-bge-m3-ollama-pgvector.md) | `bge-m3` on Ollama, stored in pgvector with an HNSW cosine index | Accepted |
