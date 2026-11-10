# ADR-0002: MassTransit v8 on RabbitMQ for notice ingestion, with idempotent consumers

## Status

Accepted — 2026-11-08, amended 2026-11-09 for the document pipeline (#18)

## Context

The worker discovers new notices by polling the TED search API on a schedule
(issue #10). Discovery and storage should be decoupled:

- Slow or failing processing (database down, later stages such as document download
  or embedding) must not block or delay polling.
- Each notice must end up processed **exactly once in effect**. Polls deliberately
  overlap (a look-back window of several days), so the same notice is discovered
  many times; and any broker can deliver a message more than once.
- Failures need a visible, recoverable place to go instead of being lost in logs.

Constraints from ADR-0001 still apply: one developer, local orchestration with
Aspire (which already provisions RabbitMQ with the management plugin), CI on a
Raspberry Pi (linux-arm64), and a likely move to Azure Container Apps.

## Options considered

### Messaging library and broker

| Criterion | **MassTransit v8 + RabbitMQ** | Raw `RabbitMQ.Client` | Azure Service Bus | Wolverine | MassTransit v9 |
| --- | --- | --- | --- | --- | --- |
| Topology (exchanges, queues, bindings) | Created from message and consumer types, with conventional names (`notice-discovered`) | Declared and maintained by hand | Created by the SDK or IaC; topics/subscriptions | Created from handler types | As v8 |
| Retry, error queue, faults | Built in: retry policies, `_error` queue, `Fault<T>` events | Hand-written ack/nack, retry counters and dead-lettering | Built-in dead-letter queue and delivery count; retry policy is ours | Built in | As v8 |
| Consumer scope / DI | One DI scope per message, so a scoped `DbContext` just works | Manual | Manual (processor callbacks) | Built in | As v8 |
| Local development | RabbitMQ container from the Aspire AppHost, management UI included | Same container | No local emulator that matches production well; needs a cloud namespace or the emulator | Same container | Same container |
| ARM64 CI (Testcontainers) | `rabbitmq` image is multi-arch | Same | Emulator needs an extra SQL Server container; ARM64 support not verified | Same | Same |
| Testing | In-memory and real-transport test harness | Test against a real broker only | Hard to run in CI | Good | As v8 |
| Transport portability | Swap RabbitMQ for Azure Service Bus by changing the bus setup, not the consumers | Rewrite | Locked to Azure | Several transports | As v8 |
| Licence / cost | Apache-2.0 | MIT/Apache-2.0 | Pay per use | MIT | Commercial licence required |
| Maturity in .NET | Very widely used | Low-level building block | Mature | Younger, smaller community | New major |

## Decision

- Use **MassTransit 8.5.x on RabbitMQ**, pinned in `Directory.Packages.props`. Stay
  on v8 (Apache-2.0); moving to the commercially licensed v9 needs its own decision.
- The worker polls TED with a `PeriodicTimer` (`Ingestion:PollingInterval`, first poll
  at startup) over a window of `Ingestion:LookBackDays` days back to today, and
  **publishes one `NoticeDiscovered` per notice**. The contract lives in
  `TenderScout.Contracts`, separate from the domain `Notice`, so the wire format
  only changes on purpose.
- `NoticeDiscoveredConsumer` on the `notice-discovered` queue stores the notice.
  Its endpoint **retries only transient failures** (timeouts, transient Npgsql
  errors, EF `RetryLimitExceededException`) with exponential backoff
  (`Ingestion:RetryLimit`, `RetryMinInterval`, `RetryMaxInterval`). Any other
  exception, or a message still failing after the last retry, is moved to
  **`notice-discovered_error`** (visible in the RabbitMQ management UI) and a
  `Fault<NoticeDiscovered>` is published.
- A failed poll (for example TED being down) is logged and simply retried on the
  next tick; the overlapping window finds the same notices again. An unreachable
  broker does not fail the poll: MassTransit keeps retrying the connection, so
  `Publish` waits and the poll continues once the broker is back.

### Documents

The same approach carries tender documents through two steps (#18), both consumed
by `TenderScout.Worker`:

- `document-import` checks the manifest and publishes one **`DocumentUploaded`**
  per valid row. The publish is **mandatory**: if no queue is bound yet, the broker
  returns the message and the row is rejected, instead of being dropped while the
  import reports success.
- `DocumentUploadedConsumer` hashes the file, and only if the hash differs from the
  stored one reads the PDF, splits it and stores the document with its chunks in
  one transaction. Then it publishes **`ChunksCreated`**, always, also for an
  unchanged or skipped document.
- `ChunksCreatedConsumer` embeds the chunks of that one document that have no
  vector yet (ADR-0003).
- Both endpoints take **one message at a time** and retry **5 times with 1 to 30 s
  backoff**, as constants. `document-uploaded` retries only transient database
  failures, so a corrupted or missing file goes straight to its `_error` queue;
  `chunks-created` also retries embedding failures.
- Recovery after an outage is **running the import again**, not replaying error
  queues.

### Idempotency strategy

Delivery is **at least once**: the same notice arrives many times, through
overlapping polls, retries and broker redelivery. Instead of deduplicating
messages, the consumer is **idempotent by publication number**:

- `notices.publication_number` has a unique index (`ux_notices_publication_number`).
- The consumer upserts: insert if new, update the changed fields if the notice was
  amended, and **write nothing** if it is identical (EF change tracking compares
  every field, so `updated_at` only moves on a real change).
- Two consumers inserting the same new notice at once are resolved by the unique
  index: the loser catches the unique violation and re-runs as an update.

So processing a message twice has the same effect as processing it once. That is
why we need **no inbox (message deduplication table) and no transactional outbox**
for now: the poller writes nothing to the database, so there is no database write
and publish to keep consistent, and the consumer has no side effect apart from the
upsert.

The document consumers follow the same rule. `DocumentUploadedConsumer` is the one
place that writes and then publishes: it commits first and publishes after. If the
process stops between the two, the message is not acknowledged, so the broker
delivers it again; the second delivery finds the same hash, writes nothing and
still publishes `ChunksCreated`. Publishing first would let the embedding step run
before the chunks are visible. The in-memory outbox would not close this gap,
because its buffer is lost when the process stops, and the EF Core outbox would add
tables for a case redelivery already covers.

## Consequences

### Easier

- Polling and storage fail independently: the in-memory retries absorb a short
  database outage. After a longer one the affected messages are in the error queue,
  and the next poll publishes every notice still inside the look-back window again.
- Retries, error queue and fault events come from configuration, not hand-written
  ack/nack code; poisoned messages are visible in the management UI and can be moved
  back to `notice-discovered` once fixed. The upsert does not compare versions, so
  replaying an old message can overwrite newer data: after an outage, prefer the
  next poll over a manual replay.
- Later stages (documents, search, agent) can subscribe to `NoticeDiscovered` or to
  new events without touching the poller.
- Tests run the production bus setup over a real RabbitMQ in Testcontainers, and the
  poller over the in-memory harness.
- Moving to Azure Service Bus later means changing the transport setup in
  `UsingTenderScoutRabbitMq`, not the consumers or contracts.

### Costs and risks

- **Republishing is not free:** every poll republishes the whole look-back window
  (tens to low hundreds of messages a day for Polish IT tenders). That is cheap at
  this volume; if it grows, the poller should skip notices that are already stored
  and unchanged.
- **Retries are immediate (in memory):** they hold the message on the consumer while
  waiting, and outages longer than the backoff end in the error queue. Delayed
  redelivery would need the RabbitMQ delayed-message plugin, which the Aspire
  RabbitMQ image does not include. The error queue is the safety net; moving
  messages back is manual for now.
- **Idempotency is per consumer:** any future consumer with external side effects
  (notifications, calls to paid APIs) must be idempotent itself or use the
  MassTransit EF Core inbox/outbox. This ADR does not cover them.
- **Licensing:** MassTransit v8 is the last Apache-2.0 major. It still receives
  fixes, but a future need for v9 features means paying for a licence or switching
  libraries.
- **Another moving part:** RabbitMQ must be running for ingestion; the worker exits
  at startup if the `messaging` connection string is missing.
- **Poller and consumer share one host:** `TenderScout.Worker` runs both, which
  guarantees the queues exist before the first publish. Running more than one replica
  would multiply the TED polls, so scaling consumers is the point to move the poller
  into its own host.
- **The import is a separate process:** on a fresh broker the Worker has to start
  before the first import, because it creates the document queues. The mandatory
  publish turns a wrong order into rejected rows instead of lost documents.
- **One document message at a time** keeps the single GPU busy with one request and
  removes races between duplicate messages, but stretches the time to `_error` when
  Ollama is away: about 50 s per document with a closed port, measured.
