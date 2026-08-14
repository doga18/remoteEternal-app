# SECURITY.md

## Postura atual

- IDs de host têm seis dígitos e espaço limitado: identificam o destino, mas não são credenciais permanentes nem fator único de autenticação.
- No modo não assistido, a senha do host nunca é transmitida em claro: o cliente deriva salt e verifier PBKDF2 localmente e envia apenas o verifier; o servidor valida com comparação em tempo constante.
- No modo assistido, cada conexão exige aprovação manual visível do host; sem aprovação, não há sessão.
- O `RateLimiter` aplica proteção anti brute force: cinco falhas de lookup em 60 segundos por IP, cobrindo senha incorreta e ID inexistente.
- Sessão direta cifrada com AES-GCM autenticado; `CreateDirectional` deriva por HKDF `keyWrite` e `keyRead` a partir do segredo da sessão, usando rótulos `info+"write"` e `info+"read"`. `SessionRole.Host` cifra com a chave de escrita e decifra com a de leitura; `SessionRole.Client` usa o sentido inverso. O canal usa `SessionSaltV1` e o info `"re-session"`; `FromSecret` permanece para compatibilidade.
- O token de sessão é gerado por conexão no `lookup`, é separado do ID do host e usado apenas na sessão direta; o cliente só recebe IP, porta e token após a aceitação do host.

## Recomendações obrigatórias

- Nunca registrar/versionar senhas, tokens, verifiers, hashes equivalentes, chaves ou payloads sensíveis.
- Nunca expor tokens em logs ou mensagens.
- Tokens de sessão precisam de expiração e revogação adequadas ao ciclo de vida da conexão.
- No modo não assistido, exigir senha forte: o ID de 6 dígitos tem espaço de 1 milhão e não deve ser usado como único fator.
- No modo assistido, a aprovação manual deve ser visível e revogável pelo host.
- Input remoto é capacidade privilegiada e deve seguir menor privilégio.
- Mensagens recebidas não confiáveis: limites de tamanho, tempo, taxa e estado.
- O servidor não deve transportar mídia sem decisão arquitetural explícita.

## Limitações conhecidas

- Acesso fora da rede local depende de conexão direta host↔cliente; não há NAT traversal nem relay.
- Não tratar conexão direta como solução para acesso pela internet.
- Plano de controle ainda carece de criptografia de transporte (confidencialidade/integridade do plano dependem de evolução).
- A senha do host no modo não assistido permanece no cliente após o uso; removê-la do cliente requer limpeza manual ou evolução futura.
