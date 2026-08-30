# EventFlow — Status Atual do Projeto & Ponto de Retomada

> **Última Atualização:** 2026-08-30  
> **Status:** Fases 1 a 6 CONCLUÍDAS E TESTADAS (90 testes passando, 0 warnings)  
> **Próxima Etapa:** **FASE 7 — Resiliência (Polly v8 / Microsoft.Extensions.Resilience)**

---

## 📊 Progresso Geral das Fases

| Fase | Título | Status | Commit Git |
| :--- | :--- | :--- | :--- |
| **FASE 1** | Domínio Base & API Inicial | ✅ Concluída | `b5c89b4` |
| **FASE 2** | PostgreSQL + EF Core (Persistência & Outbox) | ✅ Concluída | `376c587` |
| **FASE 3** | Transactional Outbox & Background Dispatcher | ✅ Concluída | `85bcdc9` |
| **FASE 4** | RabbitMQ Messaging (Publisher & W3C Headers) | ✅ Concluída | `9c4eb3d` |
| **FASE 5** | Worker & Consumer de Eventos | ✅ Concluída | `eff998f` |
| **FASE 6** | Idempotência Real & Redis | ✅ Concluída | *Este commit* |
| **FASE 7** | Resiliência (Polly v8) | ⏳ **A Iniciar na Próxima Sessão** | — |
| **FASE 8** | DLQ + Retry Strategy | ⏳ Pendente | — |
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
   - Handler de negócio: `PaymentSettledHandler` com validação defensiva de payload.
   - Abstrações limpas: `IEventStore`, `IOutboxStore`, `IEventPublisher`, `IDateTimeProvider`.
3. **Camada de Infraestrutura (`EventFlow.Infrastructure`):**
   - `EventFlowDbContext` configurado com PostgreSQL / EF Core 9.
   - Tabela `processed_events` com índice único `UNIQUE (tenant_id, scope, idempotency_key)` e migração EF Core.
   - `HybridIdempotencyManager`:
     - Fast Guard em memória no Redis (`SET completed key`, TTL 24h).
     - Trava distribuída in-flight atômica no Redis (`SET lock NX EX 30`).
     - Liberação atômica segura de lock via script Lua.
     - Single Source of Truth persistente no PostgreSQL com degradação graciosa em caso de falha do Redis.
   - `RabbitMqConnection`, `RabbitMqEventPublisher` e `RabbitMqConsumer` com driver `RabbitMQ.Client 7.x` com async nativo, QoS prefetch, binding em topic exchange e propagação de headers W3C (`traceparent`, `tenant_id`, `event_type`).
4. **Camada de API (`EventFlow.Api`):**
   - Minimal API `POST /api/v1/events` retornando `202 Accepted` com `Location` header.
   - `GlobalExceptionHandler` RFC 7807 `ProblemDetails` sem vazamento de dados sensíveis em produção.
   - Limites no Kestrel (`MaxRequestBodySize = 256KB`).
5. **Camada de Processamento Assíncrono (`EventFlow.Worker`):**
   - `OutboxDispatcherWorker`: polling periódico assíncrono com `PeriodicTimer` e escopo de DI isolado.
   - `EventConsumerWorker`: background service com escopo de DI assíncrono por mensagem (`IServiceScopeFactory`), despacho tipado para `IEventProcessor` com trava de idempotência e graceful shutdown.
6. **Qualidade e Testes (`EventFlow.UnitTests`):**
   - 90 testes unitários passando em menos de 800ms.
   - Compilação com `TreatWarningsAsErrors=true`, `.editorconfig` e `dotnet format` 100% aderentes.
7. **Decisões Arquiteturais Registradas:**
   - `docs/adr/0003-idempotency-strategy.md` (Estratégia de Idempotência Híbrida: Redis + PostgreSQL).
   - `docs/adr/0004-messaging-transport.md` (RabbitMQ Direto vs MassTransit).

---

## 🎯 Instruções para a Retomada (FASE 7)

Quando iniciar a próxima sessão, os passos para a **FASE 7: Resiliência (Polly v8)** serão:
1. Configurar o pipeline de resiliência declarativo com `Microsoft.Extensions.Resilience` (Polly v8).
2. Implementar Retry Strategy com Exponential Backoff + Jitter decorrelacionado.
3. Configurar Circuit Breaker e Timeouts granulares para chamadas externas/handlers.
4. Documentar `docs/adr/0005-resilience-strategy.md`.
5. Criar suíte de testes unitários para os pipelines de resiliência.
6. Code Review de 4 etapas e commit/push da Fase 7.
