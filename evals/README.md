# Evaluation

`src/TenderScout.Evaluation` measures how well search finds the passage that answers a question, and how well
`POST /ask` answers, cites and refuses. Results are in the main [README](../README.md#evaluation).

## Golden set

The questions about the real tender specifications are not in this repository. Their quotes are tender text, and
the chunks they point to come from documents that are not published here either, so a public copy could not be
run anyway. The file lives in a private repository and the command reads it from a path in user secrets.

One JSON object per line, in snake_case:

| Field | Meaning |
| --- | --- |
| `id` | Stable id, the only question data the results file keeps |
| `question` | The question as a user would ask it, in Polish |
| `topic` | `deadline`, `deposit`, `criteria`, `conditions`, or `none` for a question without an answer |
| `answerable` | `false` when no indexed document holds the answer |
| `paraphrase` | `true` when the question shares no content word with its quote |
| `doc_id` | Manifest id of the document with the answer |
| `publication_number` | The tender the question is about; search and `/ask` are limited to it, as in the API |
| `page` | One-based PDF page of the quote |
| `quote` | Verbatim text of at most 200 characters from one paragraph, with no personal data |
| `label_source` | `auto` for a drafted label, `verified` once a person has checked it against the PDF |

The current set has 40 questions: 8 each about the deadline, the deposit, the award criteria and the conditions for
participation, and 8 without an answer. 12 of the answerable ones are paraphrased. Labels were drafted by a model of
another family than the one that answers, then checked against the PDF pages by script. They stay `auto` until a
person verifies them; every report says how many of each were used.

Before measuring, the command checks every answerable record: some chunk of its document must contain the quote
(ignoring case and whitespace) and start on the quote's page or the one before. It stops and names the records that
fail.

## Metrics

A returned chunk is **relevant** when it belongs to the expected document and contains the quote after lowercasing
and collapsing whitespace. The page is not used, because a chunk's page is where the chunk starts.

| Metric | Definition |
| --- | --- |
| Recall@5 | Answerable questions with a relevant chunk in the top 5, over answerable questions |
| MRR@10 | Mean of 1/rank of the first relevant chunk in the top 10, 0 when there is none |
| Citation hit rate | Answered answerable questions with at least one relevant citation, over answered answerable questions |
| Citation precision | Relevant citations over all citations of answered answerable questions |
| Faithfulness | Answered questions the judge finds supported by their cited passages, over answered questions |
| Correct refusals | Refused questions without an answer, over questions without an answer |
| False refusals | Refused answerable questions, over answerable questions |

Recall and MRR are given for vector, full-text and hybrid search, for all answerable questions and separately for
paraphrased and literal ones. Every method gets the same question and tender; vector and hybrid get the same vector.

**Refusal threshold.** For every question the command records the cosine similarity of the nearest chunk, the value
`/ask` compares with its threshold, and prints the false and correct refusal rates for thresholds 0.30 to 0.60. The
chosen one is the highest whose false refusals stay at or below 10%. The table uses similarity alone: `/ask` skips the
threshold when a passage contains a number from the question, and the command counts those questions separately.

**Judge.** After all questions are answered, a second model gets each answer with its cited passages and returns
`{"faithful": bool, "reason": string}` through a JSON schema at temperature 0. The phases are separate so the two
models are never loaded at the same time. A reply that is not valid JSON counts as not faithful and is reported.
The judge is Bielik 11B, the same family as the answering Bielik 4.5B, so it may share its blind spots; its verdicts
should be spot-checked.

## Running

```sh
dotnet user-secrets --project src/TenderScout.Evaluation set "Evaluation:GoldenSetPath" "/path/to/golden.jsonl"
dotnet user-secrets --project src/TenderScout.Evaluation set "ConnectionStrings:tenderscout" "<connection string>"
dotnet user-secrets --project src/TenderScout.Evaluation set "Embedding:Endpoint" "http://<ollama-host>:11434"
dotnet user-secrets --project src/TenderScout.Evaluation set "Chat:Endpoint" "http://<ollama-host>:11434"
ollama pull SpeakLeash/bielik-11b-v3.0-instruct:Q4_K_M
dotnet run --project src/TenderScout.Evaluation
```

The database must hold the imported and embedded documents. The command prints Markdown tables and writes
`artifacts/eval/<UTC time>.json` (ignored by git) with the date, commit, model names, label counts, every metric and
one row per question: ranks, similarity, refusal, citation counts and verdict. It holds ids only, no question,
quote or passage text.

- `--retrieval-only` skips the answers and the judge, so only the embedding model is needed.
- Without it, an unreachable chat model stops the command with an error after the search results are written.
- `Evaluation:JudgeModel` changes the judge; `Chat:Model` the answering model.

## CI gate

`RetrievalGateTests` runs in `dotnet test` without a model or network. `tests/TenderScout.IntegrationTests/Evaluation`
holds 30 invented passages from five invented tenders and 20 invented questions (6 paraphrased) in the golden set
format, each with its `bge-m3` vector rounded to 4 decimals. Rounding changed no top-10 ranking and no cosine
similarity by more than 0.0002. The test loads them into the test PostgreSQL, runs the three searches and fails when a
recall@5 or MRR@10 drops below its threshold, naming the metric, its value and the threshold. Each threshold is the
measured value minus 0.05, rounded down to 0.05.

The gate catches a broken text query or a broken vector search. It does not catch hybrid search replaced by vector
search alone, nor a removed stop-word list: on 30 short passages `bge-m3` alone puts 19 of 20 answers in the top 5, so
those changes move no metric by 0.05.

Regenerate the vectors only when the embedding model changes, against a real model:

```sh
dotnet run --project src/TenderScout.Evaluation -- --write-fixture
```

Then run the gate, update the thresholds in `RetrievalGateTests` and the README, and commit both files.

## Why not an evaluation framework

- The Python `llm-eval` project has a judge and an Ollama provider but no retrieval metrics, would need an HTTP
  endpoint to reach the search, and would add Python to CI.
- `Microsoft.Extensions.AI.Evaluation` evaluators use English prompts written for large models. One Polish judge
  prompt and counting is less code than adapting them.
