# EventFlow — Status Atual do Projeto & Ponto de Retomada

> **Última Atualização:** 2026-08-30  
> **Status:** Fases 1 a 5 CONCLUÍDAS E TESTADAS (66 testes passando, 0 warnings)  
> **Próxima Etapa:** **FASE 6 — Idempotência Real & Redis**

---

## 📊 Progresso Geral das Fases

| Fase | Título | Status | Commit Git |
| :--- | :--- | :--- | :--- |
| **FASE 1** | Domínio Base & API Inicial | ✅ Concluída | `b5c89b4` |
| **FASE 2** | PostgreSQL + EF Core (Persistência & Outbox) | ✅ Concluída | `376c587` |
| **FASE 3** | Transactional Outbox & Background Dispatcher | ✅ Concluída | `85bcdc9` |
| **FASE 4** | RabbitMQ Messaging (Publisher & W3C Headers) | ✅ Concluída | `9c4eb3d` |
| **FASE 5** | Worker & Consumer de Eventos | ✅ Concluída | *Este commit* |
| **FASE 6** | Idempotência Real & Redis | ⏳ **A Iniciar na Próxima Sessão** | — |
| **FASE 7** | Resiliência (Polly v8) | ⏳ Pendente | — |
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
   - Result Pattern explícito com `Result<T>` e `DomainError` tipados.
2. **Camada de Aplicação (`EventFlow.Application`):**
   - `IngestEventUseCase` com validação defensiva via `FluentValidation` (limite de 256KB, regex estrita de tenant/tipo, formato W3C).
   - `OutboxProcessor` com batching configurável e cálculo de exponential backoff.
   - Orquestrador de eventos de consumo: `IEventProcessor`, `EventProcessor`, `IEventConsumerHandler`.
   - Handler de negócio: `PaymentSettledHandler` com validação defensiva de payload.
   - Abstrações limpas: `IEventStore`, `IOutboxStore`, `IEventPublisher`, `IDateTimeProvider`.
3. **Camada de Infraestrutura (`EventFlow.Infrastructure`):**
   - `EventFlowDbContext` configurado com PostgreSQL / EF Core 9.
   - Unique Index `(tenant_id, idempotency_key)` para garantia relacional anti-duplicação.
   - Polling index `(status, next_retry_at, created_at)` para o Outbox.
   - `EfOutboxStore` com suporte a `SELECT ... FOR UPDATE SKIP LOCKED` para concorrência entre réplicas.
   - `RabbitMqConnection` e `RabbitMqEventPublisher` utilizando driver `RabbitMQ.Client 7.x` com async nativo, entrega persistente (`DeliveryMode = 2`) e propagação de headers W3C (`traceparent`, `tenant_id`, `event_type`).
   - `RabbitMqConsumer` com declaração de fila durável (`eventflow.events.processing`), binding no exchange de tópicos (`#`), controle de QoS/backpressure (`PrefetchCount`) e extração resiliente de headers via `AmqpHeaderParser`.
4. **Camada de API (`EventFlow.Api`):**
   - Minimal API `POST /api/v1/events` retornando `202 Accepted` com `Location` header.
   - `GlobalExceptionHandler` RFC 7807 `ProblemDetails` sem vazamento de dados sensíveis em produção.
   - Limites no Kestrel (`MaxRequestBodySize = 256KB`).
5. **Camada de Processamento Assíncrono (`EventFlow.Worker`):**
   - `OutboxDispatcherWorker`: polling periódico assíncrono com `PeriodicTimer` e escopo de DI isolado.
   - `EventConsumerWorker`: background service com escopo de DI assíncrono por mensagem (`IServiceScopeFactory`), despacho tipado para `IEventProcessor` e graceful shutdown.
6. **Qualidade e Testes (`EventFlow.UnitTests`):**
   - 66 testes unitários passando em menos de 800ms.
   - Compilação com `TreatWarningsAsErrors=true`, `.editorconfig` e `dotnet format` 100% aderentes.
7. **Decisões Arquiteturais Registradas:**
   - `docs/adr/0004-messaging-transport.md` (RabbitMQ Direto vs MassTransit).

---

## 🎯 Instruções para a Retomada (FASE 6)

Quando iniciar a próxima sessão, os passos para a **FASE 6: Idempotência Real & Redis** serão:
1. Definir contrato e estratégia de idempotência híbrida (Trava atômica no Redis + persistência no Postgres).
2. Implementar `IIdempotencyStore` / `IIdempotencyManager` com `StackExchange.Redis`.
3. Criar registro de idempotência com controle de concorrência e TTL de release/lock.
4. Integrar idempotência ao pipeline do consumidor de eventos.
5. Criar suíte de testes unitários para a idempotência.
6. Code Review de 4 etapas e commit/push da Fase 6.
