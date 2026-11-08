# ADR-0003: `bge-m3` on Ollama, stored in pgvector with an HNSW cosine index

## Status

Accepted — 2026-11-08

## Context

Finding tender passages by meaning needs a vector for every chunk and a
nearest-neighbour search over them (issue #17). The corpus is Polish procurement
text: about 3500 chunks of up to 1200 characters from the first import.

The choice is constrained by:

- one developer and no budget for paid APIs while the project is developed;
- a laptop that should not run the model, and a separate PC on the local network
  with a GPU that can;
- CI on a Raspberry Pi with neither a GPU nor access to that PC;
- a likely move to Azure later (ADR-0001), which cannot reach a home network.

The vector dimension is part of the database schema, so it has to be chosen before
the first migration.

## Options considered

| Criterion | **`bge-m3` on Ollama** | Azure OpenAI `text-embedding-3-small` | Polish `mmlw` models |
| --- | --- | --- | --- |
| Dimensions | 1024 | 1536 by default, can be shortened by a request parameter | 1024 (`mmlw-retrieval-roberta-large`) |
| Cost | None beyond the PC | Pay per token | None beyond the host |
| Runs where | Own machine, one `ollama pull` | Azure resource and API key | Own Python service; no Ollama build |
| Polish quality | Multilingual, not tuned for Polish | Multilingual, not tuned for Polish | Tuned for Polish retrieval; not yet compared on our documents |
| Query handling | Same text path as documents | Same text path as documents | Queries need the prefix `zapytanie: ` |
| Works with the home network off | No | Yes | Depends on the host |
| Fits Azure later | Needs a hosted model | Directly | Needs a hosted model |
| Licence | MIT | Commercial service | Apache-2.0 (`mmlw-retrieval-roberta-large`) |

For the search itself, pgvector is already in the PostgreSQL image used by the
AppHost and the tests, so vectors stay in the same transaction boundaries and
backups as the chunks. A separate vector database would add a service to run and
keep consistent, for a few thousand rows.

## Decision

- **`bge-m3` on Ollama** for development. It costs nothing, needs no separate
  service to write, and a full run takes about two minutes on the PC. `mmlw` is
  compared on a golden set before anything is switched.
- **Ollama runs outside the Aspire containers**, on another machine, at the endpoint
  in `Embedding:Endpoint`. The address comes from user secrets or the environment
  and is never committed. The GPU is on that machine, and nothing else about the
  application depends on where it is.
- **The provider stays behind `IEmbeddingGenerator`.** Only the registration in
  `AddChunkEmbeddings` knows about OllamaSharp, the client the deprecated
  `Microsoft.Extensions.AI.Ollama` package points to.
- **`document_chunks.embedding` is `vector(1024)`**, with the model name stored next
  to it in `embedding_model`.
- **HNSW index with cosine distance**, default parameters. `bge-m3` vectors are
  compared by direction, and the score returned by search is cosine similarity
  (1 minus distance). N is capped at 40, the default `hnsw.ef_search`, which is the
  most an index scan returns.
- **Embedding is a separate development command**, so documents can be imported
  while the PC is off. Chunks go in batches of 32, each saved in its own transaction.
  After a short retry a failing batch stops the run with a non-zero exit code, and
  the next run continues with whatever has no vector yet.
- **Timeouts are set for this client on purpose.** Service defaults allow every
  `HttpClient` 10 s per attempt and 30 s in total, too little for a model that has
  to load first. This client allows `Embedding:RequestTimeout` (2 minutes) per
  attempt, and 10 s to connect, so a switched-off PC still fails within seconds.

## Consequences

### Easier

- Running the pass again costs nothing: only chunks without a vector from the
  configured model are sent, and a failed run resumes.
- A re-import of a changed file deletes its chunks and their vectors, and the next
  pass embeds only the new ones. Reusing vectors for identical text is not worth the
  logic for a two-minute full run.
- Tests and CI use a fake generator and fixed vectors, so they need neither Ollama
  nor a network.

### Costs and risks

- **Changing the model** always means embedding every chunk again, and queries must
  use the same model. A schema migration is needed only if the dimension changes.
  Because the name is stored, changing `Embedding:Model` is enough to make the next
  pass replace the vectors, and search returns only chunks from the requested model.
  The name is compared exactly: `bge-m3` and `bge-m3:latest` count as different.
- **Embedding needs the PC to be on.** Import, search over stored vectors and the
  rest of the application do not.
- **Azure is not solved here.** Production cannot reach the home network, so it needs
  a hosted `bge-m3` or a move to another provider, with a full re-embedding.
- **The index is not used yet at this size.** With about 3500 rows PostgreSQL prefers
  a sequential scan, which takes milliseconds. The index starts to matter as the
  corpus grows. Search also applies the model filter after the index scan, so while
  two models are mixed in the table it can return fewer than N rows.
- **The fake generator proves the plumbing, not the meaning.** Retrieval quality is
  measured separately, on a golden set.
- **Ollama has no login.** The port must stay inside the local network.
