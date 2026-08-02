# Evently

An event ticketing platform built on **.NET 8** and **ASP.NET Core Minimal APIs**, demonstrating how to evolve a vertical-slice monolith into a **Modular Monolith** using **Clean Architecture** and key principles from **Domain-Driven Design (DDD)**.

Three modules — **Events**, **Users**, and **Ticketing** — sit on a shared **Common** layer, each owning its own database schema and communicating only through explicit contracts. The result keeps the development simplicity of a monolith while preserving the seam lines that make future extraction into services tractable.

---

## Table of Contents

1. [The Architecture Decision](#the-architecture-decision)
2. [Project Structure](#project-structure)
3. [Module Anatomy](#module-anatomy)
4. [Module Communication](#module-communication)
5. [Domain Model](#domain-model)
6. [CQRS and the Dual-ORM Strategy](#cqrs-and-the-dual-orm-strategy)
7. [Cross-Cutting Concerns](#cross-cutting-concerns)
8. [Authentication & Authorization](#authentication--authorization)
9. [Technology Stack](#technology-stack)
10. [API Reference](#api-reference)
11. [Getting Started](#getting-started)
12. [Configuration](#configuration)
13. [Database Migrations](#database-migrations)
14. [Design Decisions & Trade-offs](#design-decisions--trade-offs)
15. [Known Gaps & Roadmap](#known-gaps--roadmap)

---

## The Architecture Decision

### Why Modular Monolith?

The classic monolith accumulates coupling: every class can reference every other, all tables share one schema, and the only seam is the deployment unit. Microservices solve coupling but introduce distributed transactions, network latency, and operational overhead — a heavy price before the domain is well understood.

**Modular Monolith** is the middle path:

- **Single deployable unit** — no distributed systems complexity
- **Hard module boundaries** enforced at the project/assembly level
- **No shared database state** — each module owns a Postgres schema (`events`, `users`, `ticketing`)
- **Explicit contracts** between modules, so extraction later is mechanical rather than archaeological

When a module needs to become a service, its application and domain layers move unchanged; only the infrastructure wiring is replaced.

### Why Clean Architecture?

Dependencies point inward. The domain has no dependency on any framework, ORM, or database, which makes it testable in isolation and framework-agnostic. The application layer defines interfaces that infrastructure must satisfy.

### Why Domain-Driven Design?

DDD is applied selectively:

- **Aggregate Roots** — `Event`, `Order`, `Payment`, `Ticket`, `User` control their own invariants
- **Factory Methods** — `Order.Create(...)` is the only way to construct a valid aggregate
- **Domain Events** — raised inside aggregates, dispatched after commit
- **Repository Pattern** — the domain declares the interface; infrastructure implements it
- **Result Pattern** — expected failures return `Result`/`Result<T>`; exceptions are reserved for the genuinely unexpected

Constructs that add ceremony without value are deliberately deferred.

---

## Project Structure

```
evently/
├── src/
│   ├── API/
│   │   └── Evently.Api/                    # Composition root
│   │
│   ├── Common/                             # Shared kernel (4 projects)
│   │   ├── Evently.Common.Domain/          # Entity, Result, Error, DomainEvent
│   │   ├── Evently.Common.Application/     # CQRS abstractions, behaviors, EventBus
│   │   ├── Evently.Common.Infrastructure/  # Auth, caching, clock, interceptors
│   │   └── Evently.Common.Presentation/    # IEndpoint, ApiResults
│   │
│   └── Modules/
│       ├── Events/                         # Events, categories, ticket types
│       ├── Users/                          # Registration, profiles, roles
│       └── Ticketing/                      # Carts, orders, payments, tickets
│
├── .files/                                 # Keycloak realm export
├── docker-compose.yml
└── Directory.Build.props
```

### The Dependency Rule

```
Common.Domain ◄── Common.Application ◄── Common.Infrastructure
     ▲                    ▲                      ▲
Module.Domain      Module.Application     Module.Infrastructure
     ▲                    ▲                      │
     └────────────────────┴── Module.Presentation┘
                                                  ▲
                                           Evently.Api
```

No `Common.*` project references a module. No module references another module's internals — only its `PublicApi` or `IntegrationEvents` contract project.

---

## Module Anatomy

Every module follows the same template, so adding one is filling in a known shape rather than inventing a structure.

| Project | References | Contains |
|---|---|---|
| **Domain** | Common.Domain | Aggregates, domain events, repository interfaces, errors |
| **Application** | Common.Application, Domain | Commands, queries, handlers, validators |
| **Infrastructure** | Common.Infrastructure, Application, Presentation | DbContext, EF configurations, repositories, module registration |
| **Presentation** | Common.Presentation, Application | `IEndpoint` implementations |
| **PublicApi** | Common.Domain | Cross-module read contracts (`IEventsApi`, `IUsersApi`) |
| **IntegrationEvents** | Common.Application | Published event contracts (Users only, so far) |

Each module exposes exactly two entry points to the host:

```csharp
public static IServiceCollection AddEventsModule(this IServiceCollection services, IConfiguration configuration);
public static void ConfigureConsumers(IRegistrationConfigurator registrationConfigurator);   // where applicable
```

Endpoints are discovered by assembly scanning (`AddEndpoints`) and mapped globally by `app.MapEndpoints()` — modules never touch the host's routing directly.

---

## Module Communication

Two mechanisms, chosen by whether the caller needs an answer:

**Synchronous reads — `PublicApi` projects.** When Ticketing needs event data, it depends on `Evently.Modules.Events.PublicApi` (an interface plus DTOs), never on `Events.Domain`. The implementation lives in the owning module's infrastructure.

**Asynchronous notifications — integration events over MassTransit.** When something noteworthy happens, the owning module publishes an `IntegrationEvent`; interested modules consume it:

```
Users:      UserRegisteredDomainEvent → UserRegisteredIntegrationEvent  ──┐
                                                                          │  MassTransit
Ticketing:  UserRegisteredIntegrationEventConsumer → creates Customer  ◄──┘
```

The bus is currently **in-memory** (`loopback://`), so swapping to RabbitMQ or Azure Service Bus is a configuration change in `InfrastructureConfiguration`, not a code change in modules.

Domain events stay **inside** a module. Integration events cross module boundaries. Keeping these distinct is what stops modules from leaking into each other.

---

## Domain Model

### Events module
`Event` (aggregate root) with lifecycle `Draft → Published → Completed/Cancelled`, plus `Category` and `TicketType`.

### Users module
`User` with `IdentityId` linking to Keycloak, and a `Role`/`Permission` model backing authorization.

### Ticketing module
The richest domain: `Customer`, `Event`/`TicketType` (local read models), `Order` + `OrderItem`, `Payment`, `Ticket`.

```csharp
public static Ticket Create(Order order, TicketType ticketType)
{
    var ticket = new Ticket
    {
        Id = Guid.NewGuid(),
        CustomerId = order.CustomerId,
        Code = $"tc_{Ulid.NewUlid()}",     // sortable, URL-safe
        CreatedAtUtc = DateTime.UtcNow
    };

    ticket.Raise(new TicketCreatedDomainEvent(ticket.Id));
    return ticket;
}
```

**Key patterns:** private setters (state changes only through methods), static factories (no invalid construction), domain events raised inside the aggregate, and `Result` returns for rule violations:

```csharp
public Result IssueTickets()
{
    if (TicketsIssued)
    {
        return Result.Failure(OrderErrors.TicketsAlreadyIssued);
    }

    TicketsIssued = true;
    Raise(new OrderTicketsIssuedDomainEvent(Id));

    return Result.Success();
}
```

Domain events are dispatched by `PublishDomainEventsInterceptor`, an EF Core `SaveChangesInterceptor` that collects events from tracked entities after commit and publishes them via MediatR.

---

## CQRS and the Dual-ORM Strategy

CQRS is applied as a real infrastructure split, not just naming.

**Writes → EF Core.** Change tracking suits aggregates with child collections; `IUnitOfWork` wraps the transaction; migrations manage schema.

**Reads → Dapper.** Read models are denormalized projections with no behavior, so raw SQL gives full control over query shape with no tracking overhead:

```csharp
await using DbConnection connection = await dbConnectionFactory.OpenConnectionAsync();

const string sql =
    """
    SELECT id AS Id, title AS Title, starts_at_utc AS StartsAtUtc
    FROM events.events
    WHERE id = @EventId
    """;

return await connection.QuerySingleOrDefaultAsync<EventResponse>(sql, request);
```

Both paths share one `NpgsqlDataSource` connection pool, so two ORMs cost nothing extra.

---

## Cross-Cutting Concerns

Registered once in `Common.Application`, applied to every module.

### MediatR Pipeline

```
Request ─► ExceptionHandling ─► RequestLogging ─► Validation ─► Handler
```

- **ExceptionHandlingPipelineBehavior** — wraps unhandled exceptions as `EventlyException`
- **RequestLoggingPipelineBehavior** — logs start/finish, pushes the module name into the Serilog `LogContext`
- **ValidationPipelineBehavior** — runs FluentValidation validators; returns a `Result` failure rather than throwing, and only applies to `IBaseCommand` (commands validate, queries don't)

### Other shared services

| Concern | Implementation |
|---|---|
| Caching | `ICacheService` over Redis, with in-memory fallback if Redis is unreachable |
| Clock | `IDateTimeProvider` — makes time testable |
| Logging | Serilog → Seq, structured, with request logging |
| Errors | RFC 7807 `ProblemDetails` via `GlobalExceptionHandler` |
| Health | `/health` aggregating Postgres, Redis, and Keycloak |

---

## Authentication & Authorization

**Keycloak** is the identity provider. Registration is a two-step flow: the user is created in Keycloak first, then persisted locally with the returned `IdentityId`.

**Authentication** — JWT bearer tokens validated against the Keycloak realm (`AddAuthenticationInternal`).

**Authorization** — permission-based rather than role-based. `Role` and `Permission` are modeled in the Users module; at request time `CustomClaimsTransformation` loads the caller's permissions, and a custom `PermissionAuthorizationPolicyProvider` resolves policies on demand:

```csharp
app.MapGet("users/profile", async (ISender sender) => { /* ... */ })
   .RequireAuthorization("users:read");
```

This means new permissions need no policy registration — the string *is* the policy.

---

## Technology Stack

| Concern | Technology | Version |
|---|---|---|
| Runtime | .NET | 8.0 |
| API | ASP.NET Core Minimal APIs | 8.0 |
| Database | PostgreSQL (schema per module) | latest |
| ORM (writes) | EF Core + Npgsql | 8.0.4 |
| ORM (reads) | Dapper | 2.1.35 |
| Mediator | MediatR | 12.2.0 |
| Validation | FluentValidation | 11.9.1 |
| Messaging | MassTransit (in-memory) | 8.2.1 |
| Identity | Keycloak + JWT Bearer | 8.0.4 |
| Cache | Redis (StackExchange) | 8.0.4 |
| Logging | Serilog + Seq | 8.0.1 |
| Health | AspNetCore.HealthChecks | 8.0.1 |
| IDs | Ulid | 1.4.1 |
| Analysis | SonarAnalyzer.CSharp | 9.24.0 |

### Build Configuration

All projects inherit from `Directory.Build.props`:

```xml
<Nullable>enable</Nullable>
<ImplicitUsings>enable</ImplicitUsings>
<TreatWarningsAsErrors>true</TreatWarningsAsErrors>
<AnalysisMode>All</AnalysisMode>
```

Strict by design — nullable warnings and analyzer findings fail the build, so quality debt cannot accumulate silently.

---

## API Reference

Swagger UI: `http://localhost:5000/swagger`

### Events

| Method | Route |
|---|---|
| `POST` | `/events` |
| `GET` | `/events` · `/events/{id}` · `/events/search` |
| `PUT` | `/events/{id}/publish` · `/events/{id}/reschedule` |
| `DELETE` | `/events/{id}/cancel` |
| `POST` / `GET` / `PUT` | `/categories`, `/categories/{id}`, `/categories/{id}/archive` |
| `POST` / `GET` / `PUT` | `/ticket-types`, `/ticket-types/{id}`, `/ticket-types/{id}/price` |

### Users

| Method | Route | Auth |
|---|---|---|
| `POST` | `/users/register` | anonymous |
| `GET` | `/users/profile` | `users:read` |
| `PUT` | `/users/{id}/profile` | authenticated |

### Ticketing

| Method | Route |
|---|---|
| `PUT` | `/carts/add` |
| `GET` | `/orders/{id}` |
| `GET` | `/tickets/{id}` · `/tickets/code/{code}` · `/tickets/order/{orderId}` |

Failures return RFC 7807 Problem Details:

```json
{
  "type": "https://tools.ietf.org/html/rfc7231#section-6.5.8",
  "title": "Identity.EmailIsNotUnique",
  "status": 409,
  "detail": "The specified email is not unique."
}
```

---

## Getting Started

### Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [Docker Desktop](https://www.docker.com/products/docker-desktop/)

### Run

```bash
docker-compose up --build
```

Migrations for all three modules are applied automatically on startup in Development.

| Service | URL | Credentials |
|---|---|---|
| API / Swagger | http://localhost:5000/swagger | — |
| Health | http://localhost:5000/health | — |
| Keycloak | http://localhost:18080 | `admin` / `admin` |
| Seq (logs) | http://localhost:8081 | — |
| PostgreSQL | `localhost:5438` | `postgres` / `postgres` |
| Redis | `localhost:6379` | — |

### Set the Keycloak client secret

The confidential client secret is **not** committed. After the realm imports, copy it from
Keycloak (Clients → `evently-confidential-client` → Credentials) and store it locally:

```bash
dotnet user-secrets set "Users:KeyCloak:ConfidentialClientSecret" "<secret>" \
  --project src/API/Evently.Api
```

### Smoke test

```bash
curl -X POST http://localhost:5000/users/register \
  -H "Content-Type: application/json" \
  -d '{"email":"test@test.com","password":"123456","firstName":"Test","lastName":"User"}'
```

---

## Configuration

Configuration is layered so each module owns its settings:

```
appsettings.json → appsettings.{Environment}.json
    → modules.{module}.json → modules.{module}.Development.json
        → user secrets (Development) → environment variables
```

`AddModuleConfiguration(["events", "users", "ticketing"])` loads each module's files. User secrets and environment variables are re-added **after** the module files so they always win — otherwise the JSON would silently override them.

### Secrets

Never commit credentials. `modules.users.json` ships with empty placeholders; real values go in user secrets locally (mounted into the container by `docker-compose.override.yml`) or environment variables in production:

```
Users__KeyCloak__ConfidentialClientSecret=<secret>
```

---

## Database Migrations

Each module owns its schema and migration history, so modules never block each other:

| Module | Schema | History table |
|---|---|---|
| Events | `events` | `events.__EFMigrationsHistory` |
| Users | `users` | `users.__EFMigrationsHistory` |
| Ticketing | `ticketing` | `ticketing.__EFMigrationsHistory` |

### Add a migration

```bash
dotnet ef migrations add <Name> \
  --context <Module>DbContext \
  --startup-project src/API/Evently.Api \
  --project src/Modules/<Module>/Evently.Modules.<Module>.Infrastructure \
  --output-dir Database/Migrations
```

Package Manager Console equivalent:

```powershell
Add-Migration <Name> -OutputDir Database\Migrations -Context <Module>DbContext `
  -StartupProject Evently.Api -Project Evently.Modules.<Module>.Infrastructure
```

### Resetting in development

Deleting migration files alone causes `42P07: relation already exists` — the tables and history rows survive. Drop the database as well:

```sql
DROP DATABASE evently WITH (FORCE);
CREATE DATABASE evently;
```

Then delete each `Database/Migrations` folder, regenerate `Create_Database` per module, and restart.

> **Keep `Migrations/.editorconfig`.** EF-generated code violates several analyzer rules (`IDE0161`, `S1186`, `S4581`, `CA1861`) that are errors under `TreatWarningsAsErrors`. The folder-scoped `.editorconfig` suppresses them for generated files only.

---

## Design Decisions & Trade-offs

**`IUnitOfWork` stays module-local.** Each module owns its `DbContext` and transaction boundary, so the abstraction belongs to the module, not Common — preserving module autonomy.

**Validation returns `Result`, not exceptions.** `ValidationPipelineBehavior` produces a failure result on the happy path. Exceptions are reserved for the genuinely unexpected, which keeps control flow explicit and cheap.

**Permission strings as policies.** A custom `PermissionAuthorizationPolicyProvider` resolves policies on demand, so adding a permission requires no startup registration.

**`TryAdd*` for shared infrastructure.** Common uses `TryAddSingleton`/`TryAddScoped` so a module can override a shared service without a duplicate-registration conflict.

**Graceful cache degradation.** If Redis is unreachable at startup, the app falls back to an in-memory distributed cache rather than failing to boot.

**Snake case naming.** `UseSnakeCaseNamingConvention()` maps PascalCase properties to Postgres columns globally, keeping entities free of database annotations. Dapper queries alias columns explicitly.

**Endpoints as classes.** Each endpoint is an `IEndpoint` implementation discovered by assembly scanning, so adding one never touches a shared registration file.

---

## Known Gaps & Roadmap

**Registration lacks compensation.** `RegisterUserCommandHandler` creates the Keycloak user, then saves locally. If the save fails, the Keycloak user is orphaned and retrying returns `409 EmailIsNotUnique`. Needs a compensating delete or a saga.

**No outbox.** `PublishDomainEventsInterceptor` dispatches after commit and in-memory, so events are lost if the process dies mid-dispatch. The durable fix is writing events to an outbox table in the same transaction and publishing from a background processor.

**In-memory event bus.** MassTransit is configured with the in-memory transport — fine for a single deployable, but integration events do not survive a restart. Swap to RabbitMQ when durability matters.

**No inbox / idempotent consumers.** Consumers do not deduplicate, so a redelivered message would be processed twice.

**No tests.** The architecture is built for testability — domain logic needs no infrastructure, handlers can be tested against mocked repositories, and integration tests can use Testcontainers.

**Ticketing is partially wired.** Domain and handlers exist; several endpoints and the payment integration remain.

**Package versions drift.** `Ulid` is referenced at both 1.3.1 and 1.4.1, FluentValidation at 11.9.1 and 11.9.2. Central Package Management (`Directory.Packages.props`) would consolidate them.

---

## License

MIT
