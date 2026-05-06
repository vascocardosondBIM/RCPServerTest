# Spike 1 - PDF Vetorial para JSON (RevitSketchPoC)

Este documento descreve o que foi implementado no **Spike 1**, como usar, o estado de qualidade atual e o que falta para evoluir para o Spike 2.

---

## Objetivo do Spike 1

Extrair dados geométricos brutos de plantas em PDF **sem interpretação semântica por IA**:

- linhas
- curvas (bezier)
- retângulos
- texto e bbox

Saída esperada:

- `raw_geometry` (auditoria completa)
- `clean_geometry` (otimizado para pipeline)

---

## O que foi implementado

### 1) UI dedicada no Revit

Foi criada uma interface separada para Spike 1 (sem misturar com o fluxo de sketch):

- botão no ribbon: **Spike 1 PDF->JSON**
- seleção de ficheiro PDF
- seleção de página (`pdfPageNumber`, base 1)
- seleção via dropdown de `tile_size_pt` (presets)
- seleção via dropdown de `raster_dpi` (presets)
- botão para gerar JSON
- botão para executar Spike 2 (LLM por tile)
- preview do JSON gerado na própria janela
- botão para guardar JSON em caminho escolhido
- botão para abrir pasta dos JSONs

Arquivos principais:

- `Spike1/Commands/PdfSpike1ExternalCommand.cs`
- `Spike1/ViewModels/PdfSpike1ViewModel.cs`
- `Spike1/Views/PdfSpike1Window.cs`
- `Spike1/Views/PdfSpike1Window.xaml`

### 2) Serviço de extração vetorial

Serviço responsável por chamar Python + PyMuPDF para extrair vetores:

- `Sketch/Services/PdfVectorJsonExtractionService.cs`

Esse serviço agora gera **quatro artefactos**:

- `*_raw.json`
- `*_clean.json`
- `*_semantic_ready_manifest.json`
- `*_semantic_pixels.json`
- pasta `*_tiles/` com os PNGs por região

### 3) Clean pass + indexação espacial

No `clean.json`, já aplicamos:

- normalização de rotação para `rotation_degrees = 0`
- remoção de linhas degeneradas (`length == 0`)
- remoção de micro-segmentos (`length < 0.5 pt`)
- clamp de coordenadas para limites da página:
  - `x in [0 .. width_pt]`
  - `y in [0 .. height_pt]`
- indexação espacial por tiles (`tile_index`) para consumo parcial por região no Spike 2

### 4) Raster controlado por tile

Além de `raw` e `clean`, o Spike 1 agora também gera:

- `*_semantic_ready_manifest.json`
- `*_semantic_pixels.json`
- pasta `*_tiles/` com PNG por tile (`tile_r{row}_c{col}.png`)

O manifesto referencia os artefactos e traz metadados de alinhamento:

- `tile_size_pt`
- `raster_dpi`
- `scale_px_per_pt`
- `bbox_pt` da região e dimensões em pixel da imagem do tile

### 5) Contrato semântico fixo (`semantic_pixels.json`)

Foi adicionado o artefacto `*_semantic_pixels.json` com schema estável para o Spike 2:

- `type`
- `confidence`
- `bbox`
- `page`
- `tile_id`

Estrutura:

- `schema`: versão do contrato (`semantic_pixels.v1`)
- `contract`: regras de formato (campos obrigatórios e formato de bbox)
- `detections`: lista de deteções semânticas (inicialmente vazia, para ser preenchida pelo LLM/tile worker)

Exemplo de item em `detections`:

```json
{
  "type": "wall",
  "confidence": 0.92,
  "bbox": [18, 42, 224, 67],
  "page": 1,
  "tile_id": "tile_r0_c1"
}
```

### 6) Validador do retorno do LLM (pré matching/calibração)

Foi adicionado um validador dedicado em:

- `Sketch/Services/SemanticPixelsValidator.cs`

Validações aplicadas antes de avançar no pipeline:

- `schema == semantic_pixels.v1`
- presença de `detections` como array
- `type` obrigatório (string não vazia)
- `confidence` numérico em `[0.0 .. 1.0]`
- `bbox` no formato `[x0, y0, x1, y1]` com `x1 > x0` e `y1 > y0`
- `page` inteiro positivo (e consistente com a página do documento quando aplicável)
- `tile_id` existente no `semantic_ready_manifest.json`
- `bbox` dentro dos limites de pixel do tile (`image_width_px`, `image_height_px`)

Uso previsto para Spike 2:

- `ValidateAndPersistFromLlm(llmResponseText, semanticPixelsPath, manifestPath, expectedPage)` para validar e persistir deteções retornadas pelo LLM.

No Spike 1 atual, o contrato-base também é validado automaticamente após a geração dos artefactos.

Exemplo de erro de validação (mensagens típicas):

- `semantic_pixels schema inválido. Esperado "semantic_pixels.v1" ...`
- `detections[3].confidence deve estar em [0.0, 1.0].`
- `detections[1].bbox deve ter 4 números [x0,y0,x1,y1].`
- `detections[2].bbox inválido: precisa de x1>x0 e y1>y0.`
- `detections[0].tile_id inválido: "tile_r9_c9" não existe no manifest.`
- `detections[4].bbox está fora dos limites do tile ...`

### 7) Pós-processamento geométrico (matching + snap)

Foi adicionado o serviço:

- `Sketch/Services/SemanticGeometryMatcher.cs`

Objetivo:

- reduzir erro do LLM fazendo snap da `bbox` semântica para geometria real do `clean.json` (linhas e retângulos).

Como funciona:

- converte `bbox` do tile (px) para coordenadas da página (pt) usando `semantic_ready_manifest.json`;
- procura candidato geométrico mais próximo via combinação de IoU + distância de centros;
- aplica snap e persiste:
  - `bbox` (atualizada em px do tile),
  - `bbox_original_pt`,
  - `bbox_snapped_pt`,
  - `is_snapped`,
  - `snap_source` (`line`/`rectangle`),
  - `snap_source_index`,
  - `snap_score`.

Uso previsto para Spike 2:

- `MatchAndPersist(semanticPixelsPath, cleanJsonPath, manifestPath, maxSnapDistancePt)`

### 8) Execução ponta a ponta do Spike 2 (já integrada no UI)

A janela do Spike 1 agora executa também a etapa semântica:

- botão: **Executar Spike 2 (LLM)**
- serviço: `Sketch/Services/SemanticTileInferenceService.cs`

Fluxo executado pelo botão:

1. percorre os tiles do `semantic_ready_manifest.json`;
2. chama o provider configurado no `pluginsettings.json` (`Ollama`, `Gemini` ou `Nvidia`) para cada imagem de tile;
3. agrega deteções em memória (`type`, `confidence`, `bbox`, `page`, `tile_id`);
4. valida e persiste no `semantic_pixels.json` com `SemanticPixelsValidator`;
5. aplica snap geométrico com `SemanticGeometryMatcher`;
6. aplica calibração explícita com `SemanticCalibrationService` e exporta coordenadas reais.

Resultado:

- `semantic_pixels.json` é atualizado com deteções validadas e metadados de snap.
- `*_semantic_real_world.json` é gerado com coordenadas finais em metros.
- `*_semantic_metrics.json` é gerado com métricas de qualidade e observabilidade.

Pré-requisitos para esta etapa:

- `pluginsettings.json` válido na pasta da DLL;
- provider configurado em `LlmProvider` (`Ollama`, `Gemini` ou `Nvidia`);
- credenciais/chaves do provider preenchidas quando aplicável;
- modelo com suporte a visão (imagem + texto).

Onde a integração está no código:

- UI/comando: `Spike1/Commands/PdfSpike1ExternalCommand.cs`
- orquestração por tile: `Sketch/Services/SemanticTileInferenceService.cs`
- validação de contrato: `Sketch/Services/SemanticPixelsValidator.cs`
- snap geométrico: `Sketch/Services/SemanticGeometryMatcher.cs`
- calibração: `Sketch/Services/SemanticCalibrationService.cs`
- métricas: `Sketch/Services/SemanticQualityMetricsService.cs`

Saída esperada após executar Spike 2:

- `semantic_pixels.json` com:
  - `detections` preenchidas pelo LLM;
  - campos de pós-processamento (`is_snapped`, `bbox_snapped_pt`, `snap_source`, etc.);
  - bloco `matching` com métricas (`matched_count`, `unmatched_count`).
- `semantic_real_world.json` com:
  - bloco `calibration` (`method`, `meters_per_point`, `scale_denominator`);
  - bloco `real_coordinates` em metros;
  - por deteção: `bbox_real_m`, `center_real_m`.
- `semantic_metrics.json` com:
  - `match_precision`;
  - `unmatched_rate`;
  - `calibration_error` (`absolute_error_m`, `error_percent`, `note`);
  - contagens (`total`, `matched`, `unmatched`, `counts_by_type`).

Notas importantes:

- o spike 2 processa tiles sequencialmente no estado atual (foco em robustez/validade);
- se o LLM retornar JSON inválido ou fora do contrato, a execução para com erro explícito de validação;
- os erros são exibidos no estado da janela para facilitar troubleshooting.

Modos de calibração disponíveis no UI:

- `AutoScale`: tenta detetar `1:N` no texto da planta (ex.: `1:100`) e usa fallback da escala manual.
- `ManualScale`: usa o valor de `Escala manual (1:N)`.
- `ReferencePoints`: usa pontos conhecidos (`p1x`, `p1y`, `p2x`, `p2y`) + `distance_m`.

Conversão aplicada para coordenada real:

- base física: `1 pt = 1/72 in = 0.0254/72 m`;
- por escala (`1:N`): `meters_per_point = N * (0.0254 / 72)`;
- por referência: `meters_per_point = distance_m / distance_pt(p1,p2)`.

Quando usar cada modo:

- `AutoScale`: plantas com escala impressa legível.
- `ManualScale`: escala conhecida, mas não legível no PDF.
- `ReferencePoints`: melhor opção quando há duas referências fiáveis medidas.

Checklist rápido de validação da calibração:

- confirmar `page_width_m`/`page_height_m` em `real_coordinates`;
- validar 2-3 `bbox_real_m` de elementos conhecidos;
- se houver erro sistemático, repetir com `ReferencePoints`.

Presets atuais no UI:

- `preset`: `Rápido`, `Balanceado`, `Alta precisão` (e `Customizado` quando a combinação não corresponde a um preset)
- `tile_size_pt`: `192`, `256`, `384`, `512`
- `raster_dpi`: `200`, `300`, `400`

Mapeamento dos presets:

- `Rápido` -> `tile_size_pt=384`, `raster_dpi=200`
- `Balanceado` -> `tile_size_pt=256`, `raster_dpi=300` (default)
- `Alta precisão` -> `tile_size_pt=192`, `raster_dpi=400`

Comportamento do preset:

- ao trocar o `preset`, os dropdowns de `tile_size_pt` e `raster_dpi` são atualizados automaticamente;
- ao trocar manualmente `tile_size_pt`/`raster_dpi`, se a combinação não bater com os 3 perfis, o preset passa para `Customizado`;
- os valores escolhidos são enviados no request e registrados no `semantic_ready_manifest.json`.

---

## Fluxo atual no plugin

1. Utilizador abre `Spike 1 PDF->JSON`.
2. Seleciona PDF e página.
3. Escolhe preset (`Rápido`, `Balanceado`, `Alta precisão`) ou ajusta `tile_size_pt` e `raster_dpi`.
4. Clica **Gerar JSON PDF**.
5. Plugin chama Python (script temporário) com PyMuPDF.
6. São gerados os artefactos:
   - `*_raw.json`
   - `*_clean.json`
   - `*_semantic_ready_manifest.json`
   - `*_semantic_pixels.json`
   - `*_tiles/*.png`
7. (Opcional) Clica **Executar Spike 2 (LLM)** para inferência semântica por tile + validação + matching.
8. O Spike 2 aplica calibração explícita e exporta `*_semantic_real_world.json`.
9. UI mostra preview do `clean`.
10. Utilizador pode:
   - guardar JSON para outro local
   - abrir pasta dos JSONs

### Exemplo de execução (resumo no status)

Após clicar em **Executar Spike 2 (LLM)**, o status apresenta uma linha final no formato:

- `Spike 2 concluído. tiles=<N>, detections=<N>, snapped=<N>, unmatched=<N>.`

e indica o caminho do ficheiro atualizado:

- `SEMANTIC PIXELS atualizado: <caminho>`
- `Calibração: <método>. Saída real-world: <caminho>`
- `Métricas: precision=<v>, unmatched_rate=<v>, calibration_error_pct=<v|n/a>`
- `METRICS: <caminho>`

Exemplos de erro de calibração (mensagens típicas):

- `Não foi possível detetar escala automaticamente. Use ManualScale ou ReferencePoints.`
- `Scale denominator deve ser > 0.`
- `ReferenceDistanceMeters deve ser > 0.`
- `Pontos de referência inválidos: distância em pt deve ser > 0.`

Pasta padrão de output temporário:

- `%TEMP%/RevitSketchPoC/pdf-json/`

---

## Qualidade atual (resultado validado)

Para o PDF de teste `6976-ARQ-AP-0100-TIPOT1-R00.pdf`:

- `raw`: completo, pesado e com ruído (esperado para auditoria)
- `clean`: reduzido, sem degenerados e sem coordenadas fora da página

Conclusão:

- **`clean.json` está pronto para ser usado como entrada técnica do Spike 2.**

---

## Dependências locais

Necessário na máquina com Revit:

- Python disponível no PATH
- PyMuPDF instalado:

```bash
python -m pip install pymupdf
```

---

## Limitações atuais

- extração ainda baseada em script Python externo (invocado pelo plugin)
- em plantas muito grandes, pode ser necessário ajustar `tile_size_pt` e `raster_dpi` para equilibrar qualidade/custo

---

## Melhorias recomendadas para passar ao Spike 2

### A) Dados e performance

- ✅ **Tile/index espacial no clean** implementado (`tile_index` com `tile_id`, `bbox_pt`, refs e contagens).
- ✅ **Raster controlado por tile** implementado (PNG por tile + manifesto semantic-ready).

### B) Contrato semântico

- ✅ **Schema semântico fixo** (`semantic_pixels.json`) implementado com:
  - `type`
  - `confidence`
  - `bbox`
  - `page`
  - `tile_id`
- ✅ **Validador de retorno do LLM** implementado (`SemanticPixelsValidator`) antes de matching/calibração.
- ✅ **Integração no fluxo de inferência por tile** implementada (`SemanticTileInferenceService` + botão no UI).

### C) Pós-processamento geométrico

- ✅ **Matching geométrico** implementado em serviço dedicado (`SemanticGeometryMatcher`).
- ✅ **Acoplado automaticamente** após validação no fluxo do Spike 2 por tile.

### D) Calibração

- ✅ **Calibração explícita** implementada de pixel para coordenada real:
  - por escala detectada (`1:100`, etc.)
  - por escala manual (`1:N`)
  - por pontos de referência conhecidos
- ✅ saída final em coordenada real consistente (`*_semantic_real_world.json`).

### E) Qualidade e observabilidade

- ✅ **Métricas automáticas** implementadas:
  - `precision de match` (`match_precision`)
  - `taxa de elementos não casados` (`unmatched_rate`)
  - `erro de escala/calibração` (`calibration_error.error_percent`)
- ✅ export em artefato dedicado (`*_semantic_metrics.json`).

---

## Próximo marco sugerido

Como o `semantic_ready_manifest.json` e os tiles já estão implementados, os próximos marcos sugeridos passam a ser:

- métricas por tile (além da visão agregada por página)
- painel de comparação entre execuções (baseline vs. nova execução)

Assim, o Spike 2 pode consumir o contexto por blocos menores, com custo e latência controlados.

---

## Base pronta(Spike 2)

Esta base já foi deixada pronta para continuidade do Spike 2 no repositório.

### O que já está implementado e utilizável

- inferência semântica por tile (LLM provider via `pluginsettings.json`);
- validação de contrato (`semantic_pixels.v1`);
- matching geométrico (snap bbox -> `clean.json`);
- calibração explícita para metros (`AutoScale`, `ManualScale`, `ReferencePoints`);
- métricas automáticas de qualidade/observabilidade.

### Arquivos-chave para continuidade

- `Sketch/Services/SemanticTileInferenceService.cs`
- `Sketch/Services/SemanticPixelsValidator.cs`
- `Sketch/Services/SemanticGeometryMatcher.cs`
- `Sketch/Services/SemanticCalibrationService.cs`
- `Sketch/Services/SemanticQualityMetricsService.cs`
- `Spike1/Commands/PdfSpike1ExternalCommand.cs`
- `Spike1/ViewModels/PdfSpike1ViewModel.cs`
- `Spike1/Views/PdfSpike1Window.xaml`
- `Sketch/Contracts/SketchContracts.cs`

### Artefatos do pipeline (saídas)

- `*_clean.json`
- `*_semantic_ready_manifest.json`
- `*_semantic_pixels.json`
- `*_semantic_real_world.json`
- `*_semantic_metrics.json`
- `*_tiles/*.png`

### Contrato semântico de referência

Campos por deteção em `semantic_pixels.json`:

- `type`
- `confidence`
- `bbox` (pixels do tile)
- `page`
- `tile_id`

### Próximos focos recomendados 

- tuning de prompts/modelos por tipologia de planta;
- paralelismo/batching de tiles para reduzir tempo de execução;
- métricas detalhadas por tile e comparação entre execuções;
- hardening para casos extremos (escala/texto pouco legível, ruído elevado).
