# ARCHITECTURE.md

## Visão geral

O RemoteEternal é dividido em **dois repositórios independentes**, mantendo o mesmo produto:

- `remoteEternal-api` (Node.js, em `api/`): **plano de controle**. API Express com WebSocket e PostgreSQL, responsável pelo diretório de hosts, pareamento e verificação de atualização. Substituiu o `RemoteEternal.Server` (C#).
- `remoteEternal-app` (C# .NET, na raiz): aplicação WPF Windows (`RemoteEternal.App`) que atua como host e cliente, e o projeto compartilhado `RemoteEternal.Core` (contratos de sessão direta, criptografia e autenticação). Contém também `docs/`, `tests/` e a solution.

O `RemoteEternal.Server` (C#) permanece no código como legado: os testes de integração C# ainda o exercitam, mas não é publicado nem documentado como parte ativa do produto.

## Planos de comunicação

### Plano de controle

O App fala com a API Node.js via **HTTP REST + WebSocket** (não existe mais servidor TCP C# ativo). O acesso usa um ID de host de seis dígitos, sem contas de usuário ou login. Endpoints (`/api`):

- `GET /api/health` — saúde e versão da API.
- `POST /api/hosts/register` `{deviceName, os}` → `{ok, hostId}` — atribui um ID único de 6 dígitos.
- `POST /api/hosts/online` `{hostId, deviceName, os, listenPort, accessMode, salt, verifier}` → `{ok}` — anuncia o host online e registra o modo de acesso (assisted/unassisted) e as credenciais do modo não assistido.
- `GET /api/hosts/:hostId/salt` → `{ok, accessMode, salt}` — consulta o salt e o modo do host.
- `POST /api/hosts/:hostId/lookup` `{authHash}` → `{ok, ip, port, sessionToken}` — encontro entre cliente e host; aguarda a decisão do host por até 20 segundos.
- `GET /api/update/latest?currentVersion=X` → `{ok, update: {version, url, sizeBytes, sha256, fileCount, notes} | null}` — verificação de atualização do App.

WebSocket em `/ws` (usado **apenas pelo host**):

- Host → API: `hello {hostId}` → API → Host: `helloResult`.
- API → Host: `connectNotify {sessionToken, clientName, clientOs, requiresApproval}`.
- Host → API: `connectAck {hostId, accepted, listenPort}` → API → Host: `connectAckResult`.

No modo não assistido, o cliente deriva `authHash` (PBKDF2) da senha e o envia no `lookup`; a API valida com comparação em tempo constante. No modo assistido, o host recebe `connectNotify` com o pedido e aprova manualmente. A API coordena o encontro, mas a mídia não passa pelo Server nem pela API.

### Sessão direta

Após o `lookup` aceito, o cliente conecta ao listener do host usando o token de sessão. O canal é convertido em `SecureFrameChannel`, com frames de controle, mídia e input. O host envia `hello`, recebe `start`, captura o display selecionado e transmite mídia; o cliente envia controle de sessão e input.

A arquitetura atual depende de conectividade direta entre host e cliente. NAT traversal, relay e acesso pela internet são evolução futura e exigem desenho próprio de segurança e operação.

## Projetos e responsabilidades

### API (Node.js — `api/`)

- `src/index.js`: aplicação Express + HTTP + WebSocket, rotas do plano de controle e canal WS do host.
- `src/db.js`: pool `pg` (formato Aiven `DB_*` ou `DATABASE_URL`), SSL com CA e `initDb()`.
- `src/schema.sql`: `CREATE TABLE IF NOT EXISTS hosts`.
- `src/registry.js`: diretório em memória de hosts online (`HostRegistry`) e de pendências de lookup.
- `src/rateLimit.js`: `RateLimiter` — 5 falhas de lookup por IP em 60 segundos.
- `src/update.js`: catálogo `RELEASES` com manifest (`version, url, sizeBytes, sha256, fileCount, notes`); `CURRENT_VERSION` derivada; hospedagem GitHub Releases.
- `src/validate.js`: validação de ID, base64, porta e nomes.
- `tests/`: unitários (`update`, `rateLimit`) e integração com Postgres (`api.integration.test.js`).

### Core (C# — `src/RemoteEternal.Core`)

- `Protocol/ControlMessages.cs`: contratos de sessão e dados compartilhados usados pelo App no plano de controle (registros de requisição/resultado como `RegisterHostResult`, `HostOnlineResult`, `GetHostSaltResult`, `LookupResult`, `ConnectNotify` e `ConnectAck`), além de `Envelope`.
- `Protocol/SessionProtocol.cs`: controle de sessão direta, monitores e eventos de input.
- `Net/FrameChannel.cs`: framing de comprimento e limite de frame.
- `Crypto/SecureFrameChannel.cs`: AES-GCM, nonce e transporte seguro da sessão; `SessionRole` define o sentido das chaves e `SessionSaltV1` identifica o salt fixo do protocolo direcional.
- `Crypto/Hkdf.cs`: derivação de chaves.
- `Auth/PasswordHasher.cs`: salt, derivação e verificação de credenciais do modo não assistido (PBKDF2, usado no cliente).

### App (C# WPF — `src/RemoteEternal.App`)

- `Views/MainWindow`: campo "URL da API", configuração do host assistido ou não assistido, exibição do ID, conexão do cliente por ID e verificação de atualização ao abrir.
- `Views/ViewerWindow`: visualização, seleção de monitor, áudio e input.
- `Services/AppState`: URL da API (`apiUrl`), porta do listener, ID persistido em `host.id` e configuração local; a senha nunca é persistida.
- `Services/ServerConnection`: cliente HTTP + WebSocket com `ConnectAsync`, `RegisterHostAsync`, `HostOnlineAsync`, `GetHostSaltAsync`, `LookupAsync`, `GetLatestUpdateAsync`, `HostWsConnectAsync` e `SendConnectAckWsAsync`.
- `Services/SessionHost`: listener reutilizável do host, captura e input.
- `Services/SessionClient`: cliente direto e recebimento de mídia.
- `Services/SessionStream`: transporte de blocos de mídia.
- `Services/MonitorEnumeration`: enumeração dos monitores.
- `Media`: FFmpeg, decodificação e NAudio.
- `Input/InputSimulator`: injeção Win32 de mouse e teclado.

### Server (C# legado — `src/RemoteEternal.Server`)

- `Program`: argumentos `--port`, `--db` e `--no-register` (porta legada 7000).
- `RemoteEternalServer`, `ClientSession`, `HostStore`, `RateLimiter` e `ClientRegistry`: servidor TCP e diretório LiteDB do plano de controle anterior.
- **Status: legado/não publicado.** Substituído pela API Node.js como plano de controle ativo; continua no repositório apenas porque os testes de integração C# (`tests/RemoteEternal.Core.Tests/ControlPlaneIntegrationTests.cs`) ainda o exercitam.

## Fluxo nominal

1. A API Node.js inicia na porta `3000` e conecta ao PostgreSQL (Aiven ou local).
2. O host inicia o App, informa a URL da API (ex.: `http://localhost:3000`) e conecta; a API responde `GET /api/health`.
3. O host escolhe acesso assistido ou não assistido; no modo não assistido, cria senha, salt e verifier PBKDF2 localmente.
4. Ao clicar em Iniciar acesso, o host obtém um ID único de seis dígitos (`registerHost`, persistido em `host.id`), anuncia-se online (`hostOnline`), conecta o WebSocket (`hello`) e inicia o listener direto na porta `5050`.
5. O cliente informa a mesma URL da API, digita o ID do host e, se necessário, a senha; obtém o salt com `getHostSalt` e consulta o destino com `lookup`.
6. A API aplica o rate limit, valida o verifier no modo não assistido, gera um token de sessão e envia `connectNotify` ao host pelo WebSocket.
7. No modo assistido, o host mostra o pedido e exige aprovação manual; no modo não assistido, aceita automaticamente.
8. O host responde `connectAck`; a API devolve ao cliente IP, porta e token de sessão.
9. O cliente conecta diretamente ao host com `SecureFrameChannel`; o host envia monitores e o cliente inicia a sessão escolhendo monitor e áudio.
10. O host captura e envia vídeo/áudio; o cliente decodifica, reproduz e envia eventos de entrada autorizados.
11. Ao desconectar, parar o acesso ou fechar o App, a sessão, o listener, o WebSocket do host e os pedidos pendentes são encerrados e os recursos liberados.

## Mídia

O host usa ScreenRecorderLib e configura vídeo H.264 e áudio conforme a sessão. O fluxo é encaminhado por `SessionStream`. O cliente usa FFmpeg para decodificar vídeo e converter áudio para PCM, reproduzido por NAudio. A distribuição publicada é a pasta `dist\RemoteEternal`, com as DLLs FFmpeg em `dist\RemoteEternal\ffmpeg`.

Buffers precisam ter limites e preservar o tamanho real dos blocos. Reinicializações por troca de monitor, alteração de áudio ou falha devem cancelar o pipeline anterior antes de iniciar o próximo.

## Segurança atual e evolução

A sessão direta possui cifragem autenticada AES-GCM com derivação HKDF de chaves separadas por direção. `SecureFrameChannel.CreateDirectional` deriva `keyWrite` com `info+"write"` e `keyRead` com `info+"read"`; `SessionRole.Host` cifra com `keyWrite` e decifra com `keyRead`, enquanto `SessionRole.Client` faz o inverso. O segredo da sessão é o token de sessão gerado no `lookup`, o salt fixo é `SessionSaltV1` e o info usado pelo App é `"re-session"`. `FromSecret` permanece disponível para compatibilidade.

No modo não assistido, a senha do host permanece no cliente e é representada por salt e verifier PBKDF2 gerados no cliente; a senha nunca é transmitida em claro. A API Node.js valida o verifier usando comparação em tempo constante (`crypto.timingSafeEqual`). O `RateLimiter` da API bloqueia temporariamente cinco falhas em 60 segundos por IP, incluindo senha incorreta e ID inexistente.

O ID de seis dígitos não é segredo suficiente por si só. O modo não assistido exige senha forte; o modo assistido exige aprovação manual visível do host. O token de sessão é separado do ID e criado para cada conexão autorizada.

A evolução para acesso pela internet, NAT traversal, relay, heartbeat efetivo com lease e encerramento explícito de hosts continua pendente e exige desenho próprio de segurança e operação.

## Build e distribuição

- Repositório **remoteEternal-app**: solution `RemoteEternal.sln`; App `net8.0-windows`, WPF, x64; Core `net8.0`; testes xUnit. Distribuição self-contained Windows x64 em `dist\RemoteEternal`, com `RemoteEternal.exe`, `ScreenRecorderLib.dll` ao lado do executável, runtime .NET e `dist\RemoteEternal\ffmpeg` com as DLLs nativas necessárias para mídia. O ZIP é `dist\RemoteEternal-1.0.0-win-x64.zip`.
- Repositório **remoteEternal-api**: Node.js 18+; `npm install` e `npm start` (porta `PORT`, default `3000`); banco PostgreSQL (Aiven ou local) via `DATABASE_URL` ou `DB_*`; testes com `node --test`. Publicação/deploy independente (ex.: Render).
- Firewall, portas e permissões devem ser validados em Windows x64.
