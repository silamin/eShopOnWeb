# Semesteropgave: eShopOnWeb to microservices (Strangler Pattern)

Systemintegration + Store Systemer, autumn 2026. Amin Aouina (single-person group).
This is a fork of Microsoft's eShopOnWeb (ASP.NET Core 8). Base commit: `4da8212`.

## Run it

Needs Docker Desktop. From the repo root (the folder with `eShopOnWeb.sln`):

```
cp .env.example .env      # once; .env is not committed (Windows PowerShell: Copy-Item .env.example .env)
docker compose build
docker compose up
```

`docker compose` stops with "copy .env.example to .env" if `.env` is missing: there are no default passwords in the compose files for the broker. The values in `.env.example` are placeholders that let a clean clone run; put your own random ones in `.env`.

| What | URL |
| --- | --- |
| Gateway (YARP), the entry point from Phase 0 | http://localhost:5100 (health: http://localhost:5100/gateway/health) |
| Web (MVC shop) and the Blazor admin | through the gateway only: http://localhost:5100 and http://localhost:5100/admin. The web's own port 5106 is not published |
| Public API (Swagger) | through the gateway: http://localhost:5100/swagger (route `publicapi-swagger`). The API's own port 5200 is not published |
| SQL Server (azure-sql-edge) | localhost:1433 |
| RabbitMQ management UI | http://localhost:15672, bound to 127.0.0.1 only; log in with `RABBITMQ_USER` / `RABBITMQ_PASSWORD` from `.env`; vhost `eshop`. The AMQP port 5672 is not published |
| Catalog service | not published; reached only through the gateway (`/api/catalog-items`, `/api/catalog-brands`, `/api/catalog-types`) |
| sql-init (one-shot) | runs at start, creates each service's database and login, then exits |

Verified Sat 7 Nov 2026 on Docker Desktop 4.88.1:
- Before the ports were unpublished (Sun 8): `localhost:5106` shows the catalog ("Showing 10 of 12 products", 2 pages).
- Before the ports were unpublished (Sun 8): `localhost:5200/swagger` loads "My API v1" with the Auth, CatalogBrand, CatalogItem and CatalogType endpoints.
- Phase 0, verified Sat 7 Nov 2026 in Chrome through the gateway: `localhost:5100/gateway/health` returns `{"status":"ok"}`; the catalog shows (10 of 12); add-to-basket works; login as the seeded admin works; `localhost:5100/admin` lists all 12 products and its API calls go to `http://localhost:5100/api/catalog-items`, `-brands` and `-types`.
- Sun 8 Nov 2026 (docker compose on a Linux container host, headless Chrome): with 5106 and 5200 not published, `localhost:5106` and `localhost:5200` refuse the connection; logged in as the seeded admin, `localhost:5100/admin` lists all 12 products, and every request the page makes goes to `localhost:5100` (`/api/catalog-items`, `-brands`, `-types`), none fails; `localhost:5100/swagger` loads the Public API's Swagger UI. The Blazor admin's `webBase` (and the web's and the API's `webBase`/`apiBase`) point at the gateway; the API's CORS origin is derived from `webBase`.
- Right after `docker compose up` the gateway can answer 502 for a while: the web waits for SQL Edge to start (up to 30 s) before it launches.
- On first start the log can show seeding errors. Web and API both try to `CREATE DATABASE` at the same time; one of them wins (here the API seeded it) and the shop works. This is a startup race in the unmodified monolith, not something added here.

## Krav checklist (from Semesteropgave.pdf)

- [ ] 1. Services: split the monolith into microservices
- [ ] 2. Strangler pattern (partly: the gateway sits in front of the monolith, and the first route is strangled: `/api/catalog-*` now goes to the Catalog service, and the monolith reads the catalog from it through `ICatalogClient`)
- [ ] 3. Lagerstyring (inventory)
- [ ] 4. API gateway (partly: YARP routing in place; JWT check on the catalog write routes in place; JWT on all of `/api/*`, rate limiting and load balancing across two instances still to do)
- [ ] 5. Asynchronous communication
- [ ] 6. Own database per service (partly: `catalog-db` with its own SQL login, created by the one-shot sql-init step)
- [ ] 7. Fault handling and monitoring
- [ ] 8. CI/CD
- [ ] 9. Documentation

## Service boundaries

_Target design, decided Sat 7 Nov 2026. Nothing below is built yet; the checklist above is updated as each part is._

The monolith (`Web` + `PublicApi` + shared `ApplicationCore` and `Infrastructure`) is cut along the business domains it already contains. Catalog and Ordering are carved out of it and Payment is added, which makes the three services of krav 1; Inventory is the separate krav 3 service. The monolith shrinks phase by phase behind a YARP gateway (Strangler Pattern).

| Part | Owns | Own database | Status in the design |
| --- | --- | --- | --- |
| **Monolith** (`Web`, `PublicApi`) | Web UI, Basket, Identity (login, JWT issuing) | existing `Microsoft.eShopOnWeb.CatalogDb` (after the split it holds only baskets; the name is inherited) + `Microsoft.eShopOnWeb.Identity` | stays, shrinks |
| **Catalog Service** | catalog items, brands, types | `catalog-db` | moved out of the monolith |
| **Ordering Service** | orders and their lines | `ordering-db` | moved out of the monolith |
| **Inventory Service** | stock per catalog item, reservations | `inventory-db` | the lagerstyringssystem from my own repo, as a service (krav 3) |
| **Payment Service** | payment decisions | `payment-db` | new, simulated |
| **API Gateway** (YARP) | one entry point, routing, JWT check, rate limiting, load balancing | none | in front of everything from phase 0 |

### Why these boundaries

- **Catalog, Ordering, Inventory and Payment** are different business capabilities with different change rates and different data. In the original code they share one `CatalogContext`, i.e. one database holding baskets, catalog items and orders. Giving each its own database (krav 6) is what makes the split real.
- **Basket and Identity stay in the monolith.** Basket stores only `CatalogItemId`, `UnitPrice` and `Quantity` (no link into the catalog tables), so it can stay while Catalog moves. Identity stays the only owner of user data.
- **Ordering stores a snapshot, not references.** An order line holds the item as ordered (`CatalogItemOrdered`: id, name, picture URI) and the unit price, so Ordering never needs Catalog to show an old order. `BuyerId` is a plain string and the shipping address is a snapshot owned by the order; no user data is copied into `ordering-db`.
- **No service reads another service's database.** Data moves by message or API call (krav 6).

### How the parts talk

- **Synchronous, one seam:** every catalog read in the monolith goes through one read-only `ICatalogClient` (HTTP to the Catalog service): the catalog page (items, brands, types), the basket page and the checkout snapshot (items by ids, batched, never one call per item) and the price at add-to-basket. One Polly retry and circuit-breaker policy sits there. The price is stored in `BasketItem.UnitPrice` as today. With the breaker open, the catalog page, the basket page and add-to-basket fail fast with "catalog unavailable, try again", and the basket is unchanged. There is no fallback to the old catalog tables, because they go stale once `catalog-db` takes the writes; they stay untouched and unused in the monolith (droppable later, no drop migration now).
- **Asynchronous over RabbitMQ** (topic exchange `eshop.events`, routing keys): everything else.
  - `CatalogItemCreated`: Catalog to Inventory, which creates a stock line. The starting quantity is `INVENTORY_STARTING_QUANTITY` (default 0; the compose file sets 10 for the demo).
  - `BasketCheckedOut`: monolith to Ordering, through an explicit mapper (no domain types on the wire). It carries the item snapshot (id, name, picture URI, unit price, quantity), built through `ICatalogClient` (items by ids), so Ordering never calls Catalog.
  - `OrderCreated`: Ordering to Inventory, which reserves stock and answers `StockReserved` or `StockRejected`.
  - `PaymentRequested` (order id, total): Ordering to Payment, published when Ordering receives `StockReserved`. Payment decides on the total and answers `PaymentSucceeded` or `PaymentFailed`.
  - `OrderCancelled`: Ordering to Inventory, published when Ordering receives `StockRejected` or `PaymentFailed`. Inventory releases the reservation only on `OrderCancelled`, and only a row with status `Reserved` (compensation).
  - Messages that keep failing go to a dead-letter queue.
- **Order flow as an orchestrated saga, Ordering in charge:** checkout, order created (pending), `StockReserved`, `PaymentRequested`, `PaymentSucceeded`, order paid; or the order is cancelled, `OrderCancelled` is published and Inventory releases the stock. The whole saga reads in one place.

### Reliability of the message flow

Inventory and Payment are idempotent by order id, store their decision (including `Rejected`), re-publish it on a duplicate, and acknowledge the incoming message only after the broker has confirmed the outgoing event. The commit-then-publish gap at the other producers is a known limit; see the Decisions list below.

### Security

The gateway validates the bearer token (same signing key as the issuer and the other validators) on the catalog write routes: `POST`, `PUT` and `DELETE` on `/api/catalog-items` need a valid token with the `Administrators` role, and are refused with 401/403 at the gateway before anything is forwarded. Reads, `/api/authenticate` and the rest of `/api/*` are anonymous at the gateway; the Public API checks its own endpoints. Extending the gateway check to all of `/api/*` is part of the gateway step (Sun 15). Page routes keep the monolith's cookie login. The Catalog service validates the forwarded token itself (defence in depth) and is not published outside the compose network. One signing key is read from the environment by the issuers and every validator.

### Krav coverage

| Krav | Where |
| --- | --- |
| 1 Services | The monolith is split into three: Catalog and Ordering are carved out of it, and Payment is the third (new: the monolith's "Pay Now" only creates the order). Inventory is the separate krav 3 system and is not counted toward krav 1. |
| 2 Strangler | YARP routes moved service by service; unconverted routes still go to the monolith |
| 3 Lagerstyring | Inventory Service: stock, reservation at checkout, release on cancel |
| 4 API gateway | YARP: routing, JWT, rate limiting, two instances for load balancing |
| 5 Async messaging | RabbitMQ events, saga, dead-letter queue |
| 6 Own database | one database per service, no cross-reads |
| 7 Fault handling | Polly retry + circuit breaker; Serilog with correlation id (ELK) |
| 8 CI/CD | GitHub Actions: build, test, Docker images, push to the registry (deploy) |
| 9 Documentation | this file (architecture decisions); APIs: Swagger per service; the Inventory service's events in and out and how it interacts with the rest, in the sections above |

### Decisions so far

- **Basket price.** At add-to-basket the monolith asks the Catalog service synchronously for the price (with retry and circuit breaker on that call). The price is still stored as a snapshot in `BasketItem.UnitPrice`, as in the original code. Checkout (`BasketCheckedOut`) and catalog to inventory stay asynchronous.
- **Catalog down at add-to-basket.** When the circuit breaker on the Catalog call is open, add-to-basket, the catalog page and the basket page fail fast with a clear "catalog unavailable, try again" message, and the basket stays as it was. Rejected: falling back to the monolith's own catalog tables. From Tue 10 `catalog-db` takes the writes, so the old tables go stale, and a silent fallback would put items in the basket at old prices.
- **No stock on the catalog page.** Stock is enforced only at checkout: Inventory reserves the items, or rejects with `StockRejected` and the order saga cancels the order. The catalog page shows no "in stock / sold out". No krav asks for it (krav 3 is reservation at checkout), and showing stock would couple Catalog to Inventory, either through a synchronous call or a replicated stock copy. Out of scope for the available hours.
- **Authentication.** The gateway validates JWTs on `/api/*` only. MVC page routes pass through untouched and keep the monolith's cookie login. Identity stays in the monolith and issues the tokens (the existing Public API `AuthenticateEndpoint`). The Catalog service also validates the forwarded JWT itself (same signing key, `AddJwtBearer`), and its port is not published outside the compose network. Gateway check + service check + no published port is defense in depth. Reads stay open and are rate-limited at the gateway.
- **Signing key and passwords: in `.env`, nowhere else.** The JWT signing key is read from configuration (`Jwt:Key`, set from `JWT_KEY`) by every component that signs or validates: the issuers (`IdentityTokenClaimService` in the monolith, which the Public API's `/api/authenticate` also uses) and the validators (Public API, gateway, Catalog service). There is no default: a missing key stops the service at startup. The inherited public constant `AuthorizationConstants.JWT_SECRET_KEY` is removed; the test projects sign their test tokens with their own test-only key and give the API that key through the environment. `.env` is in `.gitignore`; `.env.example` holds placeholders that make a clean clone run (copy it to `.env`, or put real random values there). `docker-compose.yml` passes `SA_PASSWORD`, the connection strings (`ConnectionStrings__CatalogConnection` / `__IdentityConnection` for Web and Public API, the Catalog service's own login) and `Jwt__Key` from `.env`, with no fallback value, so `docker compose` stops with "copy .env.example to .env" if one is missing. The `appsettings.Docker.json` files no longer carry a password or a key. The monolith and the Public API still connect as `sa` (they create and migrate their own databases); only the Catalog service has its own login so far. **The old key and `sa` password are still in the git history** and must be treated as public; the values in `.env` are new. After changing a password in `.env` for a stack that already ran, start from `docker compose down -v` (an existing SQL or broker volume keeps the old credentials). Done Tue 10.
- **Ordering owns the order, not the user.** `BuyerId` stays a plain string and `ShipToAddress` is a snapshot owned by the order, taken at order time. No user data is copied into ordering-db; Identity stays the only owner of user data.
- **Inventory is idempotent by order id.** Inventory keeps a reservations table with `OrderId` as primary key. Reserving the stock and inserting the reservation row run in one database transaction, so two concurrent deliveries of the same message cannot both pass the check; on a duplicate-key error the stored outcome is re-published (see the re-publish entry below). The row carries a status (`Reserved` / `Rejected` / `Released`), so the saga's compensation is idempotent too: a repeated `OrderCancelled` does not release the stock twice, and only a `Reserved` row is ever released. The RabbitMQ message is acknowledged manually, after the database commit and the confirmed publish (see the re-publish entry below), so a crash in between causes a redelivery and not a lost message.
- **Duplicate delivery re-publishes the stored outcome.** When Inventory receives an order it has already handled (duplicate key on `OrderId`), it does not just ignore the message: it reads the stored status and publishes the matching event again (`StockReserved` or `StockRejected`; a repeated `OrderCancelled` changes nothing). `Rejected` is stored as a status as well, so a redelivery repeats the same `StockRejected` and does not re-check stock that may have changed. Consumers of these events (Ordering) are idempotent in the same way. The sequence is commit, then publish, then ack, with RabbitMQ publisher confirms on the outgoing event: the incoming message is acknowledged only after the broker has confirmed the event.
- **Known limit: commit-then-publish.** The gap between the database commit and the publish exists at every producer: the monolith publishing `BasketCheckedOut`, Ordering publishing `OrderCreated`, Inventory publishing its outcome. The fix in general is the outbox pattern (write the event to an outbox table in the same transaction and publish from there). It is not built here; Inventory's re-publish on redelivery covers its own case.
- **Payment is simulated, deterministically.** An order whose total (carried by `PaymentRequested`) is above a configurable limit (`PAYMENT_DECLINE_ABOVE`, set low in tests) is rejected with `PaymentFailed`; anything else gives `PaymentSucceeded`. Nothing is random, so the saga test, the end-to-end test and the demo can trigger the compensation path on purpose. It is not a card-number rule: the checkout page has no card field (only a "[ Pay Now ]" button, and the shipping address is hardcoded in `Checkout.cshtml.cs`), so a card rule would need new UI. Payment follows the Inventory pattern: its decision is stored per order id and re-published on a duplicate, with publisher confirms and the incoming message acknowledged only after the broker has confirmed.
- **Starting stock is configurable.** `CatalogItemCreated` makes Inventory create a stock line with quantity `INVENTORY_STARTING_QUANTITY`. The default is 0, the honest real-world value; docker-compose sets 10 for the demo, so a demo checkout works without an extra step. `StockRejected` is deterministic too: put one item at quantity 11 in the basket. An admin endpoint to set stock is optional, built only if the hours allow; otherwise this README names it as the real-world step.
- **One read-only seam to Catalog.** All the monolith's catalog reads go through `ICatalogClient` (list, brands, types, item by id, items by ids). Batched: the basket page and the checkout snapshot use "items by ids", never one call per item. At checkout the monolith builds the `BasketCheckedOut` snapshot through it, so Ordering never calls Catalog. `ICatalogClient` is read-only; one retry and breaker policy lives there. It is a typed `HttpClient` (IHttpClientFactory) that calls the Catalog service directly on the compose network (`http://catalogservice:8080`), not through the gateway, so gateway rate limiting never throttles the monolith's own server-side calls and service-to-service traffic stays off the public entry point. Reads are open, so it sends no token.
- **MVC catalog admin pages are retired.** `Pages/Admin` (`EditCatalogItem` through `CatalogItemViewModelService.UpdateCatalogItem` and `UpdateAsync`) is a write path into the monolith's catalog tables; after the move those edits would go into the dormant tables and silently vanish. The pages are removed and `/Admin` redirects to `/admin`, the Blazor admin, which already does catalog create, update and delete through the gateway to the Catalog service.
- **catalog-db is its own database on the shared SQL Edge.** Each service gets its own database (krav 6) on the one SQL Edge instance, not a second container. Each new service connects with its own SQL login that can only reach its own database, never `sa`. A one-shot init step creates the databases and logins from `.env`. The server is still shared: one SQL server is a single point of failure, and isolation rests on separate databases and logins. If the hours run short and the demo uses one login, this README says so plainly.
- **Strangler data-migration step: same ids.** `catalog-db` gets the same 12 items with the same ids as the monolith's catalog tables (explicit ids in the seed, or the rows copied over once). Existing `BasketItem.CatalogItemId` values and order snapshots point at those ids; newly generated ids would silently attach baskets and orders to the wrong products. The same goes for brands and types, whose ids the items reference. Implemented by seeding with the same lists in the same order as the monolith's `CatalogContextSeed` (the HiLo sequences hand out 1..n in insertion order); the seed throws if the item ids are not 1..12.
- **ICatalogClient: plain 3 s timeout now, retry and breaker later.** The client throws `CatalogUnavailableException` on a timeout, a connection error or a 5xx; the monolith answers 503 with "The catalog is unavailable right now ... Your basket has not been changed". The catalog is read before the basket is touched, so a failed add-to-basket changes nothing. The Polly retry and circuit breaker are added to the client's registration (`ConfigureWebServices`) in the resilience step; the call sites do not change. Measured Sat 7 Nov with the Catalog service stopped: every failing request waits the full timeout, 3,02-3,05 s. With the breaker open it should answer at once; the after-number is measured in the resilience step and both go in the report.
- **The list endpoint returns `totalItems`.** The catalog page needs the total of the filtered set ("Showing 10 of 12"), so the Catalog service's list response carries `totalItems` next to `pageCount`. It is an extra field; the Blazor admin ignores it.
- **What was retired from the MVC admin.** The edit page `Pages/Admin/EditCatalogItem`, its partial `_editCatalog` and `CatalogItemViewModelService` are gone: they wrote into the monolith's catalog tables. `/admin` (the Blazor admin, `Pages/Admin/Index`) and the Admin link stay; it edits through the gateway into the Catalog service. Catalog changes show on the shop within 30 s: the monolith caches the catalog page, brands and types with an absolute 30 s expiry (not sliding: a sliding entry never expires under steady traffic, so a price change would never reach the shop).
- **RabbitMQ: one topic exchange, queues bound by key pattern, nothing published except the UI on loopback.** Exchange `eshop.events` (topic, durable). A publisher chooses only a routing key (`catalog.item.created`, `basket.checked-out`, `payment.succeeded`, ...); each consumer owns a durable queue and binds it by pattern: `inventory.catalog-events` takes `catalog.item.*`, `ordering.basket-events` takes `basket.checked-out`, `ordering.payment-events` takes `payment.*`. So a payment event never reaches Inventory's queue, which a fan-out exchange could not guarantee. The contracts (`CatalogItemCreated`, `BasketCheckedOut`, `PaymentSucceeded`), the routing keys and the topology are one shared class library, `src/Messaging`, referenced by every service; `docker compose run --rm event-probe` publishes one event per key and checks that each arrives only in the queue bound for it. Broker hardening: the image creates only the user from `.env` (the default `guest` user does not exist; `guest`/`guest` answers 401), in its own vhost `eshop` (the default `/` vhost is not created), and that user has rights on `eshop` only. **Port decision:** AMQP (5672) is not published to the host; services reach the broker on the compose network. The management UI (15672) is published as `127.0.0.1:15672` only, so the bindings can be shown in a browser on the same machine and nowhere else. Not done: TLS on the broker (the UI is plain HTTP on loopback; AMQP is plain inside the compose network).
- **Known leftover.** The monolith still creates and seeds its own catalog tables, and the Public API still has its own catalog endpoints. Nothing reads them: the gateway routes `/api/catalog-*` to the Catalog service and the monolith reads through `ICatalogClient`. They are removed in the cleanup step; the monolith's `CatalogDb` then holds only baskets.
