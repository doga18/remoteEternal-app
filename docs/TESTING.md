# FLUXO DE VALIDAÇÃO END-TO-END DO REMOTEETERNAL

## Requisitos Prévios

### Máquinas Necessárias (2 computadores)

**Máquina A (Host + API):**
- Windows 10/11 x64
- Node.js 18+ instalado no PATH
- PostgreSQL local ou Aiven configurado
- Pasta pi/ com configuração válida

**Máquina B (Cliente):**
- Windows 10/11 x64  
- Executável RemoteEternal.exe extraído do ZIP

---

## Passo a Passo

### 🟢 ETAPA 1: Inicializar API Node.js (na Máquina A)

`ash
cd api
npm install
npm start
`

**O que esperar:**
- Servidor de controle iniciado na porta 3000
- Endpoint /api/health respondendo
- WebSocket em /ws pronto para conexões do host

---

### 🟢 ETAPA 2: Registrar Host e Obter ID (na Máquina A)

**Opção 1: Via Interface Gráfica (Recomendado)**
1. Iniciar RemoteEternal.exe como **HOST** na Máquina A
2. Digitar URL da API (http://localhost:3000)
3. Escolher "Acessar como HOST"
4. Configurar modo de acesso (Assisted/Unassisted)

**Opção 2: Via Terminal**
`powershell
# Iniciar como HOST
.\dist\RemoteEternal.exe /:?HOST

# Ou diretamente com argumentos completos
.\dist\RemoteEternal.exe "/apiUrl:http://localhost:3000" "/accessMode:unassisted" "/password:SenhaFort@123"
`

**O que fazer:**
- App exibirá um ID de 6 dígitos (ex: A8F2B)
- **Anote este ID** para usar no cliente
- Se modo assisted: aguarde conexão do cliente e clique "Aceitar"

---

### 🟢 ETAPA 3: Inicializar App como CLIENTE (na Máquina B)

`powershell
cd dist\RemoteEternal
.\RemoteEternal.exe /:?CLIENT
`

**Configuração:**
1. Digitar URL da API (http://localhost:3000 ou IP do servidor)
2. Inserir ID do HOST (ex: A8F2B)
3. Se modo unassisted: digitar senha forte
4. Clique em "Conectar"

---

### 🟢 ETAPA 4: Captura e Transmissão

**O que observar no HOST (Máquina A):**
- Tela sendo capturada por ScreenRecorderLib
- Fluxo H.264 sendo transmitido via SecureFrameChannel
- Áudio sendo gravado com NAudio

**O que observar no CLIENTE (Máquina B):**
- Vídeo decodificado pelo FFmpeg nativo
- Áudio reproduzido pelo NAudio
- Sincronização A/V estável

---

### 🟢 ETAPA 5: Input Remoto

**Testar envio de eventos:**
1. No cliente, mover mouse e clicar em uma janela remota
2. No host, verificar se o movimento do mouse aparece
3. Digitar texto no cliente → aparecer no host
4. Testar atalhos de teclado (Windows + L para bloquear tela)

---

### 🟢 ETAPA 6: Encerramento Limpo

**Para desconectar:**
- No HOST: clique em "Parar acesso" ou feche a janela
- No CLIENTE: clique em "Desconectar" ou feche a janela

**O que acontece internamente:**
- Host libera listener na porta 5050
- Stream de mídia encerrado gracefully
- Cliente desconecta WebSocket e fecha SessionClient

---

## Verificações de Sucesso ✅

### ✅ Captura de Vídeo
- [ ] Fluxo vídeo visível no cliente sem artefatos
- [ ] Resolução correta (ex: 1920x1080)
- [ ] Sem travamentos ou buffering excessivo

### ✅ Transmissão de Áudio
- [ ] Áudio sincronizado com o vídeo
- [ ] Sem estalos ou cortes bruscos
- [ ] Volume adequado

### ✅ Input Remoto
- [ ] Mouse controlando janelas remotas
- [ ] Teclado enviando eventos corretamente
- [ ] Scroll e gestos funcionando

### ✅ Conectividade da API
- [ ] Conexão WebSocket estabelecida
- [ ] Mensagens de notificação recebidas
- [ ] Encerramento do listener após desconexão

### ✅ Sincronização A/V
- [ ] Áudio em sincronia com o vídeo
- [ ] Sem atrasos significativos
- [ ] Latência aceitável (< 50ms em LAN)

---

## Solução de Problemas Comuns

### 🔴 "ScreenRecorderLib.dll não encontrada"
**Solução:** Extraia o ZIP completo. A DLL está ao lado do executável, não embutida no single-file.

### 🔴 "avcodec-62.dll não carregada"  
**Solução:** Verifique que a pasta dist\RemoteEternal\ffmpeg está intacta com todas as 7 DLLs FFmpeg nativas.

### 🔴 "Conexão WebSocket falhou"
**Solução:** Verifique firewall na porta 3000 e certifique-se que a API Node.js está rodando.

### 🔴 "Áudio não funcionando"
**Solução:** Execute como administrador (necessário para permissão de áudio em Windows).

### 🔴 "Não há vídeo capturado"
**Solução:** Verifique se o app está rodando com privilégios administrativos e se ScreenRecorderLib está configurado corretamente.

---

## Configurações Opcionais Avançadas

### Modo Assisted (Mais Seguro)
`powershell
# No HOST, configure para aprovação manual de conexões
.\dist\RemoteEternal.exe "/accessMode:assisted"
`
- Cada conexão exige clique "Aceitar" no HOST antes de estabelecer stream
- Recomendado para uso produtivo

### Modo Unassisted (Conveniente)
`powershell
# Configure senha forte (minimo 12 caracteres, maiúscula, minúscula, número)
.\dist\RemoteEternal.exe "/accessMode:unassisted" "/password:S@fegraForte123"
`
- Conexões automáticas sem aprovação visual
- Use apenas em redes confiáveis

### Multi-Monitor
`powershell
# Selecionar monitor específico via argumento (0 = primeiro, 1 = segundo, etc.)
.\dist\RemoteEternal.exe "/monitorIndex:1"
`

---

## Métricas de Desempenho Esperadas (em Rede Local)

| Métrica | Alvo | Medido |
|---------|------|--------|
| Latência A/V | < 50ms | _____ ms |
| Taxa de quadros | 30 fps | ____ fps |
| Tamanho do pacote | ~1.2 MB/s | ____ MB/s |
| Perda de pacotes | 0% | _____ % |

---

## Checklists Finais

### ✅ Antes do Deploy Final
- [ ] Testar em pelo menos 3 pares de máquinas diferentes
- [ ] Validar em redes Wi-Fi e cabeadas
- [ ] Verificar funcionamento com múltiplos monitores
- [ ] Confirmar encerramento limpo ao fechar a janela
- [ ] Documentar qualquer comportamento inesperado

### ⚠️ Limitações Conhecidas
- Acesso pela internet requer NAT traversal ou relay (não implementado ainda)
- Firewall Windows deve permitir portas 3000 e 5050
- Executável exige privilégios administrativos para captura de tela e áudio

---

## Próximo Passo: Validação Real

Agora que a documentação está completa, execute o script alidate-endtoend.ps1 na Máquina A e teste manualmente em ambas as máquinas para confirmar todos os cenários acima.
