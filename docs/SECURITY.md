# SECURITY.md

## Postura atual

- **Plano de controle**: é a API Node.js (`remoteEternal-api`). A comunicação entre App e API é HTTP REST + WebSocket; em produção o acesso deve usar **HTTPS** (ex.: atrás do proxy do Render). A API aplica rate limit, valida o verifier com comparação em tempo constante e coordena o encontro; a mídia não passa pela API.
- IDs de host têm seis dígitos e espaço de 1 milhão: identificam o destino, mas **não são credenciais permanentes nem fator único de autenticação**.
- No modo não assistido, a senha do host nunca é transmitida em claro: o cliente deriva salt e verifier PBKDF2 localmente e envia apenas o verifier; a API valida com comparação em tempo constante (`crypto.timingSafeEqual`).
- No modo assistido, cada conexão exige aprovação manual visível do host; sem aprovação, não há sessão.
- A API aplica proteção anti brute force no `lookup`: **cinco falhas de lookup em 60 segundos por IP**, cobrindo senha incorreta e ID inexistente (resposta `429` com `retryAfterSeconds`). O IP é derivado de `req.ip` (com `TRUST_PROXY=true`, do `X-Forwarded-For`).
- O `sessionToken` é gerado por conexão no `lookup` (32 bytes base64), é separado do ID do host e usado apenas na sessão direta; expira ao ser usado ou após o timeout de 20 s. O cliente só recebe IP, porta e token após a aceitação do host.
- Sessão direta cifrada com AES-GCM autenticado; `CreateDirectional` deriva por HKDF `keyWrite` e `keyRead` a partir do segredo da sessão, usando rótulos `info+"write"` e `info+"read"`. `SessionRole.Host` cifra com a chave de escrita e decifra com a de leitura; `SessionRole.Client` usa o sentido inverso. O canal usa `SessionSaltV1` e o info `"re-session"`; `FromSecret` permanece para compatibilidade.

## Recomendações obrigatórias

- Nunca registrar/versionar senhas, tokens, verifiers, hashes equivalentes, chaves ou payloads sensíveis. Isso inclui `.env`, `DATABASE_URL` real, `DB_PASS` e credenciais do Aiven.
- O certificado CA do Aiven (`api\config\aiven-ca.pem`) é um certificado **público** e pode ser versionado **apenas no repositório da API** (`remoteEternal-api`); não deve ser versionado no repositório do App.
- Nunca expor tokens em logs ou mensagens.
- Tokens de sessão precisam de expiração e revogação adequadas ao ciclo de vida da conexão.
- No modo não assistido, exigir senha forte: o ID de 6 dígitos tem espaço de 1 milhão e não deve ser usado como único fator.
- No modo assistido, a aprovação manual deve ser visível e revogável pelo host.
- Input remoto é capacidade privilegiada e deve seguir menor privilégio.
- Mensagens recebidas não confiáveis: limites de tamanho (corpo HTTP `64kb`, mensagens WS 16 KiB), tempo, taxa e estado.
- O servidor/API não deve transportar mídia sem decisão arquitetural explícita.
- O `connectAck` vai pelo WebSocket do host (não por rota HTTP), de modo que apenas o socket vinculado ao `hostId` pode responder pelo host.
- O acesso deve ser desativado de forma completa: interromper listener, WebSocket do host, solicitações pendentes e sessões ativas.

## Limitações conhecidas

- Acesso fora da rede local depende de conexão direta host↔cliente; não há NAT traversal nem relay.
- Não tratar conexão direta como solução para acesso pela internet.
- O plano de controle por HTTP/WS depende de HTTPS para preservar confidencialidade e integridade no trânsito; em rede local sem HTTPS, recomenda-se restringir ao perfil de rede local.
- Online/offline e pendências de lookup ficam em memória na API (perdidas em restart); não há heartbeat efetivo com lease.
- A senha do host no modo não assistido permanece no cliente após o uso; removê-la do cliente requer limpeza manual ou evolução futura.
