# CHANGELOG.md

## 2026-08-14 — Novo modelo de acesso por ID (TeamViewer-like)

- Contrato do plano de controle substituído: removidos contas, login, lista de dispositivos e pareamento por conta (`Register`, `GetSalt`, `Login`, `ListDevices`, `Announce`, `Pair`, `PairNotify`, `PairAck`).
- Adicionados `RegisterHost`, `HostOnline`, `GetHostSalt`, `Lookup`, `ConnectNotify`, `ConnectAck` e `Ping`, com `RegisterHostRequest/Result` (HostId de 6 dígitos), `HostOnlineRequest/Result` (AccessMode, Salt, Verifier), `GetHostSaltRequest/Result`, `LookupRequest/Result` (Ip, Port, SessionToken), `ConnectNotify` (SessionToken, ClientName, ClientOs, RequiresApproval), `ConnectAck` (HostId, Accepted, ListenPort) e `HostAccess {Assisted, Unassisted}`.
- Servidor: `AccountStore` e `AuthTokens` removidos (arquivos esvaziados); novo `HostStore` LiteDB (coleção `hosts`, índice único por `HostId`, `HostDoc` com `Id` ObjectId) e `RateLimiter` (5 falhas/60s por IP). `ClientRegistry` agora mantém `OnlineHost` e `PendingLookup`. Handlers novos: `registerHost`, `hostOnline`, `getHostSalt`, `lookup` (valida senha com `FixedTimeEquals`, gera `sessionToken`, notifica o host e aguarda `connectAck` por 20s) e `connectAck`. `Program.cs` usa `--port`, `--db` e `--no-register`; removido `--token-ttl-minutes`.
- App: `MainWindow` redesenhada sem login, com painéis de conexão, HOST (assistido/não assistido, senha) e CLIENTE (ID + senha). `ServerConnection` com `RegisterHostAsync`, `HostOnlineAsync`, `GetHostSaltAsync`, `LookupAsync` e `SendConnectAckAsync`. `AppState` persiste o HostId em `host.id` (senha nunca persistida). `SessionHost` com `StopAsync` reutilizável.
- Testes: 13 testes passando (8 unitários + 5 integração) em `tests\RemoteEternal.Core.Tests`.
- Corrigido bug do `HostStore`: o índice único por `HostId` quebrava porque `HostDoc.Id` era usado como `HostId`; o `HostId` agora é campo próprio com índice único.
- Distribuição: `publish\app` (App + `ffmpeg` com 7 DLLs), `publish\server`, `publish\IniciarServidor.bat` e `publish\IniciarApp.bat`.

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
