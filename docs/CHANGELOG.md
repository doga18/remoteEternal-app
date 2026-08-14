# CHANGELOG.md

## 2026-08-14 — Endereço anunciado da sessão direta

- `HostOnlineRequest` passou a aceitar `advertisedAddress` opcional, um IPv4 alcançável pelo cliente usado somente para roteamento da sessão direta; autorização e endereço permanecem separados.
- O App resolve o IPv4 local pela rota TCP até a API, inicia o listener direto na porta `5050` antes de anunciar o host online e envia o endereço anunciado.
- O lookup usa o endereço anunciado, quando presente; o IP observado pela API fica restrito a contexto e rate limit. Sem esse campo, o fallback legado pode anunciar proxy/NAT e falhar; acesso fora da LAN continua exigindo port forwarding, relay ou NAT traversal.

## 2026-08-14 — Distribuição em pasta e manifest de update

- Distribuição atual em pasta self-contained Windows x64: `dist\RemoteEternal`, com `RemoteEternal.exe`, `ScreenRecorderLib.dll` mixed-mode ao lado do executável, runtime .NET e `ffmpeg\` com sete DLLs nativas; o ZIP é `dist\RemoteEternal-1.0.0-win-x64.zip`.
- Corrigido o empacotamento do ScreenRecorderLib: a assembly mixed-mode fica fora de bundle e ao lado do executável.
- A janela principal inicializa offline, sem depender da API para abrir.
- O manifest de update agora informa versão, tamanho, SHA-256 e número de arquivos; `CURRENT_VERSION` é derivada do catálogo `RELEASES` hospedado no GitHub Releases.
- Adicionado `docs/CODE_SIGNING.md` com o processo de assinatura dos arquivos PE da distribuição e geração do ZIP após a assinatura.

## 2026-08-14 — Migração do plano de controle para API Node.js

- Plano de controle substituído: o `RemoteEternal.Server` (C#, TCP + LiteDB) deixou de ser o componente ativo e foi substituído pela **API Node.js** (`api/`, Express + WebSocket + PostgreSQL). O App agora fala com a API via HTTP REST + WebSocket.
- Repositórios separados: `api/` = `remoteEternal-api` (https://github.com/doga18/remoteEternal-api.git); raiz (`src/`, `docs/`, `tests/`, solution) = `remoteEternal-app` (https://github.com/doga18/remoteEternal-app.git). Separação física via `.gitignore`.
- API Node.js: endpoints `GET /api/health`, `POST /api/hosts/register`, `POST /api/hosts/online`, `GET /api/hosts/:hostId/salt`, `POST /api/hosts/:hostId/lookup` (aguarda `connectAck` do host por até 20 s) e `GET /api/update/latest`. WebSocket em `/ws` com `hello`/`helloResult`, `connectNotify` e `connectAck`/`connectAckResult`.
- Banco PostgreSQL: formato Aiven (`DB_HOST`, `DB_PORT` default 14673, `DB_DATABASE` default `remoteeternalapi`, `DB_USER`, `DB_PASS`) com certificado CA em `api\config\aiven-ca.pem` (`ssl rejectUnauthorized true`); também aceita `DATABASE_URL` (Postgres local). Pool reduzido (max 5, min 1).
- App (C#): `ServerConnection` reescrito para `HttpClient` + `ClientWebSocket`; `MainWindow` com campo "URL da API"; verificação de atualização ao abrir via `GET /api/update/latest` ("Nova versão X disponível"). Fluxo do host por HTTP/WS; fluxo do cliente via salt + lookup.
- `RemoteEternal.Server` (C#) permanece como legado no código, apenas exercitado pelos testes de integração C#; não é mais publicado nem documentado como peça ativa.
- Sessão direta inalterada: `SecureFrameChannel` AES-GCM com chaves por direção, `SessionSaltV1`, info `"re-session"`, `SessionHost`/`SessionClient`/`ViewerWindow`, ScreenRecorderLib e FFmpeg em `dist\RemoteEternal\ffmpeg`.
- Testes: 13 testes C# (8 unitários + 5 integração do servidor legado) e API Node com 12 testes unitários (update, rateLimit) + 1 de integração (requer `DATABASE_URL`).

## 2026-08-14 — Novo modelo de acesso por ID (TeamViewer-like)

- Contrato do plano de controle substituído: removidos contas, login, lista de dispositivos e pareamento por conta (`Register`, `GetSalt`, `Login`, `ListDevices`, `Announce`, `Pair`, `PairNotify`, `PairAck`).
- Adicionados `RegisterHost`, `HostOnline`, `GetHostSalt`, `Lookup`, `ConnectNotify`, `ConnectAck` e `Ping`, com `RegisterHostRequest/Result` (HostId de 6 dígitos), `HostOnlineRequest/Result` (AccessMode, Salt, Verifier), `GetHostSaltRequest/Result`, `LookupRequest/Result` (Ip, Port, SessionToken), `ConnectNotify` (SessionToken, ClientName, ClientOs, RequiresApproval), `ConnectAck` (HostId, Accepted, ListenPort) e `HostAccess {Assisted, Unassisted}`.
- Servidor: `AccountStore` e `AuthTokens` removidos (arquivos esvaziados); novo `HostStore` LiteDB (coleção `hosts`, índice único por `HostId`, `HostDoc` com `Id` ObjectId) e `RateLimiter` (5 falhas/60s por IP). `ClientRegistry` agora mantém `OnlineHost` e `PendingLookup`. Handlers novos: `registerHost`, `hostOnline`, `getHostSalt`, `lookup` (valida senha com `FixedTimeEquals`, gera `sessionToken`, notifica o host e aguarda `connectAck` por 20s) e `connectAck`. `Program.cs` usa `--port`, `--db` e `--no-register`; removido `--token-ttl-minutes`.
- App: `MainWindow` redesenhada sem login, com painéis de conexão, HOST (assistido/não assistido, senha) e CLIENTE (ID + senha). `ServerConnection` com `RegisterHostAsync`, `HostOnlineAsync`, `GetHostSaltAsync`, `LookupAsync` e `SendConnectAckAsync`. `AppState` persiste o HostId em `host.id` (senha nunca persistida). `SessionHost` com `StopAsync` reutilizável.
- Testes: 13 testes passando (8 unitários + 5 integração) em `tests\RemoteEternal.Core.Tests`.
- Corrigido bug do `HostStore`: o índice único por `HostId` quebrava porque `HostDoc.Id` era usado como `HostId`; o `HostId` agora é campo próprio com índice único.
- Distribuição daquele estado histórico: `publish\app` (App + `ffmpeg` com 7 DLLs), `publish\server`, `publish\IniciarServidor.bat` e `publish\IniciarApp.bat`; posteriormente o servidor C# foi substituído pela API Node.js e deixou de ser peça publicada.

## 2026-08-14

- Adicionada expiração absoluta configurável aos tokens de login do plano de controle, com padrão de 15 minutos.
- Tokens de login passaram a ser revogados ao desconectar a sessão de controle; também foram adicionadas APIs de revogação individual e ampla e sweep periódico de expirados.
- Reorganizadas as regras do OpenCode para o projeto RemoteEternal.
- Removidas referências operacionais ao antigo projeto ReparaDone.
- Definidos agentes especializados para Core, App, Server, mídia, segurança, QA e release.
- Criada a documentação inicial de arquitetura, regras de segurança e fluxo de desenvolvimento.

## 2026-08-14 — Inventário e docs base do MVP local

- Criado inventário do estado atual (INVENTORY.md) com status "MVP Local - pré-validação".
- Documentados os requisitos operacionais (OPERATING.md): build, execução, FFmpeg, firewall e permissões.
- Criado guia de segurança (SECURITY.md) com postura atual e limitações conhecidas.
- Definidos gaps e plano de incremento do MVP local em etapas (0-6) com matriz de delegação.

## 2026-08-14 — Incremento do MVP local (etapas 0-6)

- Etapa 1: implementada a separação de chaves por direção no `SecureFrameChannel`, com `SessionRole.Host`/`SessionRole.Client`, `SessionSaltV1` e `CreateDirectional`.
- Etapa 2: implementados TTL absoluto e revogação dos tokens de login do plano de controle.
- Etapa 3: o App foi migrado para chaves por direção; o input absoluto passou a normalizar para a escala `0.65535` usando o desktop virtual; e o `ViewerWindow` passou a tratar de forma amigável a ausência das DLLs FFmpeg.
- Corrigido bug crítico em `Envelope`/`EnvelopeUtil.Data`: o `DataJson` agora é preservado antes do descarte do `JsonDocument`, eliminando `ObjectDisposedException` sem alterar o wire format.
- Etapa 4: corrigido o mapeamento da solution para x64; `dotnet build RemoteEternal.sln` concluído sem erros ou avisos.
- Etapa 5: criado o projeto de testes xUnit `RemoteEternal.Core.Tests`, com 9 testes, incluindo a integração do plano de controle.
- Pendências: DLLs FFmpeg nativas ausentes em `libs\\ffmpeg\\bin`; validação manual de WPF, captura, input e áudio ainda não executada.