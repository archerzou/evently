# Evently

A production-grade event management REST API built on **.NET 8** and **ASP.NET Core Minimal APIs**, demonstrating how to evolve a vertical-slice monolith into a **Modular Monolith** using **Clean Architecture** and key principles from **Domain-Driven Design (DDD)**.

The project serves as a reference implementation for teams who want the development simplicity of a monolith while retaining the seam lines that allow future extraction into microservices — without the distributed-systems tax paid upfront.

---

## Table of Contents

1. [The Architecture Decision](#the-architecture-decision)
2. [Project Structure](#project-structure)
3. [Module Anatomy: Events](#module-anatomy-events)
4. [Domain Model](#domain-model)
5. [CQRS and the Dual-ORM Strategy](#cqrs-and-the-dual-orm-strategy)
6. [Technology Stack](#technology-stack)
7. [API Reference](#api-reference)
8. [Getting Started](#getting-started)
9. [Configuration](#configuration)
10. [Database Migrations](#database-migrations)
11. [Design Decisions & Trade-offs](#design-decisions--trade-offs)
12. [Known Gaps & Roadmap](#known-gaps--roadmap)

---

## The Architecture Decision

### Why Modular Monolith?

The classic monolith accumulates coupling over time. Every class can reference every other class; database tables share a single schema; the only seam is the deployment unit itself. The result is a big ball of mud that cannot be safely changed, tested in isolation, or extracted into services when scaling demands it.

Microservices solve the coupling problem but introduce a different class of problems: distributed transactions, network latency, operational complexity, and the need for mature DevOps tooling from day one. For a team at the start of a product's lifecycle — before the domain is well-understood — paying the microservices tax is almost always the wrong bet.

**Modular Monolith** is the middle path:

- **Single deployable unit** (no distributed systems complexity)
- **Hard module boundaries** enforced at the project/assembly level
- **No shared mutable state** between modules (each module owns its own database schema)
- **Communication contracts** defined up front, making future extraction tractable

When traffic demands extraction, a module becomes a microservice: you deploy its Infrastructure layer separately and replace in-process calls with HTTP or message broker calls. The application and domain layers move with zero changes.

### Why Clean Architecture?

Clean Architecture (popularized by Robert C. Martin) enforces a single rule: **dependencies point inward**. The domain is the innermost layer and has no dependency on any framework, ORM, or database. This makes it:

- **Testable in isolation** — domain logic can be unit-tested without a database
- **Framework-agnostic** — swapping EF Core for another ORM does not touch the domain
- **Explicit about contracts** — the application layer defines interfaces the infrastructure must satisfy

### Why Domain-Driven Design?

DDD is applied *selectively*, not dogmatically. The project uses:

- **Aggregate Roots** — `Event` is the aggregate root; it controls its own invariants and lifecycle
- **Factory Methods** — `Event.Create(...)` is the only way to construct a valid event
- **Domain Events** — `EventCreatedDomainEvent` is raised inside the aggregate; it can be consumed by other modules or published to a message broker without changing the domain
- **Repository Pattern** — the domain defines `IEventRepository`; the infrastructure satisfies it

DDD constructs that add ceremony without value (Value Objects for simple strings, bounded context maps) are deliberately deferred until the domain grows complex enough to justify them.

---

## Project Structure

```
evently/
├── src/
│   ├── API/
│   │   └── Evently.Api/                        # Composition root — wires modules together
│   │       ├── Program.cs
│   │       ├── Extensions/
│   │       │   └── MigrationExtensions.cs
│   │       ├── appsettings.json
│   │       ├── appsettings.Development.json
│   │       └── Dockerfile
│   │
│   └── Modules/
│       └── Events/                             # Self-contained Events module
│           ├── Evently.Modules.Events.Domain/
│           ├── Evently.Modules.Events.Application/
│           ├── Evently.Modules.Events.Infrastructure/
│           └── Evently.Modules.Events.Presentation/
│
├── evently.slnx                                # Solution file
├── Directory.Build.props                       # Centralized MSBuild configuration
├── docker-compose.yml
└── docker-compose.override.yml
```

### The Dependency Rule

Each project layer may only reference layers further inward. Outward references are compile-time errors.

```
Presentation  →  Application  →  Domain
Infrastructure               →  Domain
                 Application  ←  Infrastructure (via DI, not reference)
API            →  Infrastructure (only for DI registration)
```

The API project references `Evently.Modules.Events.Infrastructure` solely to call `AddEventsModule()` and `MapEndpoints()`. It never references Domain or Application directly.

---

## Module Anatomy: Events

Every module follows the same four-project template. This is intentional: adding a new module is copying the template and filling in the domain. The structure scales from one module to twenty without architectural drift.

### Domain Layer — `Evently.Modules.Events.Domain`

**No external dependencies.** This project references only the .NET BCL.

```
Domain/
├── Abstractions/
│   ├── Entity.cs              # Base class: manages domain event collection
│   ├── IDomainEvent.cs        # Marker interface: Id + OccurredOnUtc
│   └── DomainEvent.cs         # Base record implementing IDomainEvent
└── Events/
    ├── Event.cs               # Aggregate root
    ├── EventStatus.cs         # Enum: Draft, Published, Completed, Cancelled
    ├── IEventRepository.cs    # Repository contract (dependency inversion)
    └── EventCreatedDomainEvent.cs
```

### Application Layer — `Evently.Modules.Events.Application`

**References Domain only.** Defines use cases as CQRS Commands/Queries dispatched via MediatR.

```
Application/
├── AssemblyReference.cs       # Static marker for assembly scanning
├── Abstractions/Data/
│   ├── IUnitOfWork.cs         # Commit writes
│   └── IDbConnectionFactory.cs # Open a read connection
└── Events/
    ├── CreateEvent/
    │   ├── CreateEventCommand.cs         # IRequest<Guid>
    │   ├── CreateEventCommandHandler.cs  # Orchestrates domain + persistence
    │   └── CreateEventCommandValidator.cs # FluentValidation rules
    └── GetEvent/
        ├── GetEventQuery.cs              # IRequest<EventResponse?>
        ├── GetEventQueryHandler.cs       # Raw SQL via Dapper
        └── EventResponse.cs             # Read model / DTO
```

### Infrastructure Layer — `Evently.Modules.Events.Infrastructure`

**References Application and Domain.** Satisfies every interface the inner layers define.

```
Infrastructure/
├── EventsModule.cs            # DI registration + endpoint wiring (public API of the module)
├── Database/
│   ├── EventsDbContext.cs     # EF Core context — also implements IUnitOfWork
│   ├── Schemas.cs             # Schema name constant: "events"
│   └── Migrations/            # EF Core migration files
├── Data/
│   └── DbConnectionFactory.cs # Implements IDbConnectionFactory via NpgsqlDataSource
└── Events/
    └── EventRepository.cs     # Implements IEventRepository via EF Core DbSet
```

### Presentation Layer — `Evently.Modules.Events.Presentation`

**References Application only** (via MediatR's `ISender`). Converts HTTP requests into Commands/Queries; converts results back to HTTP responses.

```
Presentation/
├── Tags.cs                    # Swagger grouping constant
└── Events/
    ├── EventEndpoints.cs      # Registers all event endpoints
    ├── CreateEvent.cs         # POST /events
    └── GetEvent.cs            # GET  /events/{id}
```

---

## Domain Model

### The `Event` Aggregate

```csharp
public sealed class Event : Entity
{
    public Guid Id { get; private set; }
    public string Title { get; private set; }
    public string Description { get; private set; }
    public string Location { get; private set; }
    public DateTime StartsAtUtc { get; private set; }
    public DateTime? EndsAtUtc { get; private set; }
    public EventStatus Status { get; private set; }

    public static Event Create(string title, string description, string location,
                               DateTime startsAtUtc, DateTime? endsAtUtc)
}
```

**Key design decisions:**

- **Private setters** — state can only change through methods defined on the aggregate. No external code can set `Status = Cancelled` directly; it must call a future `Cancel()` method that enforces business rules.
- **Static factory method** — `Event.Create()` is the single entry point for creating a valid event. Constructors are private/parameterless (required by EF Core); this prevents invalid object construction from anywhere else in the codebase.
- **Raises domain event on creation** — `EventCreatedDomainEvent` is appended to the entity's event collection inside `Create()`. The infrastructure layer can publish these events after the transaction commits.

### Domain Events

```csharp
public interface IDomainEvent
{
    Guid Id { get; }
    DateTime OccurredOnUtc { get; }
}

public sealed record EventCreatedDomainEvent(Guid EventId) : DomainEvent;
```

Domain events are raised *inside* the aggregate — which means they fire whether the call comes from an HTTP request, a background job, or a test. The aggregate has no idea what happens next; it only records that something significant occurred. This decoupling is the foundation for:

- Notifying other modules (e.g., Notifications module sends a confirmation email)
- Projecting read models
- Publishing integration events to a message broker

Currently, events are raised but not yet dispatched — see [Known Gaps](#known-gaps--roadmap).

---

## CQRS and the Dual-ORM Strategy

The project applies **Command Query Responsibility Segregation** not just as a naming convention but as a real infrastructure split.

### Writes: EF Core

Commands go through EF Core (`EventsDbContext`) because:

- EF Core's change tracking is useful for complex aggregates with child collections
- Unit of Work (`SaveChangesAsync`) wraps the write in a transaction
- EF Core migrations manage schema evolution

### Reads: Dapper

Queries go directly to the database via Dapper because:

- Read models are denormalized projections, not domain objects — they have no behavior
- Dapper's raw SQL gives full control over query shape and performance
- No change tracking overhead
- Queries can span joins and aggregations that are awkward to express in LINQ

```csharp
// GetEventQueryHandler.cs — bypasses EF Core entirely
await using DbConnection connection = await dbConnectionFactory.OpenConnectionAsync();
const string sql = """
    SELECT id AS Id, title AS Title, description AS Description,
           location AS Location, starts_at_utc AS StartsAtUtc, ends_at_utc AS EndsAtUtc
    FROM events.events
    WHERE id = @EventId
    """;
return await connection.QuerySingleOrDefaultAsync<EventResponse>(sql, request);
```

Both paths share the same PostgreSQL connection pool (`NpgsqlDataSource`), so there is no penalty for having two ORMs.

---

## Technology Stack

| Concern | Technology | Version |
|---|---|---|
| Runtime | .NET | 8.0 |
| Web Framework | ASP.NET Core Minimal APIs | 8.0 |
| Database | PostgreSQL | 15+ |
| ORM (writes) | Entity Framework Core + Npgsql | 8.0.4 |
| ORM (reads) | Dapper | 2.1.35 |
| Mediator / CQRS | MediatR | 12.2.0 |
| Validation | FluentValidation | 11.9.2 |
| API Docs | Swashbuckle (Swagger UI) | 6.6.2 |
| Containerization | Docker + Docker Compose | — |
| Code Analysis | SonarAnalyzer.CSharp | 9.24.0 |

### Build Configuration (`Directory.Build.props`)

All projects inherit:

```xml
<Nullable>enable</Nullable>
<ImplicitUsings>enable</ImplicitUsings>
<TreatWarningsAsErrors>true</TreatWarningsAsErrors>
<AnalysisMode>All</AnalysisMode>
```

`TreatWarningsAsErrors` is a deliberate choice. It prevents quality debt from accumulating silently — nullable warnings, unused variables, and analyzer findings all fail the build. This is strict but keeps the codebase clean without manual enforcement.

---

## API Reference

Base URL (development): `http://localhost:5292`

Swagger UI: `http://localhost:5292/swagger`

---

### Create Event

```
POST /events
Content-Type: application/json
```

**Request body:**

| Field | Type | Required | Notes |
|---|---|---|---|
| `title` | `string` | Yes | Must not be empty |
| `description` | `string` | Yes | Must not be empty |
| `location` | `string` | Yes | Must not be empty |
| `startsAtUtc` | `DateTime` | Yes | UTC timestamp |
| `endsAtUtc` | `DateTime?` | No | Must be after `startsAtUtc` if provided |

**Example:**

```json
{
  "title": "DDD Europe 2025",
  "description": "Europe's largest Domain-Driven Design conference",
  "location": "Amsterdam, Netherlands",
  "startsAtUtc": "2025-06-10T09:00:00Z",
  "endsAtUtc": "2025-06-12T18:00:00Z"
}
```

**Responses:**

| Status | Body | Description |
|---|---|---|
| `200 OK` | `"<guid>"` | Event created; returns the new event ID |
| `400 Bad Request` | Validation errors | One or more fields failed validation |

---

### Get Event

```
GET /events/{id}
```

**Path parameters:**

| Parameter | Type | Description |
|---|---|---|
| `id` | `Guid` | The event ID returned by Create Event |

**Responses:**

| Status | Body | Description |
|---|---|---|
| `200 OK` | `EventResponse` | Event found |
| `404 Not Found` | — | No event with that ID |

**Response body (`EventResponse`):**

```json
{
  "id": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "title": "DDD Europe 2025",
  "description": "Europe's largest Domain-Driven Design conference",
  "location": "Amsterdam, Netherlands",
  "startsAtUtc": "2025-06-10T09:00:00Z",
  "endsAtUtc": "2025-06-12T18:00:00Z"
}
```

---

## Getting Started

### Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [Docker Desktop](https://www.docker.com/products/docker-desktop/)

### Option 1: Docker Compose (Recommended)

Starts both the API and a PostgreSQL database. Migrations are applied automatically on startup.

```bash
docker-compose up --build
```

API: `http://localhost:5000`
Swagger: `http://localhost:5000/swagger`

### Option 2: Local Development

1. **Start PostgreSQL** (Docker or local install):

```bash
docker-compose up evently.database
```

2. **Run the API:**

```bash
dotnet run --project src/API/Evently.Api
```

API: `http://localhost:5292`
Swagger: `http://localhost:5292/swagger`

Migrations are applied automatically when `ASPNETCORE_ENVIRONMENT=Development` (the default when running locally).

### Verify Installation

```bash
# Create an event
curl -X POST http://localhost:5292/events \
  -H "Content-Type: application/json" \
  -d '{
    "title": "Test Event",
    "description": "Hello Evently",
    "location": "Remote",
    "startsAtUtc": "2025-12-01T10:00:00Z"
  }'

# Returns: "3fa85f64-5717-4562-b3fc-2c963f66afa6"

# Retrieve it
curl http://localhost:5292/events/3fa85f64-5717-4562-b3fc-2c963f66afa6
```

---

## Configuration

### Connection Strings

| Environment | Key | Location |
|---|---|---|
| Development | `ConnectionStrings:Database` | `appsettings.Development.json` |
| Production | `ConnectionStrings:Database` | Environment variable / secrets manager |
| Docker | `ConnectionStrings:Database` | `docker-compose.override.yml` env section |

**Development connection string** (connects to the Docker database):

```
Host=evently.database;Port=5432;Database=evently;Username=postgres;Password=postgres;Include Error Detail=true
```

`Include Error Detail=true` exposes PostgreSQL constraint details in exceptions — useful for debugging, never for production.

### Docker Port Mapping

| Service | Container Port | Host Port |
|---|---|---|
| API (HTTP) | 8080 | 5000 |
| API (HTTPS) | 8081 | 5001 |
| PostgreSQL | 5432 | 5438 |

The database data directory is persisted to `./.containers/db` so the database survives container restarts.

---

## Database Migrations

EF Core migrations are scoped per module. Each module manages its own schema and its own migrations history table, so modules cannot block each other's schema changes.

```
EventsDbContext → schema: "events"
Migrations history: events.__EFMigrationsHistory
```

### Apply Migrations

**Automatic (development):** Applied on startup via `MigrationExtensions.ApplyMigrations()`.

**Manual:**

```bash
dotnet ef database update \
  --project src/Modules/Events/Evently.Modules.Events.Infrastructure \
  --startup-project src/API/Evently.Api
```

### Add a New Migration

```bash
dotnet ef migrations add <MigrationName> \
  --project src/Modules/Events/Evently.Modules.Events.Infrastructure \
  --startup-project src/API/Evently.Api \
  --output-dir Database/Migrations
```

---

## Design Decisions & Trade-offs

### Internal Handlers and Validators

`CreateEventCommandHandler` and `CreateEventCommandValidator` are `internal sealed`. This is intentional:

- **Internal**: They are implementation details of the Application layer. Nothing outside the assembly should depend on them directly.
- **Sealed**: Prevents inheritance chains that accumulate complexity over time.
- **Registered with `includeInternalTypes: true`**: FluentValidation's assembly scanning respects the internal visibility.

MediatR handlers are registered by assembly scanning — they never need to be referenced directly, so making them internal costs nothing and gains a cleaner public API surface.

### EF Core as Unit of Work

`EventsDbContext` implements both `DbContext` and `IUnitOfWork`:

```csharp
public sealed class EventsDbContext(DbContextOptions<EventsDbContext> options)
    : DbContext(options), IUnitOfWork
```

The application layer depends on `IUnitOfWork`, not on `EventsDbContext`. This means:

- Application layer has no EF Core reference (only Domain and BCL)
- The concrete context can be replaced (e.g., with an in-memory fake for testing) without touching any use case handler

### Snake Case Naming Convention

PostgreSQL conventions use `snake_case` for column names; C# uses `PascalCase` for properties. Rather than annotating every property with `[Column("starts_at_utc")]`, the project uses `UseSnakeCaseNamingConvention()` from `EFCore.NamingConventions`. This applies the transformation globally and keeps the entity classes free of database annotations — preserving their status as pure domain objects.

Dapper queries manually alias columns to match C# property names:
```sql
SELECT starts_at_utc AS StartsAtUtc FROM events.events
```

This is a small tax for using Dapper; it is explicit and searchable.

### Module Registration as Public API

`EventsModule.cs` in the Infrastructure layer is the **only public surface** a module exposes to the outside world:

```csharp
public static class EventsModule
{
    public static void MapEndpoints(IEndpointRouteBuilder app) { ... }
    public static IServiceCollection AddEventsModule(this IServiceCollection services, IConfiguration configuration) { ... }
}
```

`Program.cs` calls exactly these two methods and nothing else. This contract is the seam: if the Events module were to become a microservice, `AddEventsModule` becomes a no-op (or registers an HTTP client) and `MapEndpoints` registers a reverse-proxy route — the rest of the solution is unchanged.

### Validation in the Application Layer

`CreateEventCommandValidator` lives in the Application layer, not the Presentation layer. This is deliberate:

- Validation is a business rule, not an HTTP concern (e.g., `EndsAtUtc > StartsAtUtc` is a domain invariant)
- The validation fires regardless of whether the command arrives via HTTP, a queue consumer, or a test
- The Presentation layer handles only HTTP binding and HTTP response mapping

---

## Known Gaps & Roadmap

The current implementation is a foundation, not a finished product. The following areas are the natural next steps, roughly in priority order.

### Domain Event Dispatching

Domain events (`EventCreatedDomainEvent`) are **raised** inside the aggregate but never **dispatched**. The dispatch pipeline needs to:

1. After `SaveChangesAsync`, collect domain events from all tracked entities
2. Clear events from entities
3. Publish each event via MediatR (`IPublisher`) or an outbox

This is typically done by overriding `SaveChangesAsync` in `EventsDbContext` or by a MediatR pipeline behavior.

### Global Exception Handling

There is no exception middleware. An unhandled domain exception or database error returns a 500 with a stack trace in development and a blank 500 in production. The standard approach in Minimal APIs is:

```csharp
app.UseExceptionHandler(errorApp => { ... });
```

A production-grade implementation maps exception types to RFC 7807 Problem Details responses.

### FluentValidation Pipeline Behavior

Validators are registered but not automatically invoked before handlers. A MediatR `IPipelineBehavior<TRequest, TResponse>` that runs all validators and short-circuits with a failure result before the handler executes is the standard pattern.

### Structured Logging

`Microsoft.Extensions.Logging` is available by default but no sinks are configured. Adding Serilog with structured JSON output and correlation IDs would make the API observable in production.

### Authentication & Authorization

No authentication or authorization is implemented. Future modules (Users, Tickets) will likely introduce JWT bearer authentication and policy-based authorization.

### Testing

No test projects exist. The architecture is designed for testability:

- Domain logic can be unit tested with zero infrastructure
- Application handlers can be tested by mocking `IEventRepository` and `IUnitOfWork`
- Integration tests can spin up a real PostgreSQL instance via Testcontainers

### Future Modules

The template supports adding modules without modifying existing code:

| Module | Responsibility |
|---|---|
| `Evently.Modules.Users` | Registration, authentication, profiles |
| `Evently.Modules.Tickets` | Ticket purchasing, inventory, check-in |
| `Evently.Modules.Notifications` | Email/SMS confirmations triggered by domain events |

Each module follows the identical four-project structure: Domain / Application / Infrastructure / Presentation.

---

## License

MIT
