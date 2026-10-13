<#
.SYNOPSIS
  Prepara um PC para receber o NexoDesk Agent: configuração protegida, regra de firewall restrita ao Tailscale
  e (opcional) início automático na sessão do usuário.

.DESCRIPTION
  Rode em um PowerShell COMO ADMINISTRADOR. O Agent em si roda sem elevação, na sessão do usuário.

  O que o script faz:
    1. Cria %ProgramData%\NexoDesk\agent.json (se não existir, ou com -Force) com acesso não assistido DESLIGADO.
    2. Ajusta a permissão da pasta: administradores e SYSTEM podem alterar; usuários comuns só leem.
    3. Cria a regra de entrada no Firewall do Windows: apenas TCP na porta do Agent, apenas pela interface
       Tailscale e apenas de endereços 100.64.0.0/10 e fd7a:115c:a1e0::/48, apenas para o executável do Agent.
    4. Com -Autostart, cria um atalho na pasta Inicializar de todos os usuários (abre minimizado).

.EXAMPLE
  .\Setup-NexoDeskAgent.ps1 -MachineId pc-oficina -DisplayName "PC Oficina" `
      -ServerCertificateSha256 3F2A...C9 `
      -AllowedAdmin "Gustavo (notebook)=A1B2...EF" `
      -AgentExe "C:\Program Files\NexoDesk\NexoDesk.Agent.exe" -Autostart
#>
#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidatePattern('^[A-Za-z0-9._-]{1,64}$')] [string] $MachineId,
    [Parameter(Mandatory)] [string] $DisplayName,
    [Parameter(Mandatory)] [ValidatePattern('^[0-9A-Fa-f:\s]{64,95}$')] [string] $ServerCertificateSha256,
    [Parameter(Mandatory)] [string[]] $AllowedAdmin,
    [Parameter(Mandatory)] [string] $AgentExe,
    [ValidateRange(1024, 65535)] [int] $Port = 47115,
    [string] $ListenAddress = 'auto',
    [string] $TailscaleInterfaceAlias = 'Tailscale',
    [switch] $Autostart,
    [switch] $Force
)

$ErrorActionPreference = 'Stop'

function Normalize-Sha([string] $value) {
    $clean = ($value -replace '[:\s-]', '').ToUpperInvariant()
    if ($clean -notmatch '^[0-9A-F]{64}$') { throw "Impressão SHA-256 inválida: $value" }
    return $clean
}

if (-not (Test-Path $AgentExe)) { throw "Executável não encontrado: $AgentExe" }
$AgentExe = (Resolve-Path $AgentExe).Path

# ---------- 1. Configuração ----------
$dir = Join-Path $env:ProgramData 'NexoDesk'
$file = Join-Path $dir 'agent.json'
New-Item -ItemType Directory -Force -Path $dir | Out-Null

$clients = foreach ($entry in $AllowedAdmin) {
    $parts = $entry -split '=', 2
    if ($parts.Count -ne 2 -or [string]::IsNullOrWhiteSpace($parts[0])) { throw "Use -AllowedAdmin ""Nome=SHA256"". Recebido: $entry" }
    [ordered]@{ name = $parts[0].Trim(); sha256 = (Normalize-Sha $parts[1]) }
}

if ((Test-Path $file) -and -not $Force) {
    Write-Host "Mantido o arquivo existente: $file (use -Force para recriar)." -ForegroundColor Yellow
}
else {
    $config = [ordered]@{
        schemaVersion                 = 1
        machineId                     = $MachineId
        displayName                   = $DisplayName
        listenAddress                 = $ListenAddress
        port                          = $Port
        serverCertificateSha256       = (Normalize-Sha $ServerCertificateSha256)
        allowedClients                = @($clients)
        unattendedAccessEnabled       = $false
        consentTimeoutSeconds         = 30
        handshakeTimeoutSeconds       = 10
        idleTimeoutSeconds            = 30
        jpegQuality                   = 70
        maxFps                        = 12
        maxConnectionAttemptsPerMinute = 10
        allowNonTailscaleAddress      = $false
    }
    $json = $config | ConvertTo-Json -Depth 5
    [System.IO.File]::WriteAllText($file, $json, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host "Configuração gravada: $file" -ForegroundColor Green
}

# ---------- 2. Permissões: só administradores alteram a política ----------
$acl = New-Object System.Security.AccessControl.DirectorySecurity
$acl.SetAccessRuleProtection($true, $false)   # remove herança
$inherit = [System.Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
$none = [System.Security.AccessControl.PropagationFlags]::None
$allow = [System.Security.AccessControl.AccessControlType]::Allow
$acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule('*S-1-5-32-544', 'FullControl', $inherit, $none, $allow)))   # Administradores
$acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule('*S-1-5-18', 'FullControl', $inherit, $none, $allow)))      # SYSTEM
$acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule('*S-1-5-32-545', 'ReadAndExecute', $inherit, $none, $allow))) # Usuários
Set-Acl -Path $dir -AclObject $acl
Write-Host "Permissões ajustadas em $dir (usuários: somente leitura)." -ForegroundColor Green

# ---------- 3. Firewall: só Tailscale, só esta porta, só este programa ----------
$ruleName = 'NexoDesk Agent (Tailscale)'
Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule
$ruleArgs = @{
    DisplayName   = $ruleName
    Description   = 'Permite o NexoDesk Admin alcançar o Agent somente pela rede Tailscale.'
    Direction     = 'Inbound'
    Action        = 'Allow'
    Protocol      = 'TCP'
    LocalPort     = $Port
    RemoteAddress = @('100.64.0.0/10', 'fd7a:115c:a1e0::/48')
    Program       = $AgentExe
    Profile       = 'Any'
}
if (Get-NetAdapter -Name $TailscaleInterfaceAlias -ErrorAction SilentlyContinue) {
    $ruleArgs.InterfaceAlias = $TailscaleInterfaceAlias
}
else {
    Write-Warning "Interface '$TailscaleInterfaceAlias' não encontrada; a regra fica limitada só às faixas de IP do Tailscale. Confira com Get-NetAdapter."
}
New-NetFirewallRule @ruleArgs | Out-Null
Write-Host "Regra de firewall criada: $ruleName (TCP $Port)." -ForegroundColor Green

# ---------- 4. Início automático (opcional) ----------
if ($Autostart) {
    $startup = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\StartUp'
    $lnk = Join-Path $startup 'NexoDesk Agent.lnk'
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($lnk)
    $shortcut.TargetPath = $AgentExe
    $shortcut.Arguments = '--minimized'
    $shortcut.WorkingDirectory = Split-Path $AgentExe
    $shortcut.Description = 'NexoDesk Agent'
    $shortcut.Save()
    Write-Host "Início automático configurado: $lnk" -ForegroundColor Green
}

Write-Host ''
Write-Host 'Próximos passos:' -ForegroundColor Cyan
Write-Host '  1. Confira se o certificado do Agent está em Cert:\CurrentUser\My da conta que usa este PC.'
Write-Host '  2. Abra o NexoDesk Agent e verifique "Aguardando conexões".'
Write-Host '  3. Na política do Tailscale, permita somente as máquinas administradoras nesta porta (veja docs\security.md).'
