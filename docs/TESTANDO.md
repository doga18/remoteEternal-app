# TESTANDO o RemoteEternal

## 1. Entenda o novo modelo

O computador controlado é o host e recebe um ID de seis números. O computador que controla é o cliente: ele digita esse ID e, no modo não assistido, também informa a senha. No modo assistido, o host precisa aprovar manualmente cada solicitação.

O plano de controle agora é uma **API Node.js** (Express + WebSocket + PostgreSQL). O App não usa mais o servidor TCP C#. Nos dois computadores, o App tem um campo **URL da API** que deve apontar para a API (ex.: `http://localhost:3000`).

A sessão direta usa a porta `5050` no host. A porta `7000` do antigo servidor C# não é mais usada.

## 2. O que você precisa

- Dois computadores Windows x64 na mesma rede local para o teste completo.
- A API Node.js em execução em uma máquina acessível pela rede (veja abaixo).
- O App publicado (pasta `dist\RemoteEternal`) nos dois computadores.
- Permissão para liberar os programas no Firewall do Windows.
- O IP da máquina que executa a API, quando ela estiver na LAN.
- Conectividade direta do cliente ao IPv4 anunciado pelo host na porta TCP `5050`; a API não transporta a sessão direta.

## 3. Onde rodar a API

Para teste local, a API pode rodar em **qualquer máquina da rede** com Node.js 18+ instalado (ex.: o computador A):

```text
cd api
npm install
npm start        # ou npm run dev
```

A API sobe na porta `3000` e conecta ao PostgreSQL (Aiven ou local) via `DATABASE_URL` ou variáveis `DB_*` — veja `api\README.md` e `api\.env.example`. Sem banco configurado a API sobe, mas os endpoints de hosts respondem `503`; para um teste completo, configure o banco antes.

Nos dois Apps, informe a mesma **URL da API**: `http://IP-de-A:3000` (ou `http://localhost:3000` se a API rodar na mesma máquina do App). Clique em **Conectar servidor** nos dois computadores.

## 4. Teste completo com dois computadores

Neste roteiro, o computador A é controlado e o computador B controla. O listener direto de A usa a porta TCP `5050`; o cliente B precisa alcançar diretamente o IPv4 que A anuncia em `advertisedAddress`. Esse endereço é usado apenas para roteamento, enquanto senha ou aprovação continuam responsáveis pela autorização.

1. No computador A, inicie a API (ou em qualquer máquina da rede) com `npm start`.
2. No computador A, execute `publish\IniciarApp.bat`; no computador B, execute o mesmo `publish\IniciarApp.bat`.
3. Nos Apps de A e B, preencha **URL da API** com `http://IP-da-api:3000` e clique em **Conectar servidor**.
4. Em A, no painel HOST, escolha **Assistido** ou **Não assistido**. Se escolher Não assistido, defina uma senha forte.
5. Em A, clique em **Iniciar acesso**. O ID de seis números aparecerá na tela.
6. Em B, no painel CLIENTE, digite o ID de A e, se o modo for Não assistido, a senha. Clique em **Conectar**.
7. Se o modo for Assistido, em A aparecerá uma solicitação como **Conexão solicitada por [cliente]. Permitir acesso?**. Clique em **Sim**.
8. Em B, a janela de visualização abrirá com a tela de A. Selecione o monitor e o áudio, se desejado; mouse, teclado e áudio devem funcionar.
9. Para encerrar, em B clique em **Desconectar**. Em A clique em **Parar acesso** ou feche o App.

## 5. Cenário via Render

1. Publique a API no Render e informe nos dois Apps a URL HTTPS da API.
2. No host A, inicie o acesso e confirme que o listener TCP `5050` está ativo; o App resolve o IPv4 local pela rota até a API e envia esse valor como `advertisedAddress` antes de anunciar-se online.
3. No cliente B, faça o lookup pelo ID e confirme que B consegue abrir uma conexão TCP direta ao IPv4 anunciado de A na porta `5050`.
4. A API coordena o lookup e a autorização, mas não faz relay da sessão, vídeo ou áudio. Se A e B estiverem fora da mesma LAN, configure port forwarding para a porta `5050` ou use uma solução futura de relay/NAT traversal; sem isso, o teste pode falhar mesmo com a API respondendo.
5. Não use o IP observado pelo Render como endereço da sessão: ele serve apenas para contexto e rate limit. Se não houver `advertisedAddress`, o fallback legado pode devolver um endereço de proxy/NAT inalcançável.

## 6. Teste com um computador

1. Inicie a API localmente (`cd api && npm start`).
2. Execute `publish\IniciarApp.bat`.
3. Use `http://localhost:3000` no campo **URL da API** e clique em **Conectar servidor**.
4. Escolha um modo de host e clique em **Iniciar acesso**.

Esse teste valida a API, a conexão e o registro do host; o ID deve aparecer. Sem um segundo dispositivo, não existe conexão remota real nem visualização completa.

## 7. Verificação de atualização

Ao conectar, o App consulta `GET /api/update/latest?currentVersion=X`. A API usa o catálogo `RELEASES`, com manifest real contendo `version`, `url`, `sizeBytes`, `sha256`, `fileCount` e `notes`; a release 1.0.0 está hospedada no GitHub Releases em `https://github.com/doga18/remoteEternal-app/releases/download/v1.0.0/RemoteEternal-1.0.0-win-x64.zip`, com `sizeBytes=151199803`, `fileCount=480` e SHA-256 `8168a0f7adf120b437d4bdd38b7c455d11f18c92d4bf23ffb59af0216bf95ee3`. Quando há atualização, o App apenas informa a versão, o número de arquivos, o tamanho aproximado e a URL para download; ele não baixa o arquivo automaticamente. Sem atualização, nada é exibido.

## 8. Firewall do Windows

Permita o acesso quando o Windows perguntar. Libere TCP `3000` na máquina que roda a API quando ela estiver na LAN e TCP `5050` no computador host. O cliente deve alcançar diretamente a porta `5050` no IPv4 anunciado pelo host; com API no Render, liberar apenas a API não substitui essa conectividade. Restrinja as regras ao perfil de rede local quando aplicável. A porta `7000` não é mais usada.

## 9. Problemas comuns

| Problema | Solução |
| --- | --- |
| Não conecta na API | Confira a **URL da API** (ex.: `http://IP:3000`), a API em execução (`npm start`) e o firewall na porta `3000`. |
| A API responde 503 | Banco não configurado ou indisponível; configure `DATABASE_URL` ou as variáveis `DB_*` (ver `api\README.md`). |
| ID não encontrado ou offline | O host pode não ter iniciado o acesso, pode estar parado ou os Apps podem usar APIs diferentes. |
| Lookup funciona, mas a sessão não abre | Confira se o cliente alcança diretamente o IPv4 anunciado em `advertisedAddress` na porta TCP `5050`; o Render não faz relay da sessão e o fallback legado pode anunciar proxy/NAT. |
| Senha incorreta | Confirme a senha do modo Não assistido. |
| Muitas tentativas | Aguarde 60 segundos; a API aplica proteção anti brute force por IP. |
| Tela preta | Confira as DLLs em `dist\RemoteEternal\ffmpeg`. |
| Áudio não funciona | Marque ou desmarque **Áudio** na janela de visualização e confira o dispositivo de áudio do host. |

## 10. Onde está a configuração

O App salva dados em `%APPDATA%\RemoteEternal`. O arquivo `host.id` guarda o ID do host e `config.txt` guarda a URL da API (`apiUrl`) e a porta do listener. A senha nunca é persistida.