# Traces and metrics

Every service exports traces, metrics and logs with OpenTelemetry. Started from the AppHost
(`dotnet run --project src/TenderScout.AppHost`), they go to the Aspire dashboard, whose address the AppHost prints;
its **Traces** tab shows one trace per question and per document, and **Metrics** the model calls. Outside the AppHost
a service exports only when `OTEL_EXPORTER_OTLP_ENDPOINT` is set; without it nothing leaves the process, and a
dashboard that is down does not stop a service. Questions need the models and a token, as described in [API and authentication](api-and-auth.md).

A question is one trace, from the HTTP request to the last model call:

```text
POST /ask
├── retrieval                         the question's vector and both searches
│   ├── embeddings bge-m3
│   └── postgresql …
└── chat <Chat:Model>                 only when relevant passages were found

POST /agent
├── orchestrate_tools                 the gathering phase
│   ├── chat <Agent:Model>            one span per request to the model
│   ├── execute_tool search_tender_documents
│   │   └── embeddings bge-m3
│   └── chat <Agent:Model>
└── chat <Agent:Model>                the answer, when sources were found
```

A tool runs after the model turn that asked for it, so its span is a sibling of that turn, not a child. A tool call
that a tool rejects ends with an error status; a call over a limit has no span, because it does not run.

A document is one trace too, across the queue: the import's `DocumentUploaded send` span is the root, and the Worker
continues it from the message's headers (MassTransit's own propagation) through `document-uploaded receive/process`,
`ChunksCreated send`, `chunks-created receive/process` and an `embeddings bge-m3` span per batch.

The model spans come from Microsoft.Extensions.AI 10.11, which follows the GenAI semantic conventions v1.41:
`gen_ai.operation.name`, `gen_ai.request.model`, `gen_ai.response.model`, `gen_ai.provider.name` (`ollama`, or `openai`
for the OpenAI-compatible client of OpenRouter, with `server.address` `openrouter.ai`; it does not say which company
ran the model) and `gen_ai.usage.input_tokens`/`output_tokens` when the provider reports them. Counts that are not
reported are left out, not set to 0; embeddings have input tokens only. The newer drafts of the conventions rename
some of this, for example split token metrics; the names here are those of the installed package.

| Metric | Unit | What it holds |
| --- | --- | --- |
| `gen_ai.client.operation.duration` | s | One value per call to a chat or embedding model, also a failed one |
| `gen_ai.client.token.usage` | {token} | The reported tokens of each call, by `gen_ai.token.type` `input` or `output` |

Both are in the meter `Experimental.Microsoft.Extensions.AI`, by operation, model, provider and server; no question,
document or user is a dimension. The spans' sources are `TenderScout` (the search of `/ask`),
`Experimental.Microsoft.Extensions.AI` and `MassTransit`, besides ASP.NET Core, HttpClient and Npgsql.

**What the telemetry does not hold.** No prompt, question, passage, answer, tool argument or tool result: the
recording of message texts is switched off in code, also when `OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT` is
`true`. A failed span keeps its error status and type but not the text of its exception. One exception remains:
when a Worker consumer fails, MassTransit adds an `exception` event with the exception's message and stack trace,
which cannot be changed after it is created; the message of a failed document may name its path. Every trace is
recorded; sampling for production is not set here.
