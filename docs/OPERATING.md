# OPERATING.md

## Build

Pré-requisito: .NET 8 SDK em Windows x64.

- Solution: `RemoteEternal.sln`.
- `RemoteEternal.App`: `net8.0-windows`, WPF, **x64 obrigatório**.
- `RemoteEternal.Server` e `RemoteEternal.Core`: `net8.0`.
- Server usa LiteDB; App usa ScreenRecorderLib, NAudio e FFmpeg.AutoGen.

Build Debug (padrão):

```text
dotnet build RemoteEternal.sln
```

Build Release:

```text
dotnet build RemoteEternal.sln -c Release
```

### Plataforma x64

- O App **não pode** ser compilado como `AnyCPU`: o pacote `ScreenRecorderLib` 6.6.0 aborta o build com `ScreenRecorderLib does not work correctly on 'AnyCPU' platform. You need to specify platform (x86, Win32, x64 or ARM64).`
- A solution mapeia os três projetos para a configuração de projeto `x64` em todas as plataformas de solução (`Any CPU` e `x64`). Por isso `dotnet build RemoteEternal.sln` (padrão `Debug|Any CPU`) já compila o App como x64, sem argumentos adicionais.
- Para selecionar explicitamente a plataforma x64: `dotnet build RemoteEternal.sln -p:Platform=x64`.
- O `RemoteEternal.App.csproj` fixa `Platforms=x64`, `PlatformTarget=x64` e redireciona `AnyCPU` para `x64`; essas propriedades devem ser mantidas.

## Execução

### Servidor de controle

Porta padrão `7000`. Argumentos:

- `--port <porta>`: porta do listener.
- `--db <arquivo>`: caminho do banco LiteDB.
- `--no-register`: desabilita a criação de novos hosts.

O servidor responde pelo plano de controle por ID (`registerHost`, `hostOnline`, `getHostSalt`, `lookup`, `connectNotify`, `connectAck`, `ping`). Não transporta mídia.

### Host

O App atua como host após escolher o modo e iniciar o acesso. O host solicita um ID de seis dígitos ao servidor, anuncia-se online com `hostOnline` e inicia o listener da sessão direta na porta `5050`. No modo assistido, cada conexão exige aprovação manual; no modo não assistido, o cliente usa ID + senha.

### Cliente

O App atua como cliente: conecta ao servidor de controle, informa o ID do host (e a senha no modo não assistido), obtém o salt com `getHostSalt` e consulta o destino com `lookup`, conecta ao host usando o token de sessão e recebe vídeo/áudio e envia eventos de input.

### Dependência nativa FFmpeg

- `FfmpegLibrary.EnsureLoaded()` procura as DLLs em `<BaseDirectory>\ffmpeg` e as adiciona ao `PATH`.
- **Estado atual: PROVISIONADO.** As DLLs estão em `publish\app\ffmpeg`:
  - `avcodec-62.dll`, `avformat-62.dll` e `avdevice-62.dll` (major 62).
  - `avutil-60.dll` (major 60).
  - `swscale-9.dll` (major 9).
  - `swresample-6.dll` (major 6).
  - `avfilter-11.dll` (major 11).
- A origem do pacote é BtbN autobuild-2025-09-30, FFmpeg 8.x, compatível com FFmpeg.AutoGen 8.1.0.

## Distribuição

A pasta `publish` contém uma distribuição self-contained `win-x64` pronta para teste:

- `publish\app\RemoteEternal.exe` é o App WPF.
- `publish\app\ffmpeg\` contém as DLLs nativas necessárias para vídeo e áudio.
- `publish\server\RemoteEternal.Server.exe` é o servidor de controle.
- `publish\IniciarServidor.bat` inicia o servidor na porta `7000` e deve permanecer aberto.
- `publish\IniciarApp.bat` inicia o App sem manter uma janela extra do terminal.
- O roteiro para iniciantes está em `docs\TESTANDO.md`.

## Firewall

- Servidor de controle: liberar a porta do listener (padrão `7000`) na rede local.
- Host: liberar a porta do listener da sessão direta (padrão `5050`).
- O plano de controle e a sessão direta trafegam em TCP; regras devem restringir ao perfil de rede adequado.

## Permissões

- ScreenRecorderLib exige sessão interativa no Windows; captura de tela em sessão de serviço não funciona.
- Host e cliente devem rodar com conta com acesso à área de trabalho e aos dispositivos de mídia.
