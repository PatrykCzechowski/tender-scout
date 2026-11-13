# Evaluation

`src/TenderScout.Evaluation` measures how well search finds the passage that answers a question, and how well
`POST /ask` answers, cites and refuses. With `--agent-cases` it measures instead which tools the agent of
`POST /agent` calls; see [Agent tool choices](#agent-tool-choices). Results are in the main
[README](../README.md#evaluation).

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
`/ask` compares with its threshold, and prints the false and correct refusal rates for thresholds 0.30 to 0.60. It
marks the highest one whose false refusals stay at or below 10%. The table uses similarity alone: `/ask` skips the
threshold when a passage contains a number from the question, and the command counts those questions separately.

The table is a measurement, not a setting: the constant in `/ask` is changed by hand. It stays 0.45 for now. In the
first run the number exception applied to every golden question, so no threshold was exercised, and the labels are
not verified. The threshold will be calibrated after the exception is narrowed and the labels are verified.

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

The gate is a coarse check that the three searches still find the answers in realistic text: a text query that
matches nothing fails it with full-text recall@5 at 0.00. It is not the test of fusion or of the stop-word list. On 30
short passages `bge-m3` alone puts 19 of 20 answers in the top 5, so neither change below moves a metric under its
threshold. Other tests in the same `dotnet test` run fail instead (measured on 277 tests):

| Regression | Gate | Tests that fail |
| --- | --- | --- |
| Hybrid search returns the vector ranking only | Passes | 11: nine cases in `HybridSearchTests` (`ExactTerm_IsFoundByFullTextWhereVectorsMissIt`, `FoundByBoth_RanksAboveTheTopOfEitherRanking`, `ChunkFirstInBothRankings_ScoresTwiceTheTopOfOne`, `CodeInTheQuestion_OutranksAFrequentWordNextToIt`, `Diacritics_AreKept`, `NewVersionOfAChunk_IsFoundByItsNewText`, `QuestionWords_DoNotMakeAChunkACandidate` and both cases of `OperatorsInTheQuestion_AreTreatedAsWords`), `AskEndpointTests.Answer_CitesTheStoredPassagesTheModelChose` and `DocumentSearchCommandTests.ExactTermWithoutAVector_IsPrinted` |
| Empty stop-word list | Passes | 3: `HybridSearchTests.QuestionWords_DoNotMakeAChunkACandidate` and one case each of `ChunkSearchValidationTests.ToTextQuery_KeepsContentWordsJoinedWithOr` and `ToTextQuery_NoContentWords_GivesNull` |

Synthetic cases that make the gate itself fail on these changes are left for later; the set is not tuned to them.

Regenerate the vectors only when the embedding model changes, against a real model:

```sh
dotnet run --project src/TenderScout.Evaluation -- --write-fixture
```

Then run the gate, update the thresholds in `RetrievalGateTests` and the README, and commit both files.

## Agent tool choices

`dotnet run --project src/TenderScout.Evaluation -- --agent-cases` asks the agent of `POST /agent` a small set of
questions and scores the tools it called. It does not judge the answers.

### Cases

Everything is invented and public, in `evals/agent`:

- `cases.jsonl`: 12 cases. 3 need only notices, 3 need the documents of a tender given by its number, 4 need a notice
  search and then a document search, 2 need no tool at all.
- `notices.jsonl`: 10 invented notices. Five belong to the tenders of the synthetic passages of the
  [CI gate](#ci-gate), which are the documents of this set; five have no documents.
- `reference-date.json`: the date the agent is told is today, so "next week" means the same on every run.

One JSON object per line:

| Field | Meaning |
| --- | --- |
| `id` | Stable id |
| `question` | The question in Polish, as a user would ask it |
| `expectedCalls` | The calls the agent should make, in order: each has `tool` and `arguments`, the key arguments only. May be `[]` |
| `allowedExtraCalls` | Tools the agent may also call without being marked down. May be `[]` |

A key argument is written in one of three ways. An argument the case does not mention is not checked.

| Written as | Matches when |
| --- | --- |
| A value: `"publicationNumber": "100202-2026"` | The call has that argument with the same value |
| A list: `"cpvPrefix": ["72", "72000000"]` | The call's value equals one of them |
| Words: `"query": {"words": ["wadium"]}` | The call's text contains every word, in any letter case, also inside a longer word |

Values are compared without outer spaces and letter case, and a number equals the text that spells it (`5` and
`"5"`). Nothing else is normalised. The file is checked when it is loaded: unique ids, known tools, only arguments
the tool has. A broken file stops the command and names the case or the line.

### Scoring one run

The expected calls are taken in order. Each is looked for after the call that was taken for the one before it:

1. The first later call to the expected tool that did not fail and has the key arguments: the expected call is
   **chosen** and its **arguments matched**.
2. Otherwise the first later call to the expected tool: **chosen**, arguments not matched.
3. Otherwise **not chosen**; the search for the next expected call starts at the same place.

A call that was not taken is an extra call. It is **unexpected** unless its tool is in `allowedExtraCalls`; a call to
a tool that does not exist is always unexpected. A failed call never matches the arguments, so after a rejected call
and a corrected one the corrected one is taken and the rejected one is an extra call.

A case run **passes** when every expected call has matched arguments, no call is unexpected and gathering did not
stop at a limit.

### Numbers

| Number | Definition |
| --- | --- |
| Expected tool chosen | Chosen expected calls over all expected calls, all runs together |
| Arguments matched | Expected calls with matched arguments over all expected calls |
| Cases passed | Passing case runs over all case runs |
| Steps | Tool calls per case run: mean, least and most |
| Any gathering limit | Case runs where the agent's `limitReached` is true. It covers three limits (round trips, tool calls, time) and does not say which |
| Runs at the call cap, at most | Case runs with `limitReached` and exactly as many tool calls as the cap of 12 |
| Unexpected calls | Their number, and how many were to tools that do not exist |
| Failed calls | Calls the tool rejected, that failed, or to a tool that does not exist |
| Errored case runs | Case runs that ended with an exception |

- Every case is run 3 times, because the model's calls can differ between runs. The first three numbers are also
  given per run, as the least and the most.
- A share with nothing to divide by, such as the expected calls of a case that expects none, is shown as `n/a (0/0)`
  and is `null` in the results file.
- "Runs at the call cap, at most" is an upper bound: a run stopped by the cap has 12 calls, but a run with 12 calls
  can also have stopped at the round limit. Zero means the cap did not get in the way on these cases, not that 12 is
  enough in general. The cap is provisional.
- An errored case run counts as not passed and is left out of every other number, because it has no tool calls.

### Running

The command needs an empty PostgreSQL database with pgvector of its own. It creates the schema and loads the notices
and the passages. It refuses to start when the database already holds a notice or a document.

```sh
dotnet user-secrets --project src/TenderScout.Evaluation set "ConnectionStrings:tenderscout" "<connection string of the empty database>"
dotnet user-secrets --project src/TenderScout.Evaluation set "Embedding:Endpoint" "http://<ollama-host>:11434"
dotnet user-secrets --project src/TenderScout.Evaluation set "Chat:Endpoint" "http://<ollama-host>:11434"
dotnet user-secrets --project src/TenderScout.Evaluation set "Agent:Model" "SpeakLeash/bielik-11b-v3.0-instruct:Q4_K_M"
dotnet run --project src/TenderScout.Evaluation -- --agent-cases
```

It runs the same agent class as the API, in process, with the same tools, prompts, limits and model setting; only
the date it is told is the reference date. `bge-m3` is needed too, for the vectors of the model's document queries.
The command prints a table per case and an overall table and writes `artifacts/eval/agent-<UTC time>.json` with the
date, commit, models, reference date, limits, the numbers and, per case run, the score and the tool calls: name,
arguments and whether the call failed, plus `limitReached` and `refused`. It holds no answer, no passage and nothing
else a tool returned, and it is enough to compute the scores again without a model.

- A model that cannot be reached stops the command; the file then says `"complete": false` and no table is printed.
- A case run that ends with another exception is recorded with the kind of the exception, the other runs are made,
  and the command exits with an error. Only a complete run without such a case run is quoted in the README.

### What the numbers do not show

- Whether an answer is right, cites the right source or reads well.
- Models in general: they measure one model with the prompt of the agent on 12 invented cases.
- Meaning: the word matchers are stems (`kryteri`, `warun`), so a paraphrased query can be marked down, and a query
  that contains the words can still be a poor one.
- How the agent does on real notices and documents.

The scoring is tested in `dotnet test` on hand-written tool calls, without a model: `AgentScoringTests`.
`AgentCasesTests` checks the public cases against the notices, and `AgentEvaluationRunnerTests` runs the command
with scripted model replies.

## Why not an evaluation framework

- The Python `llm-eval` project has a judge and an Ollama provider but no retrieval metrics, would need an HTTP
  endpoint to reach the search, and would add Python to CI.
- `Microsoft.Extensions.AI.Evaluation` evaluators use English prompts written for large models. One Polish judge
  prompt and counting is less code than adapting them.
