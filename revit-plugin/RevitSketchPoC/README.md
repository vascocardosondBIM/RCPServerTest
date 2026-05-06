# RevitSketchPoC (add-in Revit)

Add-in Revit **independente** do plugin original do ecossistema mcp-servers-for-revit, para correr **em paralelo** sem conflito de assembly nem de porta:

| | |
| --- | --- |
| `AssemblyName` | `RevitSketchPoC` |
| `FullClassName` | `RevitSketchPoC.App.SketchToBimApp` (ficheiro em `Core/Application/`) |
| `AddInId` | GUID próprio no `.addin` |
| TCP default | `8081` (o plugin original costuma usar `8080`) |

No **mesmo** servidor MCP Node: a maioria das tools fala com o plugin original em `REVIT_PLUGIN_PORT` (`8080`); a tool **`create_house_from_sketch`** usa `REVIT_SKETCH_PORT` — deve coincidir com `TcpPort` em `pluginsettings.json` deste add-in (por defeito `8081`).

---

## O que cada pasta faz (`revit-plugin/RevitSketchPoC/`)

| Pasta | Função |
| --- | --- |
| **`Core/`** | Peças transversais: `Application/SketchToBimApp.cs` (classe `RevitSketchPoC.App.SketchToBimApp` — entrada do add-in, ribbon, `ExternalEvent`), `Configuration/PluginSettings.cs` (lê `pluginsettings.json`), `ViewModels/RelayCommand.cs`, `RevitModelessWindowHost.cs` (abrir janelas WPF no `Idling` para evitar erros com o Revit). |
| **`Chat/`** | **Assistente IA** no Revit: ribbon, `LlmChatService`, contexto JSON (`RevitChatContextBuilder`, geometria em planta quando aplicável), anexos de imagem, parsing e aplicação de `revitOps` (`ChatRevitOpsParser`, `ExternalEvent`). Ver [Assistente IA (chat)](#assistente-ia-chat). |
| **`Sketch/`** | **Sketch → BIM**: fluxo de imagem + prompt → LLM → JSON de paredes/divisões/portas; `SketchLlmPrompts`, intérpretes Ollama / Gemini / NVIDIA, `SketchGenerationRunner`, **pré-visualização** (`SketchInterpretationPreviewWindow`) antes de aplicar, janela de upload (`SketchUploadWindow` + XAML embutido). |
| **`RevitOperations/`** | Operações sobre o modelo Revit usadas pelo sketch e pelo chat: **`CreateElements/`** (paredes, portas, salas), **`SketchBuild/`** (`RevitModelBuilder` — transação única após interpretação), **`JsonOps/`** (`RevitJsonOpsExecutor` — `revitOps` do chat), **`ChangeElements/`**, **`DeleteElements/`**, **`SelectElements/`**, **`Shared/`** (helpers partilhados). |
| **`Integration/`** | Ligação **TCP JSON-RPC** ao bridge Node: **`Rpc/`** (servidor, dispatcher para API thread), **`Routing/`** (`McpCommandRouter` — método `create_house_from_sketch`), **`Contracts/`** (DTOs do protocolo). |
| **`deploy/`** | Ficheiros para instalação: **`RevitSketchPoC.addin`**, **`pluginsettings.example.json`** (modelo sem segredos), e normalmente uma cópia local de **`pluginsettings.json`** (não commits com chaves). |

Ficheiros na **raiz desta pasta**: `RevitSketchPoC.csproj`, `RevitSketchPoC.sln`, `README.md` (este ficheiro).

---

## Fluxos principais

1. **Ribbon “Sketch AI PoC” → Upload sketch** — escolhes imagem e prompt; o LLM devolve JSON; opcionalmente vês a **pré-visualização** (imagem vs. interpretação); depois o Revit cria paredes/quartos/portas numa transação.
2. **Ribbon → Assistente IA (AI Chat)** — conversa com o modelo, contexto JSON do projeto/seleção, imagens opcionais, e aplicação automática de `revitOps` no Revit. **Documentação detalhada:** secção [Assistente IA (chat)](#assistente-ia-chat) abaixo.
3. **MCP `create_house_from_sketch`** — o Node envia o pedido por TCP para este add-in; o mesmo pipeline de interpretação + build que o sketch na UI.

---

## Assistente IA (chat)

O chat é a janela **“Assistente IA”** (ribbon **Sketch AI PoC** → botão **AI / Chat**). Usa o mesmo `pluginsettings.json` que o sketch (**Ollama**, **Gemini** ou **NVIDIA**). Código principal: `Chat/Services/LlmChatService.cs` (instruções de sistema + chamadas HTTP), `Chat/ViewModels/LlmChatViewModel.cs` (UI e contexto), `Chat/Services/RevitChatContextBuilder.cs` (JSON do projeto), `Chat/Services/ChatRevitOpsParser.cs` (extrair `revitOps`), `RevitOperations/JsonOps/RevitJsonOpsExecutor.cs` (executar ops no modelo).

### O que o utilizador pode fazer

| Área | Descrição |
| --- | --- |
| **Conversa geral** | Perguntas sobre Revit/BIM, interpretação de plantas ou do modelo — o assistente responde na **mesma língua** que escreves (português ou inglês), salvo pedido explícito em contrário. |
| **Texto + imagem** | Mensagens só texto, ou **anexar imagem** (PNG, JPG/JPEG, WebP, BMP; até ~6 MB) a enviar **com a próxima** mensagem; na bolha do utilizador aparece pré-visualização da imagem. Modelos **com visão** (ex. Ollama `llava`) analisam a figura. |
| **Contexto do projeto** | Botão **Atualizar projeto** — gera de novo o JSON do documento: título/caminho, vista ativa, níveis (até 20), contagens por categoria, `namedTypesForRevitOps` (nomes de tipos a usar em `wallTypeName`, `doorTypeName`, etc.), e **`planGeometryInActiveView`** quando faz sentido (paredes/portas/janelas/salas em **metros**, XY do modelo, com limites de contagem). |
| **Contexto de seleção** | No Revit, seleciona elementos → **Incluir seleção** — acrescenta um segundo bloco JSON só com a seleção atual (ids e metadados). **Limpar seleção** remove esse extra. |
| **Aplicar mudanças no Revit** | Se a resposta do modelo incluir JSON com raiz `"revitOps": [ … ]` (muitas vezes dentro de um cercado de código Markdown com etiqueta `json`), o add-in **extrai**, corre as operações na thread do Revit (`ExternalEvent`) e mostra uma linha **`[Revit] …`** com resumo (sucessos/falhas e mensagens de log). |
| **Histórico** | Todas as bolhas da janela entram no pedido ao LLM como turnos user/assistant (multimodal por turno quando há imagem na mensagem do utilizador). |

### Formato `revitOps`

O assistente é instruído a devolver **um único** objeto JSON na mensagem, com esta forma:

```json
{ "revitOps": [ { "op": "nome_da_op", ... }, ... ] }
```

O parser aceita: vários blocos fenced com ou sem etiqueta `json` (usa o primeiro que parsear com sucesso), ou texto que termine com um `{ ... }` contendo `revitOps`.

### Transacções e ops especiais

- A maior parte das `revitOps` corre **numa única transação** Revit.
- **`create_wall_roman_arch_profile`** e **`create_wall_custom_profile_void`** são exceções: **confirmam** a transação em curso, aplicam a edição de perfil da parede (incluindo `SketchEditScope` onde aplicável), **abrem nova transação** e seguem com as restantes entradas do array. Isto evita misturar estados inválidos com outras criações no mesmo lote.

### Lista de operações `op` (referência rápida)

Cada entrada do array é um objeto com `"op"` e campos específicos (metros em XY quando indicado; ids devem vir do contexto quando possível).

| `op` | Função resumida |
| --- | --- |
| `set_parameter` | Alterar parâmetro por `elementId`; `parameterName` e/ou `builtInParameter`; `value` como string (`SetValueString`). |
| `delete_elements` | Apagar por `elementIds` (até 50 ids por chamada interna). |
| `select_elements` | Selecionar elementos na UI; corre **após** o commit da transação principal. |
| `create_wall` | Parede recta: `startX/Y`, `endX/Y` (m); opcionais `heightMeters`, `levelName`, `wallTypeName`. |
| `create_wall_arc` | Parede curva: arco por três pontos ou por centro + raio + ângulos; opcionais como `create_wall`. |
| `create_room` | Sala: centro ou `boundary` (polígono fechado); opcionais `name`, `levelName`. |
| `create_door` / `create_window` | Porta/janela: `locationX/Y` ou `location`; opcionais `hostWallId`, `levelName`, tipo; há validação de proximidade em lote. |
| `create_floor` / `create_ceiling` | Laje/teto: `boundary` [{x,y}, …]; opcionais `levelName`, tipo, `name` (comentário). |
| `analyze_floor_wall_footprint` | Leitura: compara footprint do pavimento com cadeia de paredes; escreve métricas no log da execução. |
| `repair_floor_to_wall_footprint` | Recria o piso a partir das curvas de parede (alinhamento a eixo/interior/exterior). |
| `create_wall_opening` | Vão rectangular na parede (posição ao longo da parede ou rácio, dimensões). |
| `create_wall_arch_opening` | Porta **família** em arco (agendas de portas); não confundir com furo só em perfil. |
| `create_wall_roman_arch_profile` | **Arco romano** editando o **perfil** da parede recta (sem porta); ver `RevitWallArchProfileOps`. |
| `create_wall_custom_profile_void` | **Buraco fechado** através da espessura (loop interior no sketch de perfil): `hostWallId`; `shape` com `kind` **star**, **regularPolygon**, **triangle**, **diamond**, **cross/plus**, **heart**, ou **`boundary`** com pontos `{ alongMeters, heightFromWallBaseMeters }`; vários vãos com `voids[]`. Arcos romanos aqui **não** — usar `create_wall_roman_arch_profile`. |
| `flip_wall` | Inverter face da parede (`elementId` ou `elementIds`). |
| `create_family_instance` | Instância de família loadable: `familyTypeName`, posição; opcionais `levelName`, `rotationDegrees`. |
| `create_level` | Novo nível: `name`, `elevationMeters` (origem interna). |
| `create_grid` | Eixo: `startX/Y`, `endX/Y`; opcionais `levelName`, nome do eixo. |
| `change_element_level` | Mudar nível de elementos; opcional `preserveWorldPosition` / `preservePosition` para manter XYZ. |
| `change_level_preserve_position` | Igual mas **sempre** preserva posição no mundo. |

### Boas práticas e limitações

- Usa **nomes de tipo** que apareçam em `namedTypesForRevitOps` no contexto.
- **Não inventar** ids: preferir os do snapshot de seleção ou texto do contexto.
- Lotes **muito grandes** podem ser lentos ou falhar a meio; o sistema pede preferência por poucas ops ou pelo fluxo **Sketch → BIM** para plantas completas.
- O assistente recebe regras anti-sobreposição (portas/janelas no mesmo ponto, pisos duplicados, etc.) no prompt de sistema.

---

## Configuração (`pluginsettings.json`)

Coloca `pluginsettings.json` **na mesma pasta que a DLL** após o build (ou edita o que está em `deploy/` antes de copiar).

- **`TcpPort`**: alinha com `REVIT_SKETCH_PORT` no Node (ex.: `8081`).
- **`LlmProvider`**: `"Ollama"`, `"Gemini"` ou **`"Nvidia"`** (API [NVIDIA NIM](https://integrate.api.nvidia.com/), formato OpenAI `chat/completions`).
- **`OllamaBaseUrl`** / **`OllamaModel`**: só quando `LlmProvider` é Ollama; modelo com **visão** para sketch (ex. `llava`). Não commits dados sensíveis.
- **`GeminiApiKey`** / **`GeminiModel`**: só quando o provider é Gemini; chave **vazia** no Git, preenchida só localmente (ex. `gemini-2.0-flash`).
- **`NvidiaApiKey`**: token Bearer (ex. `nvapi-...`); **vazio** no Git, preenchido só localmente quando usas NVIDIA.
- **`NvidiaModel`**: id do modelo no catálogo NVIDIA (ex. `google/gemma-3n-e4b-it`). Para sketch multimodal, escolhe um modelo que suporte **imagem + texto** no endpoint de chat; modelos só texto falham nesse fluxo.
- **`NvidiaChatCompletionsUrl`**: opcional; se vazio, usa `https://integrate.api.nvidia.com/v1/chat/completions`. Preenche apenas se a tua conta ou endpoint usarem outra URL base compatível com OpenAI.

Para um clone limpo: copia `deploy/pluginsettings.example.json` para `pluginsettings.json` e edita provider, modelo e porta.

---

## Build

1. Abre `RevitSketchPoC.csproj` (ou a `.sln`) no Visual Studio.
2. Se necessário, ajusta `RevitApiPath` no `.csproj` à tua instalação do Revit.
3. Build em **Release**.

Output esperado: `bin\Release\RevitSketchPoC.dll`

---

## Instalação manual no Revit

1. Copia `RevitSketchPoC.dll` para uma pasta fixa, por exemplo `C:\RevitPlugins\RevitSketchPoC\`.
2. Copia `deploy\pluginsettings.json` (ou a tua cópia segura) para a **mesma pasta** da DLL.
3. Copia `deploy\RevitSketchPoC.addin` para `%AppData%\Autodesk\Revit\Addins\2025\` (ajusta o ano à tua versão).
4. Edita o `.addin` e aponta o caminho absoluto para a DLL.
5. Reinicia o Revit.

---

## Teste na UI (WPF)

1. Abre um projeto no Revit.
2. Tab **Sketch AI PoC** → **Upload Sketch** (ou equivalente no ribbon).
3. Escolhe a imagem do esboço / planta.
4. Opcional: ativa **«Pré-visualizar antes de aplicar»** para comparar a imagem com o que o modelo interpretou antes de criar geometria.
5. **Gerar modelo** — cria paredes, quartos (rooms) e portas conforme opções.

## Spike 1 (UI dedicada)

1. Tab **Sketch AI PoC** → **Spike 1 PDF->JSON**.
2. Seleciona o PDF vetorial.
3. Define a página (`1` = primeira).
4. (Opcional) escolhe preset de qualidade (`Rápido`, `Balanceado`, `Alta precisão`) ou ajusta `tile_size_pt` / `raster_dpi`.
5. Clica **Gerar JSON PDF** para extrair geometrias/texto sem IA.
6. Artefactos gerados: `raw`, `clean`, `semantic_ready_manifest`, `semantic_pixels` e `tiles`.
7. (Opcional) Clica **Executar Spike 2 (LLM)** para inferência semântica por tile usando o provider configurado.
8. O contrato `semantic_pixels.v1` é validado automaticamente e o matching geométrico (snap bbox -> clean) é aplicado.
9. A calibração explícita converte para coordenada real e gera `*_semantic_real_world.json`.
10. Clica **Guardar JSON…** para exportar o ficheiro gerado.

### Spike 2 (semântico por tile, integrado na mesma janela)

- Botão: **Executar Spike 2 (LLM)**.
- Provider usado: definido em `pluginsettings.json` (`LlmProvider`).
- Processamento:
  1. inferência de cada tile no modelo vision;
  2. validação de schema (`semantic_pixels.v1`);
  3. matching geométrico com snap para `clean.json`;
  4. calibração explícita para metros (`AutoScale`, `ManualScale`, `ReferencePoints`).

Notas de calibração:

- `AutoScale` tenta ler `1:N` no texto da planta;
- `ManualScale` usa a escala informada no UI;
- `ReferencePoints` usa dois pontos conhecidos e distância real em metros.

Artefato final atualizado:

- `*_semantic_pixels.json` (deteções + metadados de matching).
- `*_semantic_real_world.json` (deteções em coordenada real consistente).
- `*_semantic_metrics.json` (precision/unmatched/calibration error + contagens).

Resumo no status do plugin:

- `tiles`, `detections`, `snapped`, `unmatched`.

> [!NOTE]
> A documentação detalhada da base entregue para continuidade do Spike 2 (arquivos-chave, contrato e artefatos) está em `Spike1/README.md`, seção **Base pronta para o colega (Spike 2)**.

> [!TIP]
> Se enviares PDF no upload da UI/MCP, o plugin converte automaticamente a 1ª página para PNG antes de chamar o LLM.
> Esta conversão usa Python + PyMuPDF na máquina local:
>
> ```bash
> python -m pip install pymupdf
> ```

**Assistente IA** — vê a secção [Assistente IA (chat)](#assistente-ia-chat) (contexto JSON, imagens, tabela de `revitOps`).

---

## Teste via bridge Node (MCP)

- Método TCP JSON-RPC exposto: **`create_house_from_sketch`**.
- O servidor MCP encaminha essa tool para `REVIT_SKETCH_PORT`.
- Garante que `TcpPort` no JSON coincide com essa porta.

### Fluxo MCP + LLM (no Revit)

O servidor Node **não** chama Ollama, Gemini nem NVIDIA; o **plugin C#** lê `pluginsettings.json` e faz o HTTP ao backend escolhido.

| Provider | Caminho típico |
| --- | --- |
| Ollama | `Cliente IA → MCP (Node) → TCP :8081 → RevitSketchPoC → HTTP Ollama (ex. /api/chat)` |
| Gemini | `… → RevitSketchPoC → API Google Gemini` |
| NVIDIA | `… → RevitSketchPoC → https://integrate.api.nvidia.com/v1/chat/completions` (ou URL em `NvidiaChatCompletionsUrl`) |

## Ollama (recomendado para PoC local sem chave cloud)

1. Instala [Ollama](https://ollama.com/) e garante o serviço em `localhost:11434`.
2. Define `"LlmProvider": "Ollama"` e ajusta `OllamaBaseUrl` / `OllamaModel`.
3. Faz pull de um modelo com **visão** para sketch (ex.: `ollama pull llava` se usares `llava` no JSON).

## NVIDIA NIM / AI Foundry (cloud, OpenAI-compatible)

1. Obtém uma API key NVIDIA (formato `nvapi-...`) no [NVIDIA Build](https://build.nvidia.com/) ou na documentação do produto que estiveres a usar.
2. Em `pluginsettings.json`:
   - `"LlmProvider": "Nvidia"`
   - `NvidiaApiKey` com o Bearer token
   - `NvidiaModel` com o slug do modelo listado para **chat completions** (ver catálogo; para sketch precisas de suporte a imagem no mesmo endpoint).
3. Opcional: `NvidiaChatCompletionsUrl` se o teu ambiente não usar o default `https://integrate.api.nvidia.com/v1/chat/completions`.

Sem `NvidiaApiKey` válida com `LlmProvider: Nvidia`, o plugin reporta erro claro.

## Gemini (opcional)

- `"LlmProvider": "Gemini"`
- `GeminiApiKey` preenchida só localmente
- `GeminiModel` conforme a API Google

Sem chave válida com `Gemini`, o plugin deve reportar erro claro.

---

## Licença

Segue a licença indicada na raiz do repositório (`README.md` principal).
