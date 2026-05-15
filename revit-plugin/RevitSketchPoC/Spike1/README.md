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

- `**clean.json` está pronto para ser usado como entrada técnica do Spike 2.**

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

### Ordem recomendada de integração por steps (Spike 2)

Para uso em escritório de arquitetura (plantas grandes e heterogéneas), a ordem mais robusta é:

1. **Walls**
2. **Doors/Windows/Openings**
3. **Rooms (zoning/compartimentação)**
4. **Floors/Ceilings**
5. **Fixtures/Furniture** (sanitas, lavatórios, sofás, etc.)

Motivo desta ordem:

- `walls` define o esqueleto geométrico principal;
- `doors/windows/openings` depende diretamente de paredes;
- `rooms` usa paredes + aberturas para validar conectividade e zonas fechadas;
- `floors/ceilings` fica mais estável após ter loops de rooms consolidados;
- `fixtures/furniture` é menos estrutural e deve vir por último.

Observação:

- A ordem inicialmente sugerida com `Floors/Ceilings` antes de `Rooms` pode funcionar em casos simples, mas em plantas grandes tende a gerar mais correções posteriores.

### Estratégia recomendada para cada step (anti-contexto gigante)

- processar por `tile_id` (nunca enviar o `clean.json` inteiro para o LLM);
- usar schema semântico específico por step;
- executar validação + matching geométrico no fim de cada step;
- só avançar para o próximo step quando os critérios mínimos de qualidade forem atingidos.

---

## Proposta de UX/execução para integração Spike 1 + Spike 2

### Objetivo

Permitir dois estilos de operação no plugin:

- **Execução completa (Auto)**: gerar a casa toda de uma vez;
- **Execução guiada (Step-by-step)**: avançar por etapas com validação humana.

Isto melhora qualidade, auditabilidade e produtividade para plantas grandes e variadas.

### Fluxo proposto

1. utilizador faz upload do PDF;
2. sistema executa Spike 1 e prepara artefatos base (`clean`, `manifest`, `tiles`) em cache;
3. utilizador escolhe:
  - `Auto`: correr todos os steps em sequência;
  - `Guiado`: correr step atual, rever saída, confirmar e só depois avançar;
4. em cada step, sistema mostra loader/progresso e resultado parcial;
5. no fim, calibração + métricas + export final.

### Steps semânticos no modo guiado

1. `Walls`
2. `Doors/Windows/Openings`
3. `Rooms`
4. `Floors/Ceilings`
5. `Fixtures/Furniture`

### Confirmação por step (human-in-the-loop)

Após cada step, o utilizador deve poder:

- **Confirmar** (aceitar e avançar);
- **Refazer** (rerun do step com mesmo contexto);
- **Ajustar** (editar prompt/parâmetros e rerun);
- **Parar** (guardar estado para retomar depois).

### Loader e observabilidade por step

Mostrar no UI:

- step atual e progresso global (`step X/5`);
- tiles processados no step;
- contagens principais (`detections`, `matched`, `unmatched`);
- estado do step (`pending`, `running`, `done`, `failed`).

### Cache por job (recomendado)

Criar um `job_id` para cada execução e persistir:

- input do job (PDF + parâmetros);
- artefatos do Spike 1;
- outputs por step (semantic, real_world, metrics);
- status por step;
- logs/erros.

Benefícios:

- retomar execução sem recomputar tudo;
- rerodar só um step;
- comparar execuções (baseline vs. ajuste de prompt/modelo).

### Estrutura sugerida de estado (`job.json`)

Campos mínimos recomendados:

- `job_id`
- `created_at_utc`, `updated_at_utc`
- `input` (`pdf_path`, `page`, presets/parâmetros)
- `spike1_artifacts` (`clean`, `manifest`, `tiles_dir`)
- `steps[]`:
  - `name`
  - `status` (`pending/running/done/failed`)
  - `started_at_utc`, `ended_at_utc`
  - `artifacts` (paths de saída)
  - `metrics` (resumo)
  - `error` (se houver)
- `final_outputs` (`semantic_pixels`, `semantic_real_world`, `semantic_metrics`)

### Critério de aceite da proposta

- utilizador consegue escolher `Auto` ou `Guiado`;
- no modo guiado, cada step pode ser confirmado antes de avançar;
- execução pode ser retomada por `job_id`;
- rerun de step não invalida steps já confirmados;
- logs e métricas ficam visíveis por step.

### Mini backlog técnico (implementação)

#### Fase 1 — Fluxo base (prioridade alta)

1. **Modelo de estado do job (`job.json`)**
  - criar DTOs para `job`, `step`, `artifacts`, `metrics`, `error`;
  - serialização/leitura segura no disco;
  - convenção de paths por `job_id`.
2. **Persistência de job**
  - criar `JobStateStore` (create/load/update);
  - salvar checkpoints após cada step;
  - garantir retomada após falha/restart.
3. **Modo de execução (Auto vs Guiado)**
  - adicionar opção de modo no UI;
  - em `Auto`: pipeline completo sem pausa;
  - em `Guiado`: pausar após cada step e aguardar confirmação.
4. **Orquestrador por step**
  - criar serviço de orquestração (`StepPipelineOrchestrator`);
  - executar steps na ordem definida;
  - suportar `rerun` de step isolado.

#### Fase 2 — UX e controle (prioridade alta/média)

1. **Ações do utilizador por step**
  - botões: `Confirmar`, `Refazer`, `Ajustar`, `Parar`;
  - bloqueios de estado para evitar ações inválidas (ex.: confirmar step não concluído).
2. **Loader/progresso**
  - exibir `step X/5`, tiles processados e status (`pending/running/done/failed`);
  - atualizar progresso em tempo real durante inferência por tile.
3. **Resumo por step**
  - mostrar contagens (`detections`, `matched`, `unmatched`);
  - exibir erros curtos com link/path para logs completos.

#### Fase 3 — Observabilidade e robustez (prioridade média)

1. **Logs estruturados por job e step**
  - criar `logs/<job_id>/<step>.log`;
  - incluir timestamps, provider/model, tempo de execução e erros.
2. **Métricas por step**
  - persistir métricas parciais em cada step;
  - manter também métrica agregada final da página/job.
3. **Comparação de execuções**
  - permitir comparar baseline vs rerun (diferença de precision/unmatched/calibração).
4. **Hardening**
  - timeout/retry por tile;
    - política de falha parcial (continuar vs abortar);
    - validações defensivas para entradas incompletas.

### Definição de pronto (DoD)

- UI permite selecionar `Auto` e `Guiado`.
- `job.json` é criado e atualizado ao longo do pipeline.
- cada step pode ser confirmado ou rerodado sem perder estado.
- retomada por `job_id` funciona após fechar/reabrir plugin.
- logs e métricas por step ficam acessíveis no output do job.

---

## Fase 1 — progresso implementado (passo a passo + porquê)

### Passo 1 — Modelo de estado do job (`job.json`) ✅

O que foi implementado:

- DTOs completos em `Sketch/Contracts/JobStateContracts.cs` para:
  - job,
  - input,
  - paths,
  - artifacts,
  - steps,
  - métricas,
  - erro,
  - outputs finais.

Porquê foi feito assim:

- manter contrato de estado explícito e estável entre UI, serviços e persistência;
- facilitar retomada, auditoria e troubleshooting sem depender de memória em runtime;
- preparar evolução para modo guiado (`auto`/`guided`) sem refatoração estrutural.

### Passo 2 — Persistência segura + checkpoints por etapa ✅

O que foi implementado:

- store seguro em disco em `Sketch/Services/JobStateStore.cs`:
  - criação, leitura, try-load e save do job;
  - escrita atómica (`tmp` + replace/fallback);
  - normalização de `job_id`;
  - convenção de paths por job:
    - `%TEMP%/RevitSketchPoC/jobs/<job_id>/job.json`
    - `%TEMP%/RevitSketchPoC/jobs/<job_id>/artifacts/`
    - `%TEMP%/RevitSketchPoC/jobs/<job_id>/logs/`.
- checkpoints do pipeline em `Sketch/Services/JobPipelineCheckpointService.cs`:
  - cria job após Spike 1;
  - materializa/copia artefatos para pasta do job;
  - marca início/fim/erro da execução semântica (Spike 2).
- integração no comando UI:
  - `Spike1/Commands/PdfSpike1ExternalCommand.cs`
  - `job_id` exibido no status e propagado no request.
- request semântico atualizado:
  - `Sketch/Contracts/SketchContracts.cs` (`JobId`).
- viewmodel atualizado:
  - `Spike1/ViewModels/PdfSpike1ViewModel.cs` (armazenar e enviar `CurrentJobId`).

Porquê foi feito assim:

- evitar perda de contexto entre Spike 1 e Spike 2;
- garantir que os artefatos usados no Spike 2 ficam congelados por execução (`job_id`);
- permitir retomar/reprocessar com rastreabilidade por job;
- reduzir risco de corrupção de estado em escrita concorrente ou interrupções.
- execução completa mantém artefatos finais:
  - `semantic_pixels`
  - `semantic_real_world`
  - `semantic_metrics`

### Passo 3 — Modo de execução (Auto vs Guiado) ✅

O que foi implementado:

- UI com novo seletor de modo em `Spike1/Views/PdfSpike1Window.xaml` e binding em `Spike1/ViewModels/PdfSpike1ViewModel.cs`:
  - `Auto`
  - `Guided`.
- request semântico atualizado com `ExecutionMode`:
  - `Sketch/Contracts/SketchContracts.cs`.
- comando principal atualizado para respeitar o modo:
  - `Spike1/Commands/PdfSpike1ExternalCommand.cs`.
  - `Auto`: executa todos os steps semânticos na ordem definida.
  - `Guided`: executa apenas o próximo step pendente e pausa.
- checkpoint/state atualizado para fluxo guiado:
  - `Sketch/Services/JobPipelineCheckpointService.cs`.
  - obtém próximo step pendente por `job_id`;
  - marca start/finish por step;
  - mantém job em `paused` enquanto houver steps pendentes no modo guiado.
- inferência semântica evoluída para execução por step:
  - `Sketch/Services/SemanticTileInferenceService.cs`.
  - novo `RunStepAsync(...)` para rodar um único step;
  - novo `RunStepsAsync(...)` para rodar sequência completa;
  - prompt por step para limitar classes por fase (`walls`, `openings`, `rooms`, `floors_ceilings`, `fixtures_furniture`).

Como funciona no modo Guiado (pausa/confirm):

1. utilizador gera Spike 1 normalmente (cria `job_id`);
2. seleciona `Guided`;
3. clica em **Executar Spike 2 (LLM)**:
  - o plugin executa só o próximo step pendente;
  - atualiza artefatos/métricas;
  - pausa o job;
4. para confirmar e avançar, clica novamente no mesmo botão;
5. quando não houver mais step pendente, o job termina como `done`.

Porquê foi feito assim:

- reduzir risco operacional em plantas grandes (checkpoint por fase);
- permitir revisão incremental sem bloquear o fluxo `Auto`;
- reaproveitar `job_id` e estado persistido sem criar sessão paralela;
- preparar a próxima etapa (orquestrador dedicado + ações explícitas por step como `Confirmar/Refazer/Parar`).

### Passo 4 — Orquestrador por step (`StepPipelineOrchestrator`) ✅

O que foi implementado:

- novo serviço dedicado:
  - `Sketch/Services/StepPipelineOrchestrator.cs`
  - centraliza a ordem de execução por step e remove a lógica de fluxo de dentro do comando da UI.
- modos suportados no orquestrador:
  - `RunAutoAsync(...)`: executa todos os steps semânticos na sequência padrão;
  - `RunGuidedNextAsync(...)`: executa apenas o próximo step pendente;
  - `RerunStepAsync(...)`: permite rerodar um step isolado sem reset total do job.
- integração do comando:
  - `Spike1/Commands/PdfSpike1ExternalCommand.cs` agora delega execução para o orquestrador;
  - o comando ficou focado em UX/status (mensagens de progresso e resultado) e não mais em regra de pipeline.
- evolução dos checkpoints:
  - `Sketch/Services/JobPipelineCheckpointService.cs`
  - `MarkSemanticStarted(...)` passa a registrar modo de execução (`auto/guided`) sem acoplar start de step específico;
  - `MarkSemanticStepFinished(...)` agora calcula internamente se ainda há steps pendentes;
  - `ResetSemanticStepForRerun(...)` prepara um step para nova execução (`pending`, limpeza de métricas/artefatos de step).

Porquê foi feito assim:

- separar responsabilidades:
  - UI/comando cuida da interação com utilizador;
  - orquestrador cuida da regra de fluxo por step;
  - checkpoint service cuida de persistência/estado.
- reduzir risco de regressão:
  - com a lógica centralizada num único serviço, evita duplicação de regras (`hasMore`, próximo step, status final).
- preparar escala operacional:
  - plantas grandes exigem retomada, rerun granular e observabilidade por step;
  - o orquestrador vira ponto único para adicionar timeout/retry/políticas por step na fase seguinte.
- facilitar testes:
  - execução auto, guiada e rerun agora ficam em métodos explícitos, mais fáceis de validar de forma isolada.

### Padrão de documentação adotado (daqui para frente)

Para cada passo vamos manter sempre:

- **o que foi implementado** (arquivos e comportamento);
- **porquê foi implementado assim** (trade-offs e motivação técnica);
- **impacto no fluxo** (como usar e o que muda para o utilizador/equipe).

### Ajuste de robustez de provider — retries + diagnóstico de modelo (Nvidia) ✅

Contexto que levou à mudança:

- durante execução do Spike 2, o provider Nvidia retornou `502 Bad Gateway` no endpoint de chat completions;
- como o fluxo roda por tiles, erros transitórios de gateway podem interromper toda a pipeline;
- além disso, alguns modelos podem não aceitar payload multimodal (imagem), e isso precisa de mensagem mais explícita.

O que foi implementado:

- `Sketch/Services/SemanticTileInferenceService.cs`:
  - `PostJsonAsync(...)` com retry automático para erros transitórios (`429`, `500`, `502`, `503`, `504`);
  - backoff progressivo simples (`2s`, `4s`, `6s`), com limite de tentativas;
  - erro final inclui contagem de tentativas para facilitar troubleshooting.
- `InferWithNvidiaAsync(...)`:
  - quando o modelo configurado aparenta ser família `gpt`/`gpt-oss` e a chamada falha, o erro passa a sugerir causa provável:
    - modelo pode não suportar input de imagem para Spike 2;
    - necessidade de usar modelo VLM/multimodal em `NvidiaModel`.

Porquê foi feito assim:

- retry reduz falhas por instabilidade temporária de rede/gateway sem exigir intervenção manual;
- manter retry no cliente HTTP central evita duplicação em cada provider;
- mensagem direcionada de compatibilidade de modelo acelera diagnóstico e evita tentativa-cega em configs.

### Configuração NVIDIA avançada (thinking + reasoning budget) ✅

Contexto que levou à mudança:

- para testar `nvidia/nemotron-3-nano-omni-30b-a3b-reasoning`, precisávamos passar parâmetros adicionais de reasoning (`enable_thinking`, `reasoning_budget`) no payload OpenAI-like;
- antes disso, o plugin só enviava campos base (`model`, `messages`, `max_tokens`, etc.), sem suporte a estes knobs por configuração.

O que foi implementado:

- novos campos em `Core/Configuration/PluginSettings.cs`:
  - `NvidiaEnableThinking` (`bool`);
  - `NvidiaReasoningBudget` (`int`, `0` = desativado/default do provider).
- suporte a `extra_body` em todas as chamadas NVIDIA do plugin:
  - `Sketch/Services/SemanticTileInferenceService.cs` (Spike 2 por tile),
  - `Sketch/Services/NvidiaSketchInterpreter.cs` (Sketch-to-BIM),
  - `Chat/Services/LlmChatService.cs` (Assistente IA).
- atualização de exemplos de configuração:
  - `deploy/pluginsettings.example.json`,
  - `deploy/pluginsettings.json`.

Porquê foi feito assim:

- centralizar no `pluginsettings.json` permite ajustar reasoning sem recompilar;
- aplicar o mesmo contrato nos três pontos NVIDIA evita comportamento inconsistente entre Chat, Sketch e Spike 2;
- `reasoning_budget=0` como default mantém compatibilidade com modelos que não usam esse parâmetro.

### UX de execução do Spike 2 — janela dedicada de progresso ✅

Contexto que levou à mudança:

- na execução longa de Spike 2 (especialmente modo `Auto` em plantas grandes), acompanhar o progresso apenas na janela principal dificultava monitoramento;
- necessidade explícita de abrir uma segunda janela para acompanhar os steps em tempo real.

O que foi implementado:

- nova janela dedicada:
  - `Spike1/Views/Spike2ProgressWindow.cs`
  - abre automaticamente ao clicar em `Executar Spike 2 (LLM)`;
  - exibe log com timestamp (`HH:mm:ss`) para cada evento.
- integração no comando:
  - `Spike1/Commands/PdfSpike1ExternalCommand.cs`
  - todas as mensagens relevantes são espelhadas na janela principal e na janela de progresso;
  - ao fechar a janela principal, a janela de progresso é fechada junto.

Porquê foi feito assim:

- separa melhor o painel de configuração (janela principal) do acompanhamento de execução (janela dedicada);
- melhora observabilidade durante chamadas por tile e facilita troubleshooting ao vivo;
- reduz fricção no modo guiado, onde o utilizador executa múltiplos ciclos de step.

### Robustez de parsing NVIDIA — `message.content = null` ✅

Contexto que levou à mudança:

- com modelos reasoning, algumas respostas podem vir com `message.content = null` e `reasoning_content` preenchido;
- o parser anterior falhava diretamente com `Unexpected message.content shape: Null`.

O que foi implementado:

- atualização em `Core/OpenAiChatCompletionParser.cs`:
  - quando `content` é `null`, tenta usar `reasoning_content` **se** já vier em formato JSON;
  - se não houver JSON final, retorna erro explicando que o provider devolveu apenas reasoning sem resposta final utilizável.

Porquê foi feito assim:

- mantém compatibilidade com respostas multimodais/reasoning sem mascarar erro de contrato;
- evita crash opaco e devolve uma mensagem acionável (ex.: ajustar thinking/budget para este fluxo).

### Robustez de payload semântico por tile (JSON truncado/malformado) ✅

Contexto que levou à mudança:

- alguns tiles retornaram payload semântico truncado (`Unexpected end of content while loading JArray`), quebrando a execução inteira do Spike 2.

O que foi implementado:

- `Sketch/Services/SemanticTileInferenceService.cs`:
  - parser com recuperação de JSON balanceado (`{...}` / `[...]`) quando o retorno vem com texto extra;
  - fallback defensivo por tile: se um payload continuar inválido, o tile é ignorado e o pipeline segue com os demais.

Porquê foi feito assim:

- evita que um único tile malformado derrube o job completo;
- mantém processamento progressivo em plantas grandes, onde a robustez operacional é mais importante que falhar cedo por um tile;
- preserva qualidade global com validação final ainda aplicada sobre o agregado persistido.

