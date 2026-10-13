# Tutorial: primeira conexão entre o PC admin e um PC remoto

Nomenclatura usada aqui:

- **PC admin** — o computador de onde você acessa os outros. Roda o **NexoDesk Admin**.
- **PC remoto** — o computador que será acessado. Roda o **NexoDesk Agent**.

No estado atual (M2), a conexão segura, o pedido de permissão, a faixa de aviso e o registro já funcionam. A imagem da tela chega no M3.

---

## 0. Antes de começar (nos dois PCs)

1. **Tailscale** instalado, conectado e na **mesma rede (tailnet)**. Confira com:
   ```powershell
   tailscale status
   ```
   Anote o **nome** (MagicDNS) ou o **IP 100.x** do PC remoto (`tailscale ip -4` no PC remoto).
2. **.NET 10 Desktop Runtime x64**: https://dotnet.microsoft.com/download/dotnet/10.0 (seção *Windows Desktop Runtime*).
3. **Arquivos do NexoDesk**: no GitHub, aba **Actions** → último build verde → artefato **nexodesk-win-x64**. Ele traz `publish\Admin`, `publish\Agent` e `scripts`.
   - Ou compile você mesmo na pasta do projeto:
     ```powershell
     dotnet publish src\NexoDesk.Agent -c Release -r win-x64 --self-contained false -o C:\NexoDesk\Agent
     dotnet publish src\NexoDesk.Admin -c Release -r win-x64 --self-contained false -o C:\NexoDesk\Admin
     ```
4. Arquivos baixados da internet vêm bloqueados. Depois de extrair, rode na pasta extraída:
   ```powershell
   Get-ChildItem -Recurse | Unblock-File
   ```

## 1. Copiar os programas

| Onde | O que copiar | Destino sugerido |
|---|---|---|
| PC remoto | `publish\Agent` e `scripts` | `C:\NexoDesk\Agent` e `C:\NexoDesk\scripts` |
| PC admin | `publish\Admin` e `scripts` | `C:\NexoDesk\Admin` e `C:\NexoDesk\scripts` |

## 2. PC remoto — criar o certificado do PC

Em um PowerShell **normal** (não como administrador), **logado na conta que usa o PC no dia a dia**:

```powershell
cd C:\NexoDesk\scripts
powershell -ExecutionPolicy Bypass -File .\New-NexoDeskCertificate.ps1 -Role Agent -Name pc-oficina
```

Troque `pc-oficina` por um nome curto para este PC. **Anote a impressão SHA-256** que aparece (64 caracteres). Neste tutorial ela é a **impressão R** (do PC remoto).

## 3. PC admin — criar o certificado do administrador

No PC admin, PowerShell normal, na sua conta:

```powershell
cd C:\NexoDesk\scripts
powershell -ExecutionPolicy Bypass -File .\New-NexoDeskCertificate.ps1 -Role Admin -Name admin-gustavo
```

**Anote a impressão SHA-256**. Neste tutorial ela é a **impressão A** (do PC admin).

> As impressões não são segredo: são como a "digital" pública de cada certificado. As chaves privadas ficam protegidas pelo Windows e nunca saem do PC.

## 4. PC remoto — instalar a configuração e o firewall

No PC remoto, abra o PowerShell **como administrador**:

```powershell
cd C:\NexoDesk\scripts
powershell -ExecutionPolicy Bypass -File .\Setup-NexoDeskAgent.ps1 `
    -MachineId pc-oficina `
    -DisplayName "PC Oficina" `
    -ServerCertificateSha256 IMPRESSAO_R `
    -AllowedAdmin "Gustavo=IMPRESSAO_A" `
    -AgentExe "C:\NexoDesk\Agent\NexoDesk.Agent.exe" `
    -Autostart
```

O script:

- grava `C:\ProgramData\NexoDesk\agent.json` com o **acesso não assistido desligado** (cada conexão pede *Permitir*);
- deixa essa configuração alterável só por administradores;
- cria a regra de firewall **NexoDesk Agent (Tailscale)**: porta 47115, só pela rede Tailscale;
- com `-Autostart`, abre o Agent minimizado sempre que alguém entra no Windows.

## 5. PC remoto — abrir o Agent

Abra `C:\NexoDesk\Agent\NexoDesk.Agent.exe`. A janela deve mostrar:

- no topo: **Aguardando conexões**;
- em *Escutando em*: o IP 100.x do Tailscale e a porta 47115;
- em *Administradores autorizados*: **Gustavo**.

Pode fechar a janela: o Agent continua no ícone da área de notificação (perto do relógio).

## 6. PC admin — configurar o Admin

1. Abra `C:\NexoDesk\Admin\NexoDesk.Admin.exe`. Na primeira vez ele cria `%AppData%\NexoDesk\admin.json` e mostra um aviso amarelo de configuração pendente. Isso é esperado.
2. Clique em **Abrir configuração** e deixe o arquivo assim:

   ```json
   {
     "schemaVersion": 1,
     "clientId": "admin-gustavo",
     "clientCertificateSha256": "IMPRESSAO_A",
     "machines": [
       {
         "id": "pc-oficina",
         "displayName": "PC Oficina",
         "host": "pc-oficina.SEU-TAILNET.ts.net",
         "port": 47115,
         "serverCertificateSha256": "IMPRESSAO_R"
       }
     ]
   }
   ```

   Em `host`, use o nome MagicDNS do PC remoto ou o IP 100.x.
3. Salve e clique em **Recarregar**. O aviso some e o PC Oficina aparece na tabela.
4. Clique em **Verificar**: o status deve ficar **Disponível**.

## 7. Primeira conexão

1. No PC admin, deixe o modo em **Visualizar** e clique em **Conectar**.
2. No PC remoto aparece **Permitir acesso remoto?** com contagem regressiva de 30 s. Alguém no PC remoto clica em **Permitir**.
3. No PC remoto, uma faixa azul-marinho fica fixa no topo da tela: **Acesso remoto ativo · Gustavo · Encerrar acesso**.
4. No PC admin abre a janela da sessão com **Conexão segura**, o certificado conferido, o canal TLS e a latência atualizando a cada 2 s.
5. Para terminar: **Desconectar** no PC admin, ou **Encerrar acesso** na faixa do PC remoto. Os dois lados registram o fim no log.

## 8. Recomendado: restringir no Tailscale

Por padrão, todo PC da tailnet alcança qualquer outro. Ajuste a política no painel do Tailscale para que **só os PCs admin** alcancem a porta 47115 dos **PCs remotos**. Exemplo pronto em [security.md](security.md#política-do-tailscale-exemplo).

## 9. Acrescentar mais computadores

**Outro PC remoto** (até 5 no total): repita os passos 1, 2, 4 e 5 nele, com outro nome (ex.: `pc-recepcao`). Depois adicione mais um item em `machines` no `admin.json` do PC admin e clique em **Recarregar**.

**Outro PC admin**: no novo PC admin, repita os passos 1, 3 e 6. Em cada PC remoto, acrescente o novo administrador em `allowedClients` de `C:\ProgramData\NexoDesk\agent.json` (edição como administrador):

```json
"allowedClients": [
  { "name": "Gustavo", "sha256": "IMPRESSAO_A" },
  { "name": "Fulano",  "sha256": "IMPRESSAO_DO_NOVO_PC_ADMIN" }
]
```

Depois clique em **Reiniciar Agent** no PC remoto.

## 10. Se algo der errado

- A mensagem do Admin ou do Agent diz o motivo e o que fazer. A tabela completa está em [troubleshooting.md](troubleshooting.md).
- Os registros ficam em **Pasta de logs** (link no rodapé dos dois programas).
- Certificado perdido ou trocado: veja *Revogar ou trocar um certificado* em [security.md](security.md).
