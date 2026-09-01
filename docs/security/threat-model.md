# EventFlow — Threat Model & Security Architecture (OWASP API Top 10)

> **Documento:** Modelagem de Ameaças, Superfície de Ataque e Controles de Segurança  
> **Classificação:** Engenharia de Software / Segurança Defensiva  
> **Status:** Aprovado e Implementado (Fases 1 a 12)  

---

## 1. Visão Geral e Objetivos de Segurança

O **EventFlow** atua como uma camada crítica de ingestão e processamento assíncrono para webhooks financeiros e eventos de negócio transacionais. Em sistemas distribuídos abertos à internet ou integrados com múltiplos parceiros e tenants, a superfície de ataque é ampla e abrange desde ataques de força bruta e negação de serviço até exploração de requisições forjadas do lado do servidor (SSRF) e race conditions em liquidações financeiras.

Os três pilares de segurança do EventFlow são:
1. **Confidencialidade & Isolamento:** Garantir que nenhum tenant tenha acesso ou interfira nos dados de outro tenant, e que segredos ou PII jamais sejam expostos em logs, telemetria ou respostas HTTP.
2. **Integridade & Não-Repúdio:** Garantir que todo evento ingerido seja processado exatamente uma vez de forma imutável, auditável e protegida contra repetições forjadas (*replay attacks*).
3. **Disponibilidade & Resiliência:** Proteger o sistema contra exaustão de memória, contenção de conexões e ataques de negação de serviço distribuídos ou volumétricos.

---

## 2. Matriz de Ameaças (STRIDE & OWASP API Security Top 10)

| ID | Categoria OWASP / STRIDE | Vetor de Ataque / Risco | Impacto Potencial | Mitigação Implementada no EventFlow |
| :--- | :--- | :--- | :--- | :--- |
| **SEC-01** | **API1:2023 Broken Object Level Auth / Spoofing** | Tenant A tenta publicar eventos ou consultar DLQ em nome do Tenant B. | Violação de privacidade e manipulação indevida de dados financeiros. | Autenticação via `ApiKeyEndpointFilter`, validação de `TenantId` no header e corpo, particionamento estrito de chaves de idempotência e locks por `tenant_id`. |
| **SEC-02** | **API2:2023 Broken Authentication / Side-Channel** | Atacante executa *Timing Attacks* para inferir a API Key válida medindo variações de milissegundos na resposta. | Comprometimento total da autenticação da API. | Validação de segredos em tempo constante via `CryptographicOperations.FixedTimeEquals(a, b)` no `ApiKeyValidator`. |
| **SEC-03** | **API4:2023 Unrestricted Resource Consumption / DoS** | Flood de requisições HTTP massivas ou envio de payloads gigantescos visando derrubar o Kestrel e o banco. | Exaustão de memória (OOM), colapso do garbage collector e indisponibilidade do serviço. | Limite estrito de payload no Kestrel (`MaxRequestBodySize = 256 KB`) + **Sliding Window Rate Limiter** particionado por `X-Tenant-Id` / IP (`100 req/min`). |
| **SEC-04** | **API7:2023 Server-Side Request Forgery (SSRF)** | Consumidor despacha webhooks para URLs internas da infraestrutura (`http://169.254.169.254/` ou `http://10.0.0.1/admin`). | Vazamento de credenciais temporárias de cloud (AWS/GCP/Azure) ou invasão de serviços internos. | Inspeção ativa via `SsrfGuard`: bloqueio de esquemas não-HTTP, rejeição de loopback (`127.0.0.1`, `::1`), IPs privados RFC 1918 (`10.0.0.0/8`, `172.16.0.0/12`, `192.168.0.0/16`) e cloud metadata. |
| **SEC-05** | **Tampering & Replay Attacks / Race Conditions** | Atacante ou parceiro instável reenvia a mesma liquidação financeira dezenas de vezes simultaneamente. | Cobrança ou crédito duplicado no destino financeiro. | **Idempotência Híbrida (`HybridIdempotencyManager`)**: Lock atômico distribuído no Redis (`SET NX EX 30s` com liberação segura via Lua Script) + Constraint Relacional única permanente no PostgreSQL (`UNIQUE(tenant_id, scope, idempotency_key)`). |
| **SEC-06** | **API8:2023 Security Misconfiguration / Info Leak** | Stack traces de exceções, headers com versão de servidor ou respostas sem cabeçalhos de proteção web. | Obtenção de informações técnicas para engenharia reversa e vulnerabilidade a clickjacking/XSS. | `SecurityHeadersMiddleware` (CSP, HSTS, nosniff, DENY, no-referrer), supressão de headers `Server`/`X-Powered-By`, e respostas de erro RFC 7807 `ProblemDetails` sanitizadas. |
| **SEC-07** | **Information Disclosure in Logs / Tracing** | Chaves de API, senhas ou payloads com dados confidenciais gravados em texto claro nos logs estruturados. | Vazamento de dados em ferramentas de APM / SIEM. | Logs estruturados com Source Generators (`[LoggerMessage]`) compilados com parâmetros tipados; proibição de logging de campos confidenciais e sanitização de traces W3C. |
| **SEC-08** | **Container Escape / Privilege Escalation** | Execução de containers com privilégios de root no host Docker/Kubernetes. | Comprometimento do nó host caso haja vulnerabilidade em dependência. | Dockerfiles multi-stage compilados para execução com usuário não-root `app` (UID/GID `1654`). |

---

## 3. Detalhamento dos Mecanismos Defensivos

### 3.1 Defesa contra Timing Attacks (`ApiKeyValidator`)
Em comparações padrão de strings (`string.Equals` ou `==`), o runtime .NET interrompe a avaliação no primeiro caractere diferente. Isso permite que um atacante meça o tempo de resposta em nanosegundos e deduza o segredo byte a byte.

**Implementação no EventFlow:**
```csharp
public bool Validate(string? providedKey)
{
    if (string.IsNullOrEmpty(providedKey) || string.IsNullOrEmpty(_configuredApiKey))
        return false;

    byte[] providedBytes = Encoding.UTF8.GetBytes(providedKey);
    byte[] expectedBytes = Encoding.UTF8.GetBytes(_configuredApiKey);

    if (providedBytes.Length != expectedBytes.Length)
        return false;

    // Tempo constante garantido pelo hardware/BCL
    return CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes);
}
```

---

### 3.2 Proteção contra Server-Side Request Forgery (`SsrfGuard`)
Antes de invocar qualquer URL de webhook externa durante o despacho de eventos no consumidor, a URL passa pela guarda de segurança `SsrfGuard`:

1. **Esquema Obrigatório:** Apenas `http://` e `https://` são aceitos. Protocolos perigosos como `file://`, `gopher://`, `ftp://`, `ldap://` disparam rejeição imediata.
2. **Resolução de DNS Segura:** O IP de destino é resolvido e testado contra:
   - Loopback (`IPAddress.IsLoopback` ou `127.0.0.0/8`, `::1`).
   - Redes Privadas RFC 1918 (`10.0.0.0/8`, `172.16.0.0/12`, `192.168.0.0/16`).
   - Endereços de Link-Local / Cloud Metadata (`169.254.169.254`, `fe80::/10`).
3. **Resultado:** Se o endereço violar qualquer restrição, a requisição é abortada com `DomainError.Security("SsrfGuard.BlockedHost", ...)` e encaminhada diretamente para a Dead Letter Queue sem tentar a chamada de rede.

---

### 3.3 Headers de Segurança OWASP (`SecurityHeadersMiddleware`)
Todas as respostas HTTP emitidas pelo Kestrel recebem automaticamente um conjunto defensivo de headers:

```http
Content-Security-Policy: default-src 'none'; frame-ancestors 'none';
X-Content-Type-Options: nosniff
X-Frame-Options: DENY
Strict-Transport-Security: max-age=31536000; includeSubDomains; preload
Referrer-Policy: no-referrer
Permissions-Policy: geolocation=(), camera=(), microphone=(), payment=()
X-XSS-Protection: 0
```
*A diretiva `X-XSS-Protection: 0` segue a recomendação atual do OWASP, instruindo navegadores modernos a usarem o CSP sem ativar filtros XSS legados propensos a vulnerabilidades secundárias.*

---

### 3.4 Rate Limiting Particionado (ASP.NET Core)
Para evitar que requisições abusivas de um cliente monopolizem os recursos do servidor:
* **Algoritmo:** *Sliding Window Limiter* (100 requisições permitidas por janela de 1 minuto, dividida em 4 segmentos).
* **Particionamento:** Isolado pela chave do Tenant (`X-Tenant-Id`) com fallback para o IP remoto (`RemoteIpAddress`).
* **Resposta de Rejeição:** HTTP `429 Too Many Requests` com corpo estruturado RFC 7807 e cabeçalhos de *Retry-After*.

---

### 3.5 Idempotência Híbrida e Concorrência Segura
A estratégia implementada no `HybridIdempotencyManager` elimina *double spending* e execuções paralelas simultâneas:
1. **Lock In-Flight:** Adquirido via `SET eventflow:lock:{tenantId}:{scope}:{key} {token} NX EX 30`.
2. **Liberação Atômica:** Executada via Script Lua que checa se o token atual é o proprietário antes de remover a chave:
   ```lua
   if redis.call('get', KEYS[1]) == ARGV[1] then
       return redis.call('del', KEYS[1])
   else
       return 0
   end
   ```
3. **Persistência Relacional:** O registro é gravado em `processed_events` sob constraint única composta `(tenant_id, scope, idempotency_key)`.

---

## 4. Auditoria e Higiene Operacional

1. **Gestão de Segredos:** Segredos de produção são injetados exclusivamente via Variáveis de Ambiente ou Secrets Manager, sem valores padrão inseguros no código.
2. **Execução Não-Root:** Os containers Docker executam sob o usuário `app` (UID `1654`), reduzindo drasticamente o raio de explosão em caso de qualquer vulnerabilidade em nível de processo.
3. **Sanitização de Erros:** Erros não tratados são interceptados pelo `GlobalExceptionHandler`, que registra a stack trace internamente e retorna apenas um `ProblemDetails` opaco com identificador de correlação para o cliente externo.
