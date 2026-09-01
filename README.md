# EventFlow — Sistema Distribuído Event-Driven em .NET

[![.NET 9](https://img.shields.io/badge/.NET-9.0%20LTS-purple.svg)](https://dotnet.microsoft.com/)
[![C# 13](https://img.shields.io/badge/C%23-13.0-blue.svg)](https://learn.microsoft.com/dotnet/csharp/)
[![PostgreSQL](https://img.shields.io/badge/PostgreSQL-16-336791.svg)](https://www.postgresql.org/)
[![RabbitMQ](https://img.shields.io/badge/RabbitMQ-3.13-FF6600.svg)](https://www.rabbitmq.com/)
[![Redis](https://img.shields.io/badge/Redis-7-DC382D.svg)](https://redis.io/)
[![OpenTelemetry](https://img.shields.io/badge/OpenTelemetry-Enabled-orange.svg)](https://opentelemetry.io/)
[![Tests](https://img.shields.io/badge/Tests-157%20Passed-brightgreen.svg)]()
[![Code Quality](https://img.shields.io/badge/Warnings-0%20(TreatAsErrors)-success.svg)]()

> 🚀 **Status:** Arquitetura completa, robusta e testada (Fases 1 a 13 concluídas).

O **EventFlow** é um sistema distribuído e *event-driven* projetado para a ingestão confiável, persistência transacional atômica, roteamento por tópicos e processamento assíncrono resiliente de eventos de negócio e webhooks críticos (como liquidações financeiras e notificações de parceiros).

---

## 🎯 O Problema que o EventFlow Resolve

Em arquiteturas tradicionais de microsserviços e webhooks, o maior risco operacional é a **perda silenciosa de eventos** entre a API HTTP aceitar a requisição e o processamento final acontecer (*Dual-Write Problem*) — especialmente durante quedas de rede, reinicializações de instâncias ou indisponibilidade temporária de mensageria ou serviços externos.

O EventFlow endereça esse desafio implementando garantias arquiteturais rígidas de ponta a ponta:

1. **Garantia de Não-Perda (*Zero Data-Loss*):** **Transactional Outbox Pattern** no PostgreSQL. A gravação do evento imutável e a mensagem pendente para o broker ocorrem na **mesma transação ACID**.
2. **Despacho Assíncrono com Concorrência Segura:** Worker dedicado que consome o Outbox em lotes com `SELECT ... FOR UPDATE SKIP LOCKED`, garantindo balanceamento linear entre réplicas sem contenção.
3. **Idempotência Híbrida em Três Camadas:** Proteção contra entregas duplicadas (*at-least-once*) combinando:
   - *Fast Guard* em memória no **Redis** (TTL de 24h).
   - Trava distribuída *in-flight* atômica no **Redis** (`SET NX EX 30s` com liberação segura via Script Lua).
   - Verificação relacional imutável no **PostgreSQL** (`UNIQUE(tenant_id, scope, idempotency_key)`).
4. **Resiliência Declarativa (Polly v8):** Pipeline em 4 camadas com *Total Timeout*, *Exponential Backoff com Decorrelated Jitter*, *Circuit Breaker* e *Attempt Timeout*.
5. **Segurança por Padrão (*Secure by Default*):** Proteção ativa contra SSRF (`SsrfGuard`), comparação de API Keys em tempo constante (`CryptographicOperations.FixedTimeEquals`), *Rate Limiting* particionado por Tenant/IP, limite de payload no Kestrel (256 KB) e headers de segurança OWASP.
6. **Observabilidade End-to-End:** Rastreabilidade distribuída via **W3C TraceContext** (`traceparent`) com **OpenTelemetry**, logs estruturados compilados com `[LoggerMessage]` de alto desempenho e métricas de negócio/infraestrutura.

---

## 🏗️ Fluxo Arquitetural

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
   │ PostgreSQL Database  │ ──► [ events ] (Payload auditável imutável)
   │                      │ ──► [ outbox_messages ] (Status: Pending)
   └──────────┬───────────┘
              │ Polling Concorrente (SELECT ... FOR UPDATE SKIP LOCKED)
              ▼
   ┌──────────────────────┐
   │  Outbox Dispatcher   │ ◄── Worker BackgroundService (Scoped DI)
   └──────────┬───────────┘
              │ Publicação AMQP 0-9-1 (RabbitMQ.Client 7.x + Headers W3C)
              ▼
   ┌──────────────────────┐
   │   RabbitMQ Broker    │ ◄── Topic Exchange (eventflow.events)
   └──────────┬───────────┘
              │ Consumo com QoS Prefetch & W3C Extraction
              ▼
   ┌──────────────────────┐
   │ EventConsumerWorker  │ ◄── 1. Redis Lock In-Flight (SET NX EX + Lua Release)
   │                      │ ◄── 2. Polly v8 Pipeline (Jitter, Circuit Breaker, SSRF Guard)
   │                      │ ◄── 3. Postgres Single Source of Truth (UNIQUE Constraint)
   └──────────┬───────────┘
              │
      ┌───────┴───────┐
      ▼               ▼
 [ Sucesso ]     [ Falha Esgotada ]
      │               │
      ├─► Grava conclusão no Postgres     └─► Roteia para DLQ (eventflow.events.dlq)
      ├─► Cache no Redis (24h)                 ├─ API de Inspeção (GET /api/v1/dlq)
      └─► BasicAckAsync no Broker              └─ API de Replay (POST /api/v1/dlq/replay)
```

---

## 🛠️ Stack Tecnológica

* **Linguagem & Runtime:** .NET 9 LTS / C# 13 (compilação com `TreatWarningsAsErrors=true`).
* **API & Hosting:** ASP.NET Core Minimal APIs & `BackgroundService` em Worker Services.
* **Persistência Relacional:** PostgreSQL 16 com Entity Framework Core 9 (mapeamentos relacionais explícitos, índices compostos e `SKIP LOCKED`).
* **Mensageria:** RabbitMQ 3.13 via driver oficial assíncrono `RabbitMQ.Client 7.x`.
* **Cache & Lock Distribuído:** Redis 7 Alpine com `StackExchange.Redis` e scripts Lua atômicos.
* **Resiliência:** Polly v8 (`Microsoft.Extensions.Resilience`).
* **Validação:** FluentValidation (regras defensivas com Regex e limites de payload).
* **Observabilidade:** OpenTelemetry .NET SDK (`ActivitySource`, `Meter`, OTLP gRPC para Jaeger).
* **Segurança:** OWASP Security Headers, Constant-time Crypto, SSRF Guard e Sliding Window Rate Limiting.
* **Testes:** xUnit, FluentAssertions, NSubstitute, WebApplicationFactory e Testcontainers.

---

## 🏛️ Estrutura do Repositório e Camadas

```text
eventflow/
├── .github/workflows/ci.yml         # Pipeline CI/CD GitHub Actions (Lint, Test, Docker)
├── docker-compose.yml               # Orquestração completa de serviços (Postgres, RabbitMQ, Redis, Jaeger, Api, Worker)
├── Dockerfile.api                   # Dockerfile multi-stage não-root (UID 1654) para a API
├── Dockerfile.worker                # Dockerfile multi-stage não-root (UID 1654) para o Worker
├── docs/
│   ├── adr/                         # Architectural Decision Records (ADRs formais)
│   │   ├── 0001-modular-architecture.md
│   │   ├── 0002-transactional-outbox.md
│   │   ├── 0003-idempotency-strategy.md
│   │   ├── 0004-messaging-transport.md
│   │   ├── 0005-resilience-strategy.md
│   │   ├── 0006-observability-opentelemetry.md
│   │   └── 0007-security-hardening.md
│   ├── architecture/                # Blueprint mestre e Diagramas C4
│   │   ├── EVENTFLOW_MASTER_PLAN.md
│   │   └── c4-architecture.md
│   ├── business-rules/              # Especificação de contratos de eventos
│   │   └── event-contracts.md
│   └── security/                    # Modelagem de ameaças e segurança
│       └── threat-model.md
├── src/
│   ├── EventFlow.Api/               # Endpoints HTTP, RateLimiting, SecurityHeaders, Kestrel
│   ├── EventFlow.Application/       # Casos de uso, interfaces, orquestração e validação FluentValidation
│   ├── EventFlow.Domain/            # Entidades imutáveis, Result Pattern, erros tipados e telemetria
│   ├── EventFlow.Infrastructure/    # EF Core 9, RabbitMQ 7.x, Redis Lua Locks, Polly v8 e OTel
│   └── EventFlow.Worker/            # BackgroundServices (OutboxDispatcherWorker & EventConsumerWorker)
└── tests/
    ├── EventFlow.UnitTests/         # 135 testes unitários de domínio, aplicação e infraestrutura
    └── EventFlow.IntegrationTests/  # 22 testes de integração com Testcontainers e WebApplicationFactory
```

---

## 📚 Architectural Decision Records (ADRs)

Todas as decisões arquiteturais fundamentais estão documentadas formalmente em [`docs/adr/`](docs/adr/):

* [ADR-0001: Arquitetura Monólito Modular e Separação Limpa de Camadas](docs/adr/0001-modular-architecture.md)
* [ADR-0002: Padrão Transactional Outbox com Concorrência Segura via SKIP LOCKED](docs/adr/0002-transactional-outbox.md)
* [ADR-0003: Estratégia de Idempotência Híbrida (Redis + PostgreSQL)](docs/adr/0003-idempotency-strategy.md)
* [ADR-0004: Escolha do Transporte de Mensageria (RabbitMQ Direto vs MassTransit)](docs/adr/0004-messaging-transport.md)
* [ADR-0005: Estratégia de Resiliência Declarativa (Polly v8)](docs/adr/0005-resilience-strategy.md)
* [ADR-0006: Observabilidade Distribuída (OpenTelemetry & W3C TraceContext)](docs/adr/0006-observability-opentelemetry.md)
* [ADR-0007: Endurecimento de Segurança (OWASP Top 10, Rate Limiting & SSRF)](docs/adr/0007-security-hardening.md)

---

## 🚀 Como Executar Localmente

### Opção 1: Via Docker Compose (Recomendado — Ambiente Completo)

Para subir todos os componentes (PostgreSQL, RabbitMQ com Management, Redis, Jaeger UI, EventFlow.Api e EventFlow.Worker):

```bash
docker compose up -d --build
```

#### Portas e Interfaces Disponíveis:
* **EventFlow.Api:** `http://localhost:8080`
* **RabbitMQ Management UI:** `http://localhost:15672` (Usuário: `guest` / Senha: `guest`)
* **Jaeger OpenTelemetry UI:** `http://localhost:16686`
* **PostgreSQL:** `localhost:5432` (Usuário: `eventflow` / Senha: `eventflow_secret_pwd` / DB: `eventflow_db`)
* **Redis:** `localhost:6379`

---

### Opção 2: Execução Local com .NET 9 SDK

#### 1. Inicie apenas as dependências de infraestrutura:
```bash
docker compose up -d postgres rabbitmq redis jaeger
```

#### 2. Execute a API:
```bash
dotnet run --project src/EventFlow.Api
```

#### 3. Execute o Worker em outro terminal:
```bash
dotnet run --project src/EventFlow.Worker
```

---

## 🧪 Execução de Testes

### Executar Toda a Suíte de Testes (Unitários + Integração):
```bash
dotnet test
```

### Executar Apenas Testes Unitários:
```bash
dotnet test tests/EventFlow.UnitTests
```

### Executar Testes de Integração com Testcontainers:
*(Requer Docker ativo no host)*
```bash
dotnet test tests/EventFlow.IntegrationTests
```

### Validação de Formatação e Regras de Código:
```bash
dotnet format --verify-no-changes
```

---

## 📡 Referência da API & Exemplos `curl`

A API utiliza autenticação via cabeçalho `X-Api-Key` e suporte a particionamento de tenant via `X-Tenant-Id`.

> **Chave de API Padrão para Desenvolvimento:** `dev_secret_api_key_12345`

### 1. Ingestão de Evento de Liquidação Financeira (`POST /api/v1/events`)

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
      "currency": "BRL",
      "status": "PAID"
    },
    "metadata": {
      "environment": "production"
    }
  }'
```

#### Resposta de Sucesso (`HTTP 202 Accepted`):
```json
{
  "eventId": "01918a22-4a7b-7212-8e2b-7c3e1e9f1a01",
  "correlationId": "01918a22-4a7b-7212-8e2b-7c3e1e9f1a00",
  "status": "Accepted",
  "occurredAt": "2026-09-01T21:00:00.000Z"
}
```

---

### 2. Inspecionar Mensagens na Dead Letter Queue (`GET /api/v1/dlq`)

```bash
curl -i -X GET "http://localhost:8080/api/v1/dlq?count=10" \
  -H "X-Api-Key: dev_secret_api_key_12345"
```

#### Resposta:
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

### 3. Replay Seguro de Mensagens da DLQ (`POST /api/v1/dlq/replay`)

```bash
curl -i -X POST "http://localhost:8080/api/v1/dlq/replay?maxCount=50" \
  -H "X-Api-Key: dev_secret_api_key_12345"
```

#### Resposta:
```json
{
  "replayedCount": 1,
  "status": "Replayed"
}
```

---

### 4. Limpeza / Expurgo da DLQ (`DELETE /api/v1/dlq`)

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

## 🔍 Observabilidade com Jaeger & OpenTelemetry

Todos os componentes do EventFlow instrumentam spans de rastreabilidade distribuída seguindo o padrão **W3C TraceContext**. Quando um evento é ingerido via API e processado pelo Worker, a correlação visual completa é exibida no **Jaeger UI**:

1. Acesse `http://localhost:16686` no seu navegador.
2. Selecione o serviço `EventFlow.Api` ou `EventFlow.Worker`.
3. Visualize a cascata completa do trace:
   - `EventFlow.IngestEvent` (API Minimal Endpoint)
   - `EventFlow.OutboxBatchDispatch` (Worker Outbox Processor)
   - `EventFlow.ConsumerProcess` (Worker Event Consumer)
   - `EventFlow.HttpExternalDispatch` (Despacho HTTP externo com Polly & SSRF Guard)

---

## 🛡️ Tabela de Matriz de Segurança

| Recurso | Implementação | Garantia |
| :--- | :--- | :--- |
| **Proteção SSRF** | `SsrfGuard` | Bloqueia IPs privados RFC 1918, Loopback e Cloud Metadata (`169.254.169.254`) |
| **Timing Attack Defense** | `CryptographicOperations.FixedTimeEquals` | Validação de API Key em tempo constante independente do tamanho |
| **Proteção contra DoS** | Kestrel 256KB limit + Sliding Window Rate Limiter | 100 req/min por Tenant/IP com RFC 7807 429 ProblemDetails |
| **OWASP Security Headers** | `SecurityHeadersMiddleware` | CSP, HSTS, X-Content-Type-Options: nosniff, X-Frame-Options: DENY, etc. |
| **Isolamento de Containers** | Dockerfile Multi-Stage Non-Root | Usuário `app` (UID `1654`), sem privilégios de root |
| **Proteção Dupla Execução** | `HybridIdempotencyManager` | Redis Lock atômico (Lua) + Postgres Unique Constraint |

---

## 📄 Licença

Distribuído sob a licença MIT. Consulte `LICENSE` para obter mais informações.
