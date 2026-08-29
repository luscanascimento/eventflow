# ADR-0004: Escolha do Transporte de Mensageria — RabbitMQ.Client Direto vs. MassTransit

## Status
**Aceito** (FASE 4)

## Contexto
O EventFlow requer uma infraestrutura de mensageria assíncrona robusta para publicar eventos a partir do Transactional Outbox e consumi-los com garantias de entrega *at-least-once*, roteamento flexível por tópicos, injeção de TraceContext W3C e suporte a Dead Letter Queue (DLQ).

Duas abordagens principais foram consideradas no ecossistema .NET:
1. **RabbitMQ.Client v7+ Direto:** Driver oficial da RabbitMQ, totalmente redesenhado em v7 com APIs nativas assíncronas baseadas em `Task`, pipelines de I/O de alta performance e controle granular sobre a topologia AMQP.
2. **MassTransit:** Framework de mensageria de alto nível que encapsula o broker, fornecendo abstrações como Saga/State Machine, Outbox integrado e convenções pré-configuradas.

## Decisão
Decidimos utilizar **RabbitMQ.Client v7+ diretamente** em conjunto com nossa própria implementação de Outbox e Consumer.

### Justificativas Técnicas:
1. **Explicitude sobre Mágica (Princípio Central do Projeto):** O MassTransit cria convenções implícitas de nomes de exchanges, filas e headers que mascaram o funcionamento real do protocolo AMQP 0-9-1. A utilização direta do driver oficial permite demonstrar domínio profundo sobre topologias (Topic Exchanges, Routing Keys, Dead Letter Exchanges, DLQs e Publisher Confirms).
2. **Controle Estrito sobre o W3C TraceContext:** O controle manual dos headers AMQP garante interoperabilidade total com o padrão W3C (`traceparent`, `tracestate`) e com serviços que não utilizam o ecossistema .NET / MassTransit.
3. **Desempenho e Alocação Reduzida:** A versão 7 do `RabbitMQ.Client` foi reconstruída com `System.IO.Pipelines` e `ValueTask`, oferecendo latência mínima e zero alocações desnecessárias.
4. **Sem Dependência de Abstrações Complexas Prematuras:** Como o nosso domínio exige regras customizadas de controle de concorrência (`FOR UPDATE SKIP LOCKED`) e idempotência híbrida (Redis + PostgreSQL), nossa implementação atende exatamente ao requisito sem o overhead de um framework de alto nível.

## Alternativas Consideradas
* **MassTransit:** Excelente para cenários onde se deseja delegar toda a topologia e orquestração de Sagas complexas para uma biblioteca de terceiros. Rejeitado para o EventFlow para evitar abstrações desnecessárias e manter a total visibilidade da arquitetura distribuída.
* **Apache Kafka:** Rejeitado no momento porque o caso de uso principal (Webhooks e roteamento por tópicos com filas discretas e confirmações individuais) é perfeitamente atendido pelo modelo de mensageria do RabbitMQ sem o custo operacional e complexidade de partições/offsets do Kafka.

## Consequências
### Positivas:
- Visibilidade 100% explícita da topologia AMQP declarada no código.
- Flexibilidade total para configurar headers, routing keys e políticas de retry/DLQ customizadas.
- Zero "lock-in" em formatos de envelope específicos de bibliotecas de terceiros.

### Negativas / Custos:
- Maior responsabilidade no gerenciamento do ciclo de vida das conexões e canais.
- Necessidade de implementar manualmente o loop do consumidor e tratamento de reconexão (atendido nas Fases 4 e 5).
