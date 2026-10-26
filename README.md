# dotnet-warehouse

Stock by SKU and the orders that draw it down. The scarce number is the **on-hand units** of a SKU, and the **Inventory** module is its only writer: it holds units with a single conditional `UPDATE` that matches only while the on-hand units still cover the order, so when two orders fight for the last unit, one commits a hold and the other is short and writes none. An order reference that contains the poison token `POISON` is committed as rejected by the Dispatch module, the message is dead-lettered, and a compensating command gives the units back. A later cancellation also gives the units back, but it does not roll back the pick slip that was already issued.

One ASP.NET Core process, one Postgres database. An HTTP write saves the order and an outbox row in one local transaction; a `BackgroundService` relay publishes outbox rows to RabbitMQ (the work queue) and Kafka (the event log); the next module consumes from RabbitMQ, claims an inbox key in the same transaction as its own write, and writes its own row and outbox row. Saga steps never call each other in-process. Delivery is at-least-once: the relay can publish and crash before it marks a row sent, so the row goes out again, and every consumer dedupes with its inbox key. Each saga step only moves an order forward from the status it expects.

> The Compose network is local and unauthenticated. There is no login, no token and no cookie. Do not publish these ports; Compose binds all of them to `127.0.0.1`.

![The warehouse UI after a few orders: one dispatched, one cancelled with its pick slip kept, one rejected for the poison token, one short](docs/screenshot.png)

*The UI reads everything from the API. Shown: a dispatched order, a cancellation that gave 8 units back and kept its pick slip, an order rejected for the poison token, and one that was short.*

## Order statuses

```mermaid
flowchart LR
    Requested -->|units held| Held
    Requested -->|short: no hold written| Rejected
    Held -->|pick slip issued| Dispatched
    Held -->|poison reference: slip rejected| Rejected
    Held -->|cancellation| Cancelled
    Dispatched -->|cancellation| Cancelled
```

`Cancelled` and `Rejected` (after a hold) give the units back through a `ReleaseStock` command, and Inventory releases a hold at most once. The pick slip has its own status (`Issued` or `Rejected`) and a cancellation never changes it. If the cancellation gets there before the final mark, the late mark finds the order is no longer `Held` and does nothing.

## How the modules talk

```mermaid
sequenceDiagram
    participant C as Client
    participant O as Orders
    participant I as Inventory
    participant D as Dispatch

    C->>O: POST /api/orders
    Note over O: order + outbox row, one transaction
    O-->>C: 202 Requested
    O->>I: stock.reserve (relay → RabbitMQ, copy to Kafka)
    Note over I: inbox claim, conditional UPDATE, hold, outbox row
    alt on-hand units cover the order
        I->>D: stock.held
        I->>O: stock.held (Requested → Held)
        alt reference is clean
            Note over D: pick slip, outbox row
            D->>O: slip.issued (Held → Dispatched)
        else reference contains POISON
            Note over D: committed Rejected, then the message is dead-lettered
            D->>I: stock.release (compensating command)
            D->>O: slip.rejected (Held → Rejected)
            I->>O: stock.released
        end
    else short
        I->>O: stock.short (Requested → Rejected, no hold)
    end
    C->>O: POST /api/orders/{id}/cancellation
    O->>I: stock.release (Held or Dispatched → Cancelled)
    I->>O: stock.released
```

| Module | Owns | Consumes | Emits |
| --- | --- | --- | --- |
| **Orders** (`Modules/Orders`) | `Orders`: status, units, fee, reference. Validation, the poison rule, the fee. | `stock.held`, `stock.short`, `slip.issued`, `slip.rejected`, `stock.released` | `stock.reserve`, `stock.release` (cancellation) |
| **Inventory** (`Modules/Inventory`) | `Skus.OnHandUnits` (sole writer after the SKU is added) and `StockHolds`. | `stock.reserve`, `stock.release` (prefetch 1) | `stock.held`, `stock.short`, `stock.released` |
| **Dispatch** (`Modules/Dispatch`) | `PickSlips`. | `stock.held` | `slip.issued`, `slip.rejected`, `stock.release` (poison) |
| **Messaging** (`Messaging`) | `OutboxMessages`, `InboxMessages`, the relay, the consumer loop with retry and dead-letter, the Kafka log. | | |

RabbitMQ topology is declared in code (`Messaging/Topology.cs`): exchange `warehouse.work`, one queue per module (`inventory.commands`, `dispatch.work`, `orders.events`), a `.retry` queue per module (2 s TTL, then back to the main queue, 3 attempts), and a `.dlq` per module behind `warehouse.dlx`. A transient failure retries; a poison message goes straight to the dead-letter queue after its rejection is committed. Kafka has one topic, `warehouse.events`, one partition, replication factor 1, keyed by order id.

## Run it

Needs Node 22 (npm 10), .NET SDK 10.0.401 (`global.json`), Docker (arm64 is fine) and python3 for the smoke script.

```bash
npm ci
docker compose up -d --wait db        # dotnet test uses Postgres on 5473 (database warehouse_test)
dotnet test tests/api/Warehouse.Api.Tests.csproj
docker compose up --build --wait      # db, kafka, rabbitmq, api, web
./scripts/smoke.sh                    # last line: warehouse smoke passed
npx playwright install chromium       # once
npx playwright test                   # one spec against http://127.0.0.1:8093
docker compose down                   # keeps the Postgres volume; `down -v` drops it
```

| Service | Host port |
| --- | --- |
| Web (nginx, proxies `/api/`) | 8093 |
| API (`/scalar/v1`, `/api/health`) | 5213 |
| Postgres 17 | 5473 |
| Kafka 3.8.1 (KRaft, one broker) | 9123 |
| RabbitMQ 3.13 / management UI (`platform` / `platform`) | 5703 / 15703 |

Inside the Compose network the API and nginx listen on 8080 and Kafka's internal listener is 9092.

## Try it with curl

```bash
curl -s -X POST http://127.0.0.1:5213/api/skus \
  -H 'content-type: application/json' \
  -d '{"code":"WIDGET-BLUE-1","name":"Blue Widget","category":"Hardware","bin":"Bin A-01","receivedUnits":40}'

curl -s -X POST http://127.0.0.1:5213/api/orders \
  -H 'content-type: application/json' \
  -d '{"skuCode":"WIDGET-BLUE-1","reference":"ORD-1001","units":12}'
# 202, status Requested, feeCents 2200. Use the returned id:

curl -s http://127.0.0.1:5213/api/orders/<id>               # Dispatched after a few seconds, with a pick slip number
curl -s http://127.0.0.1:5213/api/slips/<id>
curl -s http://127.0.0.1:5213/api/skus/WIDGET-BLUE-1        # onHandUnits 28
curl -s http://127.0.0.1:5213/api/orders/<id>/events        # the Kafka log for this order

curl -s -X POST http://127.0.0.1:5213/api/orders/<id>/cancellation \
  -H 'content-type: application/json'                       # units come back; the pick slip stays
```

An order is validated at the door: reference required (at most 40 characters), 1 to 50 units. The shipping fee is 400 cents plus 150 cents per unit. The poison match is a case-sensitive substring: `ORD-POISON-7` and any longer token containing `POISON` match.

## Develop with Nx

The repository is an Nx workspace (`nx.json`, plugin `@nx/dotnet`). `npx nx show projects` lists `api` (the ASP.NET Core monolith), `web` (the Angular app) and the inferred xUnit project.

```bash
docker compose up -d --wait db kafka rabbitmq   # the brokers and Postgres, not the app
npx nx serve api                                # http://localhost:5213
npx nx serve web                                # http://localhost:4293, proxies /api to the API
WEB_URL=http://localhost:4293 npx playwright test   # the same browser specs, against the dev servers
npx nx build web && npx nx build api            # what CI builds
```

## What is tested, and where it runs

- **xUnit against real Postgres** (`tests/api`, 25 tests): two tasks with two `DbContext`s fight for one remaining unit (one `Held`, one `Short`, on-hand 0, no hold for the loser); empty input, over-limit input, integer overflow (4294967297 must not wrap to 1), the fee, the poison substring; the same message delivered twice is claimed once; a release returns the units once; the poison rejection is committed before the dead-letter; a cancellation followed by the late final mark leaves the order `Cancelled` and the pick slip `Issued`. The race test was run against a read-then-write version of the hold, where it failed, and against the conditional update, where it passes.
- **`scripts/smoke.sh`** drives the composed stack with a run id on every code, so a kept volume does not collide: validation, the happy path (fee, pick slip, Kafka log), three concurrent orders for one unit (one dispatched, two short, no hold for the losers), the poison reference (rejected, `dispatch.work.dlq` gains one message, units restored), and a cancellation that lands between the pick slip and the final mark (order `Cancelled`, slip `Issued`, units back, second cancellation `409`). To make that last window wide enough to hit on purpose, Compose sets `Orders__FinalMarkDelayMs=1500`; it is a latency knob and defaults to 0.
- **Playwright** (`e2e/specs/place-order.spec.ts`): adds a SKU in the UI, places an order, sees `Dispatched` and the pick slip number, reloads to prove the page reads the API and keeps nothing in `localStorage`, then cancels and sees the units come back. It runs against the composed web port, on a developer machine. A second spec, `e2e/specs/ui-flows.spec.ts`, drives the page through the refusal messages (over-limit, empty reference, duplicate), the poison rejection, the loser of a race for the last of the scarce number, and the Kafka event log panel. Both specs pass against the Compose stack and against the Nx dev servers.
- **CI** (`.github/workflows/ci.yml`, on push): `npm ci`, Prettier check, `dotnet format whitespace --verify-no-changes`, `dotnet test` with a Postgres 17 service, and `nx build` for web and api. CI does not run the compose smoke or the Playwright spec.

## API

All routes are unauthenticated JSON. Interactive docs are at `http://127.0.0.1:5213/scalar/v1` (OpenAPI at `/openapi/v1.json`); the web port proxies `/api/`, `/scalar/` and `/openapi/` too.

| Method and path | What it does | Responses |
| --- | --- | --- |
| `GET /api/health` | Liveness. | `200 Healthy` |
| `POST /api/skus` | Adds a SKU with the units received. | `201`, `400` invalid, `409` code exists |
| `GET /api/skus` | Lists SKUs. | `200` |
| `GET /api/skus/{code}` | One SKU with the live on-hand units. | `200`, `404` |
| `POST /api/orders` | Accepts an order (1 to 50 units) and starts the saga. | `202` Requested, `400` invalid, `404` unknown SKU, `409` duplicate reference |
| `GET /api/orders?skuCode=` | Lists the latest 100 orders, optionally for one SKU. | `200` |
| `GET /api/orders/{id}` | One order and its status. | `200`, `404` |
| `POST /api/orders/{id}/cancellation` | Moves a held or dispatched order to `Cancelled` and gives the units back. The pick slip is not touched. | `202`, `404`, `409` wrong status |
| `GET /api/orders/{id}/events` | The Kafka event log for one order, read from the start of the topic. | `200` |
| `GET /api/slips/{orderId}` | The pick slip row. | `200`, `404` until the step has run |

Errors come back as `application/problem+json` with a `title`.

## Configuration

Compose sets these on the API container. Defaults in `apps/api/appsettings.json` point at the published host ports, so `nx serve api` works against `docker compose up -d --wait db kafka rabbitmq`.

| Setting | Compose value | Meaning |
| --- | --- | --- |
| `ConnectionStrings__Warehouse` | `Host=db;Port=5432;Database=warehouse;Username=warehouse;Password=warehouse` | The one Postgres database. Migrations run on startup. |
| `RabbitMq__Host` / `__Port` / `__User` / `__Password` | `rabbitmq` / `5672` / `platform` / `platform` | Work queue. Virtual host `/`. |
| `Kafka__BootstrapServers` | `kafka:9092` | Event log. |
| `Orders__FinalMarkDelayMs` | `1500` | Milliseconds the final mark waits. A latency knob so the smoke can land a cancellation inside the window. `0` outside Compose. |
| `WAREHOUSE_TEST_DB` (tests only) | | Postgres connection for `dotnet test`. Defaults to `localhost:5473`, database `warehouse_test`. CI points it at its service container. |

## Look inside and fix things

```bash
docker compose ps                      # five services, all healthy
docker compose logs -f api             # relay, consumers, poison and retry lines
docker compose exec db psql -U warehouse -d warehouse -c 'select "RoutingKey","SentAt" from "OutboxMessages" order by "Id" desc limit 10'
open http://127.0.0.1:15703            # RabbitMQ management, platform / platform
```

- **Dead-lettered messages** sit in `dispatch.work.dlq`. Nothing replays them; inspect them in the management UI. Retries wait in `<queue>.retry` for 2 seconds.
- **A port is already in use**: `lsof -nP -i :5213` shows who holds it. Compose publishes only on `127.0.0.1`.
- **`dotnet test` cannot connect**: start Postgres first with `docker compose up -d --wait db`.
- **The smoke cannot reach the API**: run `docker compose up --build --wait` first; override the target with `API=... RABBIT=... ./scripts/smoke.sh`.
- **Start clean**: `docker compose down -v` drops Postgres, Kafka and RabbitMQ data. Plain `down` keeps them, and the smoke is safe on kept data because every code carries a run id.
- **The Kafka log looks empty right after an order**: the relay publishes within a second or so; reload the event log.

## Known limits

- It is unauthenticated on purpose. Anything that can reach the API can read and write everything.
- There is one warehouse and no receiving flow beyond adding a SKU: units only go down through holds and back up through releases.
- Delivery is at-least-once, not exactly-once. The inbox makes repeats harmless for the saga; the Kafka log can hold a duplicate event after a relay crash.
- One broker, one partition, replication factor 1, one API instance. The relay does not coordinate across instances (the inbox would absorb the duplicates, but the outbox is never trimmed).
- `GET .../events` scans the whole topic for each call. Fine here, not something to expose at scale.
- Lists are capped at 100 rows and not paginated.
- Dead-lettered messages are not replayed automatically.
- The browser spec and the compose smoke run on a developer machine; CI does not run them.

## Cloud mapping

This repository runs on Compose. Nothing in it calls a cloud account.

| Here | On AWS |
| --- | --- |
| Postgres 17 | RDS for PostgreSQL |
| Kafka 3.8 | MSK |
| RabbitMQ 3.13 | Amazon MQ, or SQS with a dead-letter queue |
| The API and the web app | ECS or App Service |

## Layout

```
apps/api      ASP.NET Core monolith: Modules/{Orders,Inventory,Dispatch}, Messaging, Data, Migrations
apps/web      Angular 21, styled with Tailwind 4 utilities only
tests/api     xUnit against Postgres
e2e/          Playwright
scripts/      smoke.sh
compose.yaml  Postgres, Kafka, RabbitMQ, API, web
```

Nx 23.2.1 orchestrates `nx build web` and `nx build api`; EF Core migrations are committed and the API applies them on startup.

## Other systems in this .NET track

- [dotnet-freight](https://github.com/joshbarros/dotnet-freight): container space on ocean sailings.
- [dotnet-clinic](https://github.com/joshbarros/dotnet-clinic): appointment slots in a provider session.
- [dotnet-ledger](https://github.com/joshbarros/dotnet-ledger): outgoing payments from an account balance.
- [dotnet-academy](https://github.com/joshbarros/dotnet-academy): course cohort seats and enrollments.
