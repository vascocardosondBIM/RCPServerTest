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
| **`Chat/`** | **Assistente IA** no Revit: comandos ribbon, `LlmChatService` (Ollama / Gemini / NVIDIA OpenAI-compatible), contexto JSON do projeto (`RevitChatContextBuilder`, incl. geometria em planta quando aplicável), parsing de `revitOps`, janela WPF embutida (`Views/`), markdown simples nas mensagens. |
| **`Sketch/`** | **Sketch → BIM**: fluxo de imagem + prompt → LLM → JSON de paredes/divisões/portas; `SketchLlmPrompts`, intérpretes Ollama / Gemini / NVIDIA, `SketchGenerationRunner`, **pré-visualização** (`SketchInterpretationPreviewWindow`) antes de aplicar, janela de upload (`SketchUploadWindow` + XAML embutido). |
| **`RevitOperations/`** | Operações sobre o modelo Revit usadas pelo sketch e pelo chat: **`CreateElements/`** (paredes, portas, salas), **`SketchBuild/`** (`RevitModelBuilder` — transação única após interpretação), **`JsonOps/`** (`RevitJsonOpsExecutor` — `revitOps` do chat), **`ChangeElements/`**, **`DeleteElements/`**, **`SelectElements/`**, **`Shared/`** (helpers partilhados). |
| **`Integration/`** | Ligação **TCP JSON-RPC** ao bridge Node: **`Rpc/`** (servidor, dispatcher para API thread), **`Routing/`** (`McpCommandRouter` — método `create_house_from_sketch`), **`Contracts/`** (DTOs do protocolo). |
| **`deploy/`** | Ficheiros para instalação: **`RevitSketchPoC.addin`**, **`pluginsettings.example.json`** (modelo sem segredos), e normalmente uma cópia local de **`pluginsettings.json`** (não commits com chaves). |

Ficheiros na **raiz desta pasta**: `RevitSketchPoC.csproj`, `RevitSketchPoC.sln`, `README.md` (este ficheiro).

---

## Fluxos principais

1. **Ribbon “Sketch AI PoC” → Upload sketch** — escolhes imagem e prompt; o LLM devolve JSON; opcionalmente vês a **pré-visualização** (imagem vs. interpretação); depois o Revit cria paredes/quartos/portas numa transação.
2. **Ribbon → Assistente IA** — conversa multimodal com contexto do projeto; o modelo pode devolver um bloco ` ```json ` com `revitOps` para alterar parâmetros, criar paredes pontuais, etc. (ver `LlmChatService` e `RevitJsonOpsExecutor`).
3. **MCP `create_house_from_sketch`** — o Node envia o pedido por TCP para este add-in; o mesmo pipeline de interpretação + build que o sketch na UI.

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

**Assistente IA** — conversa com contexto do documento; em vista de planta o contexto pode incluir `planGeometryInActiveView` (coordenadas aproximadas de paredes/portas/salas).

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
