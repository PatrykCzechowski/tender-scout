# tender-scout

## Embeddings with Ollama

Chunks are embedded with `bge-m3` on [Ollama](https://ollama.com/download). Ollama is not part of the Aspire
AppHost, so it can run on another machine with a GPU. Tests and CI do not need it.

1. Install Ollama and pull the model:

   ```sh
   ollama pull bge-m3
   ```

2. Tell the `document-embedding` command where Ollama is. Use user secrets or the environment variable
   `Embedding__Endpoint`, so the address stays out of the repository:

   ```sh
   dotnet user-secrets --project src/TenderScout.DocumentEmbedding set "Embedding:Endpoint" "http://localhost:11434"
   ```

3. Start the AppHost, run `document-import` from the dashboard if the documents are not stored yet, then start
   `document-embedding`. It embeds only chunks without a vector from the configured model, so it is safe to run
   again. If Ollama is unreachable it stops with an error, and the next run continues where it left off.

| Key | Default | Meaning |
| --- | --- | --- |
| `Embedding:Endpoint` | none, required | Address of the Ollama server |
| `Embedding:Model` | `bge-m3` | Model name, stored with every vector |
| `Embedding:BatchSize` | `32` | Chunks per request; each batch is saved separately |
| `Embedding:RequestTimeout` | `00:02:00` | Time limit of one attempt at a batch |

### Ollama on another machine

On the machine that runs Ollama, make it listen beyond localhost by setting this environment variable and
restarting Ollama:

```sh
OLLAMA_HOST=0.0.0.0:11434
```

Check the connection from the development machine, then use the same address as `Embedding:Endpoint`:

```sh
curl http://<ollama-host>:11434/api/tags
dotnet user-secrets --project src/TenderScout.DocumentEmbedding set "Embedding:Endpoint" "http://<ollama-host>:11434"
```

Ollama has no login: anyone who can reach the port can use it. Keep port 11434 inside the local network, allow
only the machines that need it in the firewall, and do not forward it on the router.

## Architecture decisions

Key technical decisions are recorded as ADRs in [`docs/adr`](docs/adr/README.md).
