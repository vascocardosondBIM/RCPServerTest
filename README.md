# mcp-server-for-revit

Servidor MCP (Node.js) para interagir com o Autodesk Revit a partir de assistentes de IA (Claude, Cursor, etc.).

Este repositório contém **duas coisas** que convém distinguir:

1. **O MCP “original” (bridge Node)** — alinhado com o ecossistema [mcp-servers-for-revit](https://github.com/mcp-servers-for-revit/mcp-servers-for-revit): expõe operações do Revit como **ferramentas MCP**; o servidor fala com o **plugin Revit oficial** por **TCP** (por defeito porta `8080`), não por WebSocket neste fluxo de bridge.
2. **A extensão deste fork** — **porta dupla** no bridge (`REVIT_PLUGIN_PORT` / `REVIT_SKETCH_PORT`) e o add-in separado **`RevitSketchPoC`** (C# / WPF) na pasta `revit-plugin/RevitSketchPoC/`, que escuta outra porta (por defeito `8081`) e trata apenas da tool **`create_house_from_sketch`** (interpretação de imagem + criação de paredes/quartos/portas no Revit), com **Ollama** ou **Gemini** configurável no plugin.

> [!NOTE]
> Para o fluxo completo precisas do **plugin Revit original** (porta `8080`) e, se usares o sketch por MCP ou UI, do **RevitSketchPoC** (porta `8081`). Instruções do plugin oficial: [mcp-servers-for-revit](https://github.com/mcp-servers-for-revit/mcp-servers-for-revit). Detalhe do PoC: [`revit-plugin/RevitSketchPoC/README.md`](revit-plugin/RevitSketchPoC/README.md).

## Segurança e Git

- **Não commits** ficheiros `.env`, chaves em JSON, nem cópias locais com segredos. O repositório inclui `.gitignore` para `node_modules/`, artefactos .NET (`bin/` / `obj/`), `.env*` e `pluginsettings.local.json`.
- O ficheiro `deploy/pluginsettings.json` do PoC deve manter **`GeminiApiKey` vazio** no Git; usa uma cópia local ou segue o [`pluginsettings.example.json`](revit-plugin/RevitSketchPoC/deploy/pluginsettings.example.json) como modelo.

## Setup (cliente MCP)

**Claude Code**

```bash
claude mcp add mcp-server-for-revit -- npx -y mcp-server-for-revit
```

**Claude Desktop** — `claude_desktop_config.json`:

```json
{
    "mcpServers": {
        "mcp-server-for-revit": {
            "command": "npx",
            "args": ["-y", "mcp-server-for-revit"],
            "env": {
                "REVIT_PLUGIN_PORT": "8080",
                "REVIT_SKETCH_PORT": "8081"
            }
        }
    }
}
```

Reinicia o cliente. O ícone de ferramentas indica que o MCP está ligado.

### Duas portas, dois plugins

| Variável de ambiente | Valor por defeito | Destino TCP |
| -------------------- | ----------------- | ----------- |
| `REVIT_PLUGIN_PORT` | `8080` | Plugin **original** — **todas** as tools exceto `create_house_from_sketch` |
| `REVIT_SKETCH_PORT` | `8081` | **RevitSketchPoC** — **apenas** `create_house_from_sketch` |

A tool `create_house_from_sketch` usa `REVIT_SKETCH_PORT` (ou `data.pluginPort` no pedido). O campo `pluginPort` **não** é enviado ao Revit (o bridge remove-o). As restantes tools usam `REVIT_PLUGIN_PORT`.

## Ferramentas MCP suportadas

| Tool | Descrição |
| ---- | ----------- |
| `get_current_view_info` | Informação da vista ativa |
| `get_current_view_elements` | Elementos na vista ativa |
| `get_available_family_types` | Tipos de família disponíveis |
| `get_selected_elements` | Elementos selecionados |
| `get_material_quantities` | Quantidades de materiais |
| `ai_element_filter` | Consulta inteligente ao modelo |
| `analyze_model_statistics` | Estatísticas do modelo |
| `create_point_based_element` | Elementos baseados em ponto |
| `create_line_based_element` | Elementos baseados em linha |
| `create_surface_based_element` | Elementos baseados em superfície |
| `create_grid` | Grelha |
| `create_level` | Níveis |
| `create_room` | Quartos |
| `create_dimensions` | Cotas |
| `create_structural_framing_system` | Estrutura em vigas |
| `delete_element` | Apagar por ID |
| `operate_element` | Operar (selecionar, cor, ocultar, etc.) |
| `color_elements` | Colorir por parâmetro |
| `tag_all_walls` / `tag_all_rooms` | Etiquetas |
| `export_room_data` | Exportar dados de quartos |
| `store_project_data` / `store_room_data` / `query_stored_data` | Metadados locais |
| `send_code_to_revit` | Enviar C# para executar no Revit |
| `create_house_from_sketch` | Imagem de esboço → interpretação LLM (**Ollama** ou **Gemini** no RevitSketchPoC) → paredes/quartos/portas |
| `say_hello` | Teste de ligação |

## Desenvolvimento (Node)

```bash
npm install
npm run build
```

## Sketch PoC (C# / WPF)

A implementação **atual** e suportada está em **`revit-plugin/RevitSketchPoC/`** (UI, router MCP TCP, Ollama/Gemini, transações Revit). A pasta `samples/RevitSketchPoC/` pode existir como referência antiga; segue o README do plugin em `revit-plugin/RevitSketchPoC/README.md`.

Fluxo resumido:

1. Utilizador envia imagem (ribbon **Sketch AI PoC** ou MCP `create_house_from_sketch`).
2. O plugin chama o LLM (Ollama ou Gemini conforme `pluginsettings.json`).
3. Resposta estruturada → criação de elementos numa `Transaction` no Revit.

## Licença

[MIT](https://github.com/mcp-servers-for-revit/mcp-servers-for-revit/blob/main/LICENSE)
