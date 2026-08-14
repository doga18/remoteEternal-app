# INVENTORY.md

## Identificação

- Data de geração: 2026-08-14
- Status: **MVP com acesso por ID (TeamViewer-like) — build e testes OK; falta validação manual em 2 máquinas**
- Escopo: inventário do estado atual do projeto RemoteEternal, com base em fatos verificados na leitura de `src/`.

## Resumo executivo

O MVP agora usa acesso por ID de seis dígitos, sem contas de usuário:

- O host solicita um ID único de seis dígitos ao servidor e anuncia-se online com `hostOnline`.
- Modo assistido exige aprovação manual do host a cada conexão; modo não assistido usa ID + senha com salt/verifier PBKDF2 gerados no cliente (senha nunca transmitida em claro).
- O servidor valida o verifier no `lookup` (comparação em tempo constante), aplica rate limit, gera um token de sessão por conexão e notifica o host; após o `connectAck`, devolve IP, porta e token ao cliente.
- Sessão direta entre host e cliente com `SecureFrameChannel`, AES-GCM autenticado e derivação HKDF com chaves por direção.
- Captura de vídeo no host com ScreenRecorderLib (H.264), áudio opcional, injeção de input via Win32 `SendInput` e decodificação FFmpeg no cliente.

## Inventário por projeto

### RemoteEternal.Core

| Arquivo | Status | Notas |
|---|---|---|
| `RemoteEternal.Core.csproj` | Pronto | `net8.0`, nullable habilitado. |
| `Protocol/ControlMessages.cs` | Pronto | Contratos por ID: `RegisterHost`, `HostOnline`, `GetHostSalt`, `Lookup`, `ConnectNotify`, `ConnectAck`, `Ping`; `HostAccess {Assisted, Unassisted}`. |
| `Protocol/SessionProtocol.cs` | Pronto | Controle de sessão, monitores e codificação de eventos de input. |
| `Net/FrameChannel.cs` | Pronto | Framing de comprimento e limite de frame. |
| `Crypto/Hkdf.cs` | Pronto | Derivação de chaves. |
| `Crypto/SecureFrameChannel.cs` | Pronto | AES-GCM autenticado, counters e chaves direcionais derivadas por HKDF para `SessionRole.Host`/`SessionRole.Client`, com `SessionSaltV1`. |
| `Auth/PasswordHasher.cs` | Pronto | Salt e PBKDF2-SHA256 para derivação e verificação de credenciais do modo não assistido. |

### RemoteEternal.Server

| Arquivo | Status | Notas |
|---|---|---|
| `RemoteEternal.Server.csproj` | Pronto | `net8.0`, LiteDB. |
| `Program.cs` | Pronto | Argumentos `--port`, `--db`, `--no-register`; porta padrão 7000. |
| `RemoteEternalServer.cs` | Pronto | Listener TCP e ciclo de vida. |
| `ClientSession.cs` | Pronto | Atendimento por conexão; handlers `registerHost`, `hostOnline`, `getHostSalt`, `lookup`, `connectAck`, `ping`. |
| `HostStore.cs` | Pronto | Diretório LiteDB de hosts na coleção `hosts`, índice único por `HostId`; `HostDoc` com `Id` ObjectId. |
| `RateLimiter.cs` | Pronto | 5 falhas de lookup por IP em 60 segundos. |
| `ClientRegistry.cs` | Pronto | `OnlineHost` (hosts online) e `PendingLookup` (consultas aguardando `connectAck`). |
| `AccountStore.cs` | Removido | Arquivo esvaziado; modelo de contas substituído. |
| `AuthTokens.cs` | Removido | Arquivo esvaziado; tokens de login do plano de controle não existem mais. |

### RemoteEternal.App

| Arquivo | Status | Notas |
|---|---|---|
| `RemoteEternal.App.csproj` | Pronto com dependência | WPF `net8.0-windows` x64; copia FFmpeg de `libs\ffmpeg\bin` somente se o diretório existir. |
| `App.xaml` / `App.xaml.cs` | Pronto | Inicialização da aplicação. |
| `Views/MainWindow.xaml(.cs)` | Pronto | Sem login; painel de conexão (servidor/porta), painel HOST (assistido/não assistido, senha, Iniciar/Parar acesso, ID) e painel CLIENTE (ID + senha + Conectar). |
| `Views/ViewerWindow.xaml(.cs)` | Pronto | Visualização, seleção de monitor, áudio, fullscreen, input e encerramento. |
| `Services/AppState.cs` | Pronto | Configuração local; HostId persistido em `host.id`; senha nunca persistida; listener do host padrão 5050. |
| `Services/ServerConnection.cs` | Pronto | Conexão com o plano de controle: `RegisterHostAsync`, `HostOnlineAsync`, `GetHostSaltAsync`, `LookupAsync`, `SendConnectAckAsync`. |
| `Services/SessionHost.cs` | Pronto | Listener reutilizável do host (`StopAsync`), aceite de sessão, captura e input. |
| `Services/SessionClient.cs` | Pronto | Conexão direta, recebimento de mídia e envio de input. |
| `Services/SessionStream.cs` | Pronto | Transporte de blocos de mídia com tamanho real e slots limitados. |
| `Services/MonitorEnumeration.cs` | Pronto | Enumeração dos monitores. |
| `Media/FfmpegLibrary.cs` | Pronto | Carregamento dinâmico de FFmpeg a partir de `<BaseDirectory>\ffmpeg`. |
| `Media/FfmpegDecoder.cs` | Pronto | Decodificação de vídeo e áudio; **define `MediaBuffer` internamente (linhas 6-100), que não é um gap.** |
| `Media/AudioPlayer.cs` | Pronto | Reprodução PCM com NAudio. |
| `Input/InputSimulator.cs` | Pronto | Injeção Win32 `SendInput`; normalização para desktop virtual implementada, com suporte a DPI, multi-monitor e coordenadas negativas. |

### Solution

- `RemoteEternal.sln` existe na raiz.

## Testes

- `tests\RemoteEternal.Core.Tests`: projeto xUnit com **13 testes passando** (8 unitários + 5 integração).
- Build da solution: 0 erros, 0 avisos.

## Correção importante

- Bug do `HostStore` corrigido: o índice único por `HostId` quebrava porque `HostDoc.Id` (ObjectId) era usado como `HostId`; o `HostId` agora é um campo próprio com índice único.

## Conformidade com AGENTS.md

| Regra de segurança | Status | Observação |
|---|---|---|
| Não registrar/versionar senhas, tokens, verifiers, hashes, chaves ou payloads sensíveis | Conforme no código | Código não loga valores derivados; docs não contêm credenciais. |
| ID do host não substitui autenticação | Conforme | Não assistido exige senha forte (salt/verifier PBKDF2); assistido exige aprovação manual visível. |
| Token de sessão separado, por conexão | Conforme | Gerado no `lookup`, usado na sessão direta AES-GCM. |
| Expiração e revogação obrigatórias | Conforme | Token de sessão por conexão; sessão exige aceite explícito do host. |
| Sessão direta preserva confidencialidade e integridade | Conforme | AES-GCM autenticado, HKDF e chaves separadas por direção. |
| Plano de controle preserva confidencialidade e integridade | Parcial | TCP + envelopes JSON; sem criptografia de transporte. |
| Input remoto privilegiado e menor privilégio | Parcial | Input só dentro da sessão aceita; normalização para desktop virtual implementada, com validação manual ainda pendente. |
| Servidor não transporta mídia | Conforme | Mídia direta host→cliente. |
| Mensagens recebidas com limites | Conforme | Limite de frame em `FrameChannel`. |
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
7. Bug do `HostStore` (índice único por `HostId` com `Id` ObjectId).

### Pendentes

1. Validação manual ponta a ponta em 2 máquinas (WPF, captura, áudio, input e encerramento).
2. `Ping` definido no contrato, mas sem `Pong`/uso efetivo.
3. `hostOffline` explícito para remover hosts do registro online.
4. Heartbeat efetivo entre host e servidor.

## Checklist de validação final

- [x] Build limpo da solution em .NET 8 (App x64).
- [x] Chaves separadas por direção no canal seguro.
- [x] Acesso por ID com assistido/não assistido.
- [x] Rate limit anti brute force no `lookup`.
- [x] Token de sessão por conexão.
- [x] Testes automatizados do Core e integração do plano de controle (13 testes).
- [x] FFmpeg provisionado em `publish\app\ffmpeg` (7 DLLs).
- [ ] Validar manualmente o fluxo end-to-end em 2 máquinas: WPF, captura, vídeo, áudio, input e encerramento.
- [ ] Heartbeat/ping e `hostOffline` explícito.
- [x] Docs atualizadas e sem segredos.
