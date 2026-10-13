# NexoDesk

Acesso remoto privado para 2 a 5 computadores Windows da empresa, pela rede **Tailscale**, sem abrir portas no roteador. O **PC admin** vê (e, a partir do M5, controla) a tela de um **PC remoto**, com autenticação mútua por certificado, consentimento local e auditoria.

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

## Colocar para funcionar

Passo a passo completo, do zero até a primeira conexão entre o **PC admin** (roda o Admin) e um **PC remoto** (roda o Agent): [`docs/tutorial.md`](docs/tutorial.md).

Resumo:

1. **PC remoto:** `New-NexoDeskCertificate.ps1 -Role Agent` e anote a impressão.
2. **PC admin:** `New-NexoDeskCertificate.ps1 -Role Admin` e anote a impressão.
3. **PC remoto (como administrador):** `Setup-NexoDeskAgent.ps1` com as duas impressões; abra o Agent.
4. **PC admin:** abra o Admin, preencha `admin.json` (impressão do PC admin e o PC remoto com a impressão dele), **Recarregar**, **Conectar**.
5. **PC remoto:** clique em **Permitir**.

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

- [Tutorial: primeira conexão](docs/tutorial.md)
- [Arquitetura e marcos](docs/architecture.md)
- [Protocolo v1](docs/protocol.md)
- [Segurança, certificados e revogação](docs/security.md)
- [Solução de problemas](docs/troubleshooting.md)
