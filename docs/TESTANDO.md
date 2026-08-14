# TESTANDO o RemoteEternal

## 1. Entenda o novo modelo

O computador controlado é o host e recebe um ID de seis números. O computador que controla é o cliente: ele digita esse ID e, no modo não assistido, também informa a senha. No modo assistido, o host precisa aprovar manualmente cada solicitação.

O servidor de controle usa a porta `7000`. A sessão direta usa a porta `5050` no host.

## 2. O que você precisa

- Dois computadores Windows x64 na mesma rede local para o teste completo.
- A pasta `publish` completa, sem separar `app` de `server`.
- Permissão para liberar os programas no Firewall do Windows.
- O IP do computador que executará o servidor.

## 3. Teste completo com dois computadores

Neste roteiro, o computador A é controlado e o computador B controla.

1. No computador A, execute `publish\IniciarServidor.bat` e deixe a janela aberta.
2. Ainda no computador A, execute `publish\IniciarApp.bat`.
3. No computador B, execute `publish\IniciarApp.bat`.
4. Nos Apps de A e B, preencha **Servidor** com o IP de A e **Porta** com `7000`. Clique em **Conectar servidor** nos dois computadores.
5. Em A, no painel HOST, escolha **Assistido** ou **Não assistido**. Se escolher Não assistido, defina uma senha forte.
6. Em A, clique em **Iniciar acesso**. O ID de seis números aparecerá na tela.
7. Em B, no painel CLIENTE, digite o ID de A e, se o modo for Não assistido, a senha. Clique em **Conectar**.
8. Se o modo for Assistido, em A aparecerá uma solicitação como **Conexão solicitada por [cliente]. Permitir acesso?**. Clique em **Sim**.
9. Em B, a janela de visualização abrirá com a tela de A. Selecione o monitor e o áudio, se desejado; mouse, teclado e áudio devem funcionar.
10. Para encerrar, em B clique em **Desconectar**. Em A clique em **Parar acesso** ou feche o App.

## 4. Teste com um computador

1. Execute `publish\IniciarServidor.bat` e deixe-o aberto.
2. Execute `publish\IniciarApp.bat`.
3. Use `127.0.0.1` no campo Servidor e `7000` na Porta; clique em **Conectar servidor**.
4. Escolha um modo de host e clique em **Iniciar acesso**.

Esse teste valida o servidor, a conexão e o registro do host; o ID deve aparecer. Sem um segundo dispositivo, não existe conexão remota real nem visualização completa.

## 5. Firewall do Windows

Permita o acesso quando o Windows perguntar. Libere TCP `7000` no computador do servidor e TCP `5050` no computador host, de preferência apenas no perfil de rede local.

## 6. Problemas comuns

| Problema | Solução |
| --- | --- |
| Não conecta no servidor | Confira IP, porta `7000`, servidor em execução e firewall. |
| ID não encontrado ou offline | O host pode não ter iniciado o acesso, pode estar parado ou os Apps podem usar servidores diferentes. |
| Senha incorreta | Confirme a senha do modo Não assistido. |
| Muitas tentativas | Aguarde 60 segundos; o servidor aplica proteção anti brute force. |
| Tela preta | Confira as 7 DLLs em `publish\app\ffmpeg`. |
| Áudio não funciona | Marque ou desmarque **Áudio** na janela de visualização e confira o dispositivo de áudio do host. |

## 7. Onde está a configuração

O App salva dados em `%APPDATA%\RemoteEternal`. O arquivo `host.id` guarda o ID do host e `config.txt` guarda a configuração do servidor e da porta. A senha nunca é persistida.
