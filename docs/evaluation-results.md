# Evaluation results

The results of the evaluation runs. How the command works, the golden set format, the metric definitions, the agent
cases and the CI gate are described in [evals/README.md](../evals/README.md).

## Search and answers on the golden set

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

## Agent tool choices

`--agent-cases` scores the tools the agent of `POST /agent` calls on 12 invented cases, 3 runs each; answers are not
judged. The cases, the scoring rules and the results file are described in
[evals/README.md](../evals/README.md#agent-tool-choices).

Two runs with the same cases, invented notices and passages, reference date 2026-12-07, agent model
`SpeakLeash/bielik-11b-v3.0-instruct:Q4_K_M`, embeddings `bge-m3`, Ollama version, limits and number of runs: 12
cases, 3 runs each (36 case runs), on an empty database. Both runs were complete and no case run ended with an
error. The first is from 2026-11-13, commit `80c2249`. The second is from 2026-11-14, commit `2736bf6`, after the
instructions of the gathering phase were changed; it is the one measured run made after that change.

| Number | 2026-11-13, `80c2249`: all runs | Per run, least-most | 2026-11-14, `2736bf6`: all runs | Per run, least-most |
| --- | ---: | ---: | ---: | ---: |
| Expected tool chosen | 1.00 (42/42) | 1.00-1.00 | 1.00 (42/42) | 1.00-1.00 |
| Arguments matched | 0.50 (21/42) | 0.50-0.50 | 0.71 (30/42) | 0.71-0.71 |
| Cases passed | 0.58 (21/36) | 0.58-0.58 | 0.67 (24/36) | 0.67-0.67 |
| Steps per case run: mean, least, most | 1.17, 0, 2 | | 1.17, 0, 2 | |
| Any gathering limit (case runs) | 0 | | 0 | |
| Runs at the call cap of 12, at most | 0 | | 0 | |
| Unexpected calls (of them to tools that do not exist) | 0 (0) | | 0 (0) | |
| Failed calls | 12 | | 12 | |
| Most tool calls in a run (cap 12) | 2 | | 2 | |
| Errored case runs | 0 | | 0 | |
| Runs passed, the four cases that need a notice search and then a document search | 0 of 12 | | 0 of 12 | |
| Runs passed, the seven cases that passed in the first run | 21 of 21 | | 21 of 21 | |

The data are invented and small, and answers are not judged: the numbers measure this model with this prompt on 12
cases, not models in general.

The first run:

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

Between the runs only the instructions of the gathering phase changed. They now state the dates of this week and of
next week, from Monday to Sunday. They tell the model to search notices with one or two distinctive words and to
give a CPV code only when the question gives one, to make a call that needs a value from the result of another call
only after that result has come, and to correct a call that a tool rejected instead of stopping. The cases, their
expected calls and the scoring are the same files.

The second run:

- **The target of the change was missed.** The four cases that need two searches were to pass in at least 9 of
  their 12 runs and passed in 0 of 12, as before. In all 12 runs the document search again carried a placeholder
  where the publication number belongs, so the model wrote it before it had the result of the notice search. The
  tool rejected it, and the model did not call again.
- **The notice search of those four cases had the expected arguments in 12 of 12 runs,** against 6 of 12 in the
  first run: none searched by a CPV code that the question does not give. These runs still do not pass, because
  their document search failed.
- **"Next week" was right in 3 of 3 runs** of the one case that asks for it. It is the only change that one case
  isolates: the model now reads the dates instead of counting them.
- **No case that passed in the first run failed:** 21 of 21 runs.
- **The cap of 12 is still not confirmed:** again at most 2 tool calls in a run, and the runs that should have gone
  on stopped early.
- **The instructions were changed after the first run on these same cases,** so the second column shows what the
  change does on them, not on other questions. Stricter wordings, among them one tool call per reply, were tried on
  other invented questions; the model still sent both calls in one reply, so they were not measured here and are
  not in the code.
- The three runs of a case scored the same in this run too.

**A hosted model on the same cases.** One run on 2026-11-14 from commit `f1a0795`, with chat through OpenRouter and
`openai/gpt-4.1-mini` as `Agent:Model`. Everything else was the same as in the second local run: the 12 cases with
their expected calls, the invented notices and passages with their stored vectors (same SHA-256 sums), reference date
2026-12-07, `bge-m3` on the same Ollama server (0.40.16) for the query vectors, the prompts and tool descriptions as
they are after the change above, temperature 0, at most 512 tokens a reply, the limits of 4 requests, 12 tool calls
and 120 seconds, and 3 runs a case on an empty database. The run was complete and no case run ended with an error.
It is the only measured hosted run.

| Number | Local: 2026-11-14, `2736bf6`, Bielik 11B v3 `Q4_K_M` | Hosted: 2026-11-14, `f1a0795`, `openai/gpt-4.1-mini` |
| --- | ---: | ---: |
| Expected tool chosen | 1.00 (42/42) | 0.93 (39/42) |
| Arguments matched | 0.71 (30/42) | 0.93 (39/42) |
| Cases passed | 0.67 (24/36) | 0.92 (33/36) |
| Cases passed per run, least-most | 0.67-0.67 | 0.92-0.92 |
| Steps per case run: mean, least, most | 1.17, 0, 2 | 1.08, 0, 2 |
| Any gathering limit (case runs) | 0 | 0 |
| Runs at the call cap of 12, at most | 0 | 0 |
| Unexpected calls (of them to tools that do not exist) | 0 (0) | 0 (0) |
| Failed calls | 12 | 0 |
| Most tool calls in a run (cap 12) | 2 | 2 |
| Errored case runs | 0 | 0 |
| Runs passed, the four cases that need a notice search and then a document search | 0 of 12 | 9 of 12 |
| Runs passed, the seven cases that passed in the first run | 21 of 21 | 21 of 21 |
| Runs passed, "next week" | 3 of 3 | 3 of 3 |

The first local run of 2026-11-13 (21 of 36) is above; the column here is the latest local result.

- **The hosted model waited for the notice before it searched the documents.** In 9 of the 12 runs of the two-step
  cases it searched the notices, read the publication number from the result and searched that tender's documents in
  its next reply, without a placeholder and without a failed call. The target of 9 of 12 that the local model missed
  is reached on this model, with no case that passed before failing.
- **The three misses are one case.** In all three runs of that case the model searched the notices with four words of
  the question; a notice search needs every word in the title or the buyer, found nothing, and the agent refused
  without searching the documents. The scoring counts the missing document search as a missed tool and a missed
  argument, which is why "expected tool chosen" is lower than for the local model.
- **The runs of a case scored the same.** In two cases the calls differed between runs (an optional `limit`, the
  words of a notice search); no score changed.
- **What this does not show.** The prompts were tuned on the local model; the cases are 12 invented ones, run 3 times;
  the scores are of the tool calls, not of the answers; the hosted model has a far larger context and another
  tokenizer, and OpenRouter may serve it from more than one upstream provider. It is no ranking of the two models in
  general, and the cap of 12 tool calls is still not confirmed: no run made more than 2.
- **Cost.** The run made about 100 requests to the model and cost about USD 0.04 by OpenRouter's usage of the key;
  the whole change, with the trials, about USD 0.06.

## CI gate

The values the [CI gate](../evals/README.md#ci-gate) measured on its 20 invented questions, and its thresholds:

| Search | Recall@5 | Threshold | MRR@10 | Threshold |
| --- | ---: | ---: | ---: | ---: |
| Vector | 0.95 | 0.90 | 0.57 | 0.50 |
| Full-text | 0.60 | 0.55 | 0.43 | 0.35 |
| Hybrid | 0.95 | 0.90 | 0.53 | 0.45 |
