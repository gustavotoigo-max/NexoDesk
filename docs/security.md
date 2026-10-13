# Segurança do NexoDesk

O NexoDesk dá acesso à tela (e, a partir do M5, ao controle) de computadores da empresa. A defesa é em camadas: rede privada, firewall, identidade por dispositivo, política local e auditoria.

## Camadas

| Camada | O que garante | Onde |
|---|---|---|
| Tailscale (WireGuard) | Só dispositivos da tailnet alcançam os PCs; tráfego cifrado; sem porta aberta no roteador | política da tailnet |
| Política do Tailscale | Só as máquinas administradoras alcançam a porta 47115 dos Agents | ACL/grants (abaixo) |
| Firewall do Windows | Porta do Agent aceita só pela interface Tailscale, só de 100.64.0.0/10 e fd7a:115c:a1e0::/48, só para o executável do Agent | `scripts/Setup-NexoDeskAgent.ps1` |
| Escuta restrita | O Agent escuta somente no IP Tailscale; `0.0.0.0`/`::` são recusados no código | `AgentHost`, `AgentConfig.Validate` |
| TLS mútuo com pinning | Cada lado só aceita o certificado cuja impressão SHA-256 está na sua lista | `PinnedCertificateValidator` |
| Autorização por identidade | Estar na tailnet **não** dá acesso: o certificado do Admin precisa estar em `allowedClients` daquele PC | `agent.json` |
| Consentimento local | Com acesso não assistido desligado (padrão), quem está no PC precisa clicar em **Permitir** | `ConsentPolicy` |
| Indicador e corte local | Faixa fixa no topo da tela durante o acesso, com **Encerrar acesso** | `SessionIndicatorWindow` |
| Limites anti-abuso | Tamanhos máximos, uma sessão por PC, prazos, 10 tentativas/min por IP, 4 negociações simultâneas | `Protocol`, `AgentHost` |
| Auditoria | Registro de cada tentativa, autenticação, decisão, início e fim | `JsonLinesAuditLog` |

## Certificados (identidade por dispositivo)

- Cada PC e cada administrador tem **o seu** certificado (ECDSA P-256, autoassinado, chave **não exportável** no repositório Pessoal do Windows, protegida por DPAPI). Não existe segredo compartilhado.
- Os arquivos de configuração guardam apenas **impressões SHA-256** (dados públicos), nunca chaves privadas.
- Validação: impressão na lista, dentro da validade, uso estendido correto (servidor para o Agent, cliente para o Admin), chave mínima P-256/RSA 2048. Erros de cadeia/nome são esperados com certificados autoassinados e são substituídos pelo pinning — o código nunca aceita "qualquer certificado".
- Validade padrão de 6 meses (`New-NexoDeskCertificate.ps1 -Months`).

### Revogar ou trocar um certificado

1. Remova a impressão de `allowedClients` em todos os `agent.json` (Admin perdido) ou do catálogo `admin.json` (Agent comprometido).
2. Clique em **Reiniciar Agent** (ou reinicie o Admin) para recarregar.
3. Apague o certificado antigo: `Remove-Item Cert:\CurrentUser\My\<thumbprint>`.
4. Gere um novo com `New-NexoDeskCertificate.ps1` e cadastre a nova impressão.
5. Se um dispositivo foi perdido, remova-o também da tailnet no painel do Tailscale.

## Política do Tailscale (exemplo)

Remova a regra padrão que libera tudo e permita apenas o necessário. Use tags para as máquinas. A sintaxe do Tailscale muda com o tempo — confira a documentação atual antes de aplicar.

```json
{
  "tagOwners": {
    "tag:nexodesk-admin": ["autogroup:admin"],
    "tag:nexodesk-agent": ["autogroup:admin"]
  },
  "grants": [
    {
      "src": ["tag:nexodesk-admin"],
      "dst": ["tag:nexodesk-agent"],
      "ip":  ["tcp:47115"]
    }
  ]
}
```

## Arquivos e permissões

| Arquivo | Conteúdo | Proteção |
|---|---|---|
| `%ProgramData%\NexoDesk\agent.json` | política do Agent (sem segredos) | só administradores alteram; usuários leem |
| `%AppData%\NexoDesk\admin.json` | identidade e catálogo do Admin (sem segredos) | perfil do usuário |
| `%LocalAppData%\NexoDesk\agent-logs\audit-*.jsonl` | auditoria do Agent | perfil do usuário |
| `%LocalAppData%\NexoDesk\admin-logs\audit-*.jsonl` | auditoria do Admin | perfil do usuário |

**Limitação conhecida:** os logs do Agent ficam no perfil do usuário do PC, que pode apagá-los. Para auditoria resistente a adulteração, envie os logs a um destino central (fase posterior).

## O que é registrado

Data/hora UTC, máquina, nome e impressão do administrador, IP de origem, sessão, resultado, motivo e versão. **Nunca** imagens da tela, teclas digitadas, área de transferência, senhas ou chaves.

## Fora do escopo (deliberadamente)

Tela de login, tela bloqueada, UAC/desktop seguro, controle de processos elevados, acesso antes do logon. O Agent recusa com "sessão indisponível" em vez de tentar contornar o Windows. Qualquer extensão privilegiada exige novo projeto de segurança.

## Menor privilégio

Admin e Agent rodam **sem elevação** (`asInvoker` no manifesto). Só o script de instalação do Agent pede administrador, para gravar a política e a regra de firewall.
