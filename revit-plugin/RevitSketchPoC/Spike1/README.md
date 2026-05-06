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
- botão para gerar JSON
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

Esse serviço gera **duas saídas**:

- `*_raw.json`
- `*_clean.json`

### 3) Clean pass implementado

No `clean.json`, já aplicamos:

- normalização de rotação para `rotation_degrees = 0`
- remoção de linhas degeneradas (`length == 0`)
- remoção de micro-segmentos (`length < 0.5 pt`)
- clamp de coordenadas para limites da página:
  - `x in [0 .. width_pt]`
  - `y in [0 .. height_pt]`

---

## Fluxo atual no plugin

1. Utilizador abre `Spike 1 PDF->JSON`.
2. Seleciona PDF e página.
3. Clica **Gerar JSON PDF**.
4. Plugin chama Python (script temporário) com PyMuPDF.
5. São gerados dois ficheiros:
   - `*_raw.json`
   - `*_clean.json`
6. UI mostra preview do `clean`.
7. Utilizador pode:
   - guardar JSON para outro local
   - abrir pasta dos JSONs

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
- `clean.json` ainda grande para mandar inteiro para LLM em cenários maiores
- ainda sem indexação espacial/tile nativa no output

---

## Melhorias recomendadas para passar ao Spike 2

### A) Dados e performance

- **Tile/index espacial no clean** (grade por regiões) para não mandar ~24 MB de contexto de uma vez ao LLM.
- **Raster controlado por tile** (gerar imagens por região) para alinhamento mais preciso `bbox(px) <-> geometria`.

### B) Contrato semântico

- **Schema semântico fixo** (`semantic_pixels.json`) com:
  - `type`
  - `confidence`
  - `bbox`
  - `page`
  - `tile_id`

### C) Pós-processamento geométrico

- **Matching geométrico** (snap de bbox em linhas/retângulos do clean) para reduzir erro do LLM.

### D) Calibração

- **Calibração explícita** de pixel para coordenada real:
  - por escala detectada (`1:100`, etc.)
  - ou por pontos de referência conhecidos
- saída final em coordenada real consistente.

### E) Qualidade e observabilidade

- **Métricas automáticas**:
  - precision de match
  - taxa de elementos não casados
  - erro de escala/calibração

---

## Próximo marco sugerido

Implementar um export adicional `semantic_ready_manifest.json` contendo:

- referência ao `clean.json`
- índice de tiles com bbox por tile
- caminhos das imagens por tile
- estatísticas básicas por tile

Assim, o Spike 2 pode consumir o contexto por blocos menores, com custo e latência controlados.
