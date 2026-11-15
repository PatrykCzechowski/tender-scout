# Settings

| Key | Default | Used by | Meaning |
| --- | --- | --- | --- |
| `DocumentImport:DocumentsFolder` | none, required | import | Folder the manifest paths point into |
| `DocumentImport:ManifestPath` | none, required | import | The manifest CSV |
| `DocumentImport:DocKinds` | `swz` | import | Manifest `doc_kind` values to import |
| `DocumentChunking:ChunkSize` | `1200` | Worker | Maximum chunk length in characters |
| `DocumentChunking:ChunkOverlap` | `150` | Worker | Text a chunk repeats from the previous one |
| `Embedding:Endpoint` | `http://localhost:11434` in the Worker, API and MCP server | Worker, search, API, MCP server, evaluation | Address of the Ollama server |
| `Embedding:Model` | `bge-m3` | Worker, search, API, MCP server | Model name, stored with every vector |
| `Embedding:BatchSize` | `32` | Worker | Chunks per request; each batch is saved separately |
| `Embedding:RequestTimeout` | `00:02:00` | Worker, search, API, MCP server | Time limit of one attempt |
| `Chat:Provider` | `Ollama` | API, evaluation | `Ollama` or `OpenRouter`, for `/ask`, `/agent` and the evaluation; see [Chat through OpenRouter](agent-and-mcp.md#chat-through-openrouter) |
| `Chat:Endpoint` | `http://localhost:11434` | API, evaluation | Address of the Ollama server with the chat model, or `https://openrouter.ai/api/v1` |
| `Chat:Model` | `SpeakLeash/bielik-4.5b-v3.0-instruct:Q8_0` | API, evaluation | Chat model that writes the answers of `/ask` |
| `OPENROUTER_API_KEY` | none; required with `OpenRouter` | API, evaluation | The OpenRouter key, as a user secret or an environment variable only |
| `Chat:RequestTimeout` | `00:01:30` | API, evaluation | Time limit of one request to the chat model |
| `Agent:Model` | none; `/agent` answers 503 without it | API, evaluation | Chat model of the agent; it must be able to call tools |
| `Authentication:Schemes:Bearer:Authority` | none, required; set by the AppHost | API, MCP server | Address of the token issuer |
| `Authentication:Schemes:Bearer:ValidAudiences:0` | none, required; set by the AppHost | API, MCP server | The audience a token must have |
| `Authentication:Schemes:Bearer:RequireHttpsMetadata` | `true` | API, MCP server | `false` only for a local issuer that speaks HTTP |
| `WebSignIn:ClientId` | none; set by the AppHost | API | The client the web page signs in with |
| `Evaluation:GoldenSetPath` | none, required | evaluation | The golden set JSONL file |
| `Evaluation:JudgeModel` | `SpeakLeash/bielik-11b-v3.0-instruct:Q4_K_M` | evaluation | Model that judges the answers |
