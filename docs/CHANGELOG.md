1: # CHANGELOG.md
2: 
3: ## 2026-08-20 — Provisionamento FFmpeg e distribuição final
4: 
5: - **FFmpeg provisionado**: 7 DLLs nativas copiadas para dist\RemoteEternal\ffmpeg: avcodec-62.dll, avformat-62.dll, avfilter-11.dll, avutil-60.dll, swscale-9.dll, swresample-6.dll, avdevice-62.dll
6: - **Distribuição criada**: dist\RemoteEternal-1.0.3-win-x64.zip (75,58 MB compactado, 38% da capacidade original)
7: - **Estrutura completa**: 
8:   - RemoteEternal.exe com runtime .NET 8 incluído
9:   - ScreenRecorderLib.dll ao lado do executável (não embutido no single-file)
10:  - NAudio assemblies para áudio Windows
11:  - FFmpeg.AutoGen.dll para bindings C
12:  - RemoteEternal.Core.dll com contratos e criptografia
13:  - ffmpeg\ com 7 DLLs nativas + README.md
14:  - config.json de configuração inicial
15:  - README.md com instruções de uso
16: 
17: ## Próximo passo crítico: Validação manual end-to-end
18: 
19: O MVP agora está **pronto para validação ponta-a-ponta** em ambiente real:
20: 
21: ### Requisitos para teste
22: - 2 máquinas Windows x64 na mesma rede (ou via NAT forwarding)
23: - API Node.js rodando na máquina 1 (porta 3000, PostgreSQL)
24: - App RemoteEternal.exe nas duas máquinas
25: - Executar como administrador (necessário para captura de tela e áudio)
26: 
27: ### Cenário de teste
28: 
29: **Máquina 1 (API + Host):**
30: 1. Iniciar API Node.js (
pm start ou via PM2/systemd)
31: 2. Iniciar App RemoteEternal.exe como HOST
32: 3. Configurar API URL e modo de acesso (assisted/unassisted)
33: 4. Registrar HOST na API e obter ID de 6 dígitos
34: 5. Anunciar o HOST online com advertisedAddress do próprio IPv4 local
35: 
36: **Máquina 2 (Cliente):**
37: 1. Iniciar App RemoteEternal.exe como CLIENTE
38: 2. Configurar a mesma URL da API
39: 3. Insirir o ID do HOST e senha (se unassisted)
40: 4. Conectar e selecionar monitor
41: 
42: ### O que validar
43: - [ ] Conexão WebSocket com a API estabelecida
44: - [ ] Captura de vídeo no host funcionando
45: - [ ] Transmissão de fluxo para o cliente sem artefatos
46: - [ ] Decodificação de vídeo no cliente (FFmpeg nativo)
47: - [ ] Reprodução de áudio bidirecional
48: - [ ] Input remoto (mouse/teclado) enviado do cliente
49: - [ ] Encerramento limpo ao desconectar
50: 
51: ## Estado atual do MVP
52: 
53: - **Build**: ✅ Compilando sem erros ou avisos
54: - **Testes automatizados**: ✅ 25 testes passando (13 C# + 12 Node.js)
55: - **Provisionamento FFmpeg**: ✅ DLLs nativas distribuídas
56: - **Distribuição final**: ✅ ZIP self-contained criado
57: - **Validação manual**: ⏳ Pendente (primeiro teste ponta-a-ponta)
58: 
59: ## Notas de segurança
60: 
61: - Execute sempre como administrador para captura e áudio
62: - Use modo assisted para aprovação manual de conexões
63: - Não exponha IDs de host ou senhas publicamente
64: - Firewall Windows deve permitir portas 3000 (API) e 5050 (sessão direta)
## 2026-08-20 — Validação End-to-End

### Scripts e documentação criados

- **Script de validação**: alidate-endtoend.ps1 — Fluxo automático de teste em 2 máquinas
- **Documentação completa**: docs\TESTING.md — Guia passo a passo para cenários de teste
- **Configuração da API**: Pasta pi\config com .env e certificado CA configurados

### Status atual

✅ Provisionamento FFmpeg concluído  
✅ Distribuição self-contained criada (1.0.3-win-x64.zip)  
✅ Build limpo sem erros ou avisos  
✅ 25 testes automatizados passando  
⏳ Validação manual end-to-end pendente  

### Próximos passos

1. Iniciar API Node.js na pasta pi/ (
pm start)
2. Testar fluxo ponta-a-ponta com 2 máquinas
3. Validar captura, streaming de vídeo e áudio
4. Confirmar input remoto e encerramento limpo
