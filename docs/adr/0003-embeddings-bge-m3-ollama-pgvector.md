# ADR-0003: `bge-m3` on Ollama, stored in pgvector with an HNSW cosine index

## Status

Accepted — 2026-11-08, amended 2026-11-09 for the document pipeline (#18) and 2026-11-10 for hybrid search

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
- **Embedding runs in the Worker, one document per `ChunksCreated` message**
  (ADR-0002). This replaces the separate embedding command of the first version:
  documents wait in the queue while the PC is off, so the import no longer needs a
  command of its own to stay independent of Ollama. Chunks go in batches of 32, each
  saved in its own transaction. A failing batch fails the message, which is retried
  and then moved to `chunks-created_error`; running the import again embeds whatever
  has no vector yet.
- **The Worker starts without Ollama.** Its `appsettings.json` has a public default of
  `http://localhost:11434`, and starting never contacts the endpoint, so notice
  polling keeps working when the PC is off.
- **Search is a development command**, `TenderScout.DocumentSearch`, run with
  `dotnet run`. It embeds the question with the configured model and prints the five
  nearest passages.
- **Timeouts are set for this client on purpose.** Service defaults allow every
  `HttpClient` 10 s per attempt and 30 s in total, too little for a model that has
  to load first. This client allows `Embedding:RequestTimeout` (2 minutes) per
  attempt, and 10 s to connect, so a switched-off PC still fails within seconds.

### Hybrid search

Vectors find passages by meaning but miss exact strings such as CPV codes, amounts and
article numbers. `HybridSearchAsync` therefore combines them with PostgreSQL full-text
search, and `DocumentSearch` uses it. `SearchAsync` stays as the vector-only search.

- **`simple` text search configuration.** The `pgvector/pgvector:pg17` image has no
  `polish` configuration, no Polish stemmer and no Hunspell dictionary. Installing one
  would mean a custom image to maintain on the Mac, in CI and on Azure. `simple` only
  lowercases and keeps diacritics, so inflected forms do not match (`wysokość` does
  not find `wysokości`); the vector side covers those. `unaccent` is not used: it
  cannot run in a generated column, and PdfPig keeps the diacritics.
- **`document_chunks.search_vector`** is a stored generated `tsvector` column with a
  GIN index. PostgreSQL fills it for existing rows and keeps it in sync with `text`,
  so no code writes it.
- **The question becomes an OR of its content words.** Words of letters and digits are
  kept, those shorter than 3 characters and about 25 Polish question and function words
  are dropped, and the rest are joined with `or` for `websearch_to_tsquery`. An AND of
  every word rarely matches a natural question, and PostgreSQL ranking ignores how
  rare a word is, so `jest` would count as much as `wadium`. Only letters and digits
  survive, so a minus sign or a quote cannot act as an operator. Text candidates come
  from all chunks, also those without a vector. Chunks containing a word with a digit
  from the question come first, then `ts_rank_cd` decides: on the real documents a CPV
  code found in one tender otherwise lost to the word "CPV", which appears in almost
  every tender.
- **Reciprocal Rank Fusion** merges the two rankings: each chunk scores the sum of
  1 / (60 + rank) over the rankings it appears in, ties go by chunk id. Each side
  gives 40 candidates, because an HNSW scan returns at most 40 (`hnsw.ef_search`), and
  N is between 1 and 20. All of these are constants.

## Consequences

### Easier

- Delivering `ChunksCreated` again costs nothing: only chunks without a vector from
  the configured model are sent, and a failed message resumes where it stopped.
- A re-import of a changed file deletes its chunks and their vectors, and its
  `ChunksCreated` embeds only the new ones. Reusing vectors for identical text is not worth the
  logic for a two-minute full run.
- Tests and CI use a fake generator and fixed vectors, so they need neither Ollama
  nor a network.

### Costs and risks

- **Changing the model** always means embedding every chunk again, and queries must
  use the same model. A schema migration is needed only if the dimension changes.
  Because the name is stored, changing `Embedding:Model` is enough to make the next
  pass replace the vectors, and search returns only chunks from the requested model.
  The name is compared exactly: `bge-m3` and `bge-m3:latest` count as different.
- **Embedding chunks and questions needs the PC to be on.** Importing, storing
  chunks and the rest of the application do not.
- **Azure is not solved here.** Production cannot reach the home network, so it needs
  a hosted `bge-m3` or a move to another provider, with a full re-embedding.
- **The index is not used yet at this size.** With about 3500 rows PostgreSQL prefers
  a sequential scan, which takes milliseconds. The index starts to matter as the
  corpus grows. Search also applies the model filter after the index scan, so while
  two models are mixed in the table it can return fewer than N rows.
- **The stopword list and the RRF constant are not tuned.** They are reasonable
  defaults; the golden set will show whether hybrid search beats either side alone.
- **The fake generator proves the plumbing, not the meaning.** Retrieval quality is
  measured separately, on a golden set.
- **Ollama has no login.** The port must stay inside the local network.
