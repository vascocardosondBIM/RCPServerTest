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
| **`Sketch/`** | **Sketch → BIM**: fluxo de imagem + prompt → LLM → JSON de paredes, **rooms**, portas, **janelas** e **pisos** (conforme interpretação); `SketchLlmPrompts`, intérpretes Ollama / Gemini / NVIDIA, `SketchGenerationRunner`, **pré-visualização** (`SketchInterpretationPreviewWindow`) antes de aplicar, janela de upload (`SketchUploadWindow` + XAML embutido). |
| **`RevitOperations/`** | Operações sobre o modelo Revit usadas pelo sketch e pelo chat: **`CreateElements/`** (paredes, portas, salas, **escadas**, **pilares**, **vigas** (`create_beam`), **guarda-corpo / corrimão** (`create_guardrail`), **pisos/tetos a partir de Room**, vãos, perfis), **`SketchBuild/`** (`RevitModelBuilder` — transação única após interpretação), **`JsonOps/`** (`RevitJsonOpsExecutor` — `revitOps` do chat), **`ReviewElements/`** (análise/reparo de footprint de piso/teto vs paredes ou Room), **`ChangeElements/`**, **`DeleteElements/`**, **`SelectElements/`**, **`Shared/`** (geometria JSON, contornos de divisão, etc.). |
| **`Integration/`** | Ligação **TCP JSON-RPC** ao bridge Node: **`Rpc/`** (servidor, dispatcher para API thread), **`Routing/`** (`McpCommandRouter` — **só** o método `create_house_from_sketch`; restantes métodos → erro), **`Contracts/`** (DTOs do protocolo). |
| **`deploy/`** | Ficheiros para instalação: **`RevitSketchPoC.addin`**, **`pluginsettings.example.json`** (modelo sem segredos), e normalmente uma cópia local de **`pluginsettings.json`** (não commits com chaves). |

Ficheiros na **raiz desta pasta**: `RevitSketchPoC.csproj`, `RevitSketchPoC.sln`, `README.md` (este ficheiro).

---

## Fluxos principais

1. **Ribbon “Sketch AI PoC” → Upload sketch** — escolhes imagem e prompt; o LLM devolve JSON; opcionalmente vês a **pré-visualização** (imagem vs. interpretação); depois o Revit aplica **paredes, rooms, portas, janelas e pisos** (conforme o JSON e as opções abaixo) numa transação.
2. **Ribbon → Assistente IA (AI Chat)** — conversa com o modelo, contexto JSON do projeto/seleção, imagens opcionais, e aplicação automática de `revitOps` no Revit. **Documentação detalhada:** secção [Assistente IA (chat)](#assistente-ia-chat) abaixo.
3. **MCP `create_house_from_sketch`** — o Node envia o pedido por TCP para este add-in; o mesmo pipeline de interpretação + build que o sketch na UI.

### Pedido sketch / MCP (`SketchToBimRequest`)

Contrato em `Sketch/Contracts/SketchContracts.cs`. Campos úteis (UI e JSON-RPC `create_house_from_sketch`):

| Campo | Função |
| --- | --- |
| `imagePath` / `imageBase64` + `mimeType` | Imagem ou planta; PDF é convertido para PNG (1.ª página; vê `pdfPageNumber`). |
| `prompt` | Instruções extra ao modelo (opcional). |
| `targetLevelName` | Nível onde criar geometria (opcional). |
| `wallTypeName`, `floorTypeName` | Tipos Revit a preferir quando aplicável. |
| `autoCreateRooms`, `autoCreateDoors`, `autoCreateWindows`, `autoCreateFloors` | Ligar/desligar criação automática de cada categoria após interpretação (todos `true` por defeito). |
| `showPreviewUi` | Se `true`, mostra a janela de pré-visualização antes de aplicar (por defeito `true`; em automação MCP podes enviar `false` para aplicar sem diálogo). |
| `pdfPageNumber` | Página base-1 quando o ficheiro é PDF (por defeito `1`). |

O JSON interpretado pelo LLM pode incluir listas `walls`, `rooms`, `doors`, `windows`, `floors` — o builder materializa o que estiver presente e permitido pelos toggles.

---

## Assistente IA (chat)

O chat é a janela **“Assistente IA”** (ribbon **Sketch AI PoC** → botão **AI / Chat**). Usa o mesmo `pluginsettings.json` que o sketch (**Ollama**, **Gemini** ou **NVIDIA**). Código principal: `Chat/Services/LlmChatService.cs` (instruções de sistema + chamadas HTTP), `Chat/ViewModels/LlmChatViewModel.cs` (UI e contexto), `Chat/Services/RevitChatContextBuilder.cs` (JSON do projeto), `Chat/Services/ChatRevitOpsParser.cs` (extrair `revitOps`), `RevitOperations/JsonOps/RevitJsonOpsExecutor.cs` (executar ops no modelo).

### O que o utilizador pode fazer

| Área | Descrição |
| --- | --- |
| **Conversa geral** | Perguntas sobre Revit/BIM, interpretação de plantas ou do modelo — o assistente responde na **mesma língua** que escreves (português ou inglês), salvo pedido explícito em contrário. |
| **Texto + imagem** | Mensagens só texto, ou **anexar imagem** (PNG, JPG/JPEG, WebP, BMP; até ~6 MB) a enviar **com a próxima** mensagem; na bolha do utilizador aparece pré-visualização da imagem. Modelos **com visão** (ex. Ollama `llava`) analisam a figura. |
| **Contexto do projeto** | Botão **Atualizar projeto** — gera o JSON do documento: título/caminho, vista ativa, níveis (até 20), contagens por categoria, **`namedTypesForRevitOps`** (strings exactas para `wallTypeName`, `floorTypeName`, `ceilingTypeName`, portas/janelas, **`stairsTypeNames`** (`create_stairs`), **`railingTypeNames`** (`create_guardrail` / `create_railing` / **`create_stairs`** com `railingTypeName` / `stairRailingTypeName` na escada), **`structuralColumnTypeNames`** / **`architecturalColumnTypeNames`** (`create_pillar`), **`structuralFramingTypeNames`** (`create_beam`), `sampleLoadableFamilyTypes`, etc.), **`planGeometryInActiveView`** em **vista de planta** (paredes/portas/janelas/rooms em metros; omitido em 3D — não impede operações que usam geometria da base de dados), **`footprintRepairHints`** (sugestões de `wallIds` por piso/teto para analyze/repair), **`revitOpsContextHints`** (notas curtas, ex.: repair em 3D), e notas gerais. |
| **Contexto de seleção** | Seleciona elementos → **Incluir seleção** — JSON com elementos (parâmetros, nível, etc.). Inclui quando aplicável: **`floorIdsInSelection`**, **`ceilingIdsInSelection`**, **`roomIdsInSelection`**; para cada **Room** há campos como **`slabFromRoomPayload`** / **`ceilingFromRoomPayload`** (o assistente deve usar **`create_floor_from_room`** / **`create_ceiling_from_room`** por defeito). **Limpar seleção** remove o extra. |
| **Aplicar mudanças no Revit** | Se a resposta do modelo incluir JSON com raiz `"revitOps": [ … ]` (muitas vezes dentro de um cercado de código Markdown com etiqueta `json`), o add-in **extrai**, corre as operações na thread do Revit (`ExternalEvent`) e mostra uma linha **`[Revit] …`** com resumo (sucessos/falhas e mensagens de log). |
| **Histórico** | Todas as bolhas da janela entram no pedido ao LLM como turnos user/assistant (multimodal por turno quando há imagem na mensagem do utilizador). |

### Formato `revitOps`

O assistente é instruído a devolver **um único** objeto JSON na mensagem, com esta forma:

```json
{ "revitOps": [ { "op": "nome_da_op", ... }, ... ] }
```

O parser aceita: vários blocos fenced com ou sem etiqueta `json` (usa o primeiro que parsear com sucesso), ou texto que termine com um `{ ... }` contendo `revitOps`.

### Pisos e tetos — fluxo por defeito (chat)

O prompt do assistente está alinhado com o seguinte:

1. **Primeira opção:** **`create_floor_from_room`** / **`create_ceiling_from_room`** quando existir uma **Room** colocada e fechada no modelo — o Revit calcula o contorno (inclui paredes **curvas**). Usa o `roomId` do contexto (seleção, `roomIdsInSelection`, ou id explícito).
2. **Só se não houver Room adequada** (ou o utilizador pedir geometria livre, laje fora de divisões, etc.): **`create_floor`** / **`create_ceiling`** com `boundary`, `boundarySegments` ou `circle` (metros).
3. **Reparo:** se o `analyze_*_wall_footprint` tiver usado a **Room** como referência, **`repair_floor_to_room_footprint`** / **`repair_ceiling_to_room_footprint`** (`floorId`/`ceilingId` + `roomId`) é o caminho preferido para recriar a laje/teto alinhada à divisão; **`repair_*_to_wall_footprint`** continua útil quando a cadeia de paredes é a referência ou os hints o indicam.

Isto evita pedir coordenadas manuais quando já existe divisão.

### Transacções e ops especiais

- A maior parte das `revitOps` corre **numa única transação** Revit.
- **`create_wall_roman_arch_profile`**, **`create_wall_custom_profile_void`** e **`create_stairs`** são exceções: **confirmam** a transação em curso, executam a operação especial (perfil da parede ou escada em componentes), **abrem nova transação** e seguem com as restantes entradas do array. Isto evita misturar estados inválidos com outras criações no mesmo lote.
- O alias **`create stairs`** (com espaço) é aceite e normalizado para `create_stairs`.

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
| **`create_floor_from_room`** | **Preferido para lajes por divisão:** `roomId` (ou `elementId` se for Room). Contorno = limite calculado da Room (arcos OK). Opcionais: `floorTypeName`, `name` (comentário), `boundaryLocation`: **`finish` (por defeito — faces de acabamento / interior da divisão)**, `center` (eixo da parede), `coreBoundary`, `coreCenter`. |
| **`create_ceiling_from_room`** | Igual ao anterior para tetos; `ceilingTypeName` opcional. Opcionais **`ceilingKind`**: `false_ceiling` (teto falso, por defeito) ou `slab_painted` / aliases como `laje`; **`falseCeilingDropMeters`** (queda do teto falso). |
| `create_floor` / `create_ceiling` | **Alternativa** quando não há Room: `boundary` [{x,y}, …], **ou** `boundarySegments` (line/arc), **ou** `circle`; opcionais `levelName`, tipo, `name`. Em tetos: mesmos **`ceilingKind`** / **`falseCeilingDropMeters`** que `create_ceiling_from_room`. |
| `analyze_floor_wall_footprint` | Leitura: com **Room** no modelo, métricas podem comparar laje vs limite da Room (opcionais `roomId`, `boundaryLocation`); senão, cadeia de paredes. Opcional `includeJson:true`, `wallIds`, tolerâncias. |
| `repair_floor_to_wall_footprint` | Recria o piso a partir das curvas de parede; `wallIds` recomendado em níveis complexos; `alignTo`: `wall_centerline` / `wall_inside` / `wall_outside`. Falha de `Floor.Create` faz rollback (não apaga o piso antigo). |
| `repair_floor_to_room_footprint` | **Reparo preferido quando o analyze usou Room como referência** — `floorId`, `roomId`; opcional `boundaryLocation`. Recria como `create_floor_from_room`; rollback em falha. |
| `analyze_ceiling_wall_footprint` | Análogo ao piso para tetos (inclui padrão Room-first quando aplicável). |
| `repair_ceiling_to_wall_footprint` | Análogo ao reparo de piso por cadeia de paredes; rollback seguro em falha. |
| `repair_ceiling_to_room_footprint` | `ceilingId`, `roomId`; opcional `boundaryLocation`; rollback em falha (equivalente a recriar teto a partir da Room). |
| `change_ceiling_kind` | Alterar modo vertical do teto existente: `ceilingId` ou `elementId`; **`ceilingKind`** `false_ceiling` ou `slab_painted` (aliases aceites); opcional **`falseCeilingDropMeters`**. Recalcula offset face a paredes e vãos. |
| `create_wall_opening` | Vão rectangular na parede (posição ao longo da parede ou rácio, dimensões). |
| `create_wall_arch_opening` | Porta **família** em arco (agendas de portas); não confundir com furo só em perfil. |
| `create_wall_roman_arch_profile` | **Arco romano** editando o **perfil** da parede recta (sem porta); ver `RevitWallArchProfileOps`. |
| `create_wall_custom_profile_void` | **Buraco fechado** no perfil da parede (`hostWallId`). Suporta **`shape.kind`** paramétricos (ex.: **circle**, **ellipse**, **star**, **regularPolygon** / **polygon**, **triangle** / **isoscelesTriangle**, **diamond** / **rhombus**, **cross** / **plus**, **heart**) com campos de raio/lados/etc.; cadeia **`segments`** (line/arc/circle/ellipse) em **`boundary`**; silhuetas livres (**boundary** por pontos); **`voids[]`** para vários buracos. No código há ainda interpretação de formas “ricas” (slot, cápsula, squircle, etc.) via boundary/segmentos — ver `RevitWallCustomProfileVoidOps.cs`. Arco romano de parede: usar **`create_wall_roman_arch_profile`**, não esta op. |
| `flip_wall` | Inverter face da parede (`elementId` ou `elementIds`). |
| `create_family_instance` | Instância de família loadable: `familyTypeName`, posição; opcionais `levelName`, `rotationDegrees`. |
| `create_pillar` | Coluna estrutural ou arquitectónica: **`pillarTypeName`** ou **`columnTypeName`** (deve coincidir com listas em `namedTypesForRevitOps`); posição em planta; opcionais nível/topo, altura, offsets, rotação, nome. |
| `create_level` | Novo nível: `name`, `elevationMeters` (origem interna). |
| `create_grid` | Eixo: `startX/Y`, `endX/Y`; opcionais `levelName`, nome do eixo. |
| `create_stairs` | Escada recta em componentes entre dois níveis: `bottomLevelName` (ou `baseLevelName`), `topLevelName` (superior), trajecto em planta `startX/Y` → `endX/Y`; opcionais `stairsTypeName`, **`justification`**: `left` / `center` / `right`. **Por defeito remove corrimãos automáticos** do Revit; **`keepStairsRailings`: true** mantém os que o Revit criar. **Tipo na escada:** `railingTypeName` / `stairRailingTypeName` / `guardrailTypeName` (em `railingTypeNames`); com `keepStairsRailings` true **retipa** corrimãos existentes; com false **recria** com esse tipo. Opcional **`stairRailingPlacement`**: `treads` (por defeito) ou `stringer`. Corrimão linear independente: **`create_guardrail`**. |
| `create_beam` | **Viga** (estrutura metálica ou madeira, categoria *Structural Framing*): **`beamTypeName`** em `structuralFramingTypeNames`; eixo em planta como `create_wall` (`startX/Y`, `endX/Y` ou `start`/`end`); **`levelName`**; opcionais **`zOffsetMeters`**, **`name`** (Comments). |
| `create_guardrail` / `create_railing` | **Guarda-corpo / corrimão** (*Railing*): **`railingTypeName`** ou **`guardrailTypeName`** em `railingTypeNames`; trajecto recto em planta (mesmos campos que `create_wall`); **`levelName`**; opcionais **`zOffsetMeters`**, **`name`**. Espaçamento de balaustres e geometria do corrimão vêm do **tipo de corrimão** no Revit (não são parâmetros desta op). |
| `change_element_level` | Mudar nível de elementos; opcional `preserveWorldPosition` / `preservePosition` para manter XYZ. |
| `change_level_preserve_position` | Igual mas **sempre** preserva posição no mundo. |

Para o detalhe campo-a-campo (tolerâncias de footprint, regras de colocação, exemplos de JSON), o prompt de sistema em **`Chat/Services/LlmChatService.cs`** está alinhado com o executor e costuma estar mais completo que esta tabela resumida.

### Boas práticas e limitações

- **Pisos/tetos:** com Room no modelo, preferir sempre **`create_*_from_room`**; `create_floor` manual é para vãos sem divisão ou pedidos explícitos de geometria livre. Após **`analyze_*_wall_footprint`** com referência a Room, preferir **`repair_*_to_room_footprint`** em vez de só `repair_*_to_wall_footprint` quando fizer sentido corrigir o contorno à divisão.
- Usa **nomes de tipo** que apareçam em `namedTypesForRevitOps` no contexto (inclui listas para **`create_stairs`**, **`create_pillar`**, **`create_beam`** e **`create_guardrail`** / **`create_railing`**).
- **Não inventar** ids: preferir seleção, `roomIdsInSelection`, `footprintRepairHints` ou texto do contexto.
- Vista **3D:** `planGeometryInActiveView` pode vir omitido — operações como repair/analyze de footprint e **`create_floor_from_room`** usam a geometria do **documento**, não da vista em planta.
- Lotes **muito grandes** podem ser lentos ou falhar a meio; preferir poucas ops ou o fluxo **Sketch → BIM** para plantas completas.
- O assistente recebe regras anti-sobreposição (portas/janelas no mesmo ponto, pisos duplicados, etc.) no prompt de sistema.

### Mapa rápido: o que o projeto faz

| Área | Capacidades |
| --- | --- |
| **Sketch → BIM** | Imagem + LLM → paredes (retas/curvas), rooms, portas, **janelas**, **pisos** (conforme interpretação e toggles `autoCreate*`); pré-visualização opcional (`showPreviewUi`); transação única no Revit. |
| **Chat IA** | Conversa + contexto JSON + imagens; `revitOps`: parâmetros, apagar, selecionar, paredes, arcos, rooms, **pisos/tetos por Room ou geometria manual**, reparos **por parede ou por Room**, **`change_ceiling_kind`**, portas/janelas, **pilares**, **vigas** (`create_beam`), **guarda-corpo** (`create_guardrail`), **escadas**, vãos e perfis (arco romano, buracos no perfil), níveis, eixos, mudança de nível, análise/reparo de footprint. |
| **TCP / MCP** | Apenas o método JSON-RPC **`create_house_from_sketch`** é suportado nesta porta; outros nomes de método devolvem erro. Alinha com `REVIT_SKETCH_PORT` / `TcpPort`. |
| **PDF (Fase 1)** | UI **Fase 1 PDF Extract**: gera só **`raw.json`** (vetor + texto via PyMuPDF). Ver `Phase1_VectorExtraction/README.md`. |

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
5. **Gerar modelo** — aplica a interpretação (paredes, rooms, portas, **janelas** e **pisos** quando o JSON e os toggles `autoCreateWindows` / `autoCreateFloors` o permitirem).

## Fase 1 — Raw JSON (`raw.json`)

1. Tab **Sketch AI PoC** → **Fase 1 PDF Extract**.
2. Seleciona o PDF e a página (base 1).
3. Clica **Gerar raw.json** — corre Python no `%TEMP%` e cria uma pasta com **`raw.json`** apenas (`get_drawings` + `get_text('words')`).
4. Usa **Abrir pasta output**, **Exportar todo o output…** ou **Guardar raw…** para copiar o ficheiro.

Nota: raster, `clean.json`, manifest e pipeline LLM por tile estão em pausa até reintroduzimos passo a passo.

<details>
<summary>Documentação antiga (pipeline completo — em pausa)</summary>

1. Tab **Sketch AI PoC** → **Fase 1 PDF Extract**.
2. …preset tile/DPI, clean, tiles, inferência LLM — ver histórico git / versões anteriores.

</details>

### Spike 2 (semântico por tile — em pausa na UI)

- O botão de inferência LLM está oculto enquanto a Fase 1 só gera `raw.json`.

<details>
<summary>Comportamento anterior (referência)</summary>

- Botão: **Executar Spike 2 (LLM)**.
- Provider: `pluginsettings.json`.
- Artefactos: `semantic_pixels`, `semantic_real_world`, métricas, etc.

</details>

> Documentação da Fase 1: `Phase1_VectorExtraction/README.md`.

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

- Método TCP JSON-RPC suportado por este add-in: **`create_house_from_sketch`** apenas (`Integration/Routing/McpCommandRouter.cs`). Qualquer outro `method` na mesma ligação responde com erro **Unknown method**.
- O servidor MCP Node encaminha essa tool para `REVIT_SKETCH_PORT`.
- Garante que `TcpPort` em `pluginsettings.json` coincide com essa porta.

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
