# ADR-0007: Endurecimento de Segurança — OWASP Top 10, Headers, Rate Limiting e SSRF

## Status
**Aceito** (FASE 10)

## Contexto
O EventFlow expõe endpoints públicos para ingestão de eventos e webhooks, além de realizar chamadas de saída para sistemas de terceiros. Sistemas event-driven são alvos frequentes de:
1. **Ataques de Negação de Serviço (DoS / Flood):** Envio massivo de requisições que esgotam memória e conexões do banco de dados e do broker.
2. **Payload Bloating / Memory Exhaustion:** Requisições com corpos gigantescos que degradam a performance do Kestrel.
3. **Timing Attacks (Side-Channel):** Ataques que medem o tempo de resposta da validação de chaves de API para deduzir segredos byte a byte.
4. **Server-Side Request Forgery (SSRF):** Eventos com URLs forjadas para acessar a rede interna ou endpoints de metadados de provedores de nuvem (ex: AWS `169.254.169.254`).
5. **Ataques Web Clássicos (Clickjacking, MIME Sniffing, XSS):** Ausência de headers defensivos em respostas HTTP.

## Decisão
Decidimos implementar uma **Estratégia de Segurança Defensiva por Padrão (*Secure by Default*)**:

### 1. Limite Estrito de Payloads no Kestrel:
* `MaxRequestBodySize = 256 KB` configurado no Kestrel. Requisições maiores são rejeitadas imediatamente no nível de transporte antes da alocação de buffers.

### 2. Rate Limiting Granular Baseado em Particionamento:
* Algoritmo: **Sliding Window Rate Limiter** no ASP.NET Core.
* Particionamento: Chave composta por `X-Tenant-Id` (com fallback para `RemoteIpAddress`).
* Resposta: HTTP `429 Too Many Requests` estruturada no padrão RFC 7807 `ProblemDetails`.

### 3. Middleware de Headers de Segurança (OWASP Standard):
* `Content-Security-Policy`: `default-src 'none'; frame-ancestors 'none';`
* `X-Content-Type-Options`: `nosniff`
* `X-Frame-Options`: `DENY`
* `Strict-Transport-Security`: `max-age=31536000; includeSubDomains; preload`
* `Referrer-Policy`: `no-referrer`
* `Permissions-Policy`: Restringe APIs de hardware e sensores do navegador.
* Remoção dos headers `Server` e `X-Powered-By`.

### 4. Proteção contra Timing Attacks em API Keys:
* Implementação do `ApiKeyValidator` utilizando `CryptographicOperations.FixedTimeEquals` para comparações em tempo constante, eliminando variações de latência exploráveis.

### 5. Defesa contra SSRF (`SsrfGuard`):
* Validação estrita de esquemas de URL (permitindo apenas `http` e `https`).
* Bloqueio ativo de resoluções para redes privadas RFC 1918 (`10.0.0.0/8`, `172.16.0.0/12`, `192.168.0.0/16`), loopback (`127.0.0.0/8`, `::1`) e metadados de nuvem (`169.254.169.254`).

### 6. Higiene Estrita de Segredos e Logs:
* Proibição absoluta de segredos codificados no repositório.
* Métodos de log compilados com `[LoggerMessage]` não interpolam payloads completos nem dados sensíveis.

## Consequências
### Positivas:
- Conformidade estrita com o guia de segurança do **OWASP API Security Top 10**.
- Proteção transparente e automática em todas as rotas da API.
- Imunidade contra vazamentos de metadados em ambientes de cloud.

### Negativas / Custos:
- Clientes que enviarem mais requisições que o limite de throughput receberão HTTP 429 e deverão respeitar backoff.
