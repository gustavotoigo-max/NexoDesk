# Tutorial: instalar e fazer o primeiro acesso

Dois papéis, um só programa:

- **ADMIN** — o computador de onde você acessa os outros.
- **PC REMOTO** — o computador que é acessado.

O mesmo instalador (`NexoDesk-Setup-x.y.z.exe`) serve para os dois. Na primeira abertura você escolhe o papel daquele computador.

> Nesta versão: imagem da tela ao vivo, **controle de mouse e teclado**, pareamento por um único código de acesso, pedido de permissão e atualização pela própria interface. Falta só o acesso com a tela bloqueada/login.

---

## 1. Antes de começar (nos dois computadores)

O **Tailscale** precisa estar instalado, aberto e conectado à **mesma rede** (a da empresa) nos dois computadores: https://tailscale.com/download

## 2. Baixar o instalador

No GitHub do projeto, abra **Releases** e baixe `NexoDesk-Setup-x.y.z.exe`.

> O instalador ainda não tem assinatura digital. Se o Windows mostrar *O Windows protegeu o computador*, clique em **Mais informações → Executar assim mesmo**.

## 3. No PC REMOTO

1. Rode o instalador (ele pede permissão de administrador do Windows uma vez, para instalar e liberar o firewall só para o Tailscale). Deixe marcado **Abrir o NexoDesk agora**.
2. Na tela **Como este computador vai ser usado?**, escolha **PC REMOTO**, confira o nome (ex.: *PC Oficina*) e clique em **Continuar**.
3. Na tela **Como este computador vai ser usado?**, escolha **PC REMOTO**, confira o nome (ex.: *PC Oficina*) e clique em **Continuar**.
4. Clique em **Gerar código de acesso** e depois em **Copiar código**. Esse código (começa com `NXD1-`) **já leva o endereço junto** e vale 10 minutos, uma vez.

Pode fechar a janela: o NexoDesk continua no ícone perto do relógio e abre sozinho com o Windows.

## 4. No ADMIN

1. Rode o mesmo instalador.
2. Na primeira tela, escolha **ADMIN**, confira o nome (ex.: *Notebook Gustavo*) e clique em **Continuar**.
3. Clique em **Adicionar PC REMOTO**, cole o **código de acesso** no único campo e clique em **Parear**.

O PC REMOTO entra na lista como **Disponível**. O pareamento é feito uma vez só por par ADMIN ↔ PC REMOTO.

## 5. Primeiro acesso

1. No ADMIN, deixe o modo em **Controlar** (padrão) e clique em **Conectar**.
2. No PC REMOTO aparece **Permitir acesso remoto?** (30 segundos). Quem estiver lá clica em **Permitir**.
3. No ADMIN abre a janela do acesso: você vê a tela e **comanda com mouse e teclado**. No PC REMOTO, um aviso aparece no topo por 1 minuto (com botão **Esconder**).
4. Para terminar: **Desconectar** no ADMIN ou **Encerrar acesso** no PC REMOTO.

## 6. No dia a dia

| Quero… | Onde |
|---|---|
| Acessar outro PC REMOTO (até 5) | No PC novo: instalar, escolher PC REMOTO, **Gerar código de acesso**. No ADMIN: **Adicionar PC REMOTO** e colar |
| Ter mais de um ADMIN | No ADMIN novo: instalar, escolher ADMIN. No PC REMOTO: **Gerar código de acesso** de novo |
| Tirar o acesso de um ADMIN | PC REMOTO → **ADMINS que podem acessar** → **Remover** |
| Tirar um PC da lista do ADMIN | ADMIN → **Remover** na linha do PC REMOTO |
| Não pedir **Permitir** a cada acesso | PC REMOTO → **Opções** → **Permitir acesso sem confirmação** (o aviso no topo continua aparecendo) |
| Trocar o papel do computador | **Configurações** (rodapé) → **Mudar para ADMIN / PC REMOTO** |
| Tema claro/escuro | **Configurações** → **Tema** |

## 7. Recomendado: restringir no Tailscale

Por padrão, todo computador da rede Tailscale alcança os outros. No painel do Tailscale, deixe só os ADMINs alcançarem a porta **47115** dos PCs REMOTOS. Exemplo em [security.md](security.md#política-do-tailscale-exemplo).

## 8. Se algo der errado

As mensagens na tela dizem o motivo e o que fazer. Tabela completa em [troubleshooting.md](troubleshooting.md). O link **Pasta de registros** no rodapé abre o histórico de acessos.

## Para quem compila

```powershell
dotnet test NexoDesk.sln -c Release
dotnet publish src\NexoDesk.App -c Release -r win-x64 --self-contained true -o publish\app
iscc /DAppVersion=0.4.0 installer\NexoDesk.iss     # gera artifacts\NexoDesk-Setup-0.3.0.exe
```

O GitHub Actions faz isso a cada envio para a `main` e publica o instalador em **Releases**.
