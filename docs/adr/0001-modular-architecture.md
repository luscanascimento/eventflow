# ADR-0001: Arquitetura Monólito Modular e Separação Limpa de Camadas

## Status
**Aceito** (FASE 1)

## Contexto
Ao projetar o EventFlow para ingestão e processamento distribuído de eventos e webhooks de alta criticidade, surge a decisão fundamental sobre a organização do código e a topologia dos serviços.

Cenários comumente adotados no ecossistema de software trazem armadilhas bem conhecidas:
1. **Microsserviços Prematuros (Distributed Monolith):** Dividir a solução em dezenas de microsserviços logo no início introduz latência de rede adicional, sobrecarga de orquestração de deploy, complexidade de serialização/deserialização entre serviços e o temido "monólito distribuído" onde mudanças quebram contratos entre múltiplos repositórios.
2. **Over-Engineering e Abstrações "Mágicas":**
   * Repositórios genéricos sobre o Entity Framework Core (`IRepository<T>`), duplicando abstrações que o próprio `DbContext` e `DbSet` já implementam nativamente como *Unit of Work* e *Repository*.
   * Wrappers artificiais de mediator (como MediatR) para operações simples de 1 para 1, introduzindo indireções de navegação em código sem ganho real.
3. **Monólito Anêmico / Código Espaguete:** Colocar lógica de negócio diretamente em controllers HTTP ou misturar chamadas de banco de dados diretamente em handlers de fila.

## Decisão
Decidimos adotar um **Monólito Modular com Clean Architecture Simplificada e Pragmática**, distribuído em dois processos de execução principais: **EventFlow.Api** (Web API síncrona / Kestrel) e **EventFlow.Worker** (Processador assíncrono / BackgroundServices).

### Estrutura de Camadas:
```text
┌────────────────────────────────────────────────────────┐
│              EventFlow.Api / EventFlow.Worker          │  ◄── Entrypoints / Hosting
└───────────────────────────┬────────────────────────────┘
                            │ Depende de
                            ▼
┌────────────────────────────────────────────────────────┐
│                EventFlow.Infrastructure                │  ◄── EF Core, RabbitMQ.Client, StackExchange.Redis, Polly
└───────────────────────────┬────────────────────────────┘
                            │ Depende de
                            ▼
┌────────────────────────────────────────────────────────┐
│                EventFlow.Application                   │  ◄── Casos de Uso, Orquestração, Interfaces e Validação
└───────────────────────────┬────────────────────────────┘
                            │ Depende de
                            ▼
┌────────────────────────────────────────────────────────┐
│                  EventFlow.Domain                      │  ◄── Modelos Imutáveis, Result Pattern, Diagnósticos OTel
└────────────────────────────────────────────────────────┘
```

### Diretrizes de Engenharia:
1. **Isolamento de Domínio e Result Pattern:**
   * O `EventFlow.Domain` não possui dependências externas além do BCL e abstrações de telemetria nativas (`System.Diagnostics`).
   * Erros de negócio e validação são representados explicitamente via `Result<T>` e `DomainError` tipados, reservando exceções exclusivamente para falhas inesperadas de infraestrutura.
2. **Explicitude sobre Mágica:**
   * Sem repositórios genéricos: persistência orientada a interfaces focadas em casos de uso (`IEventStore`, `IOutboxStore`).
   * Sem mediator genérico: injeção de dependência direta de Use Cases tipados (`IngestEventUseCase`, `OutboxProcessor`, `EventProcessor`).
3. **Separação de Processos com Código Compartilhado:**
   * A API foca exclusivamente em autenticação, validação e persistência atômica com baixa latência.
   * O Worker foca no polling do Outbox e no consumo resiliente de filas AMQP, garantindo escalabilidade independente de computação (CPU/Memória da API vs. Workers).

## Alternativas Consideradas
* **Microsserviços Autônomos em Múltiplos Repos:** Rejeitado pela complexidade operacional prematura e custos de infraestrutura antes de validar a escala real.
* **Camada de Repositório Genérico Abstrato:** Rejeitada por mascarar recursos avançados do EF Core (como queries compiladas, `AsNoTracking` e `FOR UPDATE SKIP LOCKED`).
* **MediatR / Pipeline Behaviors Complexos:** Rejeitado para favorecer clareza de fluxo de execução ("Go to Definition" imediato na IDE).

## Consequências
### Positivas:
- **Alta Coesão e Baixo Acoplamento:** Camadas bem delimitadas com responsabilidades claras.
- **Testabilidade Superior:** Domínio e Aplicação 100% testáveis com testes unitários sem mockar infraestrutura pesada.
- **Escalabilidade Independente:** Containers da API e do Worker podem ser escalados horizontalmente de forma independente no Kubernetes / Docker Compose.
- **Zero Overhead Cognitivo:** Navegação de código direta e previsível.

### Negativas / Custos:
- Mudanças em contratos de domínio exigem atualização disciplinada nas camadas consumidoras (mitigado por forte cobertura de testes e tipos estáticos do C# 13).
