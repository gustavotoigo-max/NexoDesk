# NexoDesk

Acesso remoto privado para até 5 computadores Windows da empresa, pela rede **Tailscale**, sem abrir portas no roteador.

- **ADMIN** — o computador de onde você acessa os outros.
- **PC REMOTO** — o computador que é acessado.

Um único programa e **um único instalador** para os dois: na primeira abertura você escolhe o papel daquele computador. O pareamento é feito na tela, com um código de 12 caracteres; nada de editar arquivos ou rodar scripts.

Interface no padrão **Nexotool UI** (barra azul-marinho, linha em degradê, cartões, tema claro/escuro automático).

> Estado (v0.3.0): pareamento, conexão segura com autenticação mútua, pedido de permissão, aviso de acesso ativo, registro e instalador. A imagem da tela chega no M3. Plano: [`docs/plano-desenvolvimento.md`](docs/plano-desenvolvimento.md).

## Instalar e usar

Passo a passo: **[docs/tutorial.md](docs/tutorial.md)**. Resumo:

1. Tailscale conectado nos dois computadores.
2. Baixe `NexoDesk-Setup-x.y.z.exe` em **Releases** e instale nos dois.
3. **PC REMOTO:** escolha *PC REMOTO* → **Gerar código de pareamento**.
4. **ADMIN:** escolha *ADMIN* → **Adicionar PC REMOTO** → endereço + código → **Parear**.
5. **ADMIN:** **Conectar**. **PC REMOTO:** **Permitir**.

## Compilar

```powershell
dotnet test NexoDesk.sln -c Release
dotnet publish src\NexoDesk.App -c Release -r win-x64 --self-contained true -o publish\app
iscc /DAppVersion=0.4.0 installer\NexoDesk.iss
```

O GitHub Actions compila, testa e gera o instalador a cada envio para a `main`, e publica em Releases na versão definida em `Directory.Build.props`.

## Estrutura

```text
NexoDesk.sln
├── src/
│   ├── NexoDesk.App/      o programa (ADMIN e PC REMOTO)
│   ├── NexoDesk.Shared/   protocolo, TLS mútuo, pareamento, sessões, configuração, auditoria (sem Windows)
│   └── NexoDesk.Ui/       Nexotool UI para WPF: tokens, estilos, janela padrão, diálogos, tema
├── tests/                 unitários e integração (TLS real em loopback, pareamento completo)
├── installer/             Inno Setup
└── docs/                  tutorial, arquitetura, protocolo, segurança, problemas
```

## Documentação

- [Tutorial: instalar e primeiro acesso](docs/tutorial.md)
- [Arquitetura e marcos](docs/architecture.md)
- [Protocolo v1](docs/protocol.md)
- [Segurança e pareamento](docs/security.md)
- [Solução de problemas](docs/troubleshooting.md)
