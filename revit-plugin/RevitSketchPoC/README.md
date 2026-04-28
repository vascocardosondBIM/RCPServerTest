# RevitSketchPoC (plugin separado)

Add-in Revit independente do plugin **original** do mcp-servers-for-revit, para correr **em paralelo** sem conflito de assembly nem de porta:

| | |
| --- | --- |
| `AssemblyName` | `RevitSketchPoC` |
| `FullClassName` | `RevitSketchPoC.App.SketchToBimApp` |
| `AddInId` | GUID próprio |
| TCP default | `8081` (o plugin original usa `8080`) |

No **mesmo** servidor MCP Node: todas as tools normais falam com o plugin original em `REVIT_PLUGIN_PORT` (`8080`); só a tool **`create_house_from_sketch`** usa `REVIT_SKETCH_PORT` — deve coincidir com `TcpPort` em `pluginsettings.json` deste add-in (por defeito `8081`).

## Estrutura do código

- `App/` — bootstrap e ribbon
- `Commands/` — comandos Revit (UI)
- `Views/` / `ViewModels/` — WPF (MVVM)
- `Services/` — LLM (Ollama / Gemini) e criação de elementos no Revit
- `Routing/` — router de métodos MCP expostos por TCP
- `Rpc/` — listener TCP JSON-RPC para o bridge Node
- `Contracts/` — DTOs
- `deploy/` — `RevitSketchPoC.addin`, `pluginsettings.json`, e **`pluginsettings.example.json`** (modelo sem segredos para novos clones)

## Configuração (`pluginsettings.json`)

Coloca `pluginsettings.json` **na mesma pasta que a DLL** após o build (ou edita o que está em `deploy/` antes de copiar).

- **`TcpPort`**: alinha com `REVIT_SKETCH_PORT` no Node (ex.: `8081`).
- **`LlmProvider`**: `"Ollama"` ou `"Gemini"`.
- **`OllamaBaseUrl`** / **`OllamaModel`**: no repositório o ficheiro `deploy/pluginsettings.json` reflete a **configuração escolhida pelo projeto** (incluindo o modelo Ollama que estiveres a usar — por exemplo variantes com visão ou modelos cloud da Ollama). Não alteres isto por engano ao fazer merge; ajusta só na tua máquina conforme o que tens instalado no Ollama.
- **`GeminiApiKey`**: deixa **vazio** no Git; preenche só em cópia local ou em `pluginsettings.local.json` se usares esse padrão (ver `.gitignore` na raiz).
- **`GeminiModel`**: ex. `gemini-2.0-flash`.

Para um clone limpo sem partilhar chaves, copia `deploy/pluginsettings.example.json` para `pluginsettings.json` e edita modelo/porta.

## Build

1. Abre `RevitSketchPoC.csproj` no Visual Studio.
2. Se necessário, ajusta `RevitApiPath` no `.csproj` à tua versão do Revit.
3. Build em **Release**.

Output esperado: `bin\Release\RevitSketchPoC.dll`

## Instalação manual no Revit

1. Copia `RevitSketchPoC.dll` para uma pasta fixa, por exemplo `C:\RevitPlugins\RevitSketchPoC\`.
2. Copia `deploy\pluginsettings.json` (ou a tua cópia segura) para a **mesma pasta** da DLL.
3. Copia `deploy\RevitSketchPoC.addin` para `%AppData%\Autodesk\Revit\Addins\2025\` (ajusta o ano).
4. Edita o `.addin` e substitui `C:\REPLACE_WITH_BUILD_OUTPUT\RevitSketchPoC.dll` pelo caminho real da DLL.
5. Reinicia o Revit.

## Teste na UI (WPF)

1. Abre um projeto no Revit.
2. Tab **Sketch AI PoC** → **Upload Sketch**.
3. Escolhe a imagem do esboço.
4. **Generate Model** — valida paredes, quartos e portas.

## Teste via bridge Node (MCP)

- Método TCP JSON-RPC exposto: **`create_house_from_sketch`**.
- O servidor MCP em `mcp-server-for-revit` encaminha essa tool para `REVIT_SKETCH_PORT`.
- Garante que `TcpPort` no JSON coincide com essa porta.

### Fluxo MCP + Ollama

`Cliente IA → MCP (Node) → TCP :8081 → RevitSketchPoC → HTTP Ollama (ex. /api/chat)`

O servidor Node **não** chama o Ollama; quem chama o LLM é o plugin C#.

## Ollama (recomendado para PoC sem chave cloud)

1. Instala [Ollama](https://ollama.com/) e garante o serviço em `localhost:11434`.
2. Faz pull de um modelo com **visão** adequado ao teu `OllamaModel` (ex.: `ollama pull llava` se usares `llava` no JSON).
3. Se usares modelos **cloud** da Ollama, verifica na documentação Ollama se precisas de login ou API key noutro sítio — isso **não** vai para o Git no `pluginsettings.json` como texto de chave se evitares duplicar segredos.

## Gemini (opcional)

- `"LlmProvider": "Gemini"`
- `GeminiApiKey` preenchida só localmente
- `GeminiModel` conforme a API Google

Sem chave válida com `Gemini`, o plugin deve reportar erro claro.

## Licença

Segue a licença indicada na raiz do repositório (`README.md` principal).
