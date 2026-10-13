# Solução de problemas

## No PC REMOTO

| Mensagem ou sintoma | O que fazer |
|---|---|
| **Nenhum IP Tailscale encontrado** / **Tailscale não está instalado** | Instale e abra o Tailscale, entre na rede da empresa e clique em **Tentar de novo** |
| **A porta 47115 está em uso** | Feche outra cópia do NexoDesk (ícone perto do relógio) e clique em **Tentar de novo** |
| O ADMIN diz que não conecta, mas aqui está *Pronto para o ADMIN* | Confira se o ADMIN usa o **Endereço deste PC REMOTO** exatamente como aparece aqui |
| O código de pareamento sumiu | Ele vale 10 minutos, uma vez, e é cancelado após 3 erros. Gere outro |
| Aparece *A identidade deste computador foi recriada* | O certificado do Windows sumiu (outra conta ou Windows reinstalado). Remova este PC no ADMIN e pareie de novo |
| Quero que o NexoDesk não abra com o Windows | **Opções → Abrir o NexoDesk junto com o Windows** (sem ele, o ADMIN só acessa depois que alguém abrir o programa) |

## No ADMIN

| Mensagem | Causa | O que fazer |
|---|---|---|
| **Indisponível — O NexoDesk não está aberto no PC REMOTO** | Programa fechado lá ou em modo ADMIN | Abra o NexoDesk no PC REMOTO |
| **Indisponível — Sem resposta** | PC desligado ou fora do Tailscale | Ligue o PC e confira o Tailscale nos dois lados |
| **Endereço não encontrado** | Nome digitado diferente | Use o endereço mostrado no PC REMOTO, ou o IP 100.x |
| **Código incorreto** | Código digitado errado | Confira as letras; após 3 erros gere outro no PC REMOTO |
| **O PC REMOTO recusou a conexão… código** | Código expirou ou não foi gerado | Gere um novo código no PC REMOTO |
| **O PC REMOTO não reconhece este ADMIN** | Este ADMIN foi removido lá (ou a identidade daqui mudou) | Remova o PC da sua lista e pareie de novo |
| **A identidade do PC REMOTO mudou desde o pareamento** | PC reinstalado — ou algo interceptando | Se você sabe que foi reinstalado, remova e pareie de novo. Se não, **não conecte** |
| **Acesso recusado: … recusou ou não respondeu** | Ninguém clicou em Permitir em 30 s | Peça para alguém no PC REMOTO aceitar |
| **Outro ADMIN já está acessando** | Só um acesso por vez | Aguarde |
| **O PC REMOTO está com a tela bloqueada** | Windows bloqueado lá | O usuário precisa desbloquear (o NexoDesk não acessa a tela de bloqueio) |
| **Controlar ainda não está disponível** | Recurso de versão futura | Use *Ver a tela* |

## Onde ficam as coisas

| O quê | Onde |
|---|---|
| Programa | `C:\Program Files\NexoDesk` |
| Configuração (mantida pelo programa) | `%AppData%\NexoDesk\config.json` |
| Registro de acessos | `%LocalAppData%\NexoDesk\logs\audit-AAAA-MM-DD.jsonl` (link **Pasta de registros**) |
| Identidade do computador | Repositório de certificados do usuário (*Pessoal*), nome "NexoDesk …" |
| Regra de firewall | "NexoDesk (Tailscale)" — TCP 47115, só endereços Tailscale |
