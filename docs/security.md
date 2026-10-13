# Segurança do NexoDesk

O NexoDesk dá acesso à tela (e, numa próxima versão, ao controle) de computadores da empresa. A defesa é em camadas: rede privada, firewall, identidade por computador, pareamento, permissão local e registro.

## Camadas

| Camada | O que garante | Onde |
|---|---|---|
| Tailscale (WireGuard) | Só computadores da rede da empresa se alcançam; tráfego cifrado; nenhuma porta aberta no roteador | Tailscale |
| Política do Tailscale | Só os ADMINs alcançam a porta 47115 dos PCs REMOTOS | painel do Tailscale (exemplo abaixo) |
| Firewall do Windows | Entrada só para o NexoDesk, só TCP 47115, só de 100.64.0.0/10 e fd7a:115c:a1e0::/48 | criada pelo instalador |
| Escuta restrita | O PC REMOTO escuta somente no IP Tailscale; 0.0.0.0/:: são recusados no código | `AgentHost`, `AppConfig.Validate` |
| Identidade por computador | Cada computador tem o seu certificado; não existe senha ou segredo compartilhado | `DeviceIdentity` |
| TLS mútuo com pinning | Depois do pareamento, cada lado só aceita a impressão SHA-256 que guardou do outro | `PinnedCertificateValidator` |
| Pareamento por código | Primeiro contato só com o código exibido no PC REMOTO; provas ligadas aos dois certificados | `PairingWindow`, `AdminPairing` |
| Autorização | Estar na rede Tailscale **não** dá acesso: o ADMIN precisa estar pareado naquele PC REMOTO | `ConfigClientStore` |
| Permissão local | Por padrão, cada acesso pede **Permitir** no PC REMOTO (recusa sozinho em 30 s) | `ConsentPolicy` |
| Aviso e corte local | Faixa fixa no topo da tela durante o acesso, com **Encerrar acesso** | `SessionIndicatorWindow` |
| Limites | Tamanhos máximos, um acesso por PC, prazos, 10 tentativas/min por IP, 3 erros por código | `Protocol`, `AgentHost`, `PairingWindow` |
| Registro | Cada tentativa, pareamento, decisão, início e fim | `JsonLinesAuditLog` |

## Identidade de cada computador

- Criada automaticamente na primeira abertura: certificado ECDSA P-256 autoassinado, válido por 2 anos, com uso de servidor e de cliente (o mesmo computador pode trocar de papel).
- A chave privada fica no repositório **Pessoal** do usuário do Windows, protegida pelo sistema e **não exportável**. Nunca sai do computador.
- A configuração guarda só **impressões SHA-256** (dados públicos).
- Validação: impressão conhecida, dentro da validade, uso correto, chave mínima P-256/RSA 2048. Erros de cadeia/nome são esperados com certificados autoassinados e são substituídos pelo pinning — o código nunca aceita "qualquer certificado". A única exceção controlada é durante um pareamento (abaixo).

## Pareamento

1. No PC REMOTO, **Gerar código** abre uma janela de 10 minutos com um código de 12 caracteres (60 bits). Uso único; fecha após 3 erros.
2. No ADMIN, **Adicionar PC REMOTO** com endereço + código.
3. Cada lado prova que conhece o código com um HMAC derivado por PBKDF2 (200 mil iterações) e **ligado às impressões dos dois certificados vistos na conexão**. Se houver alguém no meio, as impressões diferem e o pareamento falha nos dois lados.
4. Só então cada lado guarda a impressão do outro. Daí em diante, conexões sem pareamento são recusadas já no TLS.

Detalhes do protocolo em [protocol.md](protocol.md#pareamento). Cuidados: passe o código só para quem vai ser ADMIN e só enquanto ele estiver fazendo o pareamento.

## Remover acesso

- **No PC REMOTO:** ADMINS que podem acessar → **Remover**. Um acesso em andamento desse ADMIN é encerrado na hora.
- **No ADMIN:** **Remover** na linha do PC REMOTO (só tira da lista dele).
- Computador perdido ou roubado: remova-o em todos os PCs REMOTOS e também da rede no painel do Tailscale.

## Política do Tailscale (exemplo)

Remova a regra padrão que libera tudo e permita apenas o necessário, usando tags. A sintaxe do Tailscale muda com o tempo — confira a documentação atual antes de aplicar.

```json
{
  "tagOwners": {
    "tag:nexodesk-admin": ["autogroup:admin"],
    "tag:nexodesk-remoto": ["autogroup:admin"]
  },
  "grants": [
    {
      "src": ["tag:nexodesk-admin"],
      "dst": ["tag:nexodesk-remoto"],
      "ip":  ["tcp:47115"]
    }
  ]
}
```

## Arquivos

| Arquivo | Conteúdo |
|---|---|
| `%AppData%\NexoDesk\config.json` | papel, nome, pareamentos e opções (sem segredos), mantido pelo programa |
| `%LocalAppData%\NexoDesk\logs\audit-*.jsonl` | registro de acessos |
| Repositório *Pessoal* do Windows | identidade do computador (com a chave privada) |

**Decisão consciente:** a configuração do PC REMOTO fica no perfil do usuário, para que ele mesmo possa parear e remover ADMINs pela tela sem ser administrador do Windows. Quem usa o PC REMOTO é quem decide quem o acessa.

**Limitação conhecida:** o registro fica no perfil do usuário, que pode apagá-lo. Para auditoria à prova de adulteração, enviar os registros a um destino central (fase posterior).

## O que é registrado

Data/hora UTC, computador, nome e impressão do ADMIN, IP de origem, acesso, resultado, motivo e versão. **Nunca** imagens da tela, teclas digitadas, área de transferência, senhas ou chaves.

## Fora do escopo (deliberadamente)

Tela de login, tela bloqueada, UAC/desktop seguro, controle de programas elevados, acesso antes do logon. O PC REMOTO recusa com motivo claro em vez de tentar contornar o Windows.

## Menor privilégio

O programa roda **sem elevação** (`asInvoker`). Só o instalador pede administrador, para instalar em *Arquivos de Programas* e criar a regra de firewall. O instalador ainda **não é assinado** digitalmente (o Windows mostra um aviso); assinar é recomendado antes do uso amplo.
