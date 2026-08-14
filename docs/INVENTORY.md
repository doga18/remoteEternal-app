# INVENTORY.md

## Identificação

- Data de atualização: 2026-08-14
- Status: **plano de controle migrado para API Node.js — build e testes OK; falta validação manual em 2 máquinas**
- Escopo: inventário do estado atual do projeto RemoteEternal, com base em fatos verificados na leitura de `api/` e `src/`.

## Resumo executivo

O produto usa acesso por ID de seis dígitos, sem contas de usuário, com o plano de controle na **API Node.js**:

- A API (`api/`, Express + WebSocket + PostgreSQL) atribui ID único ao host, recebe o anúncio online, valida o verifier no `lookup`, aplica rate limit, gera um token de sessão por conexão e notifica o host via WebSocket; após o `connectAck`, devolve IP, porta e token ao cliente.
- O App C# fala com a API via HTTP REST + WebSocket (`ServerConnection` com `HttpClient` + `ClientWebSocket`); `MainWindow` tem campo "URL da API" e verificação de atualização ao abrir.
- Modo assistido exige aprovação manual do host a cada conexão; modo não assistido usa ID + senha com salt/verifier PBKDF2 gerados no cliente (senha nunca transmitida em claro).
- Sessão direta entre host e cliente com `SecureFrameChannel`, AES-GCM autenticado e derivação HKDF com chaves por direção.
- Captura de vídeo no host com ScreenRecorderLib (H.264), áudio opcional, injeção de input via Win32 `SendInput` e decodificação FFmpeg no cliente.

## Repositórios

- `remoteEternal-app` (raiz: `src/`, `docs/`, `tests/`, `RemoteEternal.sln`): https://github.com/doga18/remoteEternal-app.git
- `remoteEternal-api` (`api/`): https://github.com/doga18/remoteEternal-api.git
- Separação física via `.gitignore` (a raiz do App exclui `api/`; a API versiona `config/aiven-ca.pem`, `src/`, `tests/` e `package-lock.json`).

## Inventário por projeto

### API Node.js (`api/`) — plano de controle

| Arquivo | Status | Notas |
|---|---|---|
| `package.json` | Pronto | Node.js 18+; scripts `start`, `dev` e `test` (`node --test`). |
| `.env.example` | Pronto | Valores fictícios; `.env` nunca versionado. |
| `config/aiven-ca.pem` | Pronto | Certificado CA do Aiven (público), versionado no repo da API. |
| `src/index.js` | Pronto | Express + HTTP + WebSocket (`/ws`); rotas `health`, `register`, `online`, `salt`, `lookup`, `update/latest`; canal WS do host (`hello`, `connectAck`). |
| `src/db.js` | Pronto | Pool `pg` (formato Aiven `DB_*` ou `DATABASE_URL`), SSL com CA (`rejectUnauthorized true`), `initDb()`, pool max 5 / min 1. |
| `src/schema.sql` | Pronto | `CREATE TABLE IF NOT EXISTS hosts`. |
| `src/registry.js` | Pronto | `HostRegistry` em memória: hosts online e pendências de lookup (timeout 20 s). |
| `src/rateLimit.js` | Pronto | `RateLimiter` por IP: 5 falhas de lookup em 60 s. |
| `src/update.js` | Pronto | Catálogo `RELEASES` com manifest real (`version`, `url`, `sizeBytes`, `sha256`, `fileCount`, `notes`); `CURRENT_VERSION` é derivada (`getLatestUpdate`). |
| `src/validate.js` | Pronto | Validação de ID (6 dígitos), base64, porta e nomes. |
| `tests/update.test.js`, `tests/rateLimit.test.js` | Pronto | Testes unitários (12 no total). |
| `tests/api.integration.test.js` | Pronto | Integração com Postgres; exige `DATABASE_URL`. |

### RemoteEternal.Core

| Arquivo | Status | Notas |
|---|---|---|
| `RemoteEternal.Core.csproj` | Pronto | `net8.0`, nullable habilitado. |
| `Protocol/ControlMessages.cs` | Pronto | Registros de requisição/resultado usados pelo App no plano de controle (`RegisterHostResult`, `HostOnlineResult`, `GetHostSaltResult`, `LookupResult`, `ConnectNotify`, `ConnectAck`) e `Envelope`. |
| `Protocol/SessionProtocol.cs` | Pronto | Controle de sessão direta, monitores e codificação de eventos de input. |
| `Net/FrameChannel.cs` | Pronto | Framing de comprimento e limite de frame. |
| `Crypto/Hkdf.cs` | Pronto | Derivação de chaves. |
| `Crypto/SecureFrameChannel.cs` | Pronto | AES-GCM autenticado, counters e chaves direcionais derivadas por HKDF para `SessionRole.Host`/`SessionRole.Client`, com `SessionSaltV1`. |
| `Auth/PasswordHasher.cs` | Pronto | Salt e PBKDF2-SHA256 para derivação e verificação de credenciais do modo não assistido (usado no cliente). |

### RemoteEternal.Server (legado)

| Arquivo | Status | Notas |
|---|---|---|
| `RemoteEternal.Server.csproj` | Legado | `net8.0`, LiteDB. |
| `Program.cs` | Legado | Argumentos `--port`, `--db`, `--no-register`; porta legada 7000. |
| `RemoteEternalServer.cs`, `ClientSession.cs` | Legado | Listener TCP e handlers do plano de controle antigo. |
| `HostStore.cs` | Legado | Diretório LiteDB de hosts. |
| `RateLimiter.cs` | Legado | Rate limit do servidor antigo (o ativo agora é o da API Node). |
| `ClientRegistry.cs` | Legado | Hosts online e pendências do servidor antigo. |
| `AccountStore.cs`, `AuthTokens.cs` | Legado | Arquivos esvaziados do modelo de contas removido. |

**Status do projeto: legado/não publicado.** Substituído pela API Node.js como plano de controle; mantido apenas porque os testes de integração C# (`tests/RemoteEternal.Core.Tests/ControlPlaneIntegrationTests.cs`) ainda o exercitam.

### RemoteEternal.App

| Arquivo | Status | Notas |
|---|---|---|
| `RemoteEternal.App.csproj` | Pronto com dependência | WPF `net8.0-windows` x64; copia FFmpeg de `libs\ffmpeg\bin` somente se o diretório existir. |
| `App.xaml` / `App.xaml.cs` | Pronto | Inicialização da aplicação. |
| `Views/MainWindow.xaml(.cs)` | Pronto | Campo "URL da API"; painel HOST (assistido/não assistido, senha, Iniciar/Parar acesso, ID) e painel CLIENTE (ID + senha + Conectar); verificação de atualização ao abrir. |
| `Views/ViewerWindow.xaml(.cs)` | Pronto | Visualização, seleção de monitor, áudio, fullscreen, input e encerramento. |
| `Services/AppState.cs` | Pronto | Configuração local; `apiUrl` em `config.txt`; HostId persistido em `host.id`; senha nunca persistida; listener do host padrão 5050. |
| `Services/ServerConnection.cs` | Pronto | Cliente HTTP + WebSocket: `ConnectAsync`, `RegisterHostAsync`, `HostOnlineAsync`, `GetHostSaltAsync`, `LookupAsync`, `GetLatestUpdateAsync`, `HostWsConnectAsync`, `SendConnectAckWsAsync`. |
| `Services/SessionHost.cs` | Pronto | Listener reutilizável do host (`StopAsync`), aceite de sessão, captura e input. |
| `Services/SessionClient.cs` | Pronto | Conexão direta, recebimento de mídia e envio de input. |
| `Services/SessionStream.cs` | Pronto | Transporte de blocos de mídia com tamanho real e slots limitados. |
| `Services/MonitorEnumeration.cs` | Pronto | Enumeração dos monitores. |
| `Media/FfmpegLibrary.cs` | Pronto | Carregamento dinâmico de FFmpeg a partir de `<BaseDirectory>\ffmpeg`. |
| `Media/FfmpegDecoder.cs` | Pronto | Decodificação de vídeo e áudio; define `MediaBuffer` internamente. |
| `Media/AudioPlayer.cs` | Pronto | Reprodução PCM com NAudio. |
| `Input/InputSimulator.cs` | Pronto | Injeção Win32 `SendInput`; normalização para desktop virtual implementada, com suporte a DPI, multi-monitor e coordenadas negativas. |

### Solution

- `RemoteEternal.sln` existe na raiz.

## Testes

- `tests\RemoteEternal.Core.Tests`: projeto xUnit com **13 testes passando** (8 unitários + 5 integração do servidor C# legado).
- API Node.js: **12 testes unitários** (`update`, `rateLimit`) + **1 integração** (`api.integration.test.js`, requer `DATABASE_URL`).
- Build da solution: 0 erros, 0 avisos.

## Conformidade com AGENTS.md

| Regra de segurança | Status | Observação |
|---|---|---|
| Não registrar/versionar senhas, tokens, verifiers, hashes, chaves ou payloads sensíveis | Conforme | `.env.example` com valores fictícios; docs sem credenciais; CA público versionado apenas no repo da API. |
| ID do host não substitui autenticação | Conforme | Não assistido exige senha forte (salt/verifier PBKDF2); assistido exige aprovação manual visível. |
| Token de sessão separado, por conexão | Conforme | Gerado no `lookup`, usado na sessão direta AES-GCM. |
| Expiração e revogação obrigatórias | Conforme | Token de sessão por conexão; sessão exige aceite explícito do host. |
| Sessão direta preserva confidencialidade e integridade | Conforme | AES-GCM autenticado, HKDF e chaves separadas por direção. |
| Plano de controle preserva confidencialidade e integridade | Parcial | HTTP/WS; recomenda-se HTTPS em produção (ex.: Render). |
| Input remoto privilegiado e menor privilégio | Parcial | Input só dentro da sessão aceita; normalização para desktop virtual implementada, com validação manual ainda pendente. |
| Servidor/API não transporta mídia | Conforme | Mídia direta host→cliente. |
| Mensagens recebidas com limites | Conforme | Corpo HTTP 64 kb, mensagens WS 16 KiB, limite de frame em `FrameChannel`. |
| Buffers com tamanho real | Conforme | `MediaBuffer` e `SessionStream` preservam contagem real. |
| Cancelamento e liberação de recursos nativos | Conforme | Pipelines possuem dispose/cancel nos fluxos principais. |

## Gaps identificados

### Resolvidos

1. Separação de chaves por direção no `SecureFrameChannel`.
2. Bug de `Envelope`/`DataJson` causado pelo descarte do `JsonDocument`.
3. DPI e normalização do input para o desktop virtual.
4. Build da solution com mapeamento x64.
5. Testes automatizados do Core e integração do plano de controle (13 testes).
6. Modelo antigo de contas (login, lista de dispositivos, pareamento por conta) substituído pelo acesso por ID.
7. Migração do plano de controle para a API Node.js (HTTP/WS + PostgreSQL), com verificação de atualização e repositórios separados.

### Pendentes

1. Validação manual ponta a ponta em 2 máquinas (API em uma máquina da rede, dois Apps; WPF, captura, áudio, input e encerramento).
2. Deploy da API no Render (HTTPS) e do banco no Aiven com as variáveis reais.
3. Testes de integração da API com `DATABASE_URL` real.
4. Heartbeat efetivo entre host e API (com lease) e `hostOffline` explícito.

## Checklist de validação final

- [x] Build limpo da solution em .NET 8 (App x64).
- [x] Chaves separadas por direção no canal seguro.
- [x] Acesso por ID com assistido/não assistido.
- [x] Plano de controle em API Node.js (register, online, salt, lookup, update) com WebSocket do host.
- [x] Rate limit anti brute force no `lookup` (API, 5/60s por IP).
- [x] Token de sessão por conexão.
- [x] Testes automatizados do Core e integração do servidor C# legado (13 testes).
- [x] Testes unitários da API Node (12) + integração com Postgres (requer `DATABASE_URL`).
- [x] FFmpeg provisionado em `dist\RemoteEternal\ffmpeg` (DLLs nativas).
- [ ] Validar manualmente o fluxo end-to-end em 2 máquinas: API na rede, WPF, captura, vídeo, áudio, input e encerramento.
- [ ] Deploy da API (Render/Aiven) e teste do rate limit e HTTPS em produção.
- [ ] Heartbeat/lease e `hostOffline` explícito na API.
- [x] Docs atualizadas e sem segredos.
