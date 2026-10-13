# NexoDesk

Acesso remoto privado para 2 a 5 computadores Windows da empresa, pela rede **Tailscale**, sem abrir portas no roteador. Um **Admin** vê (e, a partir do M5, controla) a tela de um PC onde roda o **Agent**, com autenticação mútua por certificado, consentimento local e auditoria.

Interface no padrão **Nexotool UI** (janela com barra azul-marinho, linha em degradê, cartões, tema claro/escuro automático).

> Estado: **M1 e M2 concluídos** — conexão segura, handshake, sessão, consentimento, indicador e auditoria funcionando. A imagem da tela chega no M3. Plano completo em [`docs/plano-desenvolvimento.md`](docs/plano-desenvolvimento.md).

## Requisitos

- Windows 10/11 x64 nos dois lados, com **Tailscale** conectado na mesma tailnet.
- [.NET 10 SDK](https://dotnet.microsoft.com/download) para compilar (ou o .NET 10 Desktop Runtime para só executar).

## Compilar e testar

```powershell
dotnet build NexoDesk.sln -c Release
dotnet test NexoDesk.sln -c Release
```

## Colocar para funcionar (2 PCs)

**No PC remoto (Agent)**, na conta de quem usa o PC:

```powershell
# 1. Identidade do PC (anote a impressão SHA-256 mostrada)
.\scripts\New-NexoDeskCertificate.ps1 -Role Agent -Name pc-oficina
```

**No PC do administrador (Admin):**

```powershell
# 2. Identidade do administrador (anote a impressão)
.\scripts\New-NexoDeskCertificate.ps1 -Role Admin -Name admin-gustavo
```

**De volta no PC remoto**, em PowerShell **como administrador**:

```powershell
# 3. Política, permissões e firewall restrito ao Tailscale
.\scripts\Setup-NexoDeskAgent.ps1 -MachineId pc-oficina -DisplayName "PC Oficina" `
    -ServerCertificateSha256 <impressão do passo 1> `
    -AllowedAdmin "Gustavo=<impressão do passo 2>" `
    -AgentExe "C:\NexoDesk\Agent\NexoDesk.Agent.exe" -Autostart
```

4. Abra o **NexoDesk Agent**: deve mostrar *Aguardando conexões* e o IP Tailscale.
5. Abra o **NexoDesk Admin**: na primeira vez ele cria `%AppData%\NexoDesk\admin.json`. Clique em **Abrir configuração**, preencha `clientCertificateSha256` (passo 2) e adicione o PC em `machines` com `host` (MagicDNS ou IP 100.x) e `serverCertificateSha256` (passo 1). Clique em **Recarregar**.
6. **Conectar** → no PC remoto aparece *Permitir acesso remoto?* → **Permitir**. A faixa *Acesso remoto ativo* fica no topo da tela até alguém clicar em **Encerrar acesso**.

Ajuste também a política do Tailscale para só os administradores alcançarem a porta 47115 — exemplo em [`docs/security.md`](docs/security.md).

## Estrutura

```text
NexoDesk.sln
├── src/
│   ├── NexoDesk.Shared/   protocolo, TLS mútuo, sessões, AgentHost, configuração, auditoria (sem Windows)
│   ├── NexoDesk.Ui/       Nexotool UI para WPF: tokens, estilos, NxWindow, diálogos, tema
│   ├── NexoDesk.Admin/    aplicativo do administrador
│   └── NexoDesk.Agent/    aplicativo do PC remoto (bandeja, consentimento, indicador)
├── tests/
│   ├── NexoDesk.Shared.Tests/       protocolo, estados, certificados, configuração
│   └── NexoDesk.IntegrationTests/   sessões reais com TLS mútuo em loopback
├── scripts/   certificados e instalação do Agent
└── docs/      architecture, protocol, security, troubleshooting
```

## Documentação

- [Arquitetura e marcos](docs/architecture.md)
- [Protocolo v1](docs/protocol.md)
- [Segurança, certificados e revogação](docs/security.md)
- [Solução de problemas](docs/troubleshooting.md)
