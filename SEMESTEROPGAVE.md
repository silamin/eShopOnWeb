# Semesteropgave: eShopOnWeb to microservices (Strangler Pattern)

Systemintegration + Store Systemer, autumn 2026. Amin Aouina (single-person group).
This is a fork of Microsoft's eShopOnWeb (ASP.NET Core 8). Base commit: `4da8212`.

## Run it

Needs Docker Desktop. From the repo root (the folder with `eShopOnWeb.sln`):

```
docker compose build
docker compose up
```

| What | URL |
| --- | --- |
| Web (MVC shop) | http://localhost:5106 |
| Public API (Swagger) | http://localhost:5200/swagger |
| SQL Server (azure-sql-edge) | localhost:1433 |

Verified Sat 7 Nov 2026 on Docker Desktop 4.88.1:
- `localhost:5106` shows the catalog ("Showing 10 of 12 products", 2 pages).
- `localhost:5200/swagger` loads "My API v1" with the Auth, CatalogBrand, CatalogItem and CatalogType endpoints.
- On first start the log can show seeding errors. Web and API both try to `CREATE DATABASE` at the same time; one of them wins (here the API seeded it) and the shop works. This is a startup race in the unmodified monolith, not something added here.

## Krav checklist (from Semesteropgave.pdf)

- [ ] 1. Services: split the monolith into microservices
- [ ] 2. Strangler pattern
- [ ] 3. Lagerstyring (inventory)
- [ ] 4. API gateway
- [ ] 5. Asynchronous communication
- [ ] 6. Own database per service
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

- **Synchronous, one place:** at add-to-basket the monolith asks the Catalog service for the price (retry and circuit breaker on that call). The price is then stored in `BasketItem.UnitPrice` as today. With the breaker open, add-to-basket fails fast with "catalog unavailable, try again" and the basket is unchanged. There is no fallback to the old catalog tables, because they go stale once `catalog-db` takes the writes.
- **Asynchronous over RabbitMQ** (topic exchange `eshop.events`, routing keys): everything else.
  - `CatalogItemCreated`: Catalog to Inventory, which creates a stock line. The starting quantity is `INVENTORY_STARTING_QUANTITY` (default 0; the compose file sets 10 for the demo).
  - `BasketCheckedOut`: monolith to Ordering, through an explicit mapper (no domain types on the wire).
  - `OrderCreated`: Ordering to Inventory, which reserves stock and answers `StockReserved` or `StockRejected`.
  - `PaymentRequested` (order id, total): Ordering to Payment, published when Ordering receives `StockReserved`. Payment decides on the total and answers `PaymentSucceeded` or `PaymentFailed`.
  - `OrderCancelled`: Ordering to Inventory, published when Ordering receives `StockRejected` or `PaymentFailed`. Inventory releases the reservation only on `OrderCancelled`, and only a row with status `Reserved` (compensation).
  - Messages that keep failing go to a dead-letter queue.
- **Order flow as an orchestrated saga, Ordering in charge:** checkout, order created (pending), `StockReserved`, `PaymentRequested`, `PaymentSucceeded`, order paid; or the order is cancelled, `OrderCancelled` is published and Inventory releases the stock. The whole saga reads in one place.

### Reliability of the message flow

Inventory and Payment are idempotent by order id, store their decision (including `Rejected`), re-publish it on a duplicate, and acknowledge the incoming message only after the broker has confirmed the outgoing event. The commit-then-publish gap at the other producers is a known limit; see the Decisions list below.

### Security

The gateway validates JWTs on `/api/*` only; page routes keep the monolith's cookie login. The Catalog service validates the forwarded token itself and is not published outside the compose network. One signing key is read from the environment by the issuer and every validator.

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
- **Catalog down at add-to-basket.** When the circuit breaker on the Catalog call is open, add-to-basket fails fast with a clear "catalog unavailable, try again" message, and the basket stays as it was. Rejected: falling back to the monolith's own catalog tables. From Tue 10 `catalog-db` takes the writes, so the old tables go stale, and a silent fallback would put items in the basket at old prices.
- **No stock on the catalog page.** Stock is enforced only at checkout: Inventory reserves the items, or rejects with `StockRejected` and the order saga cancels the order. The catalog page shows no "in stock / sold out". No krav asks for it (krav 3 is reservation at checkout), and showing stock would couple Catalog to Inventory, either through a synchronous call or a replicated stock copy. Out of scope for the available hours.
- **Authentication.** The gateway validates JWTs on `/api/*` only. MVC page routes pass through untouched and keep the monolith's cookie login. Identity stays in the monolith and issues the tokens (the existing Public API `AuthenticateEndpoint`). The Catalog service also validates the forwarded JWT itself (same signing key, `AddJwtBearer`), and its port is not published outside the compose network. Gateway check + service check + no published port is defense in depth. Reads stay open and are rate-limited at the gateway.
- **Signing key.** The JWT signing key must come from configuration in every component that signs or validates, or moving only the validators fixes nothing. The issuer (`IdentityTokenClaimService`, in the monolith) and the validators (`PublicApi/Program.cs`, the gateway, the Catalog service) all read one key from the environment. It is set in a gitignored `.env` with a new random value, because the inherited `AuthorizationConstants.JWT_SECRET_KEY` is public in the repo. The constant stays only as an explicit development default that this README calls out, or is removed. Done with the Catalog service (Tue 10).
- **Ordering owns the order, not the user.** `BuyerId` stays a plain string and `ShipToAddress` is a snapshot owned by the order, taken at order time. No user data is copied into ordering-db; Identity stays the only owner of user data.
- **Inventory is idempotent by order id.** Inventory keeps a reservations table with `OrderId` as primary key. Reserving the stock and inserting the reservation row run in one database transaction, so two concurrent deliveries of the same message cannot both pass the check; on a duplicate-key error the stored outcome is re-published (see the re-publish entry below). The row carries a status (`Reserved` / `Rejected` / `Released`), so the saga's compensation is idempotent too: a repeated `OrderCancelled` does not release the stock twice, and only a `Reserved` row is ever released. The RabbitMQ message is acknowledged manually, after the database commit and the confirmed publish (see the re-publish entry below), so a crash in between causes a redelivery and not a lost message.
- **Duplicate delivery re-publishes the stored outcome.** When Inventory receives an order it has already handled (duplicate key on `OrderId`), it does not just ignore the message: it reads the stored status and publishes the matching event again (`StockReserved` or `StockRejected`; a repeated `OrderCancelled` changes nothing). `Rejected` is stored as a status as well, so a redelivery repeats the same `StockRejected` and does not re-check stock that may have changed. Consumers of these events (Ordering) are idempotent in the same way. The sequence is commit, then publish, then ack, with RabbitMQ publisher confirms on the outgoing event: the incoming message is acknowledged only after the broker has confirmed the event.
- **Known limit: commit-then-publish.** The gap between the database commit and the publish exists at every producer: the monolith publishing `BasketCheckedOut`, Ordering publishing `OrderCreated`, Inventory publishing its outcome. The fix in general is the outbox pattern (write the event to an outbox table in the same transaction and publish from there). It is not built here; Inventory's re-publish on redelivery covers its own case.
- **Payment is simulated, deterministically.** An order whose total (carried by `PaymentRequested`) is above a configurable limit (`PAYMENT_DECLINE_ABOVE`, set low in tests) is rejected with `PaymentFailed`; anything else gives `PaymentSucceeded`. Nothing is random, so the saga test, the end-to-end test and the demo can trigger the compensation path on purpose. It is not a card-number rule: the checkout page has no card field (only a "[ Pay Now ]" button, and the shipping address is hardcoded in `Checkout.cshtml.cs`), so a card rule would need new UI. Payment follows the Inventory pattern: its decision is stored per order id and re-published on a duplicate, with publisher confirms and the incoming message acknowledged only after the broker has confirmed.
- **Starting stock is configurable.** `CatalogItemCreated` makes Inventory create a stock line with quantity `INVENTORY_STARTING_QUANTITY`. The default is 0, the honest real-world value; docker-compose sets 10 for the demo, so a demo checkout works without an extra step. `StockRejected` is deterministic too: put one item at quantity 11 in the basket. An admin endpoint to set stock is optional, built only if the hours allow; otherwise this README names it as the real-world step.
