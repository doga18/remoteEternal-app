# ARCHITECTURE.md

## Visão geral

O RemoteEternal é dividido em três projetos .NET 8 dentro de `src/`:

- `RemoteEternal.Core`: contratos e mecanismos compartilhados.
- `RemoteEternal.App`: aplicação WPF Windows que pode atuar como host e cliente.
- `RemoteEternal.Server`: servidor de controle e diretório.

## Planos de comunicação

### Plano de controle

O App conecta ao Server por TCP usando `FrameChannel` e envelopes JSON. O acesso usa um ID de host de seis dígitos, sem contas de usuário ou login. O host solicita seu ID com `registerHost`, anuncia o acesso com `hostOnline` e o cliente resolve o destino com `getHostSalt` e `lookup`.

No modo não assistido, o cliente envia o verifier derivado da senha para validação no `lookup`. No modo assistido, o host recebe `connectNotify` com o pedido. O servidor aguarda `connectAck` por até 20 segundos; após a aceitação, retorna ao cliente IP, porta e token de sessão. `Ping` também faz parte do contrato. O servidor coordena o encontro, mas a mídia não passa pelo Server.

### Sessão direta

Após o `lookup` aceito, o cliente conecta ao listener do host usando o token de sessão. O canal é convertido em `SecureFrameChannel`, com frames de controle, mídia e input. O host envia `hello`, recebe `start`, captura o display selecionado e transmite mídia; o cliente envia controle de sessão e input.

A arquitetura atual depende de conectividade direta entre host e cliente. NAT traversal, relay e acesso pela internet são evolução futura e exigem desenho próprio de segurança e operação.

## Projetos e responsabilidades

### Core

- `Protocol/ControlMessages.cs`: contratos do plano de controle, incluindo `RegisterHost`, `HostOnline`, `GetHostSalt`, `Lookup`, `ConnectNotify`, `ConnectAck` e `Ping`.
- `Protocol/SessionProtocol.cs`: controle de sessão, monitores e eventos de input.
- `Net/FrameChannel.cs`: framing de comprimento e limite de frame.
- `Crypto/SecureFrameChannel.cs`: AES-GCM, nonce e transporte seguro da sessão; `SessionRole` define o sentido das chaves e `SessionSaltV1` identifica o salt fixo do protocolo direcional.
- `Crypto/Hkdf.cs`: derivação de chaves.
- `Auth/PasswordHasher.cs`: salt, derivação e verificação de credenciais do modo não assistido.

### App

- `Views/MainWindow`: conexão com o servidor, configuração do host assistido ou não assistido, exibição do ID e conexão do cliente por ID.
- `Views/ViewerWindow`: visualização, seleção de monitor, áudio e input.
- `Services/AppState`: servidor, porta, ID persistido em `host.id` e configuração local; a senha não é persistida.
- `Services/ServerConnection`: conexão e métodos `RegisterHostAsync`, `HostOnlineAsync`, `GetHostSaltAsync`, `LookupAsync` e `SendConnectAckAsync`.
- `Services/SessionHost`: listener reutilizável do host, captura e input.
- `Services/SessionClient`: cliente direto e recebimento de mídia.
- `Services/SessionStream`: transporte de blocos de mídia.
- `Services/MonitorEnumeration`: enumeração dos monitores.
- `Media`: FFmpeg, decodificação e NAudio.
- `Input/InputSimulator`: injeção Win32 de mouse e teclado.

### Server

- `Program`: argumentos `--port`, `--db` e `--no-register` e inicialização.
- `RemoteEternalServer`: listener TCP e ciclo de vida.
- `ClientSession`: atendimento por conexão e handlers do plano de controle.
- `HostStore`: diretório LiteDB de hosts na coleção `hosts`, com índice único por `HostId`.
- `RateLimiter`: limite de cinco falhas de lookup por IP em 60 segundos.
- `ClientRegistry`: hosts online (`OnlineHost`) e consultas pendentes (`PendingLookup`).

## Fluxo nominal

1. O Server inicia na porta de controle e abre o banco LiteDB.
2. O host inicia o App, conecta ao Server e solicita um ID único de seis dígitos com `registerHost`.
3. O host escolhe acesso assistido ou não assistido; no modo não assistido, cria senha, salt e verifier PBKDF2 localmente.
4. Ao clicar em Iniciar acesso, o host envia `hostOnline`, inicia o listener direto na porta `5050` e exibe o ID.
5. O cliente informa o ID do host e, se necessário, a senha, obtém o salt com `getHostSalt` e consulta o destino com `lookup`.
6. O Server aplica o rate limit, valida o verifier no modo não assistido, gera um token de sessão e envia `connectNotify` ao host.
7. No modo assistido, o host mostra o pedido e exige aprovação manual; no modo não assistido, aceita automaticamente.
8. O host responde `connectAck`; o Server devolve ao cliente IP, porta e token de sessão.
9. O cliente conecta diretamente ao host com `SecureFrameChannel`; o host envia monitores e o cliente inicia a sessão escolhendo monitor e áudio.
10. O host captura e envia vídeo/áudio; o cliente decodifica, reproduz e envia eventos de entrada autorizados.
11. Ao desconectar, parar o acesso ou fechar o App, a sessão, o listener e os pedidos pendentes são encerrados e os recursos liberados.

## Mídia

O host usa ScreenRecorderLib e configura vídeo H.264 e áudio conforme a sessão. O fluxo é encaminhado por `SessionStream`. O cliente usa FFmpeg para decodificar vídeo e converter áudio para PCM, reproduzido por NAudio. A distribuição publicada contém as DLLs FFmpeg em `publish\app\ffmpeg`.

Buffers precisam ter limites e preservar o tamanho real dos blocos. Reinicializações por troca de monitor, alteração de áudio ou falha devem cancelar o pipeline anterior antes de iniciar o próximo.

## Segurança atual e evolução

A sessão direta possui cifragem autenticada AES-GCM com derivação HKDF de chaves separadas por direção. `SecureFrameChannel.CreateDirectional` deriva `keyWrite` com `info+"write"` e `keyRead` com `info+"read"`; `SessionRole.Host` cifra com `keyWrite` e decifra com `keyRead`, enquanto `SessionRole.Client` faz o inverso. O segredo da sessão é o token de sessão gerado no `lookup`, o salt fixo é `SessionSaltV1` e o info usado pelo App é `"re-session"`. `FromSecret` permanece disponível para compatibilidade.

No modo não assistido, a senha do host permanece no cliente e é representada por salt e verifier PBKDF2 gerados no cliente; a senha nunca é transmitida em claro. O Server valida o verifier usando comparação em tempo constante. O `RateLimiter` bloqueia temporariamente cinco falhas em 60 segundos por IP, incluindo senha incorreta e ID inexistente.

O ID de seis dígitos não é segredo suficiente por si só. O modo não assistido exige senha forte; o modo assistido exige aprovação manual visível do host. O token de sessão é separado do ID e criado para cada conexão autorizada.

A evolução para acesso pela internet, NAT traversal, relay, heartbeat efetivo e encerramento explícito de hosts continua pendente e exige desenho próprio de segurança e operação.

## Build e distribuição

- Solution: `RemoteEternal.sln`.
- App: `net8.0-windows`, WPF, x64.
- Server e Core: `net8.0`.
- Publicação: `publish\app`, `publish\server`, `publish\IniciarServidor.bat` e `publish\IniciarApp.bat`.
- `publish\app\ffmpeg` contém as DLLs nativas necessárias para mídia.
- Firewall, portas e permissões devem ser validados em Windows x64.
