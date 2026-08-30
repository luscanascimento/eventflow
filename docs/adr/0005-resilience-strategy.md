# ADR-0005: Estratégia de Resiliência Declarativa — Polly v8 & Microsoft.Extensions.Resilience

## Status
**Aceito** (FASE 7)

## Contexto
O processamento de eventos assíncronos no EventFlow frequentemente culmina no despacho de notificações, webhooks e chamadas HTTP para parceiros e serviços downstream externos. Redes são inerentemente não confiáveis e serviços externos sofrem interrupções temporárias ou sobrecargas.

Cenários de risco identificados:
1. **Thundering Herd / Retry Storms:** Múltiplos workers retentando chamadas simultaneamente em intervalos fixos sobrecarregam o serviço dependente, impedindo sua recuperação.
2. **Cascading Failures (Efeito Dominó):** Quando um serviço externo fica fora do ar por minutos ou horas, workers continuam enfileirando requisições, consumindo threads e conexões socket do host até causar colapso do próprio EventFlow.
3. **Hung Sockets:** Chamadas HTTP sem timeouts granulares que ficam bloqueadas indefinidamente.
4. **Retries Indiscriminados em Falhas Não Recuperáveis:** Tentar novamente requisições com dados inválidos (400 Bad Request, 422) ou credenciais inválidas (401/403) apenas desperdiça recursos.

## Decisão
Decidimos implementar uma **Estratégia de Resiliência Declarativa em 4 Camadas** utilizando a arquitetura moderna do **Polly v8 (`Microsoft.Extensions.Resilience`)**:

### Pipeline de Resiliência (`ResiliencePipeline<HttpResponseMessage>`):
```text
Requisição de Entrada
         │
         ▼
 1. [ Total Timeout ] (ex: 20s) ──► Tempo máximo global para toda a operação
         │
         ▼
 2. [ Retry com Jitter ] (ex: 3 tentativas) ──► Exponential Backoff + Decorrelated Jitter
         │
         ▼
 3. [ Circuit Breaker ] (ex: 50% falhas em 30s) ──► Bloqueia chamadas quando o destino está fora do ar
         │
         ▼
 4. [ Attempt Timeout ] (ex: 5s) ──► Timeout individual por tentativa de conexão
         │
         ▼
  [ HTTP Client ] ──► Destino Externo
```

### Classificação Estrita de Operações (Retryable vs. Non-Retryable):
* **Retryable (Elegíveis para Retry):**
  * HTTP 500 Internal Server Error, 502 Bad Gateway, 503 Service Unavailable, 504 Gateway Timeout.
  * HTTP 429 Too Many Requests (respeitando rate limiting com backoff).
  * `HttpRequestException` (falhas de conexão, reset de socket, DNS transitório).
  * `TimeoutRejectedException` por tentativa.
* **Non-Retryable (Falham Imediatamente / Sem Retry):**
  * HTTP 400 Bad Request (payload inválido na origem).
  * HTTP 401 Unauthorized / HTTP 403 Forbidden (credenciais incorretas).
  * HTTP 404 Not Found / 422 Unprocessable Entity.
  * `BrokenCircuitException` (curto-circuito ativo).

### Defesa contra SSRF (Server-Side Request Forgery):
* URLs de destino devem obrigatoriamente possuir esquema `http` ou `https`. Esquemas arbitrários como `file://`, `ftp://` ou `gopher://` são bloqueados antes de qualquer invocação de rede.

## Alternativas Consideradas
* **Polly v7 (Legado):** Rejeitado por alocar instâncias excessivas de delegates e não ter integração nativa com o novo modelo de pipelines zero-allocation do .NET 8/9+.
* **Retries Manuais com `while (retryCount < 3)`:** Propenso a erros de concorrência, complexidade de jitter e ausência de Circuit Breaker coordenado.

## Consequências
### Positivas:
- **Zero Alocações Excessivas:** O Polly v8 foi construído com foco em performance e concorrência nativa.
- **Fail-Fast Inteligente:** Se o serviço externo cair, o Circuit Breaker abre e poupa threads/sockets do sistema.
- **Evita Enxame de Retries:** O Jitter decorrelacionado distribui os retries uniformemente no tempo.
- **Propagação de Rastreabilidade:** Headers W3C (`traceparent`) são injetados diretamente na requisição enviada.

### Negativas / Custos:
- Necessidade de calibrar os thresholds de timeout e circuit breaker de acordo com o SLA de cada integração externa.
