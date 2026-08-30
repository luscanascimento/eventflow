# EventFlow — Sistema Distribuído Event-Driven em .NET

> 🚧 **Status:** Projeto em desenvolvimento ativo (estruturado em etapas incrementais de engenharia).

O **EventFlow** é um sistema distribuído e event-driven projetado para a ingestão confiável, persistência transacional, roteamento e processamento assíncrono e resiliente de eventos de negócio e webhooks críticos (como liquidações financeiras e notificações de parceiros).

Este projeto foi concebido tanto como uma demonstração prática de padrões avançados de arquitetura quanto como um projeto de estudo contínuo para aprofundar e consolidar habilidades em **C#, .NET moderno, sistemas distribuídos, segurança e observabilidade**.

---

## 🎯 O Problema que o EventFlow Resolve

Em arquiteturas tradicionais de microsserviços e webhooks, o maior risco operacional é a **perda silenciosa de eventos** entre a API HTTP aceitar a requisição e o processamento final acontecer — especialmente durante quedas de rede, reinicializações de instâncias ou indisponibilidade temporária de mensageria ou serviços externos.

O EventFlow endereça esse desafio implementando garantias arquiteturais rígidas de ponta a ponta:

1. **Garantia de Não-Perda (*Zero Data-Loss*):** Utilização do **Transactional Outbox Pattern** com PostgreSQL. A gravação do evento e a mensagem pendente para o broker ocorrem na mesma transação atômica ACID.
2. **Despacho e Consumo Assíncrono:** Workers desacoplados e baseados em `BackgroundService` que leem o Outbox com concorrência segura (`SELECT ... FOR UPDATE SKIP LOCKED`) e publicam em tópicos no **RabbitMQ**.
3. **Idempotência Real e Híbrida:** Proteção contra entregas duplicadas (*at-least-once delivery*) combinando trava atômica distribuída no **Redis** (com liberação via Lua Script) e verificação relacional imutável no **PostgreSQL**.
4. **Resiliência e Tolerância a Falhas:** Pipelines de retry com exponential backoff, jitter decorrelacionado e circuit breaker para proteger integrações contra falhas em cascata.
5. **Observabilidade End-to-End:** Rastreabilidade distribuída via padrão **W3C TraceContext** (`traceparent`) utilizando **OpenTelemetry**, logs estruturados com `[LoggerMessage]` de alto desempenho e métricas de sistema.

---

## 🏗️ Fluxo Arquitetural

```text
  [ HTTP Client / Webhook ]
              │ POST /api/v1/events
              ▼
   ┌──────────────────────┐
   │    EventFlow.Api     │ ◄── Validação estrita & W3C Trace Capture
   └──────────┬───────────┘
              │ ACID Transaction
              ▼
   ┌──────────────────────┐
   │ PostgreSQL Database  │ ──► [ events ] (Imutável)
   │                      │ ──► [ outbox_messages ] (Status: Pending)
   └──────────┬───────────┘
              │ Polling Concorrente (SKIP LOCKED)
              ▼
   ┌──────────────────────┐
   │  Outbox Dispatcher   │ ◄── Worker dedicado com Scoped Lifecycle
   └──────────┬───────────┘
              │ Publicação Persistente (DeliveryMode=2 + Headers W3C)
              ▼
   ┌──────────────────────┐
   │   RabbitMQ Broker    │ ◄── Topic Exchange (eventflow.events)
   └──────────┬───────────┘
              │ Consumo com QoS & Backpressure
              ▼
   ┌──────────────────────┐
   │ EventConsumerWorker  │
   │   (EventFlow.Worker) │ ◄── In-Flight Lock Redis (SET NX EX)
   └──────────┬───────────┘
              │
      ┌───────┴───────┐
      ▼               ▼
 [ Sucesso ]     [ Falha / Exceção ]
      │               │
      ├─► Grava conclusão no Postgres     ├─► Libera Lock no Redis
      ├─► Cache no Redis (24h)            └─► Retry Backoff / DLQ
      └─► BasicAckAsync no Broker
```

---

## 🛠️ Stack Tecnológica

* **Linguagem & Runtime:** .NET 9 LTS / C# 13+ (pronto para .NET 10).
* **API & Hosting:** ASP.NET Core Minimal APIs & `BackgroundService` em Worker Services.
* **Persistência & ORM:** PostgreSQL com Entity Framework Core 9 (mapeamentos relacionais explícitos, índices compostos e concorrência otimista/pessimista).
* **Mensageria:** RabbitMQ via driver oficial `RabbitMQ.Client 7.x` nativamente assíncrono.
* **Cache & Lock Distribuído:** Redis com `StackExchange.Redis` e scripts Lua atômicos.
* **Resiliência:** Polly v8 (`Microsoft.Extensions.Resilience`).
* **Validação:** FluentValidation (regras defensivas com restrições estritas de tamanho e formato).
* **Observabilidade:** OpenTelemetry (Distributed Tracing, Metrics e W3C TraceContext propagation).
* **Testes:** xUnit, FluentAssertions e NSubstitute.

---

## 🏛️ Estrutura do Repositório

O projeto adota uma arquitetura modular limpa (*Clean Architecture* simplificada), priorizando a separação de responsabilidades sem cair em excesso de camadas ou abstrações artificiais:

```text
eventflow/
├── docs/
│   ├── adr/                         # Architectural Decision Records (ADRs formais)
│   │   ├── 0003-idempotency-strategy.md
│   │   └── 0004-messaging-transport.md
│   ├── architecture/                # Blueprint mestre e modelo conceitual
│   └── CURRENT_STATUS.md            # Memória de progresso técnico
├── src/
│   ├── EventFlow.Api/               # Endpoints HTTP Minimal APIs, Middlewares e Kestrel
│   ├── EventFlow.Application/       # Casos de uso, orquestradores de consumo e handlers de domínio
│   ├── EventFlow.Domain/            # Entidades imutáveis, Result Pattern e erros tipados
│   ├── EventFlow.Infrastructure/    # PostgreSQL EF Core, RabbitMQ Publisher/Consumer e Redis
│   └── EventFlow.Worker/            # BackgroundServices de despacho do Outbox e consumo de eventos
└── tests/
    └── EventFlow.UnitTests/         # Suíte de testes unitários rápidos e comportamentais
```

---

## 📐 Princípios de Engenharia Adotados

* **Explicitude sobre Mágica:** Preferência pelo uso direto e consciente dos drivers (`RabbitMQ.Client`, `StackExchange.Redis`, EF Core) para demonstrar domínio sobre os protocolos e topologias subjacentes, evitando abstrações "mágicas" que escondem o funcionamento real.
* **Secure by Default:** Validação defensiva de todas as entradas, proteção contra *poison messages*, limits no payload do Kestrel e ausência de dados sensíveis ou segredos nos logs.
* **Zero Warnings & Quality Enforcement:** Compilação configurada com `TreatWarningsAsErrors=true`, `.editorconfig` estrito e validações via `dotnet format`.
* **Organização Incremental:** O desenvolvimento é conduzido de forma estruturada em etapas lógicas, garantindo que cada componente seja projetado, testado e auditado antes do avanço.

---

## 🚀 Como Executar e Testar

### Pré-requisitos
* [.NET 9.0 SDK](https://dotnet.microsoft.com/download) instalado.

### Compilação da Solução
```bash
dotnet build
```

### Execução dos Testes Unitários
```bash
dotnet test
```

### Verificação de Formatação e Qualidade
```bash
dotnet format --verify-no-changes
```

---

## 📝 Notas de Desenvolvimento

Este repositório é um **trabalho em andamento**. O desenvolvimento está sendo feito de forma modular e incremental para manter o foco na qualidade do código, testes e boas práticas de engenharia de software distribuída.
