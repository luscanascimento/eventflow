# EventFlow — Contratos de Eventos, Esquemas e Regras de Negócio

> **Documento:** Especificação Técnica de Contratos, Envelopes e Regras de Idempotência  
> **Classificação:** Engenharia de Domínio e Integração  
> **Status:** Aprovado e Implementado (Fases 1 a 12)  

---

## 1. Filosofia de Contratos de Eventos

No **EventFlow**, eventos são tratados como **fatos imutáveis de negócio** ocorridos no passado. Uma vez emitidos e aceitos pela API, seus registros não sofrem mutação, servindo tanto para o processamento assíncrono de efeitos colaterais quanto para trilhas de auditoria contábil e operacional.

Para garantir interoperabilidade entre diferentes serviços e linguagens, o sistema adota um **Envelope Padronizado (`EventEnvelope`)** que encapsula metadados de governança, rastreabilidade distribuída, contexto multi-tenant e o payload específico do domínio.

---

## 2. Especificação do Envelope Canônico (`EventEnvelope`)

Todos os eventos ingeridos pela API e publicados na mensageria seguem a estrutura padronizada abaixo:

```json
{
  "eventId": "01918a22-4a7b-7212-8e2b-7c3e1e9f1a01",
  "eventType": "payment.settled.v1",
  "version": 1,
  "source": "payment-gateway-service",
  "occurredAt": "2026-08-29T14:45:00.000Z",
  "correlationId": "corr_987123984",
  "traceParent": "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
  "tenantId": "tenant_acme_corp",
  "idempotencyKey": "pay_tx_88192381",
  "payload": {
    "settlementId": "stl_01HZ",
    "orderId": "ord_99812",
    "amount": 250.75,
    "currency": "BRL",
    "status": "SETTLED"
  },
  "metadata": {
    "environment": "production",
    "region": "sa-east-1"
  }
}
```

### 2.1 Dicionário de Campos e Validações Estritas

| Campo | Tipo | Obrigatório | Restrições & Validação | Descrição |
| :--- | :--- | :--- | :--- | :--- |
| `eventId` | `Guid` (UUID v4/v7) | Sim (Gerado se omitido) | Não pode ser `Guid.Empty`. | Identificador global único e imutável do evento. |
| `eventType` | `string` | Sim | 3 a 128 chars. Regex: `^[a-z0-9]+(\.[a-z0-9_\-]+)+$` | Nome qualificado do evento em notação de pontos com versão. |
| `version` | `int` | Opcional (Default: 1) | `>= 1` | Versão do schema do payload para evolução de contratos. |
| `source` | `string` | Sim | 3 a 128 chars. | Identificador do sistema ou serviço originador do evento. |
| `occurredAt` | `DateTimeOffset` | Sim (Default: UTC Now) | Formato ISO 8601 UTC. | Momento exato em que o fato de negócio ocorreu na origem. |
| `correlationId`| `string` | Sim (Gera UUID se omitido) | 1 a 128 chars. | Identificador de correlação transversal entre múltiplos serviços. |
| `traceParent` | `string` | Opcional (Gera se omitido) | Padrão W3C: `^00-[0-9a-f]{32}-[0-9a-f]{16}-[0-9a-f]{2}$` | Identificador de rastreabilidade distribuída OpenTelemetry. |
| `tenantId` | `string` | Sim | 3 a 64 chars. Regex: `^[a-zA-Z0-9_\-]+$` | Identificador do tenant para particionamento e isolamento de dados. |
| `idempotencyKey`| `string` | Sim | 3 a 128 chars. Regex: `^[a-zA-Z0-9_\-\.:#]+$` | Chave única que garante que o evento seja processado apenas uma vez. |
| `payload` | `JSON Object/Array` | Sim | Máximo de **256 KB**. JSON válido. | Dados específicos do evento de negócio. |
| `metadata` | `Dictionary<string, string>` | Opcional | Chaves/valores de contexto adicional (região, ambiente, etc.). | Metadados operacionais não estruturados. |

---

## 3. Catálogo de Eventos Suportados

### 3.1 `payment.settled.v1` (Liquidação Financeira)
Emitido quando uma transação de pagamento é liquidada com sucesso pela adquirente ou gateway financeiro.

* **Routing Key RabbitMQ:** `payment.settled.v1`
* **Handler de Domínio:** `PaymentSettledHandler`
* **Scope de Idempotência:** `PaymentSettledHandler`
* **Payload de Exemplo:**
  ```json
  {
    "settlementId": "stl_01HZX8921",
    "orderId": "ord_998231",
    "amount": 499.90,
    "currency": "BRL",
    "paymentMethod": "PIX",
    "settledAt": "2026-08-31T18:00:00Z",
    "feeAmount": 2.50
  }
  ```

---

## 4. Regras de Idempotência e Escopos de Processamento

A idempotência no EventFlow é particionada em **três dimensões**:
$$\text{Chave Composta} = (\text{tenant\_id}, \text{scope}, \text{idempotency\_key})$$

1. **Multi-Tenancy Isolado:** O mesmo `idempotencyKey` pode ser utilizado por tenants diferentes sem colisão.
2. **Multi-Scope / Handler Independence:** Diferentes handlers (ex: `PaymentSettledHandler` e `AnalyticsNotificationHandler`) podem processar o mesmo evento de forma autônoma utilizando seus próprios escopos.
3. **Ciclo de Vida do Lock:**
   * **In-Flight Lock:** Chave temporária no Redis com TTL de 30s. Liberação atômica via Lua script caso ocorra erro transitório.
   * **Completed Record:** Inserção permanente na tabela `processed_events` do PostgreSQL + cache de 24h no Redis.

---

## 5. Topologia AMQP e Contrato de Mensageria

```text
  [ Outbox Dispatcher ]
            │
            ▼ (Publica com DeliveryMode=2 + Headers W3C)
┌───────────────────────────────────────┐
│ Topic Exchange: eventflow.events      │
└───────────────────┬───────────────────┘
                    │ Binding: # (ou payment.#)
                    ▼
┌───────────────────────────────────────┐
│ Queue: eventflow.events.queue         │ ◄── Prefetch QoS = 10
│ Arguments:                            │
│  - x-dead-letter-exchange:            │
│      "eventflow.dlx"                  │
│  - x-dead-letter-routing-key:         │
│      "eventflow.events.dlq"           │
└───────────────────┬───────────────────┘
                    │ Falha esgotada (Nack com requeue=false)
                    ▼
┌───────────────────────────────────────┐
│ DLX: eventflow.dlx                    │
└───────────────────┬───────────────────┘
                    │
                    ▼
┌───────────────────────────────────────┐
│ Queue: eventflow.events.dlq           │ ◄── Inspeção e Replay Seguro
└───────────────────────────────────────┘
```

### 5.1 Headers Obrigatórios nas Mensagens AMQP:
- `traceparent`: Contexto W3C TraceContext para correlação no APM/Jaeger.
- `tenant_id`: Identificador do tenant.
- `correlation_id`: Identificador de correlação transversal.
- `event_type`: Tipo do evento.
- `version`: Versão do esquema.

---

## 6. Contrato da Dead Letter Queue (DLQ) e Replay

Quando uma mensagem falha após todas as tentativas do pipeline de resiliência (Polly v8), ela é roteada automaticamente para a DLQ com enriquecimento do erro:

```json
{
  "messageId": "01918a22-4a7b-7212-8e2b-7c3e1e9f1a01",
  "eventType": "payment.settled.v1",
  "tenantId": "tenant_acme_corp",
  "routingKey": "payment.settled.v1",
  "failedAt": "2026-08-31T18:05:22.120Z",
  "retryCount": 3,
  "deathReason": "rejected",
  "exceptionMessage": "HTTP 503 Service Unavailable downstream partner unreachable.",
  "originalPayload": "{\"settlementId\":\"stl_01HZX8921\", ...}"
}
```

### 6.1 Semântica de Replay Seguro:
* O endpoint `POST /api/v1/dlq/replay?maxCount=50` lê mensagens da DLQ e as republica na `eventflow.events` mantendo os headers e payloads originais.
* Graças à verificação de idempotência no PostgreSQL e Redis, o replay é **100% seguro contra duplicações acidentais**.
