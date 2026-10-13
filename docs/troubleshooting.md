# Solução de problemas

| Sintoma | Causa provável | O que fazer |
|---|---|---|
| Admin: **Indisponível — porta fechada** | Agent não está aberto no PC | Abra o NexoDesk Agent (ícone na área de notificação) |
| Admin: **Indisponível — sem resposta em 4 s** | PC desligado, fora do Tailscale ou firewall | Confira o Tailscale nos dois lados (`tailscale status`) e a regra "NexoDesk Agent (Tailscale)" |
| Admin: **Nome não encontrado** | MagicDNS desligado ou nome errado | Use o IP 100.x do painel do Tailscale no campo `host` |
| Admin: **Certificado do computador remoto recusado** | Impressão no catálogo diferente da apresentada | Se o PC foi reinstalado, atualize `serverCertificateSha256`. Se não, **não conecte** e investigue |
| Admin: **Falha na autenticação TLS** | Certificado deste Admin não está em `allowedClients` do PC | Adicione a impressão do Admin no `agent.json` e clique em Reiniciar Agent |
| Admin: **Acesso recusado: ninguém respondeu** | Acesso não assistido desligado e ninguém clicou em Permitir | Peça a alguém no PC para aceitar, ou habilite `unattendedAccessEnabled` conscientemente |
| Admin: **Já existe uma sessão ativa** | Outro administrador está conectado | Aguarde ou peça para encerrar |
| Admin: **O computador remoto está bloqueado** | Windows na tela de bloqueio | Fora do escopo: o usuário precisa desbloquear |
| Admin: **Controle remoto ainda não está disponível** | Modo Controlar pedido antes do M5 | Conecte em Visualizar |
| Agent: **Nenhum IP Tailscale encontrado** | Tailscale fechado ou desconectado | Abra o Tailscale, entre na tailnet e clique em Tentar de novo |
| Agent: **certificado não encontrado** | Certificado criado em outra conta do Windows | Rode `New-NexoDeskCertificate.ps1` na conta que usa o PC |
| Agent: **porta em uso** | Outra cópia aberta ou outro programa na 47115 | Feche a outra cópia ou mude `port` (e o catálogo do Admin) |
| Agent: campo desconhecido no `agent.json` | Erro de digitação no nome de um campo | Corrija; campos desconhecidos são recusados de propósito |

## Onde estão os logs

- Agent: `%LocalAppData%\NexoDesk\agent-logs\audit-AAAA-MM-DD.jsonl`
- Admin: `%LocalAppData%\NexoDesk\admin-logs\audit-AAAA-MM-DD.jsonl`

Um evento por linha (JSON), com horário UTC. Os dois apps têm o link **Pasta de logs** no rodapé.
