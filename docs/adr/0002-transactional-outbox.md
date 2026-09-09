# ADR-0002: Padrão Transactional Outbox com Concorrência Segura via SKIP LOCKED

## Status
**Aceito** (FASE 2 & 3)

## Contexto
Em arquiteturas orientadas a eventos e webhooks, um dos problemas mais críticos de consistência de dados é o **Dual-Write Problem**:
* Se a aplicação salva o estado no banco de dados e depois tenta publicar no broker de mensagens (RabbitMQ), uma falha de rede ou crash de processo entre essas duas operações faz com que o evento seja gravado mas nunca publicado (perda silenciosa de eventos).
* Se a aplicação publica no broker primeiro e depois tenta salvar no banco, uma falha na persistência relacional faz com que consumidores processem um evento inexistente no banco (inconsistência fantasma).
* O uso de transações distribuídas (Two-Phase Commit / 2PC / XA) é amplamente desencorajado em sistemas modernos devido a gargalos severos de throughput, bloqueios de recursos e falta de suporte nativo em brokers modernos.

Adicionalmente, ao adotar uma tabela de Outbox para publicação assíncrona, surge o desafio de **concorrência entre múltiplos workers**:
* Múltiplas instâncias do worker tentando ler as mesmas mensagens pendentes podem gerar contenção de locks ou publicações redundantes se o mecanismo de consulta for ingênuo (ex: `SELECT * FROM outbox WHERE status = 'Pending'`).

## Decisão
Decidimos implementar o **Transactional Outbox Pattern** com persistência relacional atômica no **PostgreSQL** e despacho assíncrono concorrente com **`SELECT ... FOR UPDATE SKIP LOCKED`**:

### 1. Persistência Atômica ACID:
* A gravação do registro imutável do evento (`events`) e a mensagem do outbox (`outbox_messages` com `status = 'Pending'`) ocorrem na **mesma transação relacional** via `EventFlowDbContext.SaveChangesAsync()`.
* Se a transação falhar, nada é gravado. Se for commitada, a mensagem está garantida no banco de dados com integridade referencial.

### 2. Ciclo de Vida da Mensagem do Outbox (`OutboxStatus`):
* `Pending` (0): Criada e aguardando processamento.
* `Processing` (1): Bloqueada temporariamente pelo worker durante a tentativa de despacho.
* `Published` (2): Confirmada e entregue com sucesso ao broker de mensagens.
* `DeadLettered` (3): Esgotou o número máximo de retries configurados sem sucesso.

### 3. Polling Concorrente e Leasing Atômico:
* O worker despachador (`OutboxProcessor`) adquire lotes (`BatchSize = 20`) via claim atômico com transação curta no PostgreSQL:
  ```sql
  WITH pending_batch AS (
      SELECT id FROM outbox_messages
      WHERE (status = 0 OR (status = 1 AND locked_until_utc IS NOT NULL AND locked_until_utc < @NowUtc))
      ORDER BY created_at ASC
      LIMIT @BatchSize
      FOR UPDATE SKIP LOCKED
  )
  UPDATE outbox_messages
  SET status = 1,
      lock_token = @LockToken,
      locked_until_utc = @LockedUntilUtc
  WHERE id IN (SELECT id FROM pending_batch)
  RETURNING *;
  ```
* **Transações Curtas de Banco:** A transação do PostgreSQL é finalizada e a conexão é devolvida ao pool logo após o claim do lote. Durante a publicação AMQP no RabbitMQ, **nenhuma conexão relacional é retida**.
* **Proteção contra Crashes (Lease Expiration):** Se um worker sofrer crash durante a publicação, o lease expira (`locked_until_utc < @NowUtc`) e outro worker retoma o lote automaticamente.
* **Confirmação e Concorrência:** O status final (`Published` ou `Pending` com retry) é atualizado conferindo o `lock_token` proprietário para evitar sobrescritas acidentais.

### 4. Backoff Exponencial e Retries de Despacho:
* Falhas transitórias de rede com o RabbitMQ incrementam `retry_count` e calculam o próximo instante de tentativa (`next_retry_at`) com backoff exponencial.
* Mensagens que excedem o limite de tentativas são marcadas como `DeadLettered` para auditoria e intervenção operacional sem travar a fila de eventos pendentes.

## Alternativas Consideradas
* **Publicação Síncrona Direta no Broker:** Rejeitado pelo risco inerente de perda de dados durante falhas de rede.
* **Change Data Capture (CDC / Debezium):** Rejeitado no momento para evitar a necessidade de gerenciar conectores externos complexos e instâncias de Kafka Connect, mantendo a solução autocontida e de fácil manutenção no .NET.
* **Lock Otimista com Coluna de Versão:** Rejeitado para o polling de lote pois gera alto índice de conflitos e exceções de concorrência quando múltiplos workers leem a mesma página de dados.

## Consequências
### Positivas:
- **Garantia de Zero Data-Loss:** Nenhum evento aceito pela API é perdido antes de chegar à mensageria.
- **Escalabilidade Linear:** Múltiplas réplicas do `OutboxDispatcherWorker` operam simultaneamente sem contenção graças ao `SKIP LOCKED`.
- **Auditoria Completa:** Histórico completo de eventos persistidos e status de envio no PostgreSQL.

### Negativas / Custos:
- Leve latência assíncrona introduzida entre a aceitação HTTP e a publicação no broker (mitigada por polling frequente com delay configurável de 100ms a 500ms).
- Necessidade de rotinas de limpeza ou retenção de outbox messages já publicadas em cenários de volumes extremos.
