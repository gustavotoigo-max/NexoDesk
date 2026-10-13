# NexoDesk — Resumo consolidado

Acesso remoto privado para 2 a 5 PCs Windows da empresa, pela rede **Tailscale**, sem abrir portas no roteador. Um único programa e um único instalador para os dois papéis:

- **ADMIN** — o computador de onde você acessa (ex.: sua casa).
- **PC REMOTO** — o computador que é acessado (ex.: a central da empresa).

Pilha: **C#/.NET 10 + WPF**, layout **Nexotool UI** (barra azul-marinho, degradê, cartões, tema claro/escuro). Repositório: `gustavotoigo-max/NexoDesk`. Instalador publicado a cada versão em **Releases**.

---

## O que já está pronto e consolidado

| Área | Estado |
|---|---|
| Instalador único (ADMIN e PC REMOTO) | ✅ Inno Setup, self-contained (não precisa instalar .NET), firewall restrito ao Tailscale |
| Escolha do papel na 1ª abertura | ✅ Tela "Como este computador vai ser usado?" |
| Código de acesso único | ✅ Um código (`NXD1-…`) já leva **endereço + pareamento**; o ADMIN cola em um campo só |
| Pareamento seguro | ✅ mTLS com fixação de certificado (SHA-256), prova PBKDF2 + HMAC ligada aos dois certificados |
| Imagem da tela ao vivo | ✅ Captura GDI + JPEG, pula frames idênticos |
| Controle de mouse e teclado | ✅ SendInput (mouse absoluto, teclado por scan code) |
| Ver o ponteiro do PC REMOTO | ✅ Enviado à parte dos frames e desenhado por cima (com opção de ligar/desligar) |
| Atualização pela interface | ✅ Baixa e instala a nova versão direto do app |
| Início com o Windows + bandeja | ✅ PC REMOTO fica no ícone do relógio |

### Correções recentes
- **Processo não fechava / não reabria** (precisava do Gerenciador): encerramento determinístico (libera o mutex e `Environment.Exit`) e instância única tolerante a processo anterior morto. — v0.7.0
- **Delay enorme do mouse**: envio coalescente de movimentos (só o mais recente; cliques/teclas nunca descartados). — v0.8.0
- **Qualidade e FPS**: ponteiro desacoplado dos frames (mover o mouse não re-encoda a tela inteira), qualidade JPEG e FPS maiores. — v0.9.0
- **Controlar apps de administrador** (PC-3000, WinHex): confirmado que exige o PC REMOTO em nível elevado — está sendo implementado.

---

## Modelo de acesso (decidido)

Sem botão "Permitir". O PC REMOTO tem um **código fixo** (endereço) e uma **senha de acesso forte e configurável**. O ADMIN pareado envia a senha a cada conexão (fica salva no ADMIN para não redigitar). O PC REMOTO guarda só o **hash** da senha (PBKDF2-SHA256), nunca o texto. É um segundo fator além do mTLS e permite acesso sem ninguém presente — inclusive na tela de bloqueio/login.

---

## O que falta (etapa do serviço elevado)

Objetivo: controlar apps de administrador **e** acessar com a **tela bloqueada/login** (seu caso: de casa, reiniciar a central e digitar a senha), como o TeamViewer faz.

- [x] Captura/injeção numa biblioteca sem interface (reutilizável pelo agente)
- [x] Seguir o desktop ativo (Default ↔ Winlogon) na captura e injeção
- [x] Núcleo da senha de acesso (hash + verificação, com testes)
- [ ] Serviço Windows **SYSTEM** que lança o agente na sessão ativa
- [ ] Identidade/config em local compartilhado (ProgramData/LocalMachine) para a interface e o serviço verem o mesmo pareamento
- [ ] Instalador registra e inicia o serviço

Por que SYSTEM: só um processo em nível SYSTEM, acompanhando o desktop de entrada, consegue ver e comandar a tela de bloqueio, o login e as janelas de administrador.

---

## Conectividade

Tudo passa pelo **Tailscale** (rede privada WireGuard), porta **47115**. Se o Tailscale cair, o acesso para até ele voltar — é o canal por onde os dois PCs se enxergam sem abrir o roteador. Recomendado restringir no painel do Tailscale para só os ADMINs alcançarem a porta dos PCs REMOTOS.

## Estrutura do projeto

```
src/NexoDesk.App/      o programa (ADMIN e PC REMOTO), WPF
src/NexoDesk.Shared/   protocolo, mTLS, pareamento, sessões, config, auditoria (sem Windows)
src/NexoDesk.Desktop/  captura de tela e injeção (sem interface) — para o agente elevado
src/NexoDesk.Ui/       Nexotool UI para WPF
tests/                 unitários e integração (TLS real em loopback)
installer/             Inno Setup
docs/                  tutorial, arquitetura, protocolo, segurança, problemas
```
