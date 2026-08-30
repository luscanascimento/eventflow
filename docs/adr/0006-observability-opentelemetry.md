# ADR-0006: Observabilidade Distribuída — OpenTelemetry, W3C TraceContext e Métricas

## Status
**Aceito** (FASE 9)

## Contexto
O EventFlow opera em uma topologia distribuída e desacoplada: uma API HTTP recebe eventos, persiste-os de forma transacional no PostgreSQL (Outbox), um worker despacha-os para o RabbitMQ, e múltiplos workers consumidores processam as mensagens e despacham webhooks externos.

Sem observabilidade unificada:
1. É impossível correlacionar uma falha em um consumidor ou webhook com a requisição HTTP original que originou o evento.
2. A análise de causa-raiz (*Root Cause Analysis*) torna-se manual e lenta.
3. Não há visibilidade sobre métricas de throughput, latência de processamento, taxa de acerto de idempotência e acúmulo de mensagens na DLQ.
4. Frameworks proprietários de telemetria causam acoplamento (*vendor lock-in*).

## Decisão
Decidimos padronizar toda a observabilidade do EventFlow no padrão aberto **OpenTelemetry (OTel)** e **W3C TraceContext**, utilizando as APIs nativas de alta performance do .NET (`System.Diagnostics.ActivitySource` e `System.Diagnostics.Metrics.Meter`):

### 1. Rastreabilidade Distribuída de Ponta a Ponta (End-to-End Tracing):
O identificador W3C `traceparent` (versão-traceId-parentId-traceFlags) viaja por todas as etapas:
```text
[ HTTP POST /events ] ──► (Inicia Span: EventFlow.IngestEvent)
         │
         ▼
[ PostgreSQL Outbox ] ──► (Armazena traceparent no payload/envelope)
         │
         ▼
[ Outbox Dispatcher ] ──► (Span: EventFlow.OutboxBatchDispatch)
         │
         ▼
[ RabbitMQ Headers ]  ──► (Injeta header AMQP 'traceparent')
         │
         ▼
[ Consumer Worker ]   ──► (Span Filho com contexto pai: EventFlow.ConsumerProcess)
         │
         ▼
[ External Dispatch ] ──► (Injeta 'traceparent' na requisição HTTP externa downstream)
```

### 2. Métricas de Negócio e Infraestrutura (`Meter`):
* `eventflow.events.ingested`: Contador de eventos aceitos via API HTTP (por tenant e tipo).
* `eventflow.events.processed`: Contador de eventos processados pelos workers (por tenant, tipo e status).
* `eventflow.events.processing.duration`: Histograma de latência de processamento em milissegundos.
* `eventflow.outbox.dispatched`: Contador de mensagens despachadas pelo outbox (published, dead_lettered, failed_retry).
* `eventflow.idempotency.hits`: Contador de eventos duplicados interceptados pelas travas de idempotência.
* `eventflow.dlq.replayed` e `eventflow.dlq.purged`: Métricas de operações da Dead Letter Queue.

### 3. Logs Estruturados com Source Generators:
* Uso exclusivo de métodos parciais anotados com `[LoggerMessage]` (zero alocação de boxing ou parsing em runtime).
* Conformidade estrita com segurança: proibição de logging de senhas, tokens de autenticação, chaves de API e dados pessoais (PII).

### 4. Exportadores Padronizados (OTLP):
* Suporte nativo ao protocolo OTLP (gRPC/HTTP) para envio direto a coletores modernos como **Jaeger, Grafana Tempo, Prometheus, SigNoz e OpenTelemetry Collector**.

## Consequências
### Positivas:
- **Rastreabilidade 100% Correlacionada:** Qualquer evento pode ser investigado do início ao fim com um único `TraceId`.
- **Zero Lock-in:** Padrão aberto da Cloud Native Computing Foundation (CNCF).
- **Performance Máxima:** Instrumentação nativa do runtime .NET sem dependências pesadas no domínio.

### Negativas / Custos:
- Exigência de propagar explicitamente o header `traceparent` em fronteiras assíncronas de transporte (atendido nas Fases 4, 5, 7 e 8).
