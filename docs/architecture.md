# Arquitetura do NexoDesk

```text
PC ADMINISTRADOR (Windows)                 TAILSCALE (WireGuard)              PCs REMOTOS (2–5)
┌─────────────────────────┐                ┌──────────────────┐               ┌──────────────────────────┐
│ NexoDesk.Admin (WPF)    │── TCP + mTLS ─▶│ rede mesh privada │──────────────▶│ NexoDesk.Agent (WPF)     │
│  catálogo · sondagem    │                │ IP 100.x / MagicDNS│              │  sessão do usuário       │
│  AdminSession           │◀── frames ─────│ direto ou relay   │◀──────────────│  AgentHost · consentimento│
└─────────────────────────┘                └──────────────────┘               └──────────────────────────┘
```

## Projetos

| Projeto | Plataforma | Responsabilidade |
|---|---|---|
| `NexoDesk.Shared` | `net10.0` (independente de Windows) | protocolo, enquadramento, mensagens, máquina de estados, TLS mútuo com pinning, `AgentHost`, `AgentSession`, `AdminSession`, configuração, auditoria |
| `NexoDesk.Ui` | `net10.0-windows` (WPF) | Nexotool UI: tokens claro/escuro, estilos, `NxWindow`, `NxBadge`, `NxPill`, diálogo, `ThemeManager` |
| `NexoDesk.Admin` | `net10.0-windows` (WPF) | catálogo, sondagem, conexão, janela de sessão, auditoria local |
| `NexoDesk.Agent` | `net10.0-windows` (WPF + bandeja) | política de consentimento, indicador de sessão, ícone na área de notificação, integração com o Windows |
| `NexoDesk.Shared.Tests` | `net10.0` | enquadramento, mensagens, estados, certificados, configuração |
| `NexoDesk.IntegrationTests` | `net10.0` | sessões reais com TLS mútuo em loopback, `AgentHost` completo |

Toda a lógica de rede e segurança fica em `Shared` de propósito: roda e é testada em qualquer sistema, e os apps WPF ficam só com tela e integração com o Windows.

## Decisões

- **Agent na sessão do usuário, não serviço.** Serviços ficam na Session 0 e não veem o desktop do usuário.
- **Identidade = impressão do certificado**, não hostname nem IP. O nome no catálogo só diz onde conectar.
- **Uma sessão por máquina**, reservada antes de consultar a política (`AgentHost.GatedPolicy`).
- **Sem dependências NuGet nos apps.** MVVM mínimo próprio, JSON com geração de código do .NET; só os projetos de teste usam xUnit.
- **Frames: só o mais recente.** O Admin decodifica em segundo plano e descarta frames atrasados.

## Estado dos marcos

| Marco | Situação |
|---|---|
| M1 — Base | Pronto: solução, 4 projetos + 2 de testes, auditoria, CI (GitHub Actions, Windows) |
| M2 — Rede | Pronto: escuta no IP Tailscale, mTLS com pinning, autorização por certificado, handshake, Ping/Pong, uma sessão por PC, consentimento, indicador e corte local |
| M3 — Captura | Pendente: captura com Desktop Duplication (DXGI) e envio em JPEG; o formato `VideoFrame` e a exibição no Admin já estão prontos |
| M4 — Visualização | Parcial: janela de sessão com escala uniforme, descarte de frames atrasados, FPS medido |
| M5 — Controle | Pendente: `IInputSink` com `SendInput`; contrato, validação e `ReleaseAll` prontos |
| M6 — Operação | Parcial: catálogo, reconexão manual, logs, botão local; faltam ajustes de política pela tela |
| M7 — Validação | Pendente: testes entre redes diferentes, instalador |
