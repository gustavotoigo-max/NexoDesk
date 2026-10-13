# RemoteControl — Especificação técnica e plano de desenvolvimento

**Versão do documento:** 1.0  
**Data:** 12/10/2026  
**Estado:** planejamento do MVP  
**Plataforma inicial:** Windows 10/11, x64  
**Linguagem:** C# / .NET (versão LTS suportada no início da implementação)

> **Nome provisório:** RemoteControl. As sugestões de nome estão no final deste documento.

## 1. Objetivo

Construir uma solução de **acesso remoto privado para 2 a 5 computadores da empresa**, acessíveis pela internet a partir de um computador administrador. O aplicativo terá interface própria e permitirá visualizar e controlar, com autorização prévia, a sessão interativa de um PC Windows. A conectividade será fornecida por uma rede privada Tailscale, sem encaminhamento de portas no roteador nem infraestrutura própria de relay.

### 1.1 Resultado esperado do MVP

Em um computador administrador, selecionar um dos computadores cadastrados, estabelecer uma conexão autenticada por uma rede Tailscale, visualizar a tela do usuário conectado e operar mouse e teclado, com possibilidade de encerrar e reconectar. O agente deverá aceitar somente administradores expressamente autorizados e manter registro dos eventos de acesso.

### 1.2 Escopo

**Incluído no MVP:**

- Aplicativo administrador Windows com lista estática de 2 a 5 máquinas.
- Agente Windows executado na **sessão interativa do usuário** do computador remoto.
- Conexão pela rede Tailscale e canal de aplicação autenticado e criptografado.
- Exibição da tela principal do remoto em janela redimensionável.
- Controle básico de mouse (movimento, cliques, rolagem) e teclado.
- Estado da conexão, mensagens de erro, encerramento e reconexão manual.
- Lista explícita de administradores autorizados, política de acesso não assistido, logging e indicação visível de sessão ativa no PC remoto.

**Fora do MVP:** transferência de arquivos, som, área de transferência, múltiplos monitores, gravação de sessão, sessões simultâneas, streaming por hardware, controle da tela de login, elevação via UAC/desktop seguro, troca de usuário, acesso antes do login, gerenciamento de dezenas de endpoints e interface web/mobile.

## 2. Premissas, restrições e decisões

1. Todos os dispositivos inicialmente rodam Windows 10/11 x64.
2. Há **uma sessão de controle por máquina**; conexões adicionais são rejeitadas.
3. O agente atua em **sessão interativa ativa**. Não se pressupõe que um serviço Windows consiga capturar/controlar desktops de usuários: serviços ficam na Session 0.
4. Para o acesso não assistido, o proprietário/gestor da máquina habilita a funcionalidade previamente, e os administradores permitidos são definidos explicitamente.
5. O agente nunca abre uma porta de controle para a internet pública. Ele escuta somente em endereço/interface Tailscale, com firewall e regras de acesso restritivas.
6. Autenticação/autorização da aplicação são independentes de estar conectado ao Tailscale: pertencer à rede privada **não** concede automaticamente controle.
7. Primeiro será priorizada correção e segurança; otimizações de latência/fluidez ficam para uma fase posterior.

## 3. Arquitetura

```text
PC ADMINISTRADOR (Windows)                    INTERNET / TAILSCALE                    PCs REMOTOS (2–5)
┌──────────────────────────┐                  ┌────────────────────────┐             ┌─────────────────────────┐
│ RemoteControl.Admin      │                  │ Rede mesh privada      │             │ RemoteControl.Agent     │
│ WPF / MVVM               │── TCP + TLS ────▶│ Tailscale / WireGuard  │────────────▶│ UI/session process      │
│ SessionController        │                  │ IP ou MagicDNS         │             │ Capture/Input/Policy    │
│ VideoRenderer            │◀─ frames JPEG ──│ Conexão direta/relay   │◀────────────│ FrameProducer           │
└──────────────────────────┘                  └────────────────────────┘             └─────────────────────────┘
```

**Conjuntos de projetos na solução:**

```text
RemoteControl.sln
├── src/
│   ├── RemoteControl.Admin/        # WPF, lista de PCs, viewer, envio de comandos
│   ├── RemoteControl.Agent/        # Processo do usuário; captura, injeção, política
│   └── RemoteControl.Shared/       # Contratos, framing, serialização e validação
├── tests/
│   ├── RemoteControl.Shared.Tests/
│   └── RemoteControl.IntegrationTests/
├── docs/
│   ├── architecture.md
│   ├── protocol.md
│   ├── security.md
│   └── troubleshooting.md
└── README.md
```

### 3.1 Responsabilidades

**Admin:** gerencia catálogo, autentica o servidor remoto, faz handshake, recebe frames, desenha imagem, converte coordenadas do mouse e envia comandos de entrada somente enquanto a sessão está autorizada e em foco.

**Agent:** inicia no contexto do usuário Windows, verifica as permissões, autentica o cliente, captura a tela, comprime os frames, envia atualizações e executa comandos permitidos. Mostra indicador permanente de sessão remota ativa e possibilita encerrar o acesso localmente.

**Shared:** modelos de mensagens versionados, enums, limites de tamanho, utilitários de framing e validação, códigos de erro e testes do protocolo.

**Tailscale:** descoberta/endereço e tunelamento de rede privado. Não é o sistema de autorização funcional do aplicativo.

### 3.2 Captura e renderização

- **Primeira implementação:** Desktop Duplication API (DXGI/Direct3D 11) por interop C#; capturar monitor primário e copiar o frame para buffer apropriado, sempre tratando `AccessLost`/mudança de resolução e indisponibilidade da sessão.
- Como abordagem alternativa apenas para destravar o desenvolvimento inicial, uma captura por GDI/BitBlt pode validar o transporte, mas não deve ser assumida como solução definitiva para desempenho ou conteúdo protegido.
- **Codec do MVP:** JPEG com qualidade ajustável; um frame é uma imagem completa. O receptor descarta frames obsoletos em vez de acumular atraso. JPEG é inadequado para fidelidade perfeita de texto, mas serve para validar o fluxo. Futuramente avaliar H.264 por Media Foundation/encoder de hardware.
- Meta exploratória de desempenho: **1080p, 10–15 FPS**, sujeita a hardware, largura de banda e latência; não é garantia.
- Renderizar de modo assíncrono e evitar bloquear a thread da interface WPF.
- Não tentar capturar tela de login, desktop seguro de UAC, sessão bloqueada ou outros contextos não suportados nesta fase. Comunicar esse estado de maneira clara.

### 3.3 Entrada remota

- `SendInput` no Windows para teclado e mouse em contexto permitido.
- Coordenadas do ponteiro são transmitidas **normalizadas** (`x`,`y` entre 0 e 1) e convertidas para pixels da área capturada no agente; contemplar escala DPI, janela com letterboxing e resoluções diferentes.
- Enviar códigos de tecla e informações de pressionar/soltar; tratar teclas modificadoras, repetição e layout de teclado. Definir estratégia explícita para scan codes e casos de caracteres Unicode.
- `SendInput` está sujeito às restrições de integridade/UIPI do Windows: o agente não deve prometer controlar processos elevados ou o desktop seguro.
- Em perda de conexão, liberar estado lógico de teclas/botões mantidos, para evitar entradas presas.
- Enviar comandos somente se a política permitir e se houver uma sessão ativa autenticada.

## 4. Comunicação e protocolo (v1)

### 4.1 Canal e segurança de transporte

- Canal TCP sobre IP Tailscale.
- **TLS da aplicação com autenticação mútua (mTLS)**, usando `SslStream`, certificados/dispositivos previamente provisionados e validação estrita de cadeia ou pinning de chaves/certificados. Não aceitar certificados arbitrários nem usar callbacks que sempre retornem `true`.
- No protótipo, provisionar certificados de curta validade manualmente e armazenar chaves privadas em local protegido pelo Windows (repositório de certificados/DPAPI com ACL adequada). Definir como revogar/rotacionar credenciais.
- Firewall do Windows limita a porta do agente à interface/rede Tailscale. A política ACL/grants no Tailscale autoriza somente a máquina administradora a alcançar a porta específica.
- Não existe autenticação por senha simples em texto, código fixo universal ou desativação de validações para facilitar testes.

### 4.2 Framing

TCP é fluxo de bytes e **não** preserva limites de mensagens. Cada mensagem deve ter um cabeçalho fixo com comprimento máximo validado e payload delimitado.

**Formato sugerido (binário, little-endian; documentação do protocolo é a fonte de verdade):**

| Campo | Tipo | Tamanho | Descrição |
|---|---|---:|---|
| Magic | uint32 | 4 B | Assinatura para identificação do protocolo |
| Version | uint16 | 2 B | Versão 1 |
| Type | uint16 | 2 B | Tipo de mensagem |
| Length | uint32 | 4 B | Quantidade de bytes do payload |
| Sequence | uint32 | 4 B | Número sequencial, útil para diagnóstico |
| Payload | bytes | variável | JSON UTF-8 nos comandos; bytes para imagens |

**Limites iniciais:** cabeçalho de 16 B; mensagens de controle com no máximo 64 KiB; frame JPEG até 8 MiB; tempo máximo de handshake e idle configuráveis. Tamanhos inválidos causam encerramento seguro da sessão. Não alocar buffers diretamente de comprimentos recebidos sem validação.

### 4.3 Mensagens previstas

| Direção | Tipo | Conteúdo |
|---|---|---|
| Admin → Agent | `Hello` | versão, clientId, capacidades |
| Agent → Admin | `HelloAck` | versão, serverId, recursos disponíveis |
| Admin → Agent | `StartSession` | pedido de visualização/controle |
| Agent → Admin | `SessionAccepted` / `SessionDenied` | autorização e motivo seguro |
| Agent → Admin | `DisplayInfo` | largura, altura, DPI/transformações necessárias |
| Agent → Admin | `VideoFrame` | timestamp, índice, dimensões, JPEG |
| Admin → Agent | `PointerEvent` | posição normalizada, botão, ação, wheel |
| Admin → Agent | `KeyEvent` | scan code/código, estado, modificadores |
| Bidirecional | `Ping` / `Pong` | saúde/latência |
| Bidirecional | `EndSession` | término ordenado |
| Agent → Admin | `Error` | código e mensagem não sensível |

**Máquina de estados:** `Disconnected → Connecting → Authenticated → Negotiating → Streaming → Closing → Disconnected`. Rejeitar mensagens incompatíveis com o estado atual. Incluir cancelamento por `CancellationToken`, timeouts e encerramento idempotente.

### 4.4 Fluxo de sessão

1. Admin consulta o catálogo local e tenta conectar ao endereço Tailscale do Agent.
2. TCP e mTLS autenticam ambos os endpoints.
3. Troca de versões e recursos do protocolo.
4. Agente aplica política local: identidade permitida, consentimento prévio, sessão interativa disponível e ausência de outra sessão.
5. Agente confirma, envia metadados de exibição e começa os frames.
6. Admin renderiza os frames e encaminha eventos de teclado/mouse permitidos.
7. Término solicitado, timeout, revogação de permissão ou queda de rede fecha a sessão e gera evento de log.

## 5. Segurança e privacidade: requisitos bloqueantes

O software dá controle da interface de computadores corporativos e, portanto, exige defesa em profundidade.

- **Autorização explícita:** cada máquina contém lista de identidades de administrador permitidas; nenhuma autorização implícita para todos os membros da tailnet.
- **Acesso não assistido configurável:** desligado por padrão; quando habilitado pelo responsável pela máquina, deve exibir indicador visível durante conexões e permitir corte local imediato.
- **Identidades de dispositivos:** certificados individualizados, sem segredo compartilhado único para todos; revogação documentada em caso de perda/comprometimento.
- **Menor privilégio:** processo sem elevação por padrão; não solicitar privilégio de administrador desnecessariamente.
- **Restrição de rede:** permitir tráfego apenas a partir de máquinas admin aprovadas; não fazer port forwarding e não expor serviço em `0.0.0.0` sem restrição equivalente.
- **Logs sem conteúdo de tela nem teclas:** registrar timestamp UTC, máquina, identidade do cliente, início/fim, resultado da autenticação, motivo da desconexão, versão e erro técnico sanitizado. Não registrar senhas, conteúdos de clipboard ou teclas digitadas.
- **Indicador de sessão ativa e botão local para encerrar.**
- **Limites anti-abuso:** tamanho máximo de payloads, limite de sessões, timeouts, validação de mensagens e de eventos de input, rate limit básico em tentativas de conexão.
- **Segredos protegidos:** chaves em armazenamento do Windows com ACL/DPAPI; configurações não devem conter senhas/certificados privados em texto puro.
- **Sem bypass de UAC/tela de login:** limitações do sistema operacional são respeitadas. Qualquer extensão privilegiada exige novo projeto de segurança e testes.

### 5.1 Modelo simples de ameaças

| Risco | Mitigação inicial |
|---|---|
| Máquina indevida alcança a porta do Agent | Tailscale grants/ACL + firewall + mTLS |
| Um PC da tailnet foi comprometido | Autorização por identidade/dispositivo em cada Agent |
| Tráfego interceptado | Tailscale WireGuard + TLS autenticado da aplicação |
| Frames malformados causam exaustão de memória | Limites, validação antes de alocar e tratamento de erros |
| Sessão esquecida aberta | Indicador visual, encerramento local, timeout/reconexão explícita |
| Credencial/certificado vazado | Certificado por dispositivo, revogação e rotação |

## 6. Configuração

**Exemplo ilustrativo de catálogo local do Admin (sem credenciais):**

```json
{
  "schemaVersion": 1,
  "machines": [
    { "id": "pc-oficina", "displayName": "PC Oficina", "host": "pc-oficina.tailnet-exemplo.ts.net", "port": 47115 },
    { "id": "pc-recepcao", "displayName": "PC Recepção", "host": "pc-recepcao.tailnet-exemplo.ts.net", "port": 47115 }
  ]
}
```

Os domínios acima são **exemplos**, não endereços utilizáveis. Identidade/autorização do servidor não deve depender apenas do hostname configurado.

**Configurações do Agent (sem chaves privadas):** ID da máquina, porta, lista de identidades permitidas, `unattendedAccessEnabled`, qualidade JPEG, limite de FPS, timeouts e opções de logging. Arquivos de política precisam de ACL que impeça alteração por usuários sem permissão para administrar o agente.

## 7. Design do aplicativo administrador

**Janela principal:** lista de máquinas com nome, endereço, status da última sondagem e ação `Conectar`. Não assumir `online` somente porque existe uma entrada no catálogo; mostrar `Desconhecido`, `Disponível`, `Indisponível` ou `Conectando` conforme evidência real.

**Janela de sessão:** vídeo remoto ajustado à área útil, barra superior com nome do host, status, modo `Visualizar / Controlar`, latência observada, FPS medido e botão `Desconectar`. Capturar eventos de input apenas na área visível do vídeo; capturar/liberar teclado cuidadosamente para não afetar outros aplicativos locais.

**Agente remoto:** ícone na área de notificação, indicação persistente de controle ativo, nome da identidade conectada, botão `Encerrar acesso` e tela de configuração de permissões.

## 8. Plano de implementação — até 7 marcos

| Marco | Entregável | Critério de aceite |
|---|---|---|
| **M1 — Base** | Solução .NET, três projetos, logging, testes e CI básica | Compila limpo; testes de Shared executam |
| **M2 — Rede** | Tailscale entre 2 PCs; listener TCP com mTLS e autorização | Admin autorizado conecta; cliente desconhecido é rejeitado |
| **M3 — Captura** | Captura de monitor principal no Agent e envio de JPEG | Admin recebe e visualiza frames atualizados |
| **M4 — Visualização** | Viewer WPF, escala, redimensionamento e descarte de frames atrasados | Janela responsiva mesmo sob queda de FPS |
| **M5 — Controle** | Mouse, scroll e teclado com limites de segurança | Abre aplicativo e digita texto simples no PC remoto |
| **M6 — Operação** | Catálogo, desconexão, reconexão, política não assistida, logs e botão local | 2–5 máquinas cadastradas, sessões auditáveis |
| **M7 — Validação** | Testes em redes diferentes, documentação e pacote instalável | Cenários de falha e aceitação executados com sucesso |

### 8.1 Primeiro experimento técnico recomendado

Antes de implementar o viewer completo, fazer um **spike** (experimento curto para reduzir incerteza): duas VMs ou dois PCs reais com Tailscale, listener `SslStream` autenticado, handshake versionado e envio de mensagens `Ping/Pong`. Somente depois integrar captura de imagem e input. Isso valida cedo o risco central de conectividade e segurança.

## 9. Backlog técnico por projeto

### Shared

- Definir enum `MessageType`, cabeçalho e interface de serialização.
- Implementar leitura/escrita exata de bytes (`ReadExactlyAsync`) e validação de comprimentos.
- Definir versões compatíveis, timeouts e tratamento de mensagem desconhecida.
- Testar round-trip, fragmentação TCP, bytes truncados, tamanhos extremos e sequência inválida.

### Agent

- Listener por endereço privado e TLS autenticado.
- Verificar certificado/identidade em allowlist protegida.
- Implementar máquina de estados da sessão e logger.
- Integrar DXGI Desktop Duplication, tratando `AccessLost` e mudança de monitor/resolução.
- Converter imagem em JPEG e transmitir com fila limitada (preferir frame atual).
- Processar eventos `PointerEvent`/`KeyEvent` com validação.
- Mostrar bandeja/status da sessão e opção local de desconexão.

### Admin

- MVVM para lista de dispositivos e estados de conectividade.
- Cliente TCP/TLS, verificação da identidade do Agent, handshake e keepalive.
- Recepção assíncrona, fila de frames com capacidade pequena e apresentação no WPF.
- Mapeamento de eventos de mouse/teclado para coordenadas remotas.
- Indicadores de FPS/latência e encerramento sem travar interface.

## 10. Estratégia de testes

| Cenário | Resultado esperado |
|---|---|
| Admin e Agent em redes domésticas/empresariais diferentes | Conexão pelo Tailscale, sem abrir portas públicas |
| Certificado cliente desconhecido ou revogado | Conexão rejeitada e evento no log |
| Certificado servidor trocado sem autorização | Admin rejeita conexão |
| Agente configurado sem acesso não assistido | Conexão depende de consentimento local explícito |
| Mouse em janela redimensionada | Cursor remoto corresponde à posição visível |
| Resolução do PC remoto alterada durante a sessão | Reconfiguração ou erro recuperável sem crash |
| Rede lenta/perda de pacotes | Não acumula frames antigos; UI continua responsiva |
| Desconexão com botão pressionado | Não deixa mouse/teclas logicamente presos |
| PC remoto bloqueado/UAC seguro | Estado não suportado comunicado; não tenta burlar proteção |
| Segundo administrador tenta conectar | Rejeição clara por sessão ocupada |
| PC remoto sem usuário interativo ativo | Agente informa indisponibilidade da sessão |
| Reinício do processo remoto | Admin detecta queda e possibilita reconexão manual |

### 10.1 Métricas a observar

- Latência de entrada até refletir na imagem (percebida, quando possível medida).
- FPS efetivamente exibidos, não apenas gerados.
- Taxa de dados enviada (Mbps).
- CPU/RAM do Admin e do Agent; uso da GPU quando aplicável.
- Tempo para conectar e recuperar de uma desconexão.

## 11. Decisões futuras e limites conhecidos

- **Desempenho:** JPEG full-frame pode consumir muita largura de banda; avaliar compressão diferencial, H.264/HEVC ou WebRTC apenas após medir o MVP.
- **DPI/monitores:** scaling e múltiplas telas exigem ajustes cuidadosos de coordenadas; começar com um monitor principal.
- **Controle privilegiado:** logon, tela bloqueada e UAC seguro são deliberadamente fora do MVP. Se forem necessários, exigir desenho separado envolvendo serviço e sessão de usuário, assinatura e avaliação de segurança.
- **Inicialização automática:** pode ser adicionada por instalação/autostart do agente na sessão do usuário; ela não resolve por si só acesso antes do logon.
- **Atualizações:** instalador assinado e atualização segura são desejáveis antes do uso rotineiro; o primeiro protótipo pode ser instalado manualmente em ambiente controlado.
- **Política Tailscale:** manter regras atuais do serviço revisadas e restritivas; as funcionalidades e sintaxes disponíveis podem mudar.

## 12. Critérios de conclusão do MVP

O MVP estará pronto para uso interno controlado quando:

- Admin conseguir conectar-se a pelo menos dois PCs Windows em redes diferentes por Tailscale.
- O agente recusar clientes não autorizados, mesmo quando estão na mesma tailnet.
- A sessão permitir visualizar o monitor principal e controlar aplicações comuns do usuário.
- Início/fim das sessões forem registrados, com indicador remoto visível e encerramento local possível.
- Falhas de rede, mudança de resolução, sessão ocupada e ausência de desktop interativo não derrubarem a aplicação.
- Certificados/chaves estiverem protegidos, com procedimento escrito para revogação e substituição.

## 13. Nomes sugeridos

Nomes **criativos, sem verificação de domínio, registro de marca ou disponibilidade comercial**:

| Nome | Conceito | Observação |
|---|---|---|
| **NodeReach** | Alcançar máquinas (nós) da rede | Técnico e fácil de associar ao produto |
| **DeskBridge** | Ponte entre desktops | Comunica imediatamente acesso remoto |
| **NexoDesk** | Conexão + desktop | Curto, adequado a uma ferramenta Windows |
| **LinkHarbor** | Porto seguro para conexões | Evoca controle e segurança |
| **RelayNest** | Centralização de acesso | Soa como produto de gerenciamento |
| **PortaDesk** | Porta para outra estação | Memorável em português e inglês |
| **MeshPilot** | Controlar dispositivos de uma malha | Combina com arquitetura mesh/Tailscale |
| **FarDesk** | Desktop distante | Simples e descritivo |

**Minha preferência para o projeto interno: `NexoDesk`.** Se o objetivo for um nome técnico internacional, `NodeReach` é uma boa alternativa. Antes de publicar ou registrar, consultar marcas, domínio e produtos existentes.

## 14. Referências técnicas para começar

- Microsoft Learn — Desktop Duplication API: https://learn.microsoft.com/windows/win32/direct3ddxgi/desktop-dup-api
- Microsoft Learn — `SendInput`: https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-sendinput
- Microsoft Learn — `SslStream`: https://learn.microsoft.com/dotnet/api/system.net.security.sslstream
- Microsoft Learn — WPF: https://learn.microsoft.com/dotnet/desktop/wpf/
- Tailscale — documentação oficial: https://tailscale.com/kb

---

**Próxima tarefa prática recomendada:** criar `RemoteControl.sln` e realizar o marco M2 (conexão segura e handshake entre duas máquinas), antes de adicionar streaming e controle de entrada.
