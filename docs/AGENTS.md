# AGENTS.md

## Missão do produto

O RemoteEternal é um aplicativo Windows para acesso remoto local, com evolução planejada para acesso pela internet. Uma máquina atua como host, disponibilizando suas telas e o áudio; outra atua como cliente, visualizando a sessão e enviando eventos de mouse e teclado.

## Capacidades

Tela, áudio e entrada remota são capacidades independentes. Cada sessão deve negociar e autorizar explicitamente as capacidades utilizadas. O host deve ter consentimento visível e revogável para aceitar acesso.

## Segurança

- Não registre nem versione senhas, tokens, verifiers, hashes equivalentes, chaves ou payloads sensíveis.
- O ID do host identifica o destino, mas não substitui autenticação: no modo não assistido, a senha forte é usada com salt e verifier PBKDF2; no modo assistido, cada conexão exige aprovação manual visível do host.
- A sessão direta usa um token de sessão separado, gerado por conexão no lookup e com expiração e revogação adequadas ao ciclo de vida.
- Mensagens recebidas são não confiáveis e devem ter limites de tamanho, tempo, taxa e estado.
- O plano de controle e a sessão direta devem preservar confidencialidade e integridade.
- Input remoto é uma capacidade privilegiada e deve seguir menor privilégio.
- A API/plano de controle não deve transportar mídia sem uma decisão arquitetural explícita.

## Compatibilidade

O plano de controle é a API Node.js (`api/`, repositório `remoteEternal-api`); o App (C# WPF) e o `RemoteEternal.Core` pertencem ao repositório `remoteEternal-app`. Mudanças em `RemoteEternal.Core` podem afetar o App e o servidor C# legado. Primeiro altere e documente o contrato; depois atualize os consumidores de forma compatível ou introduza versionamento. Alterações de contrato entre App e API devem ser refletidas em ambos os repositórios.

## Operação

O build do App deve manter .NET 8, WPF Windows x64. A API Node.js exige Node.js 18+ e PostgreSQL (Aiven ou local); a sessão direta continua no App/Core. DLLs FFmpeg, firewall, portas e permissões são parte da operação e devem ser documentados quando alterados. O servidor C# (`RemoteEternal.Server`) é legado e não deve ser publicado como peça ativa.

## Known gotchas

- O plano de controle e a sessão direta têm requisitos de segurança diferentes, mas ambos transportam dados sensíveis.
- IDs de host têm seis dígitos e espaço limitado; não devem ser tratados como credenciais permanentes nem usados sem senha no modo não assistido.
- Desativar o acesso do host precisa interromper listener, solicitações pendentes e sessões ativas.
- Escritas concorrentes no canal seguro precisam manter ordem e backpressure.
- Buffers de mídia devem transportar o tamanho real do bloco, sem bytes residuais.
- Coordenadas de `SendInput` exigem normalização para o desktop virtual, incluindo DPI, multi-monitor e coordenadas negativas.
- Captura, decodificação e reprodução precisam ser canceláveis e liberar recursos nativos.
- Ausência de relay ou NAT traversal limita o acesso fora da rede local; não tratar conexão direta como solução para internet.
