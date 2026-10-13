<#
.SYNOPSIS
  Cria o certificado de identidade de um dispositivo NexoDesk (Agent ou Admin) no repositório Pessoal do usuário.

.DESCRIPTION
  Cada computador tem o SEU certificado (nunca um segredo compartilhado). A chave privada é ECDSA P-256,
  marcada como não exportável e fica protegida pelo Windows (DPAPI) no repositório Cert:\CurrentUser\My.
  O script mostra a impressão SHA-256, que é o que se copia para os arquivos de configuração:
    - Agent: serverCertificateSha256 no agent.json do próprio PC e no catálogo (admin.json) do administrador.
    - Admin: clientCertificateSha256 no admin.json e na lista allowedClients do agent.json de cada PC.

  Rode na conta do Windows que vai executar o NexoDesk (o Agent roda na sessão do usuário logado).

.PARAMETER Role
  Agent (servidor) ou Admin (cliente).

.PARAMETER Name
  Identificação curta do dispositivo, ex.: pc-oficina ou admin-gustavo.

.PARAMETER Months
  Validade em meses (padrão 6). Certificados curtos reduzem o impacto de um vazamento; renove antes de vencer.

.EXAMPLE
  .\New-NexoDeskCertificate.ps1 -Role Agent -Name pc-oficina
.EXAMPLE
  .\New-NexoDeskCertificate.ps1 -Role Admin -Name admin-gustavo -Months 12
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidateSet('Agent', 'Admin')] [string] $Role,
    [Parameter(Mandatory)] [ValidatePattern('^[A-Za-z0-9._-]{1,64}$')] [string] $Name,
    [ValidateRange(1, 24)] [int] $Months = 6
)

$ErrorActionPreference = 'Stop'

# 1.3.6.1.5.5.7.3.1 = autenticação de servidor; 1.3.6.1.5.5.7.3.2 = autenticação de cliente
$eku = if ($Role -eq 'Agent') { '1.3.6.1.5.5.7.3.1' } else { '1.3.6.1.5.5.7.3.2' }
$subject = "CN=NexoDesk $Role $Name, O=Nexotool"

$cert = New-SelfSignedCertificate `
    -Subject $subject `
    -FriendlyName "NexoDesk $Role ($Name)" `
    -CertStoreLocation 'Cert:\CurrentUser\My' `
    -KeyAlgorithm 'ECDSA_nistP256' `
    -HashAlgorithm 'SHA256' `
    -KeyUsage 'DigitalSignature' `
    -KeyExportPolicy 'NonExportable' `
    -NotBefore (Get-Date).AddMinutes(-5) `
    -NotAfter (Get-Date).AddMonths($Months) `
    -TextExtension @("2.5.29.37={text}$eku")

# SHA-256 do certificado (DER) — compatível com Windows PowerShell 5.1 e PowerShell 7
$sha = [System.Security.Cryptography.SHA256]::Create()
try {
    $fingerprint = -join ($sha.ComputeHash($cert.RawData) | ForEach-Object { $_.ToString('X2') })
}
finally {
    $sha.Dispose()
}

# Cópia pública (.cer) para registro/inventário — não contém a chave privada
$outDir = Join-Path $env:USERPROFILE 'Documents\NexoDesk'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$cerPath = Join-Path $outDir "nexodesk-$($Role.ToLower())-$Name.cer"
Export-Certificate -Cert $cert -FilePath $cerPath -Type CERT | Out-Null

Write-Host ''
Write-Host "Certificado criado: $subject" -ForegroundColor Green
Write-Host "Validade até:       $($cert.NotAfter.ToString('dd/MM/yyyy'))"
Write-Host "Repositório:        Cert:\CurrentUser\My\$($cert.Thumbprint)"
Write-Host "Cópia pública:      $cerPath"
Write-Host ''
Write-Host 'Impressão SHA-256 (copie para a configuração):' -ForegroundColor Cyan
Write-Host "  $fingerprint"
Write-Host ''
if ($Role -eq 'Agent') {
    Write-Host ('agent.json deste PC:   "serverCertificateSha256": "{0}"' -f $fingerprint)
    Write-Host ('admin.json (catálogo): {{ "id": "{0}", ..., "serverCertificateSha256": "{1}" }}' -f $Name, $fingerprint)
}
else {
    Write-Host ('admin.json deste PC:   "clientCertificateSha256": "{0}"' -f $fingerprint)
    Write-Host ('agent.json de cada PC: {{ "name": "{0}", "sha256": "{1}" }}' -f $Name, $fingerprint)
}
Write-Host ''
Write-Host 'Para revogar: remova a impressão das configurações e apague o certificado com' -ForegroundColor DarkGray
Write-Host "  Remove-Item Cert:\CurrentUser\My\$($cert.Thumbprint)" -ForegroundColor DarkGray
