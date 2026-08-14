# Assinatura de código do RemoteEternal no Windows

## 1. Objetivo e motivo do bloqueio

Este documento define o processo canônico para assinar e distribuir o RemoteEternal com Authenticode.

O Windows Smart App Control (SAC) pode bloquear `dist\RemoteEternal\RemoteEternal.exe` quando o executável não possui uma assinatura válida de um editor verificável. A distribuição atual é uma **pasta self-contained, Windows x64**, com `RemoteEternal.exe`, `ScreenRecorderLib.dll` e as DLLs nativas FFmpeg em `ffmpeg\`. Não é uma distribuição single-file e não há extração de FFmpeg em runtime.

Quando o serviço de segurança do SAC não consegue formar uma avaliação positiva sobre um aplicativo, ele verifica sua assinatura. Um arquivo sem assinatura válida pode ser considerado não confiável e bloqueado. Para distribuição pública, use uma identidade de assinatura publicamente confiável. Um certificado self-signed não resolve esse cenário.

O SAC aceita certificados digitais baseados em **RSA** para este fluxo e atualmente não aceita ECC. Portanto, selecione sempre certificado/perfil **RSA Code Signing**.

> Nunca armazene no repositório ou neste documento PFX, senha, chave privada, token, segredo de cliente ou qualquer outra credencial.

## 2. Opções de assinatura

| Opção | Confiança pública | Custódia da chave | Uso indicado | Limitações |
| --- | --- | --- | --- | --- |
| **Microsoft Artifact Signing** (antigo Trusted Signing) | Sim, com perfil **Public Trust** aprovado | Gerenciada pelo serviço em HSM | **Recomendado** para publicação pública e automação | Exige assinatura Azure, disponibilidade regional/organizacional, validação de identidade e permissões |
| **Certificado público RSA Code Signing OV/EV** de CA confiável | Sim | Token/HSM, serviço remoto ou PFX quando permitido pela CA | Publicação pública quando Artifact Signing não for adequado | Compra, validação OV/EV, renovação e proteção operacional da chave; requisitos da CA podem variar |
| **Certificado corporativo privado RSA** | Somente nos dispositivos que confiam na raiz corporativa | PKI/token/HSM corporativo | Máquinas gerenciadas por GPO/MDM/WDAC com a raiz instalada | Não serve para distribuição pública geral; exige administração da cadeia de confiança |
| **Certificado self-signed RSA** | Não | Local | Laboratório isolado e testes controlados | Não torna o editor publicamente verificável e não resolve a distribuição pública/SAC por si só |

OV e EV estabelecem uma identidade pública após validação da CA. A política de emissão, a forma de entrega e a proteção obrigatória da chave devem ser confirmadas com a CA escolhida. Não suponha que EV seja necessário apenas para obter uma assinatura válida; escolha conforme requisitos de identidade, custódia, operação e reputação.

## 3. Decisões e pré-requisitos

Antes da implementação:

- [ ] Definir o **nome jurídico** que será validado e aparecerá como editor.
- [ ] Confirmar documentos, domínio, contatos e autoridade interna necessários à validação de identidade.
- [ ] Escolher **Microsoft Artifact Signing/Public Trust** ou uma **CA pública OV/EV**.
- [ ] Confirmar que o certificado ou certificate profile de Code Signing usa **RSA**, não ECC.
- [ ] Instalar uma versão atual do **Windows SDK** que forneça o `signtool.exe` x64.
- [ ] Fixar e registrar a versão do Windows SDK/SignTool usada no release.
- [ ] Selecionar o endpoint **RFC 3161** oficial indicado pelo serviço ou pela CA escolhida.
- [ ] Definir onde a assinatura ocorrerá: estação Windows controlada, runner self-hosted ou CI hospedada.
- [ ] Definir autenticação sem segredo estático quando possível: workload identity/OIDC e privilégio mínimo.
- [ ] Definir proteção da chave: Artifact Signing, token, HSM ou provider da CA; PFX somente quando permitido e protegido.
- [ ] Definir versão de release antes do publish; o estado atual é `Version 1.0.0`.
- [ ] Revisar metadados: `Product` e `Company` estão no default `RemoteEternal`; sem certificado, `Publisher` está vazio.
- [ ] Confirmar que o subject validado do certificado contém o editor jurídico desejado.

O **Publisher exibido pelo Windows vem do subject da assinatura/certificado**, e não apenas de `<Company>` no projeto. Ajustar `<Company>` sem assinar não cria um editor verificável.

## 4. Caminho recomendado A — Microsoft Artifact Signing

Artifact Signing é o nome atual do serviço antes chamado Trusted Signing. O caminho recomendado para distribuição pública é um certificate profile **Public Trust**.

Os nomes de telas, comandos, extensões, endpoints, regiões, SKUs e integrações do Azure podem mudar. Consulte sempre a [documentação oficial do Artifact Signing](https://learn.microsoft.com/azure/artifact-signing/overview) e as instruções atuais de integração antes de automatizar. Não reutilize IDs de exemplos como valores reais.

### 4.1 Provisionamento de alto nível

1. Confirmar que a assinatura Azure, o tenant, a região e a organização atendem aos requisitos atuais do serviço.
2. Criar ou selecionar um resource group.
3. Criar uma conta do Artifact Signing.
4. Iniciar e concluir a validação da identidade jurídica.
5. Criar um certificate profile do tipo **Public Trust**, com identidade e algoritmo RSA adequados.
6. Conceder à identidade que executará o release somente a função/permissão atual necessária para assinar com essa conta e perfil.
7. Registrar fora do código os nomes da conta e do perfil e o endpoint da região.
8. Configurar a ferramenta/extensão de assinatura indicada pela documentação vigente e validar primeiro em um artefato de teste descartável.
9. Aplicar a mesma ordem de pipeline descrita na seção 7 e verificar o resultado com PowerShell e SignTool.

### 4.2 Placeholders seguros

Use parâmetros explícitos, nunca valores reais no Git:

```text
<ARTIFACT_SIGNING_ACCOUNT_NAME>
<ARTIFACT_SIGNING_CERTIFICATE_PROFILE_NAME>
<ARTIFACT_SIGNING_ENDPOINT>
<AZURE_TENANT_ID>
<AZURE_CLIENT_ID>
<AZURE_SUBSCRIPTION_ID>
<TIMESTAMP_RFC3161_URL>
```

Nomes de conta, profile e endpoint não são chaves privadas, mas devem ser configurados por ambiente para evitar acoplamento e exposição desnecessária. Tenant, client e subscription IDs também devem vir da configuração do ambiente/CI, não de valores inventados neste guia.

Para CI, prefira **workload identity federation/OIDC** com credenciais temporárias e escopo mínimo. Se a integração vigente exigir secrets, use o cofre do provedor de CI, aplique rotação, restrinja ambientes e aprovações e nunca grave valores no Git, em logs ou em artefatos.

## 5. Caminho B — certificado público RSA Code Signing OV/EV

### 5.1 Aquisição e preparação

1. Escolher uma CA publicamente confiável que emita certificado **RSA Code Signing OV ou EV** compatível com Authenticode e SAC.
2. Concluir a validação jurídica e operacional exigida pela CA.
3. Escolher custódia segura da chave conforme as opções e políticas da CA: token criptográfico, HSM, serviço/provider remoto ou PFX protegido.
4. Obter da CA o endpoint oficial de timestamp **RFC 3161** e a cadeia necessária.
5. Instalar o middleware/provider do token ou HSM no Windows de assinatura, quando aplicável.
6. Tornar o certificado disponível ao SignTool no repositório `Cert:\CurrentUser\My` ou `Cert:\LocalMachine\My`, ou configurar o provider/HSM conforme a documentação do fornecedor.
7. Confirmar EKU de Code Signing, cadeia válida, RSA, acesso autorizado à chave privada e subject correto.

Token, HSM ou provider de assinatura é preferível a PFX porque reduz a exposição e exportação da chave privada. Nunca copie um PFX para o repositório ou diretório de artefatos.

### 5.2 Assinatura pelo repositório de certificados

Defina os valores apenas na sessão segura do processo de release:

```powershell
$ErrorActionPreference = 'Stop'
$SignTool = '<SIGNTOOL_EXE_PATH>'
$CertificateThumbprint = $env:RE_CODE_SIGNING_THUMBPRINT
$TimestampUrl = $env:RE_TIMESTAMP_RFC3161_URL
$Exe = 'F:\NodeJs\remoteEternal\dist\RemoteEternal\RemoteEternal.exe'

if ([string]::IsNullOrWhiteSpace($CertificateThumbprint)) { throw 'RE_CODE_SIGNING_THUMBPRINT não definido.' }
if ([string]::IsNullOrWhiteSpace($TimestampUrl)) { throw 'RE_TIMESTAMP_RFC3161_URL não definido.' }

& $SignTool sign /sha1 $CertificateThumbprint /fd SHA256 /tr $TimestampUrl /td SHA256 /v $Exe
if ($LASTEXITCODE -ne 0) { throw "SignTool falhou com código $LASTEXITCODE." }
```

O nome da opção `/sha1` refere-se ao thumbprint usado para selecionar o certificado; o digest do arquivo e o timestamp permanecem SHA-256 por causa de `/fd SHA256` e `/td SHA256`. Acrescente `/sm` somente quando o certificado estiver no repositório da máquina (`LocalMachine`), após confirmar a configuração.

### 5.3 Assinatura por PFX, somente quando necessária

O PFX deve permanecer fora da árvore do repositório, em armazenamento temporário protegido, com ACL restrita e remoção segura após o job. Os valores abaixo vêm do ambiente; não os grave em scripts versionados:

```powershell
$ErrorActionPreference = 'Stop'
$SignTool = '<SIGNTOOL_EXE_PATH>'
$PfxPath = $env:RE_CODE_SIGNING_PFX_PATH
$PfxPassword = $env:RE_CODE_SIGNING_PFX_PASSWORD
$TimestampUrl = $env:RE_TIMESTAMP_RFC3161_URL
$Exe = 'F:\NodeJs\remoteEternal\dist\RemoteEternal\RemoteEternal.exe'

if ([string]::IsNullOrWhiteSpace($PfxPath)) { throw 'RE_CODE_SIGNING_PFX_PATH não definido.' }
if ([string]::IsNullOrWhiteSpace($PfxPassword)) { throw 'RE_CODE_SIGNING_PFX_PASSWORD não definido.' }
if ([string]::IsNullOrWhiteSpace($TimestampUrl)) { throw 'RE_TIMESTAMP_RFC3161_URL não definido.' }

& $SignTool sign /f $PfxPath /p $PfxPassword /fd SHA256 /tr $TimestampUrl /td SHA256 /v $Exe
if ($LASTEXITCODE -ne 0) { throw "SignTool falhou com código $LASTEXITCODE." }
```

Mesmo vindo de variável de ambiente, passar a senha por `/p` pode expô-la na linha de comando, lista de processos, telemetria ou logs do runner. Não habilite tracing. Prefira token/HSM/provider ou uma integração que não passe a senha na linha de comando.

## 6. Comandos PowerShell parametrizados

Os comandos desta seção são um roteiro para execução futura; **não foram executados por esta alteração documental**. Execute a partir de `F:\NodeJs\remoteEternal` em PowerShell 7+, depois de preencher somente via ambiente os parâmetros sensíveis.

### 6.1 Preparar caminhos temporários e localizar o SignTool

```powershell
$ErrorActionPreference = 'Stop'
$RepoRoot = 'F:\NodeJs\remoteEternal'
$Project = Join-Path $RepoRoot 'src\RemoteEternal.App\RemoteEternal.App.csproj'
$DistRoot = Join-Path $RepoRoot 'dist\RemoteEternal'
$WorkRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("RemoteEternal-signing-{0}" -f [guid]::NewGuid().ToString('N'))
$FinalExe = Join-Path $DistRoot 'RemoteEternal.exe'
$ScreenRecorderDll = Join-Path $DistRoot 'ScreenRecorderLib.dll'
$FfmpegRoot = Join-Path $DistRoot 'ffmpeg'
$FinalZip = Join-Path $RepoRoot 'dist\RemoteEternal-1.0.0-win-x64.zip'
$HashFile = Join-Path $RepoRoot 'dist\RemoteEternal-1.0.0-win-x64.sha256.txt'

$SignTool = Get-ChildItem -Path ${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe -File |
    Sort-Object { [version]$_.Directory.Parent.Name } -Descending |
    Select-Object -First 1 -ExpandProperty FullName

if (-not $SignTool) { throw 'signtool.exe x64 não encontrado. Instale o Windows SDK.' }
& $SignTool /?
```

Fixe `$SignTool` para a versão aprovada no processo de release em vez de aceitar silenciosamente uma atualização do SDK.

### 6.2 Verificar a assinatura atual

```powershell
$CurrentExe = Join-Path $RepoRoot 'dist\RemoteEternal\RemoteEternal.exe'
if (Test-Path -LiteralPath $CurrentExe) {
    Get-AuthenticodeSignature -LiteralPath $CurrentExe | Format-List Status, StatusMessage, SignerCertificate, TimeStamperCertificate, Path
    & $SignTool verify /pa /all /v $CurrentExe
}
```

No estado descrito neste documento, espera-se que o executável atual não tenha um editor verificável. Essa inspeção não altera o arquivo.

### 6.3 Verificar os arquivos PE da distribuição

A distribuição atual já é uma pasta. Os artefatos PE a assinar são `dist\RemoteEternal\RemoteEternal.exe`, `dist\RemoteEternal\ScreenRecorderLib.dll` e exatamente as sete DLLs nativas FFmpeg em `dist\RemoteEternal\ffmpeg`. Não altere `libs\ffmpeg` nem gere um bundle; valide os nomes e assine as cópias finais da pasta de distribuição.

### 6.4 Assinar e verificar as DLLs da distribuição

```powershell
$ApprovedFfmpegDllNames = @(
    'avcodec-62.dll', 'avdevice-62.dll', 'avfilter-11.dll',
    'avformat-62.dll', 'avutil-60.dll', 'swresample-6.dll', 'swscale-9.dll'
)
$FfmpegDlls = @($ApprovedFfmpegDllNames | ForEach-Object { Join-Path $FfmpegRoot $_ })
$PeFiles = @($FinalExe, $ScreenRecorderDll) + $FfmpegDlls

if (-not (Test-Path -LiteralPath $FinalExe -PathType Leaf)) { throw 'RemoteEternal.exe ausente.' }
if (-not (Test-Path -LiteralPath $ScreenRecorderDll -PathType Leaf)) { throw 'ScreenRecorderLib.dll ausente.' }
if (@($FfmpegDlls | Where-Object { -not (Test-Path -LiteralPath $_ -PathType Leaf) }).Count -ne 0) { throw 'DLL FFmpeg ausente.' }

foreach ($PeFile in $PeFiles) {
    & $SignTool sign /sha1 $CertificateThumbprint /fd SHA256 /tr $TimestampUrl /td SHA256 /v $PeFile
    if ($LASTEXITCODE -ne 0) { throw "Falha ao assinar $PeFile." }
    & $SignTool verify /pa /all /v $PeFile
    if ($LASTEXITCODE -ne 0) { throw "Falha ao verificar $PeFile." }
}
```

### 6.5 Assinar o executável e as DLLs com SHA-256 e timestamp RFC 3161

A assinatura ocorre sobre os arquivos PE já presentes em `dist\RemoteEternal`. O ZIP só deve ser criado depois de todas as assinaturas e verificações.

```powershell
$CertificateThumbprint = $env:RE_CODE_SIGNING_THUMBPRINT
$TimestampUrl = $env:RE_TIMESTAMP_RFC3161_URL

if ([string]::IsNullOrWhiteSpace($CertificateThumbprint)) { throw 'RE_CODE_SIGNING_THUMBPRINT não definido.' }
if ([string]::IsNullOrWhiteSpace($TimestampUrl)) { throw 'RE_TIMESTAMP_RFC3161_URL não definido.' }

foreach ($PeFile in $PeFiles) {
    & $SignTool sign /sha1 $CertificateThumbprint /fd SHA256 /tr $TimestampUrl /td SHA256 /v $PeFile
    if ($LASTEXITCODE -ne 0) { throw "Falha ao assinar $PeFile." }
}
```

`<TIMESTAMP_RFC3161_URL>` ou `RE_TIMESTAMP_RFC3161_URL` é um placeholder. Use o endpoint RFC 3161 oficial publicado pela CA ou pelo serviço escolhido. Exemplos de fornecedores encontrados em documentação podem mudar e **não são uma exigência do RemoteEternal**; valide o endpoint diretamente na documentação oficial da opção contratada.

### 6.6 Verificar assinatura e timestamp

```powershell
foreach ($PeFile in $PeFiles) {
    $Signature = Get-AuthenticodeSignature -LiteralPath $PeFile
    $Signature | Format-List Status, StatusMessage, SignerCertificate, TimeStamperCertificate, Path
    if ($Signature.Status -ne 'Valid') { throw "Authenticode inválido: $PeFile" }
    if ($null -eq $Signature.TimeStamperCertificate) { throw "Timestamp Authenticode ausente: $PeFile" }
    & $SignTool verify /pa /all /v $PeFile
    if ($LASTEXITCODE -ne 0) { throw "Verificação SignTool falhou: $PeFile" }
}
```

Confira manualmente no resultado o subject/publisher, a cadeia, o digest SHA-256 e o timestamp. Um exit code zero isolado não substitui essas verificações.

### 6.7 Empacotar somente depois da assinatura e gerar hashes

```powershell
Remove-Item -LiteralPath $FinalZip -Force -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $DistRoot '*') -DestinationPath $FinalZip -CompressionLevel Optimal

$ZipHash = Get-FileHash -LiteralPath $FinalZip -Algorithm SHA256
$PeHashes = @($PeFiles | ForEach-Object { Get-FileHash -LiteralPath $_ -Algorithm SHA256 })
@(
    $PeHashes | ForEach-Object { "$($_.Hash)  $($_.Path)" }
    "$($ZipHash.Hash)  $([System.IO.Path]::GetFileName($FinalZip))"
) | Set-Content -LiteralPath $HashFile -Encoding utf8NoBOM

$PeHashes
$ZipHash
```

Não assine o ZIP: assine os PEs antes e preserve os arquivos assinados dentro da pasta e do ZIP. Extraia o ZIP em outro diretório e repita as verificações sobre os arquivos PE extraídos antes de publicar.

## 7. Ordem obrigatória do pipeline de release

1. Compilar/publicar a distribuição self-contained em `dist\RemoteEternal`.
2. Confirmar `RemoteEternal.exe`, `ScreenRecorderLib.dll` ao lado do executável e exatamente as sete DLLs FFmpeg em `dist\RemoteEternal\ffmpeg`.
3. Assinar e verificar todos os arquivos PE da pasta com Authenticode, digest SHA-256 e timestamp RFC 3161 SHA-256.
4. Gerar o ZIP **somente depois** da assinatura.
5. Gerar e publicar hashes SHA-256 dos arquivos PE e do ZIP.
6. Extrair o ZIP e verificar novamente os arquivos PE extraídos.
7. Testar em uma máquina limpa/representativa com Windows 11 e Smart App Control ativo.
8. Executar um teste funcional de captura, decodificação e reprodução usando as DLLs da pasta.

Qualquer alteração de um arquivo PE após a assinatura invalida a assinatura. Mudança de versão ou de arquivos da distribuição exige novo publish e nova assinatura.

## 8. Consideração específica das DLLs FFmpeg

As sete DLLs FFmpeg são arquivos PE separados em `dist\RemoteEternal\ffmpeg`. SAC, Windows Defender Application Control (WDAC), antivírus ou políticas corporativas podem avaliá-las no carregamento. Assine e verifique as cópias finais da pasta antes de criar o ZIP; não há extração em runtime nem FFmpeg embutido.

O teste de aceite deve confirmar a execução do EXE e captura/decodificação/reprodução suficiente para carregar todas as bibliotecas nativas requeridas. Nunca assine nem modifique os arquivos originais em `libs\ffmpeg`; a assinatura deve ocorrer nos artefatos da distribuição.

## 9. GitHub Actions e CI

A automação deve conter conceitualmente os seguintes passos, sem codificar valores específicos do ambiente:

1. Runner Windows aprovado e fixação das versões de .NET SDK e Windows SDK.
2. Checkout sem persistir credenciais desnecessárias.
3. Restauração/build e preparação da pasta `dist\RemoteEternal`.
4. Autenticação federada OIDC/workload identity no Azure, ou acesso protegido ao provider/HSM da CA.
5. Assinatura e verificação de todos os arquivos PE da distribuição.
6. Verificação obrigatória de Authenticode, publisher e timestamp.
7. Criação do ZIP e hashes somente após a verificação.
8. Upload dos artefatos assinados mediante ambiente protegido/aprovação.

GitHub-hosted runners Windows podem usar Artifact Signing conforme a integração vigente. Confirme na documentação atual quais action/extensão, permissões, regiões e parâmetros são suportados; não copie um workflow antigo sem revisão.

Para Artifact Signing, prefira OIDC e conceda à identidade do job somente a permissão de assinatura no account/profile correto. Proteja o environment de release com revisores e restrições de branch/tag. Para CA/token/HSM, use a integração oficial e um runner apropriado. Secrets, quando inevitáveis, devem ficar no GitHub Environments/Secrets, ser mascarados, rotacionados e nunca impressos. Não salve PFX como artifact, cache, base64 em YAML ou secret material dentro do ZIP.

## 10. Critérios de aceite

Uma release está pronta somente quando:

- [ ] `Get-AuthenticodeSignature` retorna `Status: Valid` para os arquivos PE finais e para os arquivos PE extraídos do ZIP.
- [ ] `signtool verify /pa /all /v` termina com sucesso para todos os arquivos PE.
- [ ] A assinatura usa RSA e digest SHA-256.
- [ ] O timestamp RFC 3161 está presente e usa SHA-256.
- [ ] O publisher exibido corresponde ao nome jurídico aprovado no subject do certificado.
- [ ] A versão publicada corresponde à versão planejada, não permanece acidentalmente em `1.0.0`.
- [ ] O ZIP foi criado depois da assinatura e contém os mesmos arquivos PE assinados verificados.
- [ ] Os hashes SHA-256 dos arquivos PE e ZIP foram gerados e conferidos.
- [ ] O EXE não é bloqueado em Windows 11 com SAC ativo.
- [ ] As sete DLLs FFmpeg são carregadas da pasta sem bloqueio SAC/WDAC.
- [ ] Nenhum PFX, senha, chave, token ou credencial aparece no repositório, logs ou artefatos.

## 11. Troubleshooting

### `Get-AuthenticodeSignature` não retorna `Valid`

- Confirme que o arquivo verificado é exatamente o arquivo assinado.
- Confira validade, EKU Code Signing, cadeia e revogação do certificado.
- Confirme confiança pública ou, em ambiente corporativo privado, instalação da cadeia raiz/intermediária.
- Verifique se algum passo alterou o EXE após a assinatura.
- Rode `signtool verify /pa /all /v` e examine cadeia, política e todas as assinaturas.

### Timestamp ausente ou inválido

- Use `/tr <TIMESTAMP_RFC3161_URL> /td SHA256` no mesmo comando de assinatura.
- Confirme que o endpoint é o RFC 3161 oficial da CA/serviço e está acessível pelo runner.
- Não publique se o timestamp falhar, mesmo que uma assinatura sem timestamp tenha sido criada.

### Publisher incorreto ou vazio

- Examine `SignerCertificate.Subject`.
- Confirme a identidade validada no Artifact Signing ou pela CA.
- Não tente corrigir o Publisher alterando apenas `<Company>`; o Windows usa o subject da assinatura para identificar o editor.

### EXE ou DLL assinado, mas bloqueado

- Confirme quais arquivos PE foram assinados e verificados na pasta de distribuição.
- Verifique individualmente as cópias com Authenticode e logs SAC/WDAC autorizados.

### Smart App Control versus SmartScreen

**Smart App Control** aplica uma decisão de confiança/assinatura e política de execução no Windows 11. **Microsoft Defender SmartScreen** também considera reputação de arquivo, download, URL e editor; uma assinatura válida não garante reputação imediata nem elimina todos os avisos do SmartScreen. Diagnostique qual componente apresentou a mensagem antes de agir.

Não desative o SAC como solução de distribuição. Corrija assinatura, cadeia, timestamp, publisher e assinatura das DLLs quando necessário. Para laboratório, uma política controlada pode ajudar no diagnóstico, mas não substitui o aceite com SAC ativo.

## 12. Links oficiais

- [Assinatura para conformidade com Smart App Control](https://learn.microsoft.com/windows/apps/develop/smart-app-control/code-signing-for-smart-app-control)
- [Microsoft Artifact Signing — visão geral](https://learn.microsoft.com/azure/artifact-signing/overview)
- [SignTool — referência oficial](https://learn.microsoft.com/windows/win32/seccrypto/signtool)
- [Smart App Control — perguntas frequentes (Microsoft Support)](https://support.microsoft.com/windows/smart-app-control-frequently-asked-questions-285ea03d-fa88-4d56-882e-6698afdb7003)

## 13. Checklist final copiável

```text
[ ] Nome jurídico/editor aprovado
[ ] Artifact Signing Public Trust ou CA pública OV/EV escolhido
[ ] Certificado/perfil RSA Code Signing confirmado; ECC rejeitado para SAC
[ ] Identidade validada e subject/publisher conferido
[ ] Windows SDK/SignTool instalado e versão fixada
[ ] Endpoint RFC 3161 oficial da opção escolhida configurado por ambiente
[ ] Local/CI e autenticação definidos; OIDC/workload identity preferido
[ ] Nenhum PFX, senha, chave, token ou credencial no Git, logs ou artefatos
[ ] Versão de release definida e metadados revisados
[ ] Distribuição em pasta self-contained win-x64 preparada
[ ] `RemoteEternal.exe` e `ScreenRecorderLib.dll` ao lado do executável
[ ] Exatamente sete DLLs FFmpeg em `dist\RemoteEternal\ffmpeg`
[ ] Arquivos PE da distribuição assinados com /fd SHA256 /tr RFC3161 /td SHA256
[ ] Get-AuthenticodeSignature retorna Valid e timestamp presente para cada PE
[ ] signtool verify /pa /all /v retorna sucesso para cada PE
[ ] Publisher corresponde ao subject jurídico aprovado
[ ] ZIP criado somente após a assinatura
[ ] Arquivos PE extraídos do ZIP continuam com assinatura Valid
[ ] Hashes SHA-256 dos arquivos PE e ZIP gerados e conferidos
[ ] Windows 11 com Smart App Control ativo não bloqueia o EXE nem as DLLs
[ ] DLLs FFmpeg carregadas no teste funcional
[ ] Materiais temporários removidos ao final
```
