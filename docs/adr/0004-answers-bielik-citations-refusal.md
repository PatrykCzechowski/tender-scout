# ADR-0004: Answers from Bielik 4.5B with numbered-passage citations and a refusal rule

## Status

Accepted — 2026-11-10

## Context

`POST /ask` answers a question about the stored tender documents. Every claim must
point at a passage the user can check, and the endpoint must say so when the
documents do not hold the answer instead of letting the model guess.

The constraints are those of ADR-0003: no paid APIs during development, a PC on the
local network with an 8 GB GPU that already holds `bge-m3` (1.2 GB), and CI with no
access to it. The documents are Polish.

## Options considered

| Criterion | **Bielik 4.5B v3 Q8_0** | Bielik 11B v3 Q4_K_M | Qwen 2.5 7B (Q4_K_M) |
| --- | --- | --- | --- |
| Ollama tag | `SpeakLeash/bielik-4.5b-v3.0-instruct:Q8_0` | `SpeakLeash/bielik-11b-v3.0-instruct:Q4_K_M` | `qwen2.5:7b` |
| Size | 5.1 GB | 6.7 GB | 4.7 GB |
| Fits 8 GB next to `bge-m3` | Yes | No, one of them is swapped out | Yes |
| Context | 8K | 32K | 32K |
| Language | Trained for Polish | Trained for Polish | Multilingual, not tuned for Polish |
| Measured here | Real run below | Not run | Not run |

## Decision

- **Bielik 4.5B v3 Q8_0** answers. It is trained for Polish and fits in VRAM next to
  `bge-m3`, so a question does not wait for a model swap. 8K tokens are enough: five
  passages of up to 1200 characters gave 1250-1900 prompt tokens. Bielik 11B is kept
  as the judge candidate for the evaluation.
- **The model stays behind `IChatClient`.** Only `AddQuestionAnswering` knows about
  OllamaSharp. The `Chat` section has the endpoint, the model and a 90 s
  `RequestTimeout`, validated on start without contacting the server. It can point at
  another machine than `Embedding`.
- **One attempt, no retry.** The service defaults handlers (10 s per attempt, 30 s in
  total) are removed for this client. The connect timeout is 10 s, so a switched-off
  PC gives a 503 within seconds; a retried generation would keep the caller waiting
  for minutes.
- **Five numbered passages.** Hybrid search (ADR-0003) gives the top five, written as
  `[n] dokument <docId>, strona <page>` and the passage text. The model gets nothing
  else from the database.
- **The prompt** is a constant in `QuestionAnswerer`:

  ```text
  Jesteś asystentem, który odpowiada na pytania o dokumenty przetargowe.
  Odpowiadaj po polsku i wyłącznie na podstawie podanych fragmentów.
  W polu "citations" podaj numery fragmentów z nawiasów kwadratowych, na których opierasz odpowiedź, a nie numery stron.
  Jeśli fragmenty nie zawierają odpowiedzi, zwróć pustą listę "citations".
  Zwróć wyłącznie JSON zgodny ze schematem: <the schema below>
  ```

  The user message is `Pytanie: <question>`, then `Fragmenty:` and the passages.
- **The reply is JSON with a schema**, sent as Ollama's `format` through
  `ChatOptions.ResponseFormat`, with temperature 0 and `num_ctx` 8192:

  ```json
  {"type":"object","properties":{"answer":{"type":"string"},"citations":{"type":"array","items":{"type":"integer"}}},"required":["answer","citations"]}
  ```

  Citation numbers outside 1..5 and repeats are dropped, the model's order is kept.
  The API, not the model, fills in the document id, publication number, page and the
  stored passage text, so a quote cannot be invented. The answer text itself is not
  checked against the passages; the evaluation measures that.
- **Refusal rule.** The endpoint answers 200 with `refused: true`, the fixed message
  `Nie znalazłem odpowiedzi w dokumentach.` and no citations when:
  1. hybrid search returns no passage (for example an unknown publication number), or
  2. the cosine similarity of the nearest chunk is below **0.45** and no passage
     contains a word with a digit from the question, or
  3. the model's reply is not valid JSON, or has no answer, or no valid citation.

  Rules 1 and 2 are checked before the chat model is called. Fused RRF scores depend
  only on rank, so the similarity comes from a separate vector search with limit 1
  and the same tender filter. The digit exception keeps questions about a CPV code or
  an amount answerable, because their vector can be far from every chunk. 0.45 lies
  between the similarities seen so far for relevant passages (0.53-0.55) and for
  unrelated sentences (0.23-0.29). **It is provisional** until the golden set
  calibrates it.
- **Errors.** Bad input is 400 with problem details naming the field (question blank
  or over 1000 characters, publication number not like `123456-2026`). A connection
  error, an HTTP error such as a model that is not pulled, or a timeout from either
  model is 503 with problem details and no internal details. The API starts and
  `/health` is healthy with both Ollama endpoints off.
- **No authentication yet.** The endpoint is for local development until
  authentication is added.

### Real run, 2026-11-10

27 SWZ, 3512 chunks embedded with `bge-m3`, Bielik 4.5B v3 Q8_0 on Ollama on a laptop
(Apple silicon), temperature 0:

| Question | Result | Citation |
| --- | --- | --- |
| Wadium, one tender | Correct: no deposit required | Correct page |
| Deadline for offers, one tender | Correct date and hour | Correct page |
| What a CPV code covers, all tenders | Correct, the only tender with that code | Correct page |
| Award criteria, one tender | Correct: price 100 %, with the formula | Correct page |
| Weather in Warsaw | Refused before the model | None |
| Bank account for the deposit, tender without one | Explains that no deposit is required | Correct page |
| Contractual penalty for delay, one tender | Correct amount | Wrong passage of the same document |

All 10 replies were valid JSON matching the schema. In the first run the model cited
`[30]`, the page number of a passage, instead of its number `[4]`; the citation was
dropped and the question refused. The sentence "a nie numery stron" in the prompt
fixed it. Answers took 8-25 s and prompts had 1250-1900 tokens.

## Consequences

### Easier

- Tests replace `IChatClient` and `IEmbeddingGenerator` with fakes and run the API
  through `WebApplicationFactory` against PostgreSQL, with no Ollama and no broker.
- Every citation can be checked: it is a stored passage with its document and page.
- Changing the model is a configuration change; the prompt and the threshold are
  versioned with the code.

### Costs and risks

- **Paraphrases are refused** while their similarity stays under 0.45, for example
  the legal synonym measured at 0.27. The golden set will show how often.
- **A valid citation can still be the wrong passage**, as in the penalty question.
  Only the citation number is checked here; the evaluation measures citation
  precision.
- **Each question runs two vector searches**, one for the passages and one for the
  similarity. At this size each takes milliseconds.
- **The tender filter is exact only while PostgreSQL scans sequentially.** If the
  HNSW index starts being used, the filter is applied after the index scan and can
  return fewer passages; `hnsw.iterative_scan` is left for then.
- **Answers need the PC on**, or another reachable Ollama, and take seconds.
- **Ollama has no login** (ADR-0003); neither has `/ask` yet.
