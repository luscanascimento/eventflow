# EventFlow — Distributed Event-Driven Ingestion & Processing Platform in .NET

[![.NET 9](https://img.shields.io/badge/.NET-9.0%20LTS-purple.svg)](https://dotnet.microsoft.com/)
[![C# 13](https://img.shields.io/badge/C%23-13.0-blue.svg)](https://learn.microsoft.com/dotnet/csharp/)
[![PostgreSQL](https://img.shields.io/badge/PostgreSQL-16-336791.svg)](https://www.postgresql.org/)
[![RabbitMQ](https://img.shields.io/badge/RabbitMQ-3.13-FF6600.svg)](https://www.rabbitmq.com/)
[![Redis](https://img.shields.io/badge/Redis-7-DC382D.svg)](https://redis.io/)
[![OpenTelemetry](https://img.shields.io/badge/OpenTelemetry-Enabled-orange.svg)](https://opentelemetry.io/)
[![Tests](https://img.shields.io/badge/Tests-206%20Passed%20(184%20Unit%20%2B%2022%20Integration)-brightgreen.svg)]()
[![Code Quality](https://img.shields.io/badge/Warnings-0%20(TreatAsErrors)-success.svg)]()

> 🚀 **Status:** Complete, robust, and chaos-engineering audited architecture (All 14 phases completed — Approved for Production).

**EventFlow** is an enterprise-grade, distributed, event-driven system engineered for reliable ingestion, atomic transactional persistence, topic-based routing, and resilient asynchronous processing of mission-critical business events and webhooks (such as financial settlements, partner notifications, and transaction webhooks).

---

## 🎯 The Engineering Problem EventFlow Solves

In traditional microservices and webhook architectures, the single largest operational hazard is **silent event loss** occurring between the HTTP API accepting the request and final asynchronous processing (*The Dual-Write Problem*) — especially during transient network partitions, instance restarts, broker downtimes, or external endpoint unresponsiveness.

EventFlow resolves this challenge by enforcing strict, end-to-end architectural guarantees:

1. **Zero Data-Loss Guarantee:** **Transactional Outbox Pattern** in PostgreSQL. Storing immutable event payloads and queuing pending broker dispatches occur within the **same ACID database transaction**.
2. **Safe Concurrent Asynchronous Dispatching:** Dedicated background workers consume pending outbox entries in batches using `SELECT ... FOR UPDATE SKIP LOCKED`, guaranteeing linear horizontal scaling across replicas without lock contention or duplicate worker fetches.
3. **Three-Tier Hybrid Idempotency:** End-to-end protection against duplicated deliveries (*at-least-once*) combining:
   - *In-Memory Fast Guard* in **Redis** (24h TTL).
   - Atomic in-flight distributed lock in **Redis** (`SET NX EX 30s` with safe release via Lua Scripts).
   - Relational immutable single-source-of-truth in **PostgreSQL** (`UNIQUE(tenant_id, scope, idempotency_key)`).
4. **Declarative Fault Tolerance (Polly v8):** 4-layer resilience pipeline featuring *Total Timeout*, *Exponential Backoff with Decorrelated Jitter*, *Circuit Breakers*, and *Attempt Timeouts*.
5. **Secure by Default:** Active SSRF protection (`SsrfGuard`), constant-time API key verification (`CryptographicOperations.FixedTimeEquals`), Tenant/IP-partitioned Rate Limiting, Kestrel payload constraints (256 KB), and OWASP security headers.
6. **End-to-End Distributed Observability:** Standardized **W3C TraceContext** (`traceparent`) propagation using **OpenTelemetry**, high-performance source-generated structured logging (`[LoggerMessage]`), and custom business/infrastructure metrics.

---

## 🏗️ Architectural Data Flow

```text
  [ HTTP Client / Webhook ]
              │ POST /api/v1/events (RateLimit, OWASP, ApiKey, W3C Trace)
              ▼
   ┌──────────────────────┐
   │    EventFlow.Api     │ ◄── ASP.NET Core Minimal APIs + Result Pattern
   └──────────┬───────────┘
              │ ACID Transaction (Atomic Write)
              ▼
   ┌──────────────────────┐
   │ PostgreSQL Database  │ ──► [ events ] (Auditable immutable payload)
   │                      │ ──► [ outbox_messages ] (Status: Pending)
   └──────────┬───────────┘
              │ Concurrent Polling (SELECT ... FOR UPDATE SKIP LOCKED)
              ▼
   ┌──────────────────────┐
   │  Outbox Dispatcher   │ ◄── BackgroundService Worker (Scoped DI)
   └──────────┬───────────┘
              │ AMQP 0-9-1 Publication (RabbitMQ.Client 7.x + W3C Headers)
              ▼
   ┌──────────────────────┐
   │   RabbitMQ Broker    │ ◄── Topic Exchange (eventflow.events)
   └──────────┬───────────┘
              │ Consumption with QoS Prefetch & W3C Extraction
              ▼
   ┌──────────────────────┐
   │ EventConsumerWorker  │ ◄── 1. In-Flight Redis Lock (SET NX EX + Lua Release)
   │                      │ ◄── 2. Polly v8 Pipeline (Jitter, Circuit Breaker, SSRF Guard)
   │                      │ ◄── 3. Postgres Single Source of Truth (UNIQUE Constraint)
   └──────────┬───────────┘
              │
      ┌───────┴───────┐
      ▼               ▼
 [ Success ]     [ Exhausted Failure ]
      │               │
      ├─► Record completion in Postgres   └─► Route to DLQ (eventflow.events.dlq)
      ├─► Cache status in Redis (24h)          ├─ Inspection API (GET /api/v1/dlq)
      └─► BasicAckAsync on Broker              └─ Replay API (POST /api/v1/dlq/replay)
```

---

## 🛠️ Technology Stack

* **Language & Runtime:** .NET 9 LTS / C# 13 (`TreatWarningsAsErrors=true`).
* **API & Hosting:** ASP.NET Core Minimal APIs & `BackgroundService` Worker Services.
* **Relational Persistence:** PostgreSQL 16 with Entity Framework Core 9 (explicit relational mappings, composite indexes, and `SKIP LOCKED`).
* **Message Broker:** RabbitMQ 3.13 via official asynchronous driver `RabbitMQ.Client 7.x`.
* **Caching & Distributed Locks:** Redis 7 Alpine with `StackExchange.Redis` and atomic Lua scripts.
* **Resilience:** Polly v8 (`Microsoft.Extensions.Resilience`).
* **Validation:** FluentValidation (defensive regex validation and payload boundary limits).
* **Observability:** OpenTelemetry .NET SDK (`ActivitySource`, `Meter`, OTLP gRPC for Jaeger).
* **Security:** OWASP Security Headers, Constant-time Cryptography, SSRF Guard, and Sliding Window Rate Limiting.
* **Testing:** xUnit, FluentAssertions, NSubstitute, WebApplicationFactory, and Testcontainers.

---

## 🏛️ Repository Structure & Clean Architecture

```text
eventflow/
├── .github/workflows/ci.yml         # CI/CD GitHub Actions Pipeline (Lint, Test, Docker)
├── docker-compose.yml               # Multi-container orchestration (Postgres, RabbitMQ, Redis, Jaeger, Api, Worker)
├── Dockerfile.api                   # Multi-stage non-root container (UID 1654) for the API
├── Dockerfile.worker                # Multi-stage non-root container (UID 1654) for the Worker
├── docs/
│   ├── adr/                         # Formal Architectural Decision Records (ADRs)
│   │   ├── 0001-modular-architecture.md
│   │   ├── 0002-transactional-outbox.md
│   │   ├── 0003-idempotency-strategy.md
│   │   ├── 0004-messaging-transport.md
│   │   ├── 0005-resilience-strategy.md
│   │   ├── 0006-observability-opentelemetry.md
│   │   └── 0007-security-hardening.md
│   ├── architecture/                # Master architectural blueprints and C4 diagrams
│   │   ├── EVENTFLOW_MASTER_PLAN.md
│   │   └── c4-architecture.md
│   ├── business-rules/              # Event contracts and schema definitions
│   │   └── event-contracts.md
│   └── security/                    # Threat modeling and mitigation strategies
│       └── threat-model.md
├── src/
│   ├── EventFlow.Api/               # HTTP endpoints, RateLimiting, SecurityHeaders, Kestrel config
│   ├── EventFlow.Application/       # Use cases, interfaces, orchestration, and FluentValidation rules
│   ├── EventFlow.Domain/            # Immutable entities, Result Pattern, typed domain errors, telemetry
│   ├── EventFlow.Infrastructure/    # EF Core 9, RabbitMQ 7.x, Redis Lua Locks, Polly v8, OpenTelemetry
│   └── EventFlow.Worker/            # BackgroundServices (OutboxDispatcherWorker & EventConsumerWorker)
└── tests/
    ├── EventFlow.UnitTests/         # 184 unit tests covering domain, application, and infrastructure
    └── EventFlow.IntegrationTests/  # 22 integration tests utilizing Testcontainers and WebApplicationFactory
```

---

## 📚 Architectural Decision Records (ADRs)

All core architectural decisions are formally documented in [`docs/adr/`](docs/adr/):

* [ADR-0001: Modular Monolith Architecture & Clean Layer Separation](docs/adr/0001-modular-architecture.md)
* [ADR-0002: Transactional Outbox Pattern with Concurrent Safe SKIP LOCKED Polling](docs/adr/0002-transactional-outbox.md)
* [ADR-0003: Hybrid Idempotency Strategy (Redis + PostgreSQL)](docs/adr/0003-idempotency-strategy.md)
* [ADR-0004: Messaging Transport Selection (Direct RabbitMQ vs MassTransit)](docs/adr/0004-messaging-transport.md)
* [ADR-0005: Declarative Fault Tolerance & Resilience Strategy (Polly v8)](docs/adr/0005-resilience-strategy.md)
* [ADR-0006: Distributed Observability (OpenTelemetry & W3C TraceContext)](docs/adr/0006-observability-opentelemetry.md)
* [ADR-0007: Security Hardening (OWASP Top 10, Rate Limiting & SSRF Protection)](docs/adr/0007-security-hardening.md)

---

## 🚀 Running Locally

### Option 1: Via Docker Compose (Recommended — Complete Environment)

Spin up all services (PostgreSQL, RabbitMQ with Management UI, Redis, Jaeger UI, EventFlow.Api, and EventFlow.Worker):

```bash
docker compose up -d --build
```

#### Exposed Ports & Management Interfaces:
* **EventFlow.Api:** `http://localhost:8080`
* **RabbitMQ Management UI:** `http://localhost:15672` (Username: `guest` / Password: `guest`)
* **Jaeger OpenTelemetry UI:** `http://localhost:16686`
* **PostgreSQL:** `localhost:5432` (Username: `eventflow` / Password: `eventflow_secret_pwd` / DB: `eventflow_db`)
* **Redis:** `localhost:6379`

---

### Option 2: Local Execution via .NET 9 SDK

#### 1. Start the supporting infrastructure dependencies:
```bash
docker compose up -d postgres rabbitmq redis jaeger
```

#### 2. Run the API:
```bash
dotnet run --project src/EventFlow.Api
```

#### 3. Run the Worker in a separate terminal:
```bash
dotnet run --project src/EventFlow.Worker
```

---

## 🧪 Test Execution Suite

### Run All Tests (Unit + Integration):
```bash
dotnet test
```

### Run Unit Tests Only:
```bash
dotnet test tests/EventFlow.UnitTests
```

### Run Integration Tests with Testcontainers:
*(Requires active Docker daemon)*
```bash
dotnet test tests/EventFlow.IntegrationTests
```

### Verify Code Formatting & Analyzer Rules:
```bash
dotnet format --verify-no-changes
```

---

## 📡 API Reference & `curl` Examples

The API requires authentication via the `X-Api-Key` header and supports tenant partitioning via `X-Tenant-Id`.

> **Default Development API Key:** `dev_secret_api_key_12345`

### 1. Ingest Financial Settlement Event (`POST /api/v1/events`)

```bash
curl -i -X POST http://localhost:8080/api/v1/events \
  -H "Content-Type: application/json" \
  -H "X-Api-Key: dev_secret_api_key_12345" \
  -H "X-Tenant-Id: tenant_acme_corp" \
  -d '{
    "tenantId": "tenant_acme_corp",
    "eventType": "payment.settled.v1",
    "version": 1,
    "source": "payment-gateway",
    "idempotencyKey": "pay_tx_99882312",
    "payload": {
      "orderId": "ord_88129",
      "amount": 149.90,
      "currency": "USD",
      "status": "PAID"
    },
    "metadata": {
      "environment": "production"
    }
  }'
```

#### Success Response (`HTTP 202 Accepted`):
```json
{
  "eventId": "01918a22-4a7b-7212-8e2b-7c3e1e9f1a01",
  "correlationId": "01918a22-4a7b-7212-8e2b-7c3e1e9f1a00",
  "status": "Accepted",
  "occurredAt": "2026-09-01T21:00:00.000Z"
}
```

---

### 2. Inspect Dead Letter Queue Messages (`GET /api/v1/dlq`)

```bash
curl -i -X GET "http://localhost:8080/api/v1/dlq?count=10" \
  -H "X-Api-Key: dev_secret_api_key_12345"
```

#### Response:
```json
{
  "count": 1,
  "messages": [
    {
      "messageId": "01918a22-4a7b-7212-8e2b-7c3e1e9f1a01",
      "eventType": "payment.settled.v1",
      "tenantId": "tenant_acme_corp",
      "failedAt": "2026-09-01T21:05:00.000Z",
      "retryCount": 3,
      "exceptionMessage": "HTTP 503 Service Unavailable"
    }
  ]
}
```

---

### 3. Safe Replay of Dead Letter Queue Messages (`POST /api/v1/dlq/replay`)

```bash
curl -i -X POST "http://localhost:8080/api/v1/dlq/replay?maxCount=50" \
  -H "X-Api-Key: dev_secret_api_key_12345"
```

#### Response:
```json
{
  "replayedCount": 1,
  "status": "Replayed"
}
```

---

### 4. Purge / Clear DLQ Messages (`DELETE /api/v1/dlq`)

```bash
curl -i -X DELETE http://localhost:8080/api/v1/dlq \
  -H "X-Api-Key: dev_secret_api_key_12345"
```

---

### 5. Health Checks (`GET /health`, `/health/live`, `/health/ready`)

```bash
curl -i http://localhost:8080/health
```

---

## 🔍 Distributed Observability with Jaeger & OpenTelemetry

Every component within EventFlow instruments distributed tracing spans adhering to the **W3C TraceContext** standard. When an event is ingested through the API and processed by background workers, the complete distributed trace is visually inspectable in the **Jaeger UI**:

1. Open `http://localhost:16686` in your browser.
2. Select service `EventFlow.Api` or `EventFlow.Worker`.
3. Inspect the distributed trace cascade:
   - `EventFlow.IngestEvent` (API Minimal Endpoint)
   - `EventFlow.OutboxBatchDispatch` (Worker Outbox Processor)
   - `EventFlow.ConsumerProcess` (Worker Event Consumer)
   - `EventFlow.HttpExternalDispatch` (External HTTP dispatch with Polly & SSRF Guard)

---

## 🛡️ Security Hardening Matrix

| Security Feature | Implementation Mechanism | Engineering Guarantee |
| :--- | :--- | :--- |
| **SSRF Protection** | `SsrfGuard` | Blocks RFC 1918 private subnets, loopback, and cloud metadata endpoints (`169.254.169.254`) |
| **Timing Attack Defense** | `CryptographicOperations.FixedTimeEquals` | Constant-time API key verification immune to string length timing leaks |
| **DoS Mitigation** | Kestrel 256KB request limits + Sliding Window Rate Limiting | 100 req/min per Tenant/IP with RFC 7807 429 ProblemDetails |
| **OWASP Security Headers** | `SecurityHeadersMiddleware` | CSP, HSTS, X-Content-Type-Options: nosniff, X-Frame-Options: DENY, etc. |
| **Container Isolation** | Multi-Stage Non-Root Dockerfiles | Dedicated `app` user (UID `1654`) without root privileges |
| **Duplicate Execution Guard** | `HybridIdempotencyManager` | Redis atomic lock (Lua script) + Postgres composite UNIQUE constraint |

---

## 📄 License

Distributed under the MIT License. See `LICENSE` for more details.
