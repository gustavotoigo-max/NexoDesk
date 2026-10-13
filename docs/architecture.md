# Arquitetura do NexoDesk

```text
ADMIN (Windows)                            TAILSCALE (WireGuard)                PC REMOTO (até 5)
┌───────────────────────────┐              ┌───────────────────┐               ┌───────────────────────────┐
│ NexoDesk (modo ADMIN)     │── TCP+mTLS ─▶│ rede mesh privada │──────────────▶│ NexoDesk (modo PC REMOTO) │
│ lista · pareamento        │              │ IP 100.x/MagicDNS │               │ sessão do usuário         │
│ AdminSession              │◀── frames ───│ direto ou relay   │◀──────────────│ AgentHost · permissão     │
└───────────────────────────┘              └───────────────────┘               └───────────────────────────┘
          mesmo programa e mesmo instalador; o papel é escolhido na primeira abertura
```

## Projetos

| Projeto | Plataforma | Responsabilidade |
|---|---|---|
| `NexoDesk.App` | `net10.0-windows` (WPF) | o programa: escolha de papel, telas ADMIN e PC REMOTO, pareamento, sessões, bandeja, aviso de acesso, consentimento |
| `NexoDesk.Shared` | `net10.0` (independente de Windows) | protocolo, enquadramento, mensagens, máquina de estados, TLS mútuo com pinning, pareamento, `AgentHost`, `AgentSession`, `AdminSession`, `AdminPairing`, identidade, configuração, auditoria |
| `NexoDesk.Ui` | `net10.0-windows` (WPF) | Nexotool UI: tokens claro/escuro, estilos, `NxWindow`, `NxBadge`, `NxPill`, diálogo, `ThemeManager` |
| `NexoDesk.Shared.Tests` | `net10.0` | enquadramento, mensagens, estados, certificados, pareamento, configuração |
| `NexoDesk.IntegrationTests` | `net10.0` | sessões reais com TLS mútuo em loopback, `AgentHost`, pareamento completo, identidade no repositório de certificados |
| `installer/` | Inno Setup | instalador único, .NET embutido, regra de firewall restrita ao Tailscale |

Toda a lógica de rede e segurança fica em `Shared` de propósito: roda e é testada em qualquer sistema; o app fica só com tela e integração com o Windows.

## Decisões

- **Um programa, dois papéis.** O papel (ADMIN ou PC REMOTO) é escolhido na primeira abertura e pode ser trocado em Configurações.
- **PC REMOTO na sessão do usuário, não serviço.** Serviços ficam na Session 0 e não veem o desktop do usuário. Abre com o Windows (chave Run do usuário).
- **Pareamento por código** em vez de trocar impressões manualmente; a identidade de cada computador é criada pelo próprio programa.
- **Identidade = impressão do certificado**, não hostname nem IP. O nome no catálogo só diz onde conectar.
- **Uma sessão por máquina**, reservada antes de consultar a política (`AgentHost.GatedPolicy`).
- **Sem dependências NuGet no programa.** MVVM mínimo próprio, JSON com geração de código do .NET; só os projetos de teste usam xUnit.
- **Frames: só o mais recente.** O ADMIN decodifica em segundo plano e descarta frames atrasados.

## Estado dos marcos

| Marco | Situação |
|---|---|
| M1 — Base | Pronto: solução, testes, auditoria, CI no Windows |
| M2 — Rede | Pronto: escuta no IP Tailscale, mTLS com pinning, pareamento por código, handshake, Ping/Pong, um acesso por PC, consentimento, aviso e corte local |
| M3 — Captura | Pronto: captura GDI + JPEG com cursor desenhado; pula frames idênticos |
| M4 — Visualização | Pronto: janela de acesso com escala uniforme, descarte de frames atrasados, FPS medido |
| M5 — Controle | Pronto (sessão desbloqueada): mouse, roda e teclado via SendInput; libera teclas/botões ao fim |
| M6 — Operação | Pronto na interface: lista, pareamento, remoção, reconexão, opções, registros, bandeja, início com o Windows |
| M7 — Validação | Parcial: instalador pronto; faltam testes entre redes diferentes e assinatura digital |
