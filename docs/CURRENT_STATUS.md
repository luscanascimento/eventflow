# EventFlow — Status Atual do Projeto & Ponto de Retomada

> **Última Atualização:** 2026-08-30  
> **Status:** Fases 1 a 7 CONCLUÍDAS E TESTADAS (99 testes passando, 0 warnings)  
> **Próxima Etapa:** **FASE 8 — DLQ + Retry Strategy (Dead Letter Exchange & Replay)**

---

## 📊 Progresso Geral das Fases

| Fase | Título | Status | Commit Git |
| :--- | :--- | :--- | :--- |
| **FASE 1** | Domínio Base & API Inicial | ✅ Concluída | `b5c89b4` |
| **FASE 2** | PostgreSQL + EF Core (Persistência & Outbox) | ✅ Concluída | `376c587` |
| **FASE 3** | Transactional Outbox & Background Dispatcher | ✅ Concluída | `85bcdc9` |
| **FASE 4** | RabbitMQ Messaging (Publisher & W3C Headers) | ✅ Concluída | `9c4eb3d` |
| **FASE 5** | Worker & Consumer de Eventos | ✅ Concluída | `eff998f` |
| **FASE 6** | Idempotência Real & Redis | ✅ Concluída | `23c26ae` |
| **FASE 7** | Resiliência (Polly v8) | ✅ Concluída | *Este commit* |
| **FASE 8** | DLQ + Retry Strategy | ⏳ **A Iniciar na Próxima Sessão** | — |
| **FASE 9** | OpenTelemetry (Distributed Tracing & Metrics) | ⏳ Pendente | — |
| **FASE 10** | Security Hardening (OWASP Top 10) | ⏳ Pendente | — |
| **FASE 11** | Testes de Integração com Testcontainers | ⏳ Pendente | — |
| **FASE 12** | Docker Multi-Stage & CI/CD GitHub Actions | ⏳ Pendente | — |
| **FASE 13** | Documentação Técnica Completa & ADRs | ⏳ Pendente | — |
| **FASE 14** | Adversarial Audit & Chaos Review | ⏳ Pendente | — |

---

## 🏗️ O que está pronto e funcionando no repositório

1. **Camada de Domínio (`EventFlow.Domain`):**
   - Contratos imutáveis: `EventEnvelope`, `EventRecord`, `OutboxMessage` (com máquina de estados: Pending, Processing, Published, DeadLettered).
   - Entidade de Idempotência: `ProcessedEvent` com validação de invariantes e identificação multi-tenant/scope.
   - Result Pattern explícito com `Result<T>` e `DomainError` tipados.
2. **Camada de Aplicação (`EventFlow.Application`):**
   - `IngestEventUseCase` com validação defensiva via `FluentValidation` (limite de 256KB, regex estrita de tenant/tipo, formato W3C).
   - `OutboxProcessor` com batching configurável e cálculo de exponential backoff.
   - Contratos de Idempotência: `IIdempotencyManager`, `IdempotencyAcquireResult`, `IdempotencyAcquireStatus`.
   - Orquestrador de eventos de consumo: `IEventProcessor`, `EventProcessor`, `IEventConsumerHandler` integrado com checagem e lock de idempotência.
   - Contrato de Resiliência: `IExternalDispatcher` para envio de webhooks e notificações downstream.
   - Handler de negócio: `PaymentSettledHandler` com validação defensiva de payload.
   - Abstrações limpas: `IEventStore`, `IOutboxStore`, `IEventPublisher`, `IDateTimeProvider`.
3. **Camada de Infraestrutura (`EventFlow.Infrastructure`):**
   - `EventFlowDbContext` configurado com PostgreSQL / EF Core 9 e tabela `processed_events`.
   - `HybridIdempotencyManager`: Fast Guard no Redis (TTL 24h) + Trava Distribuída (NX EX 30 com liberação atômica via Lua script) + Postgres ACID fallback.
   - `RabbitMqConnection`, `RabbitMqEventPublisher` e `RabbitMqConsumer` com driver `RabbitMQ.Client 7.x` com async nativo, QoS prefetch, binding em topic exchange e propagação de headers W3C (`traceparent`, `tenant_id`, `event_type`).
   - **Pipelines de Resiliência com Polly v8 (`Microsoft.Extensions.Resilience`):**
     - Total Timeout (20s) -> Retry com Exponential Backoff e Decorrelated Jitter -> Circuit Breaker (50% taxa de falhas em janela de 30s) -> Per-Attempt Timeout (5s).
     - `HttpExternalDispatcher` com mitigação de SSRF (validação estrita de esquemas HTTP/HTTPS) e injeção de TraceContext W3C.
4. **Camada de API (`EventFlow.Api`):**
   - Minimal API `POST /api/v1/events` retornando `202 Accepted` com `Location` header.
   - `GlobalExceptionHandler` RFC 7807 `ProblemDetails` sem vazamento de dados sensíveis em produção.
   - Limites no Kestrel (`MaxRequestBodySize = 256KB`).
5. **Camada de Processamento Assíncrono (`EventFlow.Worker`):**
   - `OutboxDispatcherWorker`: polling periódico assíncrono com `PeriodicTimer` e escopo de DI isolado.
   - `EventConsumerWorker`: background service com escopo de DI assíncrono por mensagem (`IServiceScopeFactory`), despacho tipado para `IEventProcessor` com trava de idempotência e graceful shutdown.
6. **Qualidade e Testes (`EventFlow.UnitTests`):**
   - 99 testes unitários passando em menos de 800ms.
   - Compilação com `TreatWarningsAsErrors=true`, `.editorconfig` e `dotnet format` 100% aderentes.
7. **Decisões Arquiteturais Registradas:**
   - `docs/adr/0003-idempotency-strategy.md` (Estratégia de Idempotência Híbrida: Redis + PostgreSQL).
   - `docs/adr/0004-messaging-transport.md` (RabbitMQ Direto vs MassTransit).
   - `docs/adr/0005-resilience-strategy.md` (Estratégia de Resiliência Declarativa: Polly v8).

---

## 🎯 Instruções para a Retomada (FASE 8)

Quando iniciar a próxima sessão, os passos para a **FASE 8: DLQ + Retry Strategy** serão:
1. Declarar Dead Letter Exchange (DLX) e Dead Letter Queue (DLQ) no RabbitMQ (`eventflow.dlx` e `eventflow.events.dlq`).
2. Configurar metadados e headers de enriquecimento de falhas no envio para DLQ (motivo da falha, stack trace sanitizado, contagem de tentativas, timestamp, payload original).
3. Implementar publisher/consumer de DLQ para suporte a inspeção e replay seguro de eventos.
4. Suíte de testes unitários para a infraestrutura de DLQ.
5. Code Review de 4 etapas e commit/push da Fase 8.
