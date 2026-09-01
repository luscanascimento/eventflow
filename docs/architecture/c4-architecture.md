# EventFlow — Diagramas C4 de Arquitetura & Fluxos de Sequência

> **Documento:** Diagramas Estruturais C4 (Contexto, Containers, Componentes) e Sequência Ponta a Ponta  
> **Classificação:** Engenharia e Arquitetura de Software  
> **Status:** Aprovado e Implementado (Fases 1 a 12)  

---

## 1. C4 Nível 1: Diagrama de Contexto do Sistema

O Diagrama de Contexto descreve o posicionamento do **EventFlow** em relação aos atores externos, clientes de API, destinos de webhooks e sistemas de observabilidade.

```mermaid
C4Context
    title Diagrama de Contexto do Sistema (C4 Nível 1) — EventFlow

    Person(client, "Cliente / Sistema Externo", "Envia webhooks e eventos de negócio (ex: liquidações, pedidos)")
    Person(admin, "Operador / Engenheiro SRE", "Inspeciona e gerencia a Dead Letter Queue (DLQ)")

    System(eventflow, "EventFlow Platform", "Ingestão confiável, persistência transacional ACID (Outbox) e processamento assíncrono resiliente de eventos.")

    System_Ext(partner, "Parceiro / Serviço Downstream", "Recebe notificações de eventos processados via HTTP/Webhooks externos")
    System_Ext(jaeger, "Jaeger / OTel Collector", "Coleta traces distribuídos W3C e métricas de telemetria do sistema")

    Rel(client, eventflow, "Envia eventos HTTP POST /api/v1/events", "JSON / HTTPS / API Key")
    Rel(admin, eventflow, "Gerencia DLQ via /api/v1/dlq", "HTTP / REST")
    Rel(eventflow, partner, "Despacha webhooks processados", "HTTP / HTTPS com Polly & SSRF Guard")
    Rel(eventflow, jaeger, "Exporta traces W3C e métricas", "gRPC / OTLP (4317)")
```

---

## 2. C4 Nível 2: Diagrama de Containers

O Diagrama de Containers detalha as aplicações em execução, bancos de dados, brokers de mensageria e ferramentas auxiliares que compõem a solução.

```mermaid
C4Container
    title Diagrama de Containers (C4 Nível 2) — EventFlow

    Person(client, "Cliente Externo", "Emite requisições de eventos")
    
    Container_Boundary(c1, "EventFlow Architecture Boundary") {
        Container(api, "EventFlow.Api", "ASP.NET Core 9 Minimal APIs", "Valida, autentica, captura W3C TraceContext e persiste eventos atomicamente no Outbox (Porta 8080).")
        Container(worker, "EventFlow.Worker", ".NET 9 BackgroundServices", "Executa o OutboxDispatcherWorker (SKIP LOCKED) e o EventConsumerWorker (AMQP listener).")
        
        ContainerDb(postgres, "PostgreSQL 16", "Banco Relacional ACID", "Armazena eventos imutáveis, mensagens do outbox e registros de auditoria/idempotência.")
        ContainerDb(rabbitmq, "RabbitMQ 3.13", "AMQP 0-9-1 Message Broker", "Roteamento por tópicos (eventflow.events) e gestão de Dead Letter Exchange (eventflow.dlx).")
        ContainerDb(redis, "Redis 7 Alpine", "In-Memory Cache & Lock Store", "Gerencia travas distribuídas in-flight (SET NX EX) e cache de idempotência de 24h.")
    }

    Container_Ext(jaeger, "Jaeger All-in-One", "OpenTelemetry UI", "Visualização de spans, dependências e latências (Portas 16686 / 4317).")
    Container_Ext(downstream, "Serviços Downstream", "HTTP Webhooks", "Recebimento final de efeitos colaterais de negócio.")

    Rel(client, api, "POST /api/v1/events", "HTTPS / JSON")
    Rel(api, postgres, "Grava evento e outbox atomicamente", "Npgsql / EF Core 9")
    Rel(worker, postgres, "Lê Outbox com SKIP LOCKED e grava conclusões", "Npgsql / EF Core 9")
    Rel(worker, rabbitmq, "Publica e consome mensagens AMQP", "RabbitMQ.Client 7.x")
    Rel(worker, redis, "Adquire travas e consulta cache de idempotência", "StackExchange.Redis")
    Rel(worker, downstream, "Despacha requisições HTTP externas", "HttpClient / Polly v8")
    Rel(api, jaeger, "Exporta spans e métricas", "OTLP / gRPC")
    Rel(worker, jaeger, "Exporta spans e métricas", "OTLP / gRPC")
```

---

## 3. C4 Nível 3: Diagrama de Componentes (Worker & Consumo)

O Diagrama de Componentes detalha a arquitetura interna dos módulos de processamento assíncrono do Worker:

```mermaid
C4Component
    title Diagrama de Componentes (C4 Nível 3) — EventFlow.Worker

    Container_Boundary(worker_boundary, "EventFlow.Worker") {
        Component(outboxWorker, "OutboxDispatcherWorker", "BackgroundService", "Coordena o ciclo de vida do polling de outbox messages em background.")
        Component(outboxProc, "OutboxProcessor", "Application Service", "Executa queries SKIP LOCKED e orquestra batches com backoff.")
        Component(consumerWorker, "EventConsumerWorker", "BackgroundService", "Escuta a fila AMQP e despacha para o container de injeção de dependência.")
        Component(eventProc, "EventProcessor", "Application Service", "Orquestra idempotência híbrida, spans W3C e handlers de domínio.")
        Component(idempotency, "HybridIdempotencyManager", "Infrastructure Service", "Combina Redis Lock (Lua) + Postgres Single Source of Truth.")
        Component(resilience, "ResiliencePipelineFactory", "Infrastructure Service", "Configura pipelines Polly v8 (Timeouts, Jitter, Circuit Breaker).")
        Component(dispatcher, "HttpExternalDispatcher", "Infrastructure Service", "Executa chamadas HTTP com proteção SsrfGuard e injeção de headers W3C.")
        Component(ssrf, "SsrfGuard", "Security Guard", "Inspeciona e bloqueia IPs privados RFC 1918 e metadados de nuvem.")
        Component(dlq, "RabbitMqDeadLetterManager", "Infrastructure Service", "Gerencia inspeção, replay e expurgo da DLQ.")
    }

    Rel(outboxWorker, outboxProc, "Aciona processamento de lote")
    Rel(consumerWorker, eventProc, "Repassa mensagem consumida")
    Rel(eventProc, idempotency, "Valida e adquire trava distribuída")
    Rel(eventProc, dispatcher, "Encaminha evento para despacho externo")
    Rel(dispatcher, resilience, "Aplica pipeline de resiliência")
    Rel(dispatcher, ssrf, "Valida URL de destino")
    Rel(eventProc, dlq, "Roteia para DLQ em falhas esgotadas")
```

---

## 4. Diagrama de Sequência de Ponta a Ponta (End-to-End Flow)

O fluxo completo detalha a jornada do evento desde a requisição HTTP inicial até a confirmação assíncrona ou Dead Lettering:

```mermaid
sequenceDiagram
    autonumber
    actor Client as HTTP Client
    participant API as EventFlow.Api
    participant DB as PostgreSQL 16
    participant Dispatcher as Outbox Dispatcher
    participant Broker as RabbitMQ Broker
    participant Consumer as Event Consumer
    participant Redis as Redis 7
    participant Target as Destino Externo

    %% 1. Ingestão HTTP
    Note over Client, DB: Fase 1: Ingestão HTTP & Transação ACID
    Client->>API: POST /api/v1/events (com X-Api-Key e Payload)
    API->>API: Validação FluentValidation & Rate Limiter
    API->>API: Cria Span W3C TraceContext (traceparent)
    API->>DB: Inicia Transação ACID
    DB-->>API: INSERT INTO events + INSERT INTO outbox_messages (Pending)
    API-->>Client: HTTP 202 Accepted { eventId, correlationId }

    %% 2. Despacho Assíncrono
    Note over DB, Broker: Fase 2: Transactional Outbox Dispatching
    loop Polling a cada 200ms
        Dispatcher->>DB: SELECT * FROM outbox_messages WHERE status=0 FOR UPDATE SKIP LOCKED
        DB-->>Dispatcher: Retorna lote de mensagens bloqueadas
        Dispatcher->>Broker: BasicPublishAsync (Topic Exchange: eventflow.events + W3C Headers)
        Broker-->>Dispatcher: Publisher Confirm (ACK)
        Dispatcher->>DB: UPDATE outbox_messages SET status = 'Published'
    end

    %% 3. Consumo com Idempotência Híbrida
    Note over Broker, Target: Fase 3: Consumo Assíncrono Resiliente
    Broker->>Consumer: Entrega Mensagem (QoS Prefetch = 10)
    Consumer->>Consumer: Extrai W3C traceparent dos headers AMQP
    
    Consumer->>Redis: 1. Checa Cache de Conclusão (24h)
    alt Já Finalizado Anteriormente
        Redis-->>Consumer: Hit (Evento já processado)
        Consumer->>Broker: BasicAckAsync (Descarta duplicata com segurança)
    else Não Finalizado
        Consumer->>Redis: 2. SET eventflow:lock:{tenant}:{key} NX EX 30
        alt Lock Adquirido com Sucesso
            Consumer->>DB: 3. Consulta processed_events (PostgreSQL)
            alt Inexistente no Banco
                Consumer->>Target: 4. Invoca Webhook com Polly (Retry + Jitter + Circuit Breaker + SsrfGuard)
                alt Sucesso HTTP (200 OK)
                    Consumer->>DB: INSERT INTO processed_events (...)
                    Consumer->>Redis: Cacheia Conclusão (TTL 24h)
                    Consumer->>Broker: BasicAckAsync
                else Falha Esgotada / Erro Fatal
                    Consumer->>Redis: Libera Lock via Script Lua atômico
                    Consumer->>Broker: BasicNackAsync (requeue=false -> Roteia para DLQ)
                end
            else Já Existe no Banco
                Consumer->>Broker: BasicAckAsync (Idempotente)
            end
        else Lock Ocupado (Concorrência Ativa)
            Consumer->>Broker: BasicNackAsync (requeue=true -> Aguarda liberação)
        end
    end
```
