# tender-scout

## Document pipeline

Tender documents are listed in a CSV manifest and read from a local folder. They go through RabbitMQ in two steps,
so each step can fail and be retried on its own:

1. `document-import` reads the manifest, checks each file against its SHA-256 and publishes one `DocumentUploaded`
   per valid row. It needs no database and no Ollama.
2. The Worker reads the PDF, splits it into chunks, stores the document with its chunks and publishes
   `ChunksCreated`. Scans and Word files are stored as skipped, with the reason.
3. The Worker embeds the chunks of that document with `bge-m3` and stores the vectors.

Both consumers can receive the same message again without duplicating anything: an unchanged file is not read
again, and only chunks without a vector are embedded. Running the import again is therefore always safe, and it is
also how to recover after a failure.

### Ollama

Chunks are embedded with `bge-m3` on [Ollama](https://ollama.com/download). Ollama is not part of the Aspire
AppHost, so it can run on another machine with a GPU. Tests and CI do not need it.

```sh
ollama pull bge-m3
```

The Worker uses `http://localhost:11434` by default and starts even when Ollama is not running. Set the real address
in user secrets or the environment variable `Embedding__Endpoint`, so it stays out of the repository:

```sh
dotnet user-secrets --project src/TenderScout.Worker set "Embedding:Endpoint" "http://<ollama-host>:11434"
```

#### Ollama on another machine

On the machine that runs Ollama, make it listen beyond localhost by setting this environment variable and
restarting Ollama:

```sh
OLLAMA_HOST=0.0.0.0:11434
```

Check the connection from the development machine before using the address:

```sh
curl http://<ollama-host>:11434/api/tags
```

Ollama has no login: anyone who can reach the port can use it. Keep port 11434 inside the local network, allow
only the machines that need it in the firewall, and do not forward it on the router.

### Running an import

1. Point the import at the documents, with absolute paths. The PDFs stay out of git.

   ```sh
   dotnet user-secrets --project src/TenderScout.DocumentImport set "DocumentImport:DocumentsFolder" "/absolute/path/to/documents"
   dotnet user-secrets --project src/TenderScout.DocumentImport set "DocumentImport:ManifestPath" "/absolute/path/to/manifest.csv"
   ```

2. Start the AppHost and wait until the `worker` resource is running. On a fresh broker the Worker creates the
   queues, so it must start before the first import. Otherwise the broker returns every message and the import
   rejects all rows instead of losing them silently.
3. Start `document-import` from the dashboard. Its log ends with the number of published, ignored and rejected
   rows; any rejected row makes it exit with a non-zero code.
4. The work is done when the `document-uploaded` and `chunks-created` queues are empty in the RabbitMQ management
   UI, which opens from the `messaging` resource in the dashboard.

Messages that still fail after the retries go to `document-uploaded_error` or `chunks-created_error`. A corrupted
or missing file goes there at once. If Ollama was off, start it and run `document-import` again: every document
is published again, and only what is missing is processed. The old messages in the error queues can then be purged.

### Searching

`TenderScout.DocumentSearch` is a development command that embeds a question with the same model and prints the
five nearest passages, with document, page and score. It runs outside the AppHost, so give it the database
connection string shown for the `tenderscout` resource in the dashboard, and the Ollama address:

```sh
dotnet user-secrets --project src/TenderScout.DocumentSearch set "ConnectionStrings:tenderscout" "<connection string>"
dotnet user-secrets --project src/TenderScout.DocumentSearch set "Embedding:Endpoint" "http://<ollama-host>:11434"
dotnet run --project src/TenderScout.DocumentSearch -- "Jaka jest wysokość wadium?"
```

It exits with a non-zero code when the question is missing or Ollama cannot be reached.

### Settings

| Key | Default | Used by | Meaning |
| --- | --- | --- | --- |
| `DocumentImport:DocumentsFolder` | none, required | import | Folder the manifest paths point into |
| `DocumentImport:ManifestPath` | none, required | import | The manifest CSV |
| `DocumentImport:DocKinds` | `swz` | import | Manifest `doc_kind` values to import |
| `DocumentChunking:ChunkSize` | `1200` | Worker | Maximum chunk length in characters |
| `DocumentChunking:ChunkOverlap` | `150` | Worker | Text a chunk repeats from the previous one |
| `Embedding:Endpoint` | `http://localhost:11434` in the Worker | Worker, search | Address of the Ollama server |
| `Embedding:Model` | `bge-m3` | Worker, search | Model name, stored with every vector |
| `Embedding:BatchSize` | `32` | Worker | Chunks per request; each batch is saved separately |
| `Embedding:RequestTimeout` | `00:02:00` | Worker, search | Time limit of one attempt |

### Local result

Run on 2026-11-09 with the manifest of 68 rows, of which the 31 main tender specifications (`swz`) are in scope:

| Outcome | Documents |
| --- | --- |
| Indexed | 27 |
| Skipped: PDF without a text layer (scan) | 1 |
| Skipped: Word file (DOC or DOCX) | 3 |
| Rejected | 0 |

The 27 indexed documents gave 3512 chunks, all embedded with `bge-m3`. A second import changed nothing and sent
nothing to Ollama. The other 37 manifest rows (answers to bidders' questions and amendments) are ignored for now.

## Architecture decisions

Key technical decisions are recorded as ADRs in [`docs/adr`](docs/adr/README.md).
