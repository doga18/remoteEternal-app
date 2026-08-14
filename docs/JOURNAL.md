# JOURNAL.md

## 2026-08-14 — Migração para acesso por ID

Decisão do usuário: substituir o modelo de contas e login por um modelo estilo TeamViewer. Cada host recebe um ID único de seis dígitos; o cliente informa o ID (e, no modo não assistido, a senha); no modo assistido, o host aprova manualmente cada conexão. O servidor atua como diretório de hosts e coordena o encontro, mas a mídia continua direta entre host e cliente.

Etapas executadas:

- Core: contrato do plano de controle substituído por `RegisterHost`, `HostOnline`, `GetHostSalt`, `Lookup`, `ConnectNotify`, `ConnectAck` e `Ping`, com `HostAccess {Assisted, Unassisted}`.
- Server: `AccountStore`/`AuthTokens` removidos; novo `HostStore` LiteDB (coleção `hosts`, índice único por `HostId`), `RateLimiter` (5 falhas/60s por IP) e `ClientRegistry` com `OnlineHost`/`PendingLookup`. Handlers novos e `Program.cs` com `--port`, `--db` e `--no-register`.
- App: `MainWindow` redesenhada sem login, com painéis de conexão, host (assistido/não assistido) e cliente (ID + senha); `ServerConnection` com os novos métodos; `AppState` persiste o HostId em `host.id`; `SessionHost` com `StopAsync` reutilizável.
- QA: 13 testes passando (8 unitários + 5 integração). Bug do `HostStore` corrigido durante a validação: o índice único por `HostId` quebrava porque `HostDoc.Id` (ObjectId) era usado como `HostId`; o `HostId` agora é campo próprio com índice único.

Pendências: validação manual em 2 máquinas, heartbeat/ping efetivo, `hostOffline` explícito e reindexação do RAG após a atualização das docs.

## 2026-08-14 — Reorientação do workspace

A pasta `src/` foi identificada como o projeto relevante. O produto é um aplicativo WPF Windows de acesso remoto local, com captura de telas e áudio no host, visualização e input no cliente, e um servidor separado para controle e pareamento.

A configuração anterior referenciava um sistema Node/React de outro domínio. A nova organização usa os limites reais da solution: Core compartilhado, App WPF, Server de controle, mídia, segurança, QA e distribuição. O acesso pela internet permanece uma evolução futura; a arquitetura atual de conexão direta não deve ser tratada como NAT traversal ou relay.

A documentação em `docs/` passou a ser a fonte de contexto do RAG e não contém credenciais ou segredos.

## 2026-08-14 — Expiração e revogação de tokens do plano de controle

`AuthTokens` agora usa TTL absoluto, com padrão de 15 minutos e configuração opcional por `--token-ttl-minutes`. A expiração é verificada de forma lazy em `GetUser` e por um sweep periódico a cada minuto, ligado ao `CancellationToken` de `RemoteEternalServer.RunAsync`. O token criado no login da sessão é revogado no `finally` de `ClientSession.RunAsync`; isso afeta somente esse token, não tokens de outras sessões do mesmo usuário. O pareamento direto continua usando um token de sessão separado, portanto não é invalidado por essa revogação do plano de controle.

## 2026-08-14 — Inventário completo do MVP local

Investigação completa de `src/` concluída. O estado do projeto foi verificado por leitura direta dos arquivos-fonte, não por suposição.

Descoberta: `MediaBuffer` está definido dentro de `src\RemoteEternal.App\Media\FfmpegDecoder.cs` (linhas 6-100). Ele não é um gap; o buffer de mídia existe com filas, pool e wake/abort.

Descoberta: `libs\ffmpeg\bin` não existe no estado atual. O csproj do App copia DLLs de lá condicionalmente e `FfmpegLibrary.EnsureLoaded()` procura em `<BaseDirectory>\ffmpeg`; sem as DLLs provisionadas, o cliente não decodifica vídeo/áudio. Dependência nativa FFmpeg pendente.

Decisão: o incremento vertical do MVP local seguirá as etapas 0-6: Etapa 0 docs base; Etapa 1 Core com chaves por direção no `SecureFrameChannel`; Etapa 2 Server com TTL/expiração de tokens; Etapa 3 App com correção de DPI no input; Etapa 4 Release com investigação do provisionamento FFmpeg; Etapa 5 QA de validação de integração; Etapa 6 docs finais.

## 2026-08-14 — Incremento do MVP local concluído (etapas 0-6)

As etapas 1-5 foram implementadas e validadas: o canal seguro agora separa chaves por direção; tokens de login têm TTL e revogação; o App usa os papéis direcionais, normaliza input no desktop virtual e trata a ausência de FFmpeg no ViewerWindow; o bug de `ObjectDisposedException` em `Envelope` foi corrigido sem mudança no wire format; a solution compila em x64; e os 9 testes xUnit, incluindo a integração do plano de controle, passam. A documentação final das etapas 0-6 foi atualizada.

Permanecem pendentes o provisionamento das DLLs FFmpeg nativas em `libs\\ffmpeg\\bin` e a validação manual ponta a ponta de WPF, captura, áudio e input.
