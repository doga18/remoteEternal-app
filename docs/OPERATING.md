# OPERATING.md

## Visão geral de operação

O produto tem dois componentes independentes:

- **API Node.js** (`api/`, repositório `remoteEternal-api`): plano de controle (Express + WebSocket + PostgreSQL). É o que substitui o antigo servidor TCP C#.
- **App Windows** (`RemoteEternal.App`, repositório `remoteEternal-app`): aplicação WPF que atua como host e cliente.

## Build do App (.NET 8)

Pré-requisito: .NET 8 SDK em Windows x64.

- Solution: `RemoteEternal.sln`.
- `RemoteEternal.App`: `net8.0-windows`, WPF, **x64 obrigatório**.
- `RemoteEternal.Core`: `net8.0`.
- App usa ScreenRecorderLib, NAudio e FFmpeg.AutoGen.

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
- A solution mapeia os projetos para a configuração de projeto `x64` em todas as plataformas de solução. Por isso `dotnet build RemoteEternal.sln` (padrão `Debug|Any CPU`) já compila o App como x64, sem argumentos adicionais.
- Para selecionar explicitamente a plataforma x64: `dotnet build RemoteEternal.sln -p:Platform=x64`.
- O `RemoteEternal.App.csproj` fixa `Platforms=x64`, `PlatformTarget=x64` e redireciona `AnyCPU` para `x64`; essas propriedades devem ser mantidas.

## Execução da API Node.js (plano de controle)

Pré-requisito: Node.js 18+ em qualquer máquina da rede (para produção, um serviço web como o Render).

```text
cd api
npm install
npm start        # ou npm run dev (recarrega em mudanças)
```

A API sobe na porta `PORT` (default `3000`) e expõe HTTP e WebSocket (`/ws`) na mesma porta.

### Banco de dados (PostgreSQL)

A API usa PostgreSQL. Dois formatos de conexão (use apenas um):

- **Formato Aiven** (produção, ex.: `remoteeternalapi` no Aiven): variáveis `DB_HOST`, `DB_PORT` (default `14673`), `DB_DATABASE` (default `remoteeternalapi`), `DB_USER`, `DB_PASS`, e o certificado CA em `api\config\aiven-ca.pem` (`ssl` com `rejectUnauthorized: true`). O CA é público (não é segredo) e pode ser versionado no repositório da API.
- **`DATABASE_URL`** (Postgres local de teste): `postgres://usuario:senha@localhost:5432/remoteeternal`. Sem certificado CA e sem `sslmode`, o SSL é desabilitado; com `sslmode=require`, usa `rejectUnauthorized: false`; com `sslmode=verify-ca`/`verify-full`, exige certificado.

A tabela `hosts` é criada no boot (`CREATE TABLE IF NOT EXISTS`). O pool é reduzido (**max 5, min 1**) porque o Aiven tem `max_connections=20`. Se `DATABASE_URL` e `DB_*` existirem juntos, `DATABASE_URL` tem prioridade.

Sem banco configurado a API sobe mesmo assim: `GET /api/health` responde e os endpoints de banco respondem `503`. Configure as variáveis antes de iniciar em produção.

## Execução do App

O App é distribuído como WPF Windows x64. Ao abrir, informe a **URL da API** (ex.: `http://localhost:3000`) e conecte. O App então pode atuar como host (obter ID, anunciar-se online, conectar o WebSocket do host e aguardar conexões) ou como cliente (consultar salt, `lookup` e conectar direto ao host).

## Scripts de início

- **`IniciarAPI.bat`** (ou `npm start` em `api/`): inicia o plano de controle Node.js. A janela deve permanecer aberta.
- **`IniciarApp.bat`**: inicia o App sem manter uma janela extra do terminal.

Os scripts de distribuição são tratados pela tarefa de release; este documento registra apenas o conceito de cada um.

## Dependência nativa FFmpeg

- `FfmpegLibrary.EnsureLoaded()` procura as DLLs em `<BaseDirectory>\ffmpeg` e as adiciona ao `PATH`.
- **Estado atual: PROVISIONADO.** As DLLs estão em `dist\RemoteEternal\ffmpeg`:
  - `avcodec-62.dll`, `avformat-62.dll` e `avdevice-62.dll` (major 62).
  - `avutil-60.dll` (major 60).
  - `swscale-9.dll` (major 9).
  - `swresample-6.dll` (major 6).
  - `avfilter-11.dll` (major 11).
- A origem do pacote é BtbN autobuild, FFmpeg 8.x, compatível com FFmpeg.AutoGen.

## Distribuição

- `dist\RemoteEternal\RemoteEternal.exe` é o App WPF.
- `dist\RemoteEternal\ScreenRecorderLib.dll` é a assembly mixed-mode C++/CLI e deve ficar ao lado do executável.
- `dist\RemoteEternal\ffmpeg\` contém as DLLs nativas necessárias para vídeo e áudio.
- A API é distribuída independentemente (repo `remoteEternal-api`), ex.: no Render como Web Service (`npm install` / `npm start`).
- O roteiro para iniciantes está em `docs\TESTANDO.md`.

## Firewall

- API (plano de controle): liberar a porta `3000` (ou `PORT`) na máquina que roda a API.
- Host: liberar a porta do listener da sessão direta (padrão `5050`).
- A porta `7000` do antigo servidor C# não é mais usada.
- Regras devem restringir ao perfil de rede adequado.

## Permissões

- ScreenRecorderLib exige sessão interativa no Windows; captura de tela em sessão de serviço não funciona.
- Host e cliente devem rodar com conta com acesso à área de trabalho e aos dispositivos de mídia.
