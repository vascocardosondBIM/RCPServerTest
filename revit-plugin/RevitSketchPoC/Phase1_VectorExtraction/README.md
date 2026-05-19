# Phase 1 — Raw JSON only (passo atual)

## O que corre

- **Entrada:** caminho do PDF + número da página (base 1).
- **Processo:** Python + PyMuPDF — `page.get_drawings()` e `page.get_text('words')`.
- **Saída:** uma pasta em `%TEMP%\RevitSketchPoC\phase1\{nome}_page{N}_{timestamp}\` com **`raw.json`**.
- **Schema:** `phase1_raw_min.v1` — `summary`, `page.geometry` (lines, beziers, rectangles, quads, other_path_commands), `page.text_words`.

## O que não corre (por agora)

- Raster/tiles, `clean.json`, topologia, manifest, semantic pixels, Parquet, grafo.

## CLI (teste manual)

```bash
python phase1_extract.py "C:\path\doc.pdf" "C:\out\folder" 1
```

## Dependências

```bash
python -m pip install pymupdf
```

Timeout no plugin: **20 minutos** (a extração só vetor costuma ser rápida).

## Resumo na UI

Após **Gerar Fase 1**, o ecrã mostra **contagens** (linhas, polilinhas, retângulos, hatches, words, blocos, spans, clean) e **cores distintas** (traço/preenchimento da geometria, com top cores por nº de entidades) em vez de preview JSON. Depois de exportar zonas, o resumo inclui **PDF completo** e **cada zona** em `regions/{id}/`.

## Zonas da folha (desenho vs. legendas)

1. **Gerar Fase 1** — cria `raster/preview/page.png` e JSON modular.
2. Abre o **editor de zonas** (automaticamente após gerar, ou botão **Definir zonas…**).
3. Arrasta retângulos sobre o preview (ou preset **2 colunas**).
4. **Exportar zonas** — grava `page_regions.json` e, por zona, em `regions/{id}/`:
   - `page.png` (recorte)
   - `geometry/*`, `text/*`, `topology/*` filtrados
   - `clean_slice.json`
5. **Exportar por cor** (editor de zonas) — por zona seleccionada ou todas:
   - **Render fiel** da zona (`get_pixmap` clip, DPI 300) + **máscara por pixel** (tolerância RGB ~32)
   - Inclui texto, imagens, hachuras e traços finos que o replay vectorial não captava
   - Saída: `regions/{id}/by_color/zone_full_render.png`, `by_color/{RRGGBB}/page.png`, JSON filtrado, `by_color_manifest.json` (`schema`: `phase1.zone_by_color.v3`, `render_mode`: `pixel_mask`)

## Exportar

O botão **Exportar todo o output…** copia a pasta da corrida (JSON, raster, `regions/`, etc.).
\