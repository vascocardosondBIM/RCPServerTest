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

## Exportar

O botão **Exportar todo o output…** copia a pasta da corrida (neste passo, sobretudo `raw.json`).
