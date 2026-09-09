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

### 2. Rate Limiting Granular Baseado em Particionamento Composto:
* Algoritmo: **Sliding Window Rate Limiter** no ASP.NET Core.
* Particionamento: Chave composta por `IP:TenantHeader` (ex: `192.168.1.10:tenant_acme`), garantindo isolamento justo entre múltiplos clientes compartilhando a mesma rede ou um mesmo tenant operando em múltiplos nós.
* Resposta: HTTP `429 Too Many Requests` estruturada no padrão RFC 7807 `ProblemDetails`.

### 3. Middleware de Headers de Segurança (OWASP Standard):
* `Content-Security-Policy`: `default-src 'none'; frame-ancestors 'none';`
* `X-Content-Type-Options`: `nosniff`
* `X-Frame-Options`: `DENY`
* `Strict-Transport-Security`: `max-age=31536000; includeSubDomains; preload`
* `Referrer-Policy`: `no-referrer`
* `Permissions-Policy`: Restringe APIs de hardware e sensores do navegador.
* Remoção dos headers `Server` e `X-Powered-By`.

### 4. Proteção contra Timing Attacks & Fail-Closed em API Keys:
* Implementação do `ApiKeyValidator` utilizando `CryptographicOperations.FixedTimeEquals` comparando hashes SHA-256 dos segredos, eliminando vazamentos de temporização tanto por conteúdo quanto por tamanho de chave.
* `SecurityOptions.ValidateOnStart()` bloqueia a inicialização da API em ambientes não-Development caso a chave não esteja configurada ou possua entropia insuficiente (< 16 caracteres).

### 5. Defesa contra SSRF e DNS Rebinding (`SsrfGuard` + `SafeSocketsHttpHandler`):
* Validação estrita de esquemas de URL (permitindo apenas `http` e `https`).
* Bloqueio ativo de resoluções para redes privadas RFC 1918 (`10.0.0.0/8`, `172.16.0.0/12`, `192.168.0.0/16`), loopback (`127.0.0.0/8`, `::1`), link-local e metadados de nuvem (`169.254.169.254`).
* Criação de `SocketsHttpHandler` seguro com callback `ConnectCallback` que valida o IP de destino no momento exato do estabelecimento do socket, impedindo ataques de **DNS Rebinding** (onde o domínio aponta para um IP público na checagem e resolve para um IP privado na conexão).

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
