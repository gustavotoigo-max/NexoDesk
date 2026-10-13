# Nexotool UI — regras de layout

Guia visual do NexoDesk (e de outros apps Nexotool). Implementado em `src/NexoDesk.Ui`.

## Estrutura da janela

- Barra de título **azul-marinho** (`Nx.Navy`), com o título à esquerda e ações/menus à direita.
- Logo abaixo da barra, uma **linha em degradê** (`Nx.AccentGradient`), da esquerda para a direita.
- Conteúdo em **cartões** (`Nx.Card`) sobre o fundo da superfície.
- Rodapé azul-marinho com informações discretas.

## Cantos (raio)

Cantos **discretos** — nada muito arredondado. Tokens em `Themes/Brand.xaml`:

| Token | Valor | Uso |
|---|---|---|
| `Nx.Radius.Card` | **6** | cartões, caixas, faixas de aviso, log, diálogos |
| `Nx.Radius.Button` | **5** | botões |
| `Nx.Radius.Control` | **4** | campos de texto, itens de segmento |

Pílulas/alternadores (switch) e círculos (ícones redondos) continuam totalmente arredondados. Sempre referencie o token, não um número solto.

## Cores

Cores de marca em `Themes/Brand.xaml` (fontes, raios, log, texto sobre marinho). Superfícies claro/escuro em `Themes/Light.xaml` e `Dark.xaml`.

### Acentos de cor (modelos visuais)

O **acento** (barra, degradê, botões primários, destaque) é trocável em tempo de execução em **Configurações → Cor**. Cada acento é um dicionário em `Themes/Accents/` carregado depois do tema. Modelos atuais:

| Acento | Barra/base | Destaque | Primário |
|---|---|---|---|
| **Azul** (padrão) | `#0B1733` | `#22D3EE` | `#2563EB` |
| **Esmeralda** | `#07261F` | `#34D399` | `#059669` |
| **Violeta** | `#1E1033` | `#C084FC` | `#7C3AED` |
| **Grafite** | `#0F172A` | `#38BDF8` | `#475569` |
| **Âmbar** | `#2A1A05` | `#FBBF24` | `#D97706` |

Para criar um acento novo: copie um arquivo de `Themes/Accents/`, troque as cores e adicione o nome ao enum `NxAccent` e ao seletor em Configurações. Os tons suaves (`Nx.BlueSoft`/`Nx.BlueSelection`) usam a cor primária com transparência, então funcionam em claro e escuro sem ajuste.

## Tipografia

- Texto: **Segoe UI Variable Text, Segoe UI** (`Nx.Font`).
- Monoespaçada (log, impressões, IDs): **Cascadia Mono, Consolas** (`Nx.Mono`).
- Ícones: **Segoe Fluent Icons** (`Nx.Icons`).

## Componentes

- **Badge** (`NxBadge`): estado com pontinho colorido (sucesso, aviso, perigo, neutro).
- **Segmentado**: grupo de opções lado a lado (modo, tema, cor, qualidade).
- **Switch** (`Nx.Switch`): liga/desliga.
- **Botões**: `Nx.Button` (padrão), `Nx.Button.Primary` (ação principal), `Nx.Button.Navy` (sobre a barra), `Nx.Link`.

## Regra geral

Tudo pronto para uso na interface — sem editar arquivos nem rodar scripts. Claro/escuro automático segue o Windows. Tom sóbrio e profissional; destaque pelo acento e pela linha em degradê, não por cantos muito arredondados.
