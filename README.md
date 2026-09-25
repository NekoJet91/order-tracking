# Order Tracking

Orders with a lifecycle, and status changes that reach the browser by themselves.

A status change is committed to PostgreSQL together with an event row, published to RabbitMQ
by a background worker, and pushed to connected clients over a WebSocket.

**Task 2 — the SQL function — is in [`task2-sql/`](task2-sql/README.md), with its own README.**
The rest of this file is about the application.

## Current state

| Area | Status |
|---|---|
| Domain model, EF Core persistence, migrations | done |
| REST API, OpenAPI, validation, ProblemDetails | done |
| Transactional outbox, RabbitMQ publish and consume | done |
| WebSocket notifications | done |
| React + TypeScript client | done |
| Docker and `docker-compose` | done |
| Tests: Testcontainers, Vitest, MSW | done |
| Serilog, OpenTelemetry traces and metrics | done |

## Running it

Docker is the only prerequisite. From a clean clone:

```bash
docker compose up --build
```

- Client — <http://localhost:8080>
- API — <http://localhost:5138>
- RabbitMQ management — <http://localhost:15673> (`ordertracking` / `ordertracking`)
- Jaeger — <http://localhost:16686>
- Prometheus — <http://localhost:9090>

The first build takes a few minutes, mostly restoring packages; after that the layer cache
makes it quick. Compose starts the parts in order and waits for each to be ready rather than
merely started: the migrator does not run until PostgreSQL answers `pg_isready`, and the API
does not start until the migrator has exited successfully.

To watch the system work without clicking anything, turn on the simulator — it creates orders
and walks them through their lifecycle, and every open tab follows along:

```bash
SIMULATOR=true docker compose up --build
```

Every port and credential has a default and every one is overridable; copy `.env.example` to
`.env` to change them. `ASPNETCORE_ENVIRONMENT=Development` additionally exposes Swagger at
<http://localhost:5138/swagger>.

To look inside, go through the containers — the database and the AMQP port are deliberately
not published to the host:

```bash
docker compose exec postgres psql -U ordertracking -d ordertracking -c 'select count(*) from orders'
docker compose exec rabbitmq rabbitmqctl list_queues
docker compose logs -f api
```

`docker compose down` stops everything and keeps the data; `docker compose down -v` also
discards the volumes, which is how to get back to an empty database.

Running the two processes directly, with hot reload and a debugger, is described at the end
under [Running it without Docker](#running-it-without-docker).

## Poking at it

A round trip through the whole pipeline. The API listens on the same port whether it is
running in a container or from `dotnet run`, so this works against either:

```bash
NUMBER=$(curl -s -X POST http://localhost:5138/api/orders \
  -H 'Content-Type: application/json' \
  -d '{"description":"Кабель ВВГнг 3x2.5, 200 м"}' | jq -r .orderNumber)

curl -s -X PATCH "http://localhost:5138/api/orders/$NUMBER/status" \
  -H 'Content-Type: application/json' \
  -d '{"status":"Shipped"}'
```

The log then shows the event arriving back from the broker.

And the socket itself: `/ws/orders` sends a snapshot of recent orders on connect, then one frame per change, plus a
heartbeat while nothing is happening. Any WebSocket client will do; with Node 21 or later, no
dependencies are needed:

```bash
node -e 'const ws = new WebSocket("ws://127.0.0.1:5138/ws/orders");
         ws.onmessage = e => console.log(e.data);'
```

Frames are discriminated by a `type` field — `snapshot`, `order-changed`, `heartbeat` — and
every order carries `updatedAt`. **A client must ignore an order whose `updatedAt` is not
newer than the copy it holds.** Delivery is at-least-once and a delta queued before the
snapshot was taken can arrive after it, so comparing the version is what makes the stream
safe to apply.

## Observability

The point of the instrumentation is one question: a user changed a status and the other tab
did not update — where did it stop? A distributed trace answers it, because the whole journey
is a single trace even though it crosses a database table, a broker and two background
threads.

Change a status, then open Jaeger at <http://localhost:16686>, pick `order-tracking-api` and
the `PATCH /api/orders/{orderNumber}/status` operation. One trace, ten spans:

```
PATCH /api/orders/{orderNumber}/status                18.6ms
  ordertracking  [SELECT o.id, o.created_at, ...]      1.8ms
  ordertracking  [UPDATE orders SET status = @p0...]   0.8ms
  order.status-changed publish                         1.6ms
    order.status-changed process                      12.6ms
      ordertracking  [SELECT EXISTS (...processed...)]  1.0ms
      socket broadcast                                 3.3ms
        ordertracking  [SELECT o.order_number, ...]     0.6ms
        ordertracking  [SELECT o.status, count(*)...]   1.8ms
      ordertracking  [INSERT INTO processed_messages]   0.7ms
```

That tree is only possible because the trace context survives two handovers. The interceptor
writes the current `traceparent` onto the outbox row inside the same transaction as the order;
the publisher reads it back minutes later if need be, starts its own span as a child of it,
and puts *that* span's id on the AMQP message; the consumer restores it from the header. So
the consumer's work is attributed to the request that caused it, not to whatever the poller
happened to be doing.

Metrics are on `/metrics` in Prometheus format and scraped every five seconds; Prometheus is
at <http://localhost:9090>. The ones worth a graph:

| Metric | Why it is here |
|---|---|
| `ordertracking_outbox_lag_seconds` | How long an event waited between being committed and being published. The single best signal that the pipeline is unwell, and free to measure. |
| `ordertracking_outbox_failures_total` | Publishes that threw. Rises before the lag does. |
| `ordertracking_consumer_handled_total` | Split by outcome: `handled`, `duplicate`, `unreadable`, `failed`. The `duplicate` count is the deduplication table earning its keep. |
| `ordertracking_socket_connections` | Open sockets right now. Goes up on connect and down on disconnect, so a leak shows as a number that never comes back down. |
| `ordertracking_orders_status_changed_total` | Split by `from` and `to`. A business number rather than a technical one, counted at the same choke point as the events so it cannot disagree with them. |

Logs are Serilog. In `Development` the console gets the readable template; everywhere else it
gets compact JSON, because there the reader is a log shipper and re-parsing a rendered string
to recover fields that were already structured is work that should not exist. Every event
carries `@tr` and `@sp`, so a log line and a span find each other:

```bash
docker compose logs api | grep '"@tr":"<trace id from Jaeger>"'
```

A rolling file sink writes the same JSON to `/app/logs` inside the container, daily, seven
files kept — so a container that has already restarted still has evidence.

Nothing here requires a collector. Traces are exported over OTLP only when
`OTEL_EXPORTER_OTLP_ENDPOINT` is set, which compose does and a bare `dotnet run` does not;
metrics are always on `/metrics` whether or not anything scrapes them.

## Tests

```bash
dotnet test                              # 39 unit, 43 integration
cd client && npm test                    # 47, in jsdom
```

Nothing to set up for either. The integration suite starts its own PostgreSQL and RabbitMQ
through Testcontainers, so the only prerequisite is the Docker that runs the application
anyway. The containers come up once per run and are torn down afterwards, including if the
run is killed — Testcontainers leaves a reaper behind for that. A whole run, containers
included, takes about eight seconds once the images are local.

Each test class gets its own database on that shared server, and its own exchange and queue
names on that shared broker. That is what lets the classes run in parallel: there is nothing
left for two of them to collide over.

Nothing is substituted for the real thing. The endpoint tests run the shipping pipeline
against a real PostgreSQL, because half of what is worth testing — the concurrency token, the
sequence, the unique index — only exists there. The messaging tests run the real outbox
publisher and the real consumer against a real broker, with one seam: the handler at the end
of the chain, so a test has somewhere to look.

On the client, `npm test` runs Vitest in jsdom. The API is stubbed at the network boundary
with MSW rather than by mocking the client module, so the code under test is the real one —
the same URL building, the same problem-details parsing. The socket is the exception: a fake
`WebSocket` and fake timers, because what is under test there is entirely about delays.

```bash
npm run coverage                         # 88% of statements
cd client && npx tsc -b && npm run lint  # also run by the container build
```

## How it is put together

```
OrderTracking.Api  ──►  OrderTracking.Infrastructure  ──►  OrderTracking.Domain
```

References point one way only. `Domain` knows nothing about EF Core or RabbitMQ; the status
machine and its rules live there and are unit-testable without any infrastructure at all.

The path a status change takes:

```
PATCH /api/orders/{number}/status
        │
        ▼
  Order.ChangeStatus            rejects an illegal transition → 409
        │
        ▼
  SaveChangesAsync              order row + outbox row, one transaction
        │
        ▼
  OutboxPublisher               claims rows with FOR UPDATE SKIP LOCKED
        │
        ▼
  RabbitMQ                      topic exchange, persistent, publisher confirms
        │
        ▼
  OrderEventsConsumer           dedupes, acks after committing, DLQ on failure
        │
        ▼
  WebSocketOrderEventHandler    reads the current row, fans it out
        │
        ▼
  every open /ws/orders         one bounded queue per client
```

The reason for the outbox is that a database commit and a broker publish cannot share a
transaction. Writing the event as a row next to the order makes the two atomic, and moves
the broker off the critical path at the cost of at-least-once delivery — which the consumer
is built to absorb.

### The client

```
client/src
├── api/           fetch wrapper and the types the API publishes
├── app/           store, typed hooks, shell and routes
├── components/    badge, connection indicator, problem message
├── features/
│   ├── connection/   whether the socket is live
│   └── orders/       slice, selectors, pages, Russian wording
├── realtime/      the socket, and everything that keeps it alive
└── test/          builders, a store without the socket, a fake WebSocket
```

Tests sit next to what they test, as `*.test.ts` and `*.test.tsx`.

The socket lives in Redux middleware, not in a hook. It is one connection for the whole
application, opened by the shell, so moving between the list and a detail page does not tear
it down and pay for a fresh handshake and snapshot. Nothing else in the client holds a
`WebSocket`; components ask for a connection by dispatching an action.

An order reaches the store by four routes — a REST page, the snapshot, a delta, and the reply
to the user's own POST or PATCH — and all four go through one guard that drops anything whose
`updatedAt` is not newer than the copy already held. That single comparison is what makes
duplicates and late frames harmless, and it is why the creation form adds nothing to the list
itself.

What is displayed is derived from that one collection rather than from whatever the last
request returned. Filtering by status fetches its own page — the twenty newest cancelled
orders are not a subset of the twenty newest orders, so each filter carries its own cursor —
but the list itself is a selector over the store. That is what keeps a filtered view live: an
order the socket moves into the status appears, one it moves out of disappears, and neither
needs a refetch.

### The containers

Three images out of two Dockerfiles. The root one has a single build stage and two outputs —
the API and the migrator — so the second costs a copy rather than a second compile. Both run
as the non-root account the .NET base image provides. The client one builds with Node and
serves with nginx, which is the only reason the final image is 74 MB rather than several
hundred: nothing from the build survives into it except the `dist` folder.

nginx does one more job than serving files. It proxies `/api` and `/ws` to the API, so the
browser sees a single origin in production exactly as it does behind the Vite dev proxy, and
CORS never has to exist. The `/ws` location carries three extra lines because nginx speaks
HTTP/1.0 upstream by default, which has no upgrade mechanism at all: without
`proxy_http_version 1.1` and the two hop-by-hop headers forwarded by hand, the handshake comes
back as an ordinary response and the browser reports a failure that explains nothing.

## Deliberate simplifications

Things left undone on purpose, listed so they are not mistaken for oversights.

**The outbox is polled, not signalled.** A sweep every second is the floor on notification
latency. The better design has the writer wake the publisher — PostgreSQL `LISTEN`/`NOTIFY`
fits exactly — and keeps polling only as the safety net.

**One API instance.** The notification queue is a shared work queue, so each event goes to
exactly one consumer. That is right for doing work once and wrong for a broadcast: scale out
and only the clients attached to one instance would be notified. Fixing it means a queue per
instance bound to the same exchange — and reworking deduplication, because a marker table
keyed globally on event id would then make the second instance skip the event. For a
broadcast, deduplication is not needed at all: a notification carries the current status, so
resending it changes nothing.

**Traces live in memory and die with the container.** Jaeger runs as the all-in-one image
with no storage behind it, which is right for a demonstration and wrong for anything else.
Prometheus keeps a day. Neither is a claim about how this would be deployed.

**Nothing reads the dead-letter queue.** Messages that fail processing are parked there with
RabbitMQ's `x-death` header for diagnosis, but no alert fires and no human is told. There is
also no delayed retry for failures that heal on their own; that would be a queue with a TTL
dead-lettering back into the main one.

**No authentication or authorisation.** Not in the brief, and adding it would have obscured
the parts that are. The pagination cursor is base64, neither encrypted nor signed, so nothing
that affects access control may ever be put in it.

**No end-to-end test drives a real browser.** The client tests run in jsdom against a stubbed
network, and the server tests run against real infrastructure, but nothing exercises the two
together through an actual browser. Playwright would close that. Until then "the tab
reconnects and catches up" is checked by hand and nothing re-checks it.

**Status totals cost one aggregate per event.** The counts on the filter chips are computed
by the server and sent with the snapshot and with every change frame. That is the one thing
here whose cost grows with the table rather than with the traffic. It buys exactness — every
change emits a frame, so a total sent this way is never stale and there is no refresh interval
to tune. The replacement, when the volume asks for one, is a counter row updated in the same
transaction as the status change.

**A client that stops reading is disconnected, not buffered.** Each connection has a bounded
send queue, and filling it closes the socket. The alternative — dropping frames — leaves a
screen quietly showing the wrong thing, where a disconnect is noisy but self-healing,
because the client reconnects and gets a fresh snapshot.

## Design decisions worth a sentence

**Statuses travel as names, not numbers.** `"Shipped"`, never `2`. Readable in a browser,
stable if the enum is ever reordered, and it gives OpenAPI a real enum schema for the
TypeScript client to mirror. Integer input is rejected rather than quietly accepted.

**Pagination is keyset, not offset.** `OFFSET` re-reads and discards the rows it skips, and
shifts under inserts so a row can be seen twice or missed. The cursor encodes the last
`(created_at, id)` seen, which is stable and costs the same on page one and page one
thousand.

**The clock is injected.** `TimeProvider` rather than `DateTimeOffset.UtcNow`, so tests can
control time instead of sleeping. Timestamps are truncated to microseconds before being
stored, because that is the resolution `timestamptz` actually keeps — without it the API
would report a value the database never held.

**Concurrency is optimistic.** PostgreSQL's `xmin` is mapped as a shadow row version, so two
simultaneous status changes cannot silently overwrite one another; the loser gets a 409.

**Raw WebSockets rather than SignalR.** SignalR would supply reconnection, fallbacks and a
backplane for free. It also supplies its own protocol and client library, and the brief asks
for "WebSocket or Server-Sent Events" — so the frames here are plain JSON that any client can
read, and the reconnect logic is small enough to own.

**The socket sends state, not events.** A frame carries the whole order as it now stands.
That makes creation and change the same message, lets a client render an order it has never
seen, and makes a burst converge on the latest truth instead of replaying a sequence nobody
is watching.

**Reconnection backs off with jitter, and a watchdog decides when to start.** The delay
doubles to a fifteen-second ceiling and lands at a random point in the upper half of that
window, because when the API restarts every open browser is disconnected in the same instant
and would otherwise retry in the same instant too. The watchdog is the client half of the
heartbeat: a connection whose far end vanished stays `OPEN` indefinitely, and only the missing
heartbeat tells the two apart. It also covers waking from sleep, when timers resume against a
stale timestamp.

**The server decides which transitions are legal, and the client forgets them.** Buttons are
built from `allowedNextStatuses`; the rules are not reimplemented in TypeScript. A socket
frame carries the new status but not the new transitions, so the reducer discards the list
and the detail page re-reads it — showing a button the server is about to reject would be
worse than a round trip.

**Migrations are a step in the deployment, not something the application does to itself.**
Compose runs an EF migration bundle as a service that has to exit successfully before the API
is allowed to start. `Database.MigrateAsync()` in `Program.cs` would be one line instead, and
two API replicas starting together would race to apply the same migration. A bundle also
keeps the SDK out of the runtime image: the alternative is shipping a compiler, a NuGet cache
and the sources in order to run one command.

**Telemetry is produced with the base class library and exported by the host.** Nothing
outside `Program.cs` and one extensions file references OpenTelemetry. The instrumented code
uses `ActivitySource` and `Meter` from `System.Diagnostics`, which have been in .NET since
before OpenTelemetry existed, so choosing Jaeger, or a collector, or nothing at all, is a
hosting decision and not a change to the domain or the infrastructure.

**Spans are pushed and metrics are pulled.** Traces go out over OTLP because nobody can guess
when a request happened; counters are scraped off `/metrics` because a counter is a current
value that a scraper can ask for whenever it likes. The two backends want different things and
the code does not pretend otherwise.

**Retention on the outbox is a policy; retention on the deduplication table is correctness.**
Published outbox rows older than a week are deleted because they are only history. The
processed-message markers look like the same kind of row and are not: deleting a marker while
the broker could still redeliver its message turns at-least-once delivery into actually-twice.
The period is therefore chosen against the longest redelivery the broker can produce, not
against disk.

**Statuses are English on the wire and Russian on screen.** The translation happens in one
file, typed as `Record<OrderStatus, string>`, so a status added in C# breaks the client's
build exactly where a word has to be chosen for it. Colour is carried by a `data-status`
attribute and lives entirely in the stylesheet.

## Running it without Docker

The development path: the client gets hot reload and the API gets a debugger. Everything above
applies to a stack started this way too, apart from the `docker compose` commands themselves.

### Prerequisites

- .NET 8 SDK
- Node 20 or later
- PostgreSQL 14 or later
- RabbitMQ 3.9 or later, reachable over AMQP

### Database

One. The tests bring up their own through Testcontainers and never touch this.

```bash
createdb -h localhost -U postgres ordertracking_dev
```

### Broker

```bash
docker run -d --name ordertracking-rabbit \
  -p 5672:5672 -p 15672:15672 \
  -e RABBITMQ_DEFAULT_USER=ordertracking \
  -e RABBITMQ_DEFAULT_PASS=ordertracking \
  rabbitmq:4-management-alpine
```

Exchanges and queues are declared by the application on every start, so there is nothing to
create by hand. The management UI is at <http://localhost:15672>.

### Configuration

`appsettings.json` carries the shape and the non-secret defaults. Credentials are left out
of it deliberately and come from the environment or user-secrets:

```bash
export ConnectionStrings__OrderTracking="Host=localhost;Port=5432;Database=ordertracking_dev;Username=postgres;Password=..."
export RabbitMq__UserName=ordertracking
export RabbitMq__Password=ordertracking
```

The equivalent with user-secrets, which keeps the values out of the shell history:

```bash
dotnet user-secrets --project src/OrderTracking.Api set "ConnectionStrings:OrderTracking" "..."
```

### Migrations

The design-time factory reads its own variable, so migrations never accidentally run against
whatever the application happens to be pointed at:

```bash
export ORDERTRACKING_MIGRATIONS_CONNECTION="Host=localhost;Port=5432;Database=ordertracking_dev;Username=postgres;Password=..."

dotnet ef database update \
  --project src/OrderTracking.Infrastructure \
  --startup-project src/OrderTracking.Infrastructure
```

### The two processes

In either order.

```bash
dotnet run --project src/OrderTracking.Api

cd client && npm install && npm run dev
```

- Client — <http://localhost:5173>
- API — <http://localhost:5138>
- Swagger UI — <http://localhost:5138/swagger>
- Liveness — <http://localhost:5138/health>

The dev server proxies `/api` and `/ws` to the API, so the browser only ever sees one origin
and CORS never enters the picture. Turning the simulator on gives the client something to do:

```bash
StatusSimulator__Enabled=true dotnet run --project src/OrderTracking.Api
```

Orders then appear and move through their lifecycle by themselves, and every open tab follows
along without being asked to.
