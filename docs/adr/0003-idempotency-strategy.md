# ADR-0003: Estratégia de Idempotência Híbrida — Redis Distributed Lock + PostgreSQL Single Source of Truth

## Status
**Aceito** (FASE 6)

## Contexto
Em sistemas distribuídos orientados a eventos, o transporte de mensagens (RabbitMQ) opera sob a garantia de entrega *at-least-once*. Isso significa que eventos podem ser entregues e processados mais de uma vez devido a:
1. Re-enfileiramento após quedas ou reinicializações do worker antes do envio do ACK.
2. Partições transitórias de rede e timeouts de confirmação.
3. Duplicação na origem (chamadas repetidas de webhooks por clientes externos).

Uma abordagem ingênua como `if (redis.KeyExists(key))` falha em cenários do mundo real:
* **Race Conditions:** Duas réplicas simultâneas verificam que a chave não existe e ambas executam o efeito colateral (ex: cobrança duplicada).
* **Process Crash:** Se a chave for marcada antes da execução e o processo morrer, o evento é perdido para sempre (falso positivo de idempotência). Se for marcada apenas no final, requisições concorrentes passam.
* **Queda do Redis:** Tratar o Redis (que é um cache volátil e in-memory) como fonte definitiva de consistência pode causar perda de histórico de idempotência após reinicializações do cluster.

## Decisão
Decidimos implementar uma **Estratégia de Idempotência Híbrida em Três Camadas (`HybridIdempotencyManager`)**:

1. **Fast Guard em Memória (Redis Completion Cache):**
   * Chave: `eventflow:completed:{tenantId}:{scope}:{idempotencyKey}`
   * TTL: 24 horas.
   * Objetivo: Bloquear eventos já processados em sub-milissegundos sem sobrecarregar o banco relacional.

2. **Trava Distribuída In-Flight Atômica (Redis Distributed Lock):**
   * Chave: `eventflow:lock:{tenantId}:{scope}:{idempotencyKey}`
   * Comando: `SET key token NX EX 30` (adquirido apenas se não existir, com TTL de 30 segundos).
   * Liberação Atômica: Script Lua que verifica a posse do token (`ARGV[1] == KEYS[1]`) antes de deletar a chave, evitando que um worker libere acidentalmente o lock renovado/adquirido por outra réplica.

3. **Garantia Relacional Permanente & Claim Atômico (PostgreSQL Imutável):**
   * Tabela: `processed_events`
   * Constraint única estrita: `UNIQUE (tenant_id, scope, idempotency_key)`
   * Ciclo de Vida: Status `Processing` (com `lock_token` e `locked_until_utc`) inserido atomicamente **antes** do efeito colateral de negócio, e atualizado para `Completed` após a conclusão com sucesso.
   * Objetivo: Fonte definitiva da verdade (*Single Source of Truth*). Garante consistência ACID e proteção contra race conditions mesmo quando o Redis estiver desativado ou inalcançável.

4. **Degradação Graciosa (Graceful Fallback):**
   * Se o Redis estiver indisponível (queda de rede, timeout), o `HybridIdempotencyManager` registra um aviso e recorre ao claim atômico relacional diretamente no PostgreSQL com tratamento de violação de chave única (`23505`). O processamento preserva a consistência e nunca é corrompido.

## Alternativas Consideradas
* **Apenas Redis com `KeyExists`:** Rejeitado por não ser atômico, não prevenir race conditions e ser vulnerável a perda de dados.
* **Apenas PostgreSQL Unique Constraints:** Extremamente consistente, mas gera contenção excessiva de I/O e locks relacionais em alto throughput de eventos repetidos.
* **Redlock (Algoritmo Multi-Instância do Redis):** Desnecessário para este cenário, pois temos o PostgreSQL fornecendo a consistência ACID final.

## Consequências
### Positivas:
- **Zero Double-Processing:** Proteção em tempo real contra execuções concorrentes idênticas.
- **Alta Performance:** Mais de 99% das checagens de eventos repetidos são resolvidas em memória pelo Redis.
- **Resiliência a Quedas:** A consistência nunca depende exclusivamente da saúde do Redis.
- **Isolamento Multi-Tenant e Multi-Scope:** Chaves particionadas por Tenant e por Handler, permitindo que diferentes serviços processem o mesmo evento de forma independente.

### Negativas / Custos:
- Complexidade arquitetural moderada com necessidade de coordenar o ciclo de vida (Adquirir -> Executar -> Persistir -> Liberar).
- Necessidade de gerenciar a conexão e fallbacks com `StackExchange.Redis`.
