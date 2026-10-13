# Protocolo NexoDesk v1

Fonte de verdade do contrato de rede entre **NexoDesk Admin** e **NexoDesk Agent**. Implementação: `src/NexoDesk.Shared/Protocol` e `src/NexoDesk.Shared/Messages`. Qualquer mudança aqui exige nova versão do protocolo.

## Transporte

- TCP sobre o endereço Tailscale do Agent (porta padrão **47115**).
- TLS 1.2 ou 1.3 com **autenticação mútua**. Cada lado fixa (pinning) a impressão SHA-256 do certificado do outro. Ver `security.md`.
- Depois do TLS, as mensagens trafegam com o enquadramento abaixo.

## Enquadramento

TCP não preserva limites de mensagem. Cada mensagem tem um cabeçalho fixo de 16 bytes, little-endian:

| Campo | Tipo | Bytes | Valor |
|---|---|---:|---|
| Magic | uint32 | 4 | `0x4B44584E` — bytes `N X D K` |
| Version | uint16 | 2 | `1` |
| Type | uint16 | 2 | ver tabela de tipos |
| Length | uint32 | 4 | tamanho do payload |
| Sequence | uint32 | 4 | contador por direção, começa em 1 (diagnóstico) |
| Payload | bytes | Length | JSON UTF-8 (controle) ou binário (VideoFrame) |

Regras de leitura (todas testadas em `MessageFramerTests`):

- O cabeçalho é validado **antes** de alocar o payload: assinatura, versão, tipo conhecido e `Length` dentro do limite do tipo.
- Limites: mensagens de controle até **64 KiB**; `VideoFrame` até **8 MiB**.
- Leitura com `ReadExactlyAsync`; conexão fechada no meio de uma mensagem é erro.
- Fechamento limpo só é aceito na fronteira entre mensagens.
- Qualquer violação encerra a sessão com `Error` (melhor esforço) e registro em auditoria.

## Tipos de mensagem

| Valor | Tipo | Direção | Payload |
|---:|---|---|---|
| 1 | `Hello` | Admin → Agent | `protocolVersion`, `clientId`, `capabilities[]`, `appVersion` |
| 2 | `HelloAck` | Agent → Admin | `protocolVersion`, `serverId`, `displayName`, `features[]`, `appVersion` |
| 3 | `StartSession` | Admin → Agent | `mode`: `View` \| `Control` |
| 4 | `SessionAccepted` | Agent → Admin | `sessionId`, `mode` |
| 5 | `SessionDenied` | Agent → Admin | `reason`, `message` (texto seguro em português) |
| 6 | `DisplayInfo` | Agent → Admin | `width`, `height`, `dpiScale` |
| 7 | `VideoFrame` | Agent → Admin | binário, ver abaixo |
| 8 | `PointerEvent` | Admin → Agent | `x`, `y` (0–1), `button`, `action`, `wheelDelta` |
| 9 | `KeyEvent` | Admin → Agent | `scanCode`, `virtualKey`, `isExtended`, `action` |
| 10 | `Ping` | ambos | `timestamp` (relógio monotônico de quem envia) |
| 11 | `Pong` | ambos | `echoTimestamp` (devolve o recebido) |
| 12 | `EndSession` | ambos | `reason` |
| 13 | `Error` | ambos | `code`, `message` |

JSON: nomes em camelCase, enums como texto (`"View"`), campos obrigatórios, `null` rejeitado em campos não anuláveis, campos desconhecidos ignorados. Cada mensagem é validada depois de desserializada (faixas, tamanho de texto, caracteres de controle). Ver `MessageTests`.

### VideoFrame (binário)

| Campo | Tipo | Bytes |
|---|---|---:|
| FrameIndex | uint32 | 4 |
| Timestamp | int64 (ms Unix, UTC) | 8 |
| Width | uint16 | 2 |
| Height | uint16 | 2 |
| JPEG | bytes | restante (deve começar com `FF D8`) |

Cada frame é uma imagem completa. O Admin decodifica só o mais recente e descarta os atrasados.

## Máquina de estados

```text
Disconnected → Connecting → Authenticated → Negotiating → Streaming → Closing → Disconnected
                                 (qualquer estado ativo pode ir para Closing)
```

Mensagens aceitas por estado (o resto é `UnexpectedMessage` e encerra a sessão):

| Estado | Agent recebe | Admin recebe |
|---|---|---|
| Authenticated | `Hello` | `HelloAck`, `Error` |
| Negotiating | `StartSession`, `Ping`, `EndSession` | `SessionAccepted`, `SessionDenied`, `Ping`, `Pong`, `EndSession`, `Error` |
| Streaming | `PointerEvent`, `KeyEvent`, `Ping`, `Pong`, `EndSession` | `DisplayInfo`, `VideoFrame`, `Ping`, `Pong`, `EndSession`, `Error` |

## Fluxo de sessão

1. Admin conecta por TCP ao endereço Tailscale do Agent; TLS mútuo autentica os dois lados.
2. Admin envia `Hello`; Agent responde `HelloAck` com os recursos disponíveis.
3. Admin envia `StartSession`.
4. Agent aplica a política: modo suportado → vaga livre (uma sessão por máquina) → sessão do Windows desbloqueada → consentimento local (se o acesso não assistido estiver desligado).
5. Agent responde `SessionAccepted` + `DisplayInfo` (ou `SessionDenied` com motivo) e passa a `Streaming`.
6. Admin envia `Ping` a cada 2 s; latência = ida e volta do `Pong`.
7. Fim: `EndSession` de qualquer lado, `Error`, tempo de inatividade (Agent 30 s, Admin 15 s) ou queda de rede.

## Prazos padrão

| Prazo | Valor | Onde |
|---|---|---|
| Conexão TCP + TLS | 10 s | Admin e Agent |
| Handshake (`Hello`, `StartSession`) | 10 s | ambos |
| Decisão da política (inclui consentimento) | consentimento + 5 s | Agent |
| Inatividade em sessão | 30 s Agent / 15 s Admin | ambos |
| Intervalo de `Ping` | 2 s | Admin |
