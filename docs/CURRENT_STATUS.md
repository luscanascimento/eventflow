# EventFlow — Status Atual do Projeto & Ponto de Retomada

> **Última Atualização:** 2026-08-29  
> **Status:** Fases 1 a 4 CONCLUÍDAS E TESTADAS (46 testes passando, 0 warnings)  
> **Próxima Etapa:** **FASE 5 — Worker/Consumer de Eventos**

---

## 📊 Progresso Geral das Fases

| Fase | Título | Status | Commit Git |
| :--- | :--- | :--- | :--- |
| **FASE 1** | Domínio Base & API Inicial | ✅ Concluída | `b5c89b4` |
| **FASE 2** | PostgreSQL + EF Core (Persistência & Outbox) | ✅ Concluída | `376c587` |
| **FASE 3** | Transactional Outbox & Background Dispatcher | ✅ Concluída | `85bcdc9` |
| **FASE 4** | RabbitMQ Messaging (Publisher & W3C Headers) | ✅ Concluída | *Este commit* |
| **FASE 5** | Worker & Consumer de Eventos | ⏳ **A Iniciar na Próxima Sessão** | — |
| **FASE 6** | Idempotência Real & Redis | ⏳ Pendente | — |
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
   - Abstrações limpas: `IEventStore`, `IOutboxStore`, `IEventPublisher`, `IDateTimeProvider`.
3. **Camada de Infraestrutura (`EventFlow.Infrastructure`):**
   - `EventFlowDbContext` configurado com PostgreSQL / EF Core 9.
   - Unique Index `(tenant_id, idempotency_key)` para garantia relacional anti-duplicação.
   - Polling index `(status, next_retry_at, created_at)` para o Outbox.
   - `EfOutboxStore` com suporte a `SELECT ... FOR UPDATE SKIP LOCKED` para concorrência entre réplicas.
   - `RabbitMqConnection` e `RabbitMqEventPublisher` utilizando o driver oficial `RabbitMQ.Client 7.x` com async nativo, entrega persistente (`DeliveryMode = 2`) e propagação de headers W3C (`traceparent`, `tenant_id`, `event_type`).
4. **Camada de API (`EventFlow.Api`):**
   - Minimal API `POST /api/v1/events` retornando `202 Accepted` com `Location` header.
   - `GlobalExceptionHandler` RFC 7807 `ProblemDetails` sem vazamento de dados sensíveis em produção.
   - Limites no Kestrel (`MaxRequestBodySize = 256KB`).
5. **Camada de Processamento Assíncrono (`EventFlow.Worker`):**
   - `OutboxDispatcherWorker` background service baseado em `PeriodicTimer` assíncrono com escopos isolados de DI (`IServiceScopeFactory`).
6. **Qualidade e Testes (`EventFlow.UnitTests`):**
   - 46 testes unitários passando em menos de 800ms.
   - Compilação com `TreatWarningsAsErrors=true`, `.editorconfig` e `dotnet format` 100% aderentes.
7. **Decisões Arquiteturais Registradas:**
   - `docs/adr/0004-messaging-transport.md` (RabbitMQ Direto vs MassTransit).

---

## 🎯 Instruções para a Retomada (FASE 5)

Quando iniciar a próxima sessão, os passos para a **FASE 5: Worker & Event Consumer** serão:
1. Criar o background service de consumo contínuo: `EventConsumerWorker` em `EventFlow.Worker`.
2. Declarar a fila AMQP do consumidor (`eventflow.orders.processing` ou binding no exchange `eventflow.events`).
3. Implementar a extração e parsing do envelope de evento a partir da mensagem AMQP e headers W3C.
4. Implementar o orquestrador de processamento assíncrono (`IEventHandler` / `IEventProcessor`).
5. Suíte de testes unitários para o consumer.
6. Code Review de 4 etapas e commit/push da Fase 5.
