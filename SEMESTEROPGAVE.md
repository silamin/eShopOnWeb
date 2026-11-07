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

_To be written: domain analysis, which services and why._

### Decisions so far

- **Basket price.** At add-to-basket the monolith asks the Catalog service synchronously for the price (with retry and circuit breaker on that call). The price is still stored as a snapshot in `BasketItem.UnitPrice`, as in the original code. Checkout (`BasketCheckedOut`) and catalog to inventory stay asynchronous.
