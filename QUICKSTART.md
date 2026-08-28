# Guia de Início Rápido — RemoteEternal 1.0.3

## Qual versão usar para testar?

### RECOMENDADO: **RemoteEternal-1.0.3-win-x64.zip** (75,58 MB)

Por que 1.0.3?
- FFmpeg nativo provisionado (funciona imediatamente)
- Tamanho menor (38% de economia vs versão anterior)
- Build limpo sem erros ou avisos  
- Todos os 25 testes passando

### Alternativa: RemoteEternal-1.0.2-win-x64.zip (140,65 MB)

Use se já tem esta versão disponível ou para comparar mudanças.

---

## Como testar

### Passo 1: Extrair o ZIP
Extraia em uma pasta sem espaços no nome: F:\remoteeternal-test\

### Passo 2: Preparar Ambiente

Máquina A (HOST + API):
1. Iniciar API Node.js na pasta api/: npm start
2. Executar RemoteEternal.exe como HOST (clique com botão direito → Administrador)
3. Configurar URL da API e modo de acesso

Máquina B (CLIENTE):
1. Executar RemoteEternal.exe como CLIENTE (Administrador)
2. Inserir ID do HOST e senha (se unassisted) ou conectar (assisted)

### Passo 3: Obter ID do HOST
Anote o ID de 6 dígitos exibido no app HOST (ex: A8F2B).

---

## O que validar

- Captura de vídeo no host → visível no cliente
- Transmissão de áudio sincronizada sem cortes
- Decodificação FFmpeg funcionando
- Input remoto com mouse e teclado
- Encerramento limpo ao desconectar

---

## Modos de acesso

Modo Assisted (recomendado): Aprovação manual de conexões
Modo Unassisted: Conexões automáticas com senha forte (min 12 chars)

---

## Solução rápida de problemas

- ScreenRecorderLib.dll não encontrada → extraia ZIP completo
- avcodec-62.dll não carregada → verifique ffmpeg/ com DLLs nativas
- Conexão WebSocket falhou → inicie API e verifique firewall na porta 3000
- Áudio não funcionando → execute como administrador

---

## Importante

1. Sempre execute como administrador para captura de tela e áudio
2. Use modo assisted para testes iniciais (mais seguro)  
3. Verifique firewall Windows — permita portas 3000 e 5050
4. Caminho sem espaços no nome da pasta

---

Pronto! Extraia o ZIP 1.0.3 e execute em ambas as máquinas.
