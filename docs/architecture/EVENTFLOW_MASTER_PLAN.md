# EventFlow — Master Engineering Blueprint & Roadmap

> **Status:** Architecture Master Plan & Roadmap  
> **Role & Level:** Staff / Principal Software Engineer  
> **Runtime Target:** .NET 9 STS / .NET 10 Ready (C# 13/14)  
> **Repository:** `eventflow`

---

## 1. Visão Geral e Levantamento de Requisitos

O **EventFlow** é uma plataforma distribuída para ingestão, persistência resiliente, roteamento e processamento assíncrono de eventos e webhooks críticos de negócio (ex.: eventos de pagamento, liquidação financeira e notificações de parceiros).

### 1.1 Requisitos Funcionais (FR)
- **FR-01 (Ingestão Segura e Rápida):** A API HTTP deve receber payloads de eventos em lote ou unitários, autenticar a origem, validar o schema estritamente e persistir o evento com latência mínima (P99 < 50ms).
- **FR-02 (Garantia Transacional de Entrega - Outbox):** A gravação do estado do negócio e o registro do evento para despacho devem ocorrer na mesma transação atômica ACID no PostgreSQL.
- **FR-03 (Despacho Confiável para Broker):** Um worker dedicado (Outbox Processor) deve despachar eventos pendentes para o broker (RabbitMQ) garantindo entrega *at-least-once*, com suporte a particionamento lógico, atomic leasing e ordenação quando exigido.
- **FR-04 (Processamento Assíncrono com Idempotência Estrita):** Os consumidores (Workers) devem processar eventos garantindo execução *logicamente idempotente* através de verificação de chave de idempotência atômica (PostgreSQL persistent lease + Redis fast guard).
- **FR-05 (Resiliência e Políticas de Falha):** Falhas transitórias no envio ou processamento externo devem disparar retries com exponential backoff e jitter. Falhas não recuperáveis devem ser encaminhadas para Dead Letter Queue (DLQ) com enriquecimento de contexto de erro.
- **FR-06 (Observabilidade e Rastreabilidade End-to-End):** Todo o ciclo de vida de um evento (HTTP -> DB -> Outbox -> RabbitMQ -> Consumer -> Destino) deve compartilhar o mesmo W3C TraceContext (`traceparent`), permitindo rastreabilidade contínua em APM/Jaeger.

### 1.2 Requisitos Não Funcionais (NFR)
- **NFR-01 (Confiabilidade At-Least-Once & No Silent Loss):** Nenhuma falha de rede temporária com broker ou crash de processo pode resultar em perda de eventos já aceitos pela API com HTTP 202/200.
- **NFR-02 (Segurança por Padrão):** Validação estrita de input, ausência de mass assignment, mitigação de SSRF e DNS rebinding em webhooks externos, autenticação robusta e isolamento por tenant/recurso.
- **NFR-03 (Simplicidade e Manutenibilidade):** Proibição expressa de abstrações desnecessárias (ex.: Generic Repository genérico sobre EF Core, MediatR artificial sem necessidade, microserviços artificiais antes de consolidar o monólito modular).

---

## 2. Definição do Domínio de Negócio

Para que o projeto tenha valor técnico real e não seja um "TODO app com filas", definimos o domínio central como:

### Cenário: **Payment & Order Event Ingestion Engine**
- **Entidades Centrais:**
  - `EventRecord`: Representa o registro imutável do evento recebido (metadados + payload JSON auditável).
  - `OutboxMessage`: Registro transacional pendente de despacho para a mensageria.
  - `IdempotencyRecord`: Bloqueio e registro de processamento garantindo unicidade por `(TenantId, IdempotencyKey, Scope)`.
  - `ProcessedEventAudit`: Registro de auditoria do processamento com status final, latência e destino.

### Contrato Base do Evento (`EventEnvelope<T>` / `EventContract`)
```json
{
  "eventId": "01918a22-4a7b-7212-8e2b-7c3e1e9f1a01",
  "eventType": "payment.settled.v1",
  "version": 1,
  "source": "payment-gateway-service",
  "occurredAt": "2026-08-29T14:45:00.000Z",
  "correlationId": "01918a22-4a7b-7212-8e2b-7c3e1e9f1a00",
  "traceParent": "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
  "idempotencyKey": "pay_tx_99882312",
  "tenantId": "tenant_acme_corp",
  "payload": {
    "orderId": "ord_88129",
    "amount": 149.90,
    "currency": "BRL",
    "status": "PAID"
  },
  "metadata": {
    "environment": "production",
    "region": "sa-east-1"
  }
}
```

---

## 3. Arquitetura do Sistema e Limites de Contexto

```
                    ┌────────────────────────────────────────────────────────┐
                    │                      HTTP CLIENT                       │
                    └───────────────────────────┬────────────────────────────┘
                                                │ POST /api/v1/events
                                                ▼
┌────────────────────────────────────────────────────────────────────────────────────────┐
│ EventFlow.Api (ASP.NET Core)                                                           │
│  ├─ Authentication & Rate Limiting                                                     │
│  ├─ Input Validation (FluentValidation / DataAnnotations)                              │
│  ├─ OpenTelemetry Activity Context Capture (W3C TraceContext)                          │
│  └─ Ingestion Controller / Minimal Endpoint                                            │
└───────────────────────────────────────────────┬────────────────────────────────────────┘
                                                │
                                                ▼
┌────────────────────────────────────────────────────────────────────────────────────────┐
│ EventFlow.Application & EventFlow.Domain                                               │
│  ├─ Domain Validation & Invariant Enforcement                                          │
│  ├─ Event Envelope Normalization                                                       │
│  └─ Transaction Scope (EF Core DbContext)                                              │
└───────────────────────────────────────────────┬────────────────────────────────────────┘
                                                │
                    ┌───────────────────────────┴────────────────────────────┐
                    │ ACID Database Transaction (PostgreSQL)                 │
                    │ ├─ INSERT INTO events (...)                            │
                    │ └─ INSERT INTO outbox_messages (status='Pending', ...) │
                    └───────────────────────────┬────────────────────────────┘
                                                │
                                                ▼ HTTP 202 Accepted { eventId }
                                                
──────────────────────────────────────────────────────────────────────────────────────────

┌────────────────────────────────────────────────────────────────────────────────────────┐
│ EventFlow.Worker (Background Service - Outbox Dispatcher)                              │
│  ├─ Polls Pending Outbox Messages (Batch with SELECT ... FOR UPDATE SKIP LOCKED)       │
│  ├─ Publishes to RabbitMQ Topic Exchange (with TraceContext injected into headers)     │
│  └─ Updates Outbox status to 'Published' (or deletes depending on retention policy)    │
└───────────────────────────────────────────────┬────────────────────────────────────────┘
                                                │
                                                ▼
                                    ┌───────────────────────┐
                                    │ RabbitMQ Exchange/Q   │
                                    └───────────┬───────────┘
                                                │
                                                ▼
┌────────────────────────────────────────────────────────────────────────────────────────┐
│ EventFlow.Worker (Background Service - Event Consumer)                                 │
│  ├─ W3C TraceContext Extraction from Headers                                           │
│  ├─ Idempotency Check (Redis Fast Guard + PostgreSQL Unique Constraint)                │
│  ├─ Resilience Pipeline (Polly: Retry + Backoff + Circuit Breaker)                     │
│  ├─ External Dispatch / Target Processing                                              │
│  └─ On Failure Exhausted -> Publish to Dead Letter Queue (DLQ) with error metadata    │
└────────────────────────────────────────────────────────────────────────────────────────┘
```

---

## 4. Threat Model & Security Checklist (OWASP Top 10)

| Risco / Vetor | Impacto | Mitigação no EventFlow |
| :--- | :--- | :--- |
| **Broken Access Control (IDOR)** | Acesso/publicação de eventos em nome de outros tenants | Validação de `TenantId` extraído de credenciais autenticadas (Token/API Key) vs. payload. |
| **Mass Assignment & Insecure Deserialization** | Injeção de metadados não autorizados ou tipos polimórficos maliciosos | DTOs de entrada estritos e isolados; deserialização com `System.Text.Json` sem `TypeNameHandling.All`. |
| **SSRF (Server-Side Request Forgery)** | Consumidor despacha para URLs internas maliciosas (`http://169.254.169.254/`) | Validação rigorosa de URLs de destino com allowlist de esquemas e bloqueio de IPs privados/loopback. |
| **Replay Attacks & Race Conditions** | Processamento duplicado cobrando cliente mais de uma vez | Chave de idempotência única no Postgres (`UNIQUE (tenant_id, idempotency_key, scope)`) + Trava atômica no Redis. |
| **Denial of Service (DoS/Resource Exhaustion)** | Flood de payloads gigantescos | Limite estrito de tamanho de payload no Kestrel (ex.: 256KB max) e Rate Limiting por API Key/IP. |
| **Secrets Exposure & Information Disclosure** | Vazamento de credenciais ou stack traces | Proibição de secrets no código/Git; `ProblemDetails` sanitizado (sem stack traces em Produção). |

---

## 5. Estratégia de Observabilidade

1. **Distributed Tracing (OpenTelemetry + W3C Trace Context):**
   - Ingestão HTTP extrai ou cria `traceparent`.
   - Outbox salva o `traceparent` no metadado da mensagem.
   - Dispatcher propaga `traceparent` nas headers AMQP do RabbitMQ.
   - Consumer extrai o `traceparent` e inicia um Activity filho vinculado ao trace original.
2. **Logs Estruturados (`ILogger`):**
   - Parâmetros sempre nomeados: `logger.LogInformation("Event {EventId} processed for Tenant {TenantId} in {ElapsedMs}ms", ...)`
   - Sem interpolação de strings. Zero dados sensíveis (PII, tokens, senhas).
3. **Métricas Essenciais (RED & USE Method):**
   - `eventflow_events_received_total` (Counter, labels: event_type, status)
   - `eventflow_ingestion_duration_seconds` (Histogram)
   - `eventflow_outbox_pending_count` (ObservableGauge)
   - `eventflow_processing_duration_seconds` (Histogram)
   - `eventflow_idempotency_conflicts_total` (Counter)
   - `eventflow_dlq_messages_total` (Counter, labels: reason, event_type)

---

## 6. Decisões Arquiteturais Iniciais (ADR Previews)

- **ADR-0001: Arquitetura Monólito Modular com Separação Limpa de Camadas** (Evita complexidade de rede prematura entre API e Worker enquanto compartilha Domain/Infrastructure de forma desacoplada).
- **ADR-0002: Outbox Pattern Transacional com `SKIP LOCKED`** (Garante consistência ACID sem distributed transactions/2PC).
- **ADR-0003: Estratégia de Idempotência Híbrida (Redis Lock de Curto Prazo + Postgres Unique Constraint Permanente)** (Combina performance de lock em memória com a consistência imutável do banco relacional).
- **ADR-0004: Escolha do Transporte de Mensagens: RabbitMQ Direto vs MassTransit** (Avaliado tecnicamente para priorizar controle explícito, clareza e domínio dos conceitos fundamentais).
- **ADR-0005: Resiliência Declarativa com `Microsoft.Extensions.Resilience` (Polly v8)** (Pipeline de resiliência moderno e zero alocação).

---

## 7. Estrutura Proposta da Solução

```text
eventflow/
├── .editorconfig
├── .gitignore
├── EventFlow.sln
├── Directory.Build.props
├── README.md
├── docker-compose.yml
├── docs/
│   ├── architecture/
│   │   └── EVENTFLOW_MASTER_PLAN.md
│   ├── adr/
│   │   ├── 0001-modular-architecture.md
│   │   ├── 0002-transactional-outbox.md
│   │   ├── 0003-idempotency-strategy.md
│   │   ├── 0004-messaging-transport.md
│   │   └── 0005-resilience-strategy.md
│   ├── security/
│   │   └── threat-model.md
│   └── business-rules/
│       └── event-contracts.md
├── src/
│   ├── EventFlow.Domain/
│   │   ├── Common/          (Result, Error, Entity base)
│   │   ├── Events/          (EventEnvelope, EventMetadata)
│   │   └── Outbox/          (OutboxMessage, OutboxStatus)
│   ├── EventFlow.Application/
│   │   ├── Abstractions/    (Data, Messaging, Time, Observability)
│   │   ├── Events/          (IngestEventCommand, IngestEventValidator)
│   │   └── Outbox/          (OutboxProcessorService)
│   ├── EventFlow.Infrastructure/
│   │   ├── Persistence/     (EF Core DbContext, Configurations, Migrations)
│   │   ├── Messaging/       (RabbitMQ Publisher, Consumer, DLQ)
│   │   ├── Idempotency/     (Redis & Postgres Implementations)
│   │   └── Observability/   (OpenTelemetry Setup, ActivitySources, Metrics)
│   ├── EventFlow.Api/
│   │   ├── Endpoints/       (Event Ingestion Endpoints)
│   │   ├── Middleware/      (Exception Handling, TraceContext, RateLimiting)
│   │   ├── Program.cs
│   │   └── appsettings.json
│   ├── EventFlow.Migrator/
│   │   ├── Program.cs       (Standalone resilient migration runner)
│   │   └── appsettings.json
│   └── EventFlow.Worker/
│       ├── BackgroundServices/ (OutboxDispatcherWorker, EventConsumerWorker)
│       ├── Program.cs
│       └── appsettings.json
└── tests/
    ├── EventFlow.UnitTests/
    │   ├── Domain/
    │   └── Application/
    └── EventFlow.IntegrationTests/
        ├── Api/
        ├── Outbox/
        └── Consumers/
```

---

## 8. Roadmap Incremental de Execução (14 Fases)

Cada fase é executada sequencialmente e validada pelo **Processo de 4 Etapas de Revisão (Corretude, Arquitetura, Segurança, Manutenibilidade)** seguido de commit Git formal.

| Fase | Título | Entregável Principal |
| :--- | :--- | :--- |
| **FASE 1** | **Domínio Base & API Inicial** | Contratos de evento, envelope, validação estrita, endpoint de ingestão HTTP, Testes Unitários de Domínio. |
| **FASE 2** | **PostgreSQL & EF Core 9/10** | DbContext, mapeamento relacional estrito, constraints, migrações, persistência de eventos e Outbox na mesma transação. |
| **FASE 3** | **Transactional Outbox Pattern** | Tabela de outbox, `SELECT FOR UPDATE SKIP LOCKED`, estado de despacho, tratamento de concorrência e falhas. |
| **FASE 4** | **RabbitMQ Messaging** | Configuração de exchanges, routing keys, conexão resiliente, publisher com injeção de TraceContext. |
| **FASE 5** | **Worker & Event Consumer** | Worker service com scoped lifecycle, consumo assíncrono seguro, parsing de envelope e orquestração. |
| **FASE 6** | **Idempotência Real & Redis** | Trava atômica no Redis + Constraint no Postgres. Análise de falhas parciais e TTL. |
| **FASE 7** | **Resiliência (Polly v8)** | Retry com Exponential Backoff + Jitter, Circuit Breaker e Timeouts granulares. |
| **FASE 8** | **Dead Letter Queue (DLQ)** | Enriquecimento de erros, roteamento para DLQ após retries esgotados, estratégia de replay seguro. |
| **FASE 9** | **OpenTelemetry & Observabilidade** | Distributed Tracing W3C end-to-end, Métricas de performance/fila, Logs estruturados sanitizados. |
| **FASE 10** | **Security Hardening** | Autenticação, autorização por tenant, mitigação OWASP, sanitização e rate limiting. |
| **FASE 11** | **Testes de Integração (Testcontainers)** | Testes com PostgreSQL, RabbitMQ e Redis reais validando fluxos de ponta a ponta e concorrência. |
| **FASE 12** | **Docker & CI/CD** | `Dockerfile` multi-stage não-root, `docker-compose.yml`, GitHub Actions workflow com security checks. |
| **FASE 13** | **Documentação Técnica & ADRs** | Todos os ADRs finalizados, diagramas C4 atualizados, README de nível sênior. |
| **FASE 14** | **Adversarial Audit & Code Review** | Testes de caos, tentativas de quebra (race condition, duplicate replay, poison message), relatório final de engenharia. |
