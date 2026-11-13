# ADR-0006: Agent in two phases: tool calls gather sources, one call answers from them

## Status

Accepted — 2026-11-13

## Context

`POST /ask` runs one fixed search. It cannot answer a question that names a tender by
its title or buyer, or that needs both the notices and the documents. A model that
calls the tools of [ADR-0005](0005-mcp-server-shared-tools.md) can decide what to
look up, but it can also loop, invent sources or answer from its own knowledge.

Constraints: the answer must rest only on what the tools returned, a run must end in
bounded time on a local model, CI has no Ollama, and the tools must not get a second
definition.

## Options considered

| Question | **Chosen** | Rejected |
| --- | --- | --- |
| Shape of a run | Two phases: a tool loop that only gathers, then one call that answers from numbered sources | One loop whose last reply is the answer: its sources would be whatever the model wrote, and could not be checked against the tool results |
| Who runs the loop | `FunctionInvokingChatClient` from `Microsoft.Extensions.AI` | A hand-written loop: the same message handling again, with more to test |
| How the tools are called | The methods of `TenderTools`, in process, as `AIFunction`s made from the MCP attributes | An MCP client of the own server (see ADR-0005); a second list of names and descriptions |
| Model | Its own setting `Agent:Model`, chosen by a trial | The model of `/ask`: Bielik 4.5B does not call tools |
| At a limit | Answer from what was found, with `limitReached: true` | An error: the caller would lose sources that were already found |
| Embedding model down | 503 | An answer from the notices alone, which would look complete |

## Decision

- **Gathering.** The model gets the question, today's date in Poland and the three
  tools. Each tool result is kept: notices once per publication number, passages
  once per document, tender, page and text. The model's own text is thrown away.
- **Answering.** One call without tools gets the question and a numbered list: first
  up to 25 notices, then up to 12 passages, in the order they were first returned.
  It returns the JSON of `/ask` (`answer` and the numbers it used). The response
  holds the sources with those numbers, in list order, exactly as the tools returned
  them. No valid number, an empty answer or no source at all gives the refusal of
  `/ask`; without a source the answering call is not made.
- **Limits, as constants.** 4 requests to the model while gathering (the tool calls
  of the fourth reply are still run), 12 tool calls in a run, 120 seconds for
  gathering on the host clock. Reaching one sets `limitReached`; cutting the list of
  sources does not. The numbers are provisional until an evaluation of the agent.
- **Tool errors the model sees.** Invalid arguments: the tool's own message, which
  names the parameter and the rule, the same text an MCP client gets. Any other
  failure: one fixed sentence without details. An unknown tool: the framework's
  message. In each case the run goes on and the call is listed as failed.
- **Errors the caller sees.** An unreachable chat model in either phase, an
  unreachable embedding model during a document search, or no `Agent:Model`: 503.
  Any other failure outside a tool: 500. Neither carries the tool calls.
- **Logging.** One entry per tool call (name, arguments, duration, failed) and one
  per run (outcome, requests, tool calls, limit, sources gathered and used,
  duration), at information level. No entry holds a result or the answer.
- **Model: Bielik 11B v3, `Q4_K_M`,** with a context of 12288 tokens, at most 512
  tokens for a gathering reply and 512 for the answer, temperature 0.

### Model trial

Run on 2026-11-13 through `POST /agent` against a database of 10 invented notices
and 3 invented documents (17 chunks, embedded with `bge-m3`), on a GPU with 8 GB of
memory. Six fixed questions: two about notices, two about the documents of a tender
given by its number, two that need both. A question passes when the response is 200,
not refused, within the limits, and names at least one source; a model passes with 5
of 6. Bielik 11B was tried first and passed, so the second candidate, `qwen3:8b`,
was not tried.

| Run | Passed | Named the expected source | Total time |
| --- | --- | --- | --- |
| First prompt, context 8192 | 4 of 6 | 3 of 6 | 127 s |
| Final prompt, context 8192, two runs | 6 of 6 | 4 of 6 | 120 s, 117 s |
| Final prompt, context 12288 | 6 of 6 | 4 of 6 | 144 s |

In the final run a question took 12 to 45 seconds, with 2 requests to the model
while gathering and 1 or 2 tool calls; every answer was in Polish. The first prompt
failed two questions because the model ignored today's date and searched the
documents without the publication number; the prompt now states the date with its
weekday and says where the number goes.

The context was set by measurement. The largest answering call, 25 notices and 12
passages of 1200 characters, has about 9400 tokens, and Ollama cuts a longer prompt
without an error, so 8192 is too small. With 16384 the model no longer fits the GPU
and one question took 77 seconds.

## Consequences

### Easier

- Every source in a response was returned by a tool in that run. The model chooses
  numbers; it cannot add a source or change its text.
- The tools have one definition. A test compares what the model is offered with the
  contract file of the MCP server, and the tool results with the MCP server's JSON.
- Tests script the model's replies and run the real tools against PostgreSQL, with a
  clock the test moves, so limits and failures are tested without Ollama.
- A run can be explained afterwards from `toolCalls` or from the log.

### Costs and risks

- **Two model calls at least,** and a larger model than `/ask`: 10 to 45 seconds a
  question in the trial, against several seconds for `/ask`.
- **The trial is small and its data invented.** Six questions show that the model
  calls the tools and that the limits hold, not how often the answer is right. In 2
  of 6 the answer named a source, but not the expected one.
- **The model sometimes fills arguments badly.** In the trial it used a parameter
  that does not exist, which is ignored, so that search ran without a filter; it
  counted "next week" a few days off; and after a rejected call it stopped instead
  of correcting it. The first two give a wrong or wider result, not an error.
- **Arguments that are not readable JSON are listed as `null`,** because the
  framework does not pass on their text.
- **A call to an unknown tool is recorded when the reply arrives,** so it is listed
  before the other calls of that reply.
- **`Microsoft.Extensions.AI` is a direct dependency** of `TenderScout.Infrastructure`.
  It was already there through OllamaSharp; the agent uses its function-invoking
  client and `AIFunctionFactory`.
- **Not measured:** answers on real tender documents, the second candidate model,
  more than one request at a time, and how the answers change with other limits.
