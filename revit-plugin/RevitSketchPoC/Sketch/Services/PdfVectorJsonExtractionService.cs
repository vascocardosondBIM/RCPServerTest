using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace RevitSketchPoC.Sketch.Services
{
    public sealed class PdfVectorJsonExtractionResult
    {
        public string RawJsonPath { get; set; } = string.Empty;
        public string CleanJsonPath { get; set; } = string.Empty;
        public string SemanticReadyManifestPath { get; set; } = string.Empty;
        public string SemanticPixelsPath { get; set; } = string.Empty;
        public string TilesDirectoryPath { get; set; } = string.Empty;
        public string CleanJsonPreview { get; set; } = string.Empty;
    }

    /// <summary>
    /// Spike 1 helper inside the plugin:
    /// converts a vector PDF page into raw geometry JSON via Python + PyMuPDF.
    /// </summary>
    public static class PdfVectorJsonExtractionService
    {
        public static PdfVectorJsonExtractionResult Extract(
            string pdfPath,
            int pageNumber,
            int tileSizePt,
            int rasterDpi)
        {
            if (string.IsNullOrWhiteSpace(pdfPath) || !File.Exists(pdfPath))
            {
                throw new FileNotFoundException("PDF não encontrado.", pdfPath);
            }

            var requestedPage = pageNumber < 1 ? 1 : pageNumber;
            var normalizedTileSizePt = tileSizePt < 64 ? 64 : tileSizePt;
            var normalizedRasterDpi = rasterDpi < 72 ? 72 : rasterDpi;
            var tempDir = Path.Combine(Path.GetTempPath(), "RevitSketchPoC", "pdf-json");
            Directory.CreateDirectory(tempDir);

            var safeName = Path.GetFileNameWithoutExtension(pdfPath);
            foreach (var c in Path.GetInvalidFileNameChars())
            {
                safeName = safeName.Replace(c, '_');
            }

            var basePath = Path.Combine(
                tempDir,
                safeName + "_page" + requestedPage + "_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff"));

            var rawJsonPath = basePath + "_raw.json";
            var cleanJsonPath = basePath + "_clean.json";
            var semanticReadyManifestPath = basePath + "_semantic_ready_manifest.json";
            var semanticPixelsPath = basePath + "_semantic_pixels.json";
            var tilesDirectoryPath = basePath + "_tiles";

            var scriptPath = EnsurePythonScript(tempDir);
            RunPythonExtraction(
                scriptPath,
                pdfPath,
                rawJsonPath,
                cleanJsonPath,
                semanticReadyManifestPath,
                semanticPixelsPath,
                tilesDirectoryPath,
                normalizedTileSizePt,
                normalizedRasterDpi,
                requestedPage);

            // Guarantees the semantic contract template is structurally valid
            // before any downstream matching/calibration consumes it.
            SemanticPixelsValidator.ValidateTemplate(semanticPixelsPath, semanticReadyManifestPath);

            var preview = ReadPreview(cleanJsonPath, 30000);
            return new PdfVectorJsonExtractionResult
            {
                RawJsonPath = rawJsonPath,
                CleanJsonPath = cleanJsonPath,
                SemanticReadyManifestPath = semanticReadyManifestPath,
                SemanticPixelsPath = semanticPixelsPath,
                TilesDirectoryPath = tilesDirectoryPath,
                CleanJsonPreview = preview
            };
        }

        private static string EnsurePythonScript(string tempDir)
        {
            var scriptPath = Path.Combine(tempDir, "extract_pdf_vector_json.py");
            // Always rewrite to ensure latest embedded script logic is used,
            // even when older versions already exist in %TEMP%.
            var script = @"
import fitz, json, sys, datetime, math, os

def r(v):
    return round(float(v), 4)

def pxy(p):
    return {'x': r(p.x), 'y': r(p.y)}

def rect_to_bbox(rect):
    return [r(rect.x0), r(rect.y0), r(rect.x1), r(rect.y1)]

pdf_path = sys.argv[1]
out_raw = sys.argv[2]
out_clean = sys.argv[3]
out_manifest = sys.argv[4]
out_semantic_pixels = sys.argv[5]
tiles_dir = sys.argv[6]
tile_size_pt = float(sys.argv[7])
tile_raster_dpi = float(sys.argv[8])
page_num = int(sys.argv[9])

doc = fitz.open(pdf_path)
if page_num < 1 or page_num > doc.page_count:
    raise ValueError(f'pdfPageNumber out of range. Requested={page_num}, pages={doc.page_count}')

page = doc.load_page(page_num - 1)
drawings = page.get_drawings()
words = page.get_text('words')

lines = []
beziers = []
rectangles = []
quads = []
others = []

for di, drawing in enumerate(drawings):
    items = drawing.get('items', [])
    for item in items:
        if not item:
            continue
        op = item[0]
        if op == 'l' and len(item) >= 3:
            lines.append({
                'from': pxy(item[1]),
                'to': pxy(item[2]),
                'stroke_width_pt': r(drawing.get('width', 0) or 0),
                'drawing_index': di
            })
        elif op == 'c' and len(item) >= 5:
            beziers.append({
                'p1': pxy(item[1]),
                'p2': pxy(item[2]),
                'p3': pxy(item[3]),
                'p4': pxy(item[4]),
                'stroke_width_pt': r(drawing.get('width', 0) or 0),
                'drawing_index': di
            })
        elif op == 're' and len(item) >= 2:
            rectangles.append({
                'bbox_pt': rect_to_bbox(item[1]),
                'stroke_width_pt': r(drawing.get('width', 0) or 0),
                'drawing_index': di
            })
        elif op == 'qu' and len(item) >= 2:
            quad = item[1]
            pts = []
            for attr in ('ul', 'ur', 'll', 'lr'):
                if hasattr(quad, attr):
                    pts.append(pxy(getattr(quad, attr)))
            quads.append({
                'points': pts,
                'stroke_width_pt': r(drawing.get('width', 0) or 0),
                'drawing_index': di
            })
        else:
            others.append({
                'opcode': str(op),
                'drawing_index': di
            })

text_words = []
for w in words:
    x0, y0, x1, y1, text, block_no, line_no, word_no = w
    text_words.append({
        'text': text,
        'bbox_pt': [r(x0), r(y0), r(x1), r(y1)],
        'block_no': int(block_no),
        'line_no': int(line_no),
        'word_no': int(word_no)
    })

raw_result = {
    'source_pdf': pdf_path,
    'generated_at_utc': datetime.datetime.utcnow().isoformat() + 'Z',
    'units': {'coordinates': 'pdf_points'},
    'summary': {
        'pages': doc.page_count,
        'selected_page_number': page_num,
        'is_vector_page': len(drawings) > 0,
        'drawings_count': len(drawings),
        'text_words_count': len(text_words)
    },
    'page': {
        'page_index': page_num - 1,
        'page_number': page_num,
        'width_pt': r(page.rect.width),
        'height_pt': r(page.rect.height),
        'rotation_degrees': int(page.rotation),
        'geometry': {
            'lines': lines,
            'beziers': beziers,
            'rectangles': rectangles,
            'quads': quads,
            'other_path_commands': others
        },
        'text_words': text_words
    }
}

def norm_point(pt, rotation, width, height):
    x = float(pt['x'])
    y = float(pt['y'])
    if rotation == 90:
        nx = y
        ny = height - x
    elif rotation == 180:
        nx = width - x
        ny = height - y
    elif rotation == 270:
        nx = width - y
        ny = x
    else:
        nx = x
        ny = y

    # Clamp to page bounds to avoid small negative/overflow noise after transforms.
    if nx < 0:
        nx = 0.0
    elif nx > width:
        nx = width

    if ny < 0:
        ny = 0.0
    elif ny > height:
        ny = height

    return {'x': r(nx), 'y': r(ny)}

def norm_bbox(b, rotation, width, height):
    pts = [
        {'x': b[0], 'y': b[1]},
        {'x': b[2], 'y': b[1]},
        {'x': b[2], 'y': b[3]},
        {'x': b[0], 'y': b[3]},
    ]
    npts = [norm_point(p, rotation, width, height) for p in pts]
    xs = [p['x'] for p in npts]
    ys = [p['y'] for p in npts]
    return [r(min(xs)), r(min(ys)), r(max(xs)), r(max(ys))]

rotation = int(page.rotation)
width = float(page.rect.width)
height = float(page.rect.height)

def line_length(ln):
    dx = ln['to']['x'] - ln['from']['x']
    dy = ln['to']['y'] - ln['from']['y']
    return math.hypot(dx, dy)

min_len_pt = 0.5
clean_lines = []
removed_zero = 0
removed_micro = 0

for ln in lines:
    nln = {
        'from': norm_point(ln['from'], rotation, width, height),
        'to': norm_point(ln['to'], rotation, width, height),
        'stroke_width_pt': ln.get('stroke_width_pt', 0),
        'drawing_index': ln.get('drawing_index')
    }
    L = line_length(nln)
    if L == 0:
        removed_zero += 1
        continue
    if L < min_len_pt:
        removed_micro += 1
        continue
    clean_lines.append(nln)

clean_beziers = []
for bz in beziers:
    clean_beziers.append({
        'p1': norm_point(bz['p1'], rotation, width, height),
        'p2': norm_point(bz['p2'], rotation, width, height),
        'p3': norm_point(bz['p3'], rotation, width, height),
        'p4': norm_point(bz['p4'], rotation, width, height),
        'stroke_width_pt': bz.get('stroke_width_pt', 0),
        'drawing_index': bz.get('drawing_index')
    })

clean_rectangles = []
for rc in rectangles:
    clean_rectangles.append({
        'bbox_pt': norm_bbox(rc['bbox_pt'], rotation, width, height),
        'stroke_width_pt': rc.get('stroke_width_pt', 0),
        'drawing_index': rc.get('drawing_index')
    })

clean_words = []
for wd in text_words:
    clean_words.append({
        'text': wd['text'],
        'bbox_pt': norm_bbox(wd['bbox_pt'], rotation, width, height),
        'block_no': wd['block_no'],
        'line_no': wd['line_no'],
        'word_no': wd['word_no']
    })

def bbox_for_line(ln):
    x0 = min(ln['from']['x'], ln['to']['x'])
    y0 = min(ln['from']['y'], ln['to']['y'])
    x1 = max(ln['from']['x'], ln['to']['x'])
    y1 = max(ln['from']['y'], ln['to']['y'])
    return [r(x0), r(y0), r(x1), r(y1)]

def bbox_for_bezier(bz):
    xs = [bz['p1']['x'], bz['p2']['x'], bz['p3']['x'], bz['p4']['x']]
    ys = [bz['p1']['y'], bz['p2']['y'], bz['p3']['y'], bz['p4']['y']]
    return [r(min(xs)), r(min(ys)), r(max(xs)), r(max(ys))]

def clamp_bbox_to_page(bbox, width, height):
    x0 = max(0.0, min(width, float(bbox[0])))
    y0 = max(0.0, min(height, float(bbox[1])))
    x1 = max(0.0, min(width, float(bbox[2])))
    y1 = max(0.0, min(height, float(bbox[3])))
    if x1 < x0:
        x0, x1 = x1, x0
    if y1 < y0:
        y0, y1 = y1, y0
    return [r(x0), r(y0), r(x1), r(y1)]

def tile_count_for_axis(size_pt, tile_pt):
    return int(math.ceil(size_pt / tile_pt))

def tile_id_from_row_col(row, col):
    return f'tile_r{row}_c{col}'

rows_count = tile_count_for_axis(height, tile_size_pt)
cols_count = tile_count_for_axis(width, tile_size_pt)
tile_index = {}

def ensure_tile(row, col):
    row = max(0, min(rows_count - 1, int(row)))
    col = max(0, min(cols_count - 1, int(col)))
    tile_id = tile_id_from_row_col(row, col)
    if tile_id not in tile_index:
        x0 = col * tile_size_pt
        y0 = row * tile_size_pt
        x1 = min(width, (col + 1) * tile_size_pt)
        y1 = min(height, (row + 1) * tile_size_pt)
        tile_index[tile_id] = {
            'tile_id': tile_id,
            'row': row,
            'col': col,
            'bbox_pt': [r(x0), r(y0), r(x1), r(y1)],
            'entity_refs': {
                'lines': [],
                'beziers': [],
                'rectangles': [],
                'text_words': []
            }
        }
    return tile_id

def tile_range_from_bbox(bbox):
    x0, y0, x1, y1 = clamp_bbox_to_page(bbox, width, height)
    eps = 1e-6
    col_min = int(max(0.0, x0) // tile_size_pt)
    row_min = int(max(0.0, y0) // tile_size_pt)
    col_max = int(max(0.0, x1 - eps) // tile_size_pt)
    row_max = int(max(0.0, y1 - eps) // tile_size_pt)
    col_max = min(cols_count - 1, col_max)
    row_max = min(rows_count - 1, row_max)
    return row_min, row_max, col_min, col_max

def add_entity_to_tiles(entity_type, entity_index, bbox):
    row_min, row_max, col_min, col_max = tile_range_from_bbox(bbox)
    for row in range(row_min, row_max + 1):
        for col in range(col_min, col_max + 1):
            tid = ensure_tile(row, col)
            tile_index[tid]['entity_refs'][entity_type].append(entity_index)

for idx, ln in enumerate(clean_lines):
    add_entity_to_tiles('lines', idx, bbox_for_line(ln))

for idx, bz in enumerate(clean_beziers):
    add_entity_to_tiles('beziers', idx, bbox_for_bezier(bz))

for idx, rc in enumerate(clean_rectangles):
    add_entity_to_tiles('rectangles', idx, rc['bbox_pt'])

for idx, wd in enumerate(clean_words):
    add_entity_to_tiles('text_words', idx, wd['bbox_pt'])

for tile in tile_index.values():
    tile['counts'] = {
        'lines': len(tile['entity_refs']['lines']),
        'beziers': len(tile['entity_refs']['beziers']),
        'rectangles': len(tile['entity_refs']['rectangles']),
        'text_words': len(tile['entity_refs']['text_words']),
    }
    tile['counts']['total'] = (
        tile['counts']['lines'] +
        tile['counts']['beziers'] +
        tile['counts']['rectangles'] +
        tile['counts']['text_words']
    )

clean_result = {
    'source_pdf': pdf_path,
    'generated_at_utc': datetime.datetime.utcnow().isoformat() + 'Z',
    'units': {'coordinates': 'pdf_points'},
    'normalization': {
        'applied': True,
        'rotation_degrees_original': rotation,
        'target_coordinate_system': 'derotated_page_space',
        'line_filter': {
            'min_length_pt': min_len_pt,
            'removed_zero_length': removed_zero,
            'removed_micro_segments': removed_micro
        }
    },
    'spatial_index': {
        'enabled': True,
        'tile_size_pt': r(tile_size_pt),
        'rows': rows_count,
        'cols': cols_count,
        'tile_count_with_content': len(tile_index)
    },
    'summary': {
        'pages': doc.page_count,
        'selected_page_number': page_num,
        'is_vector_page': len(drawings) > 0,
        'drawings_count': len(drawings),
        'text_words_count': len(text_words),
        'clean_lines_count': len(clean_lines),
        'clean_beziers_count': len(clean_beziers),
        'clean_rectangles_count': len(clean_rectangles),
        'tiles_with_content': len(tile_index)
    },
    'page': {
        'page_index': page_num - 1,
        'page_number': page_num,
        'width_pt': r(width),
        'height_pt': r(height),
        'rotation_degrees': 0,
        'geometry': {
            'lines': clean_lines,
            'beziers': clean_beziers,
            'rectangles': clean_rectangles,
            'quads': [],
            'other_path_commands': others
        },
        'text_words': clean_words
    },
    'tile_index': sorted(tile_index.values(), key=lambda t: (t['row'], t['col']))
}

with open(out_raw, 'w', encoding='utf-8') as f:
    json.dump(raw_result, f, ensure_ascii=False, indent=2)

with open(out_clean, 'w', encoding='utf-8') as f:
    json.dump(clean_result, f, ensure_ascii=False, indent=2)

os.makedirs(tiles_dir, exist_ok=True)
scale = tile_raster_dpi / 72.0
matrix = fitz.Matrix(scale, scale)
tile_images = []
for tile in sorted(tile_index.values(), key=lambda t: (t['row'], t['col'])):
    if tile['counts']['total'] <= 0:
        continue
    x0, y0, x1, y1 = tile['bbox_pt']
    clip = fitz.Rect(float(x0), float(y0), float(x1), float(y1))
    pix = page.get_pixmap(matrix=matrix, clip=clip, alpha=False)
    file_name = tile['tile_id'] + '.png'
    image_path = os.path.join(tiles_dir, file_name)
    pix.save(image_path)
    tile_images.append({
        'tile_id': tile['tile_id'],
        'row': tile['row'],
        'col': tile['col'],
        'bbox_pt': tile['bbox_pt'],
        'bbox_px': [0, 0, int(pix.width), int(pix.height)],
        'image_path': image_path,
        'image_width_px': int(pix.width),
        'image_height_px': int(pix.height)
    })

manifest = {
    'source_pdf': pdf_path,
    'generated_at_utc': datetime.datetime.utcnow().isoformat() + 'Z',
    'selected_page_number': page_num,
    'tile_strategy': {
        'tile_size_pt': r(tile_size_pt),
        'raster_dpi': r(tile_raster_dpi),
        'scale_px_per_pt': r(scale)
    },
    'page': {
        'width_pt': r(width),
        'height_pt': r(height),
        'rotation_degrees': 0
    },
    'artifacts': {
        'raw_json_path': out_raw,
        'clean_json_path': out_clean,
        'semantic_pixels_path': out_semantic_pixels,
        'tiles_dir': tiles_dir
    },
    'tiles': tile_images
}

semantic_pixels = {
    'schema': 'semantic_pixels.v1',
    'generated_at_utc': datetime.datetime.utcnow().isoformat() + 'Z',
    'source_pdf': pdf_path,
    'page': page_num,
    'contract': {
        'required_fields': ['type', 'confidence', 'bbox', 'page', 'tile_id'],
        'bbox_format': '[x0, y0, x1, y1] in pixels relative to the tile image',
        'type_allowed_examples': ['wall', 'door', 'window', 'room', 'stairs', 'column', 'other'],
        'confidence_range': [0.0, 1.0]
    },
    'detections': []
}

with open(out_manifest, 'w', encoding='utf-8') as f:
    json.dump(manifest, f, ensure_ascii=False, indent=2)

with open(out_semantic_pixels, 'w', encoding='utf-8') as f:
    json.dump(semantic_pixels, f, ensure_ascii=False, indent=2)

print(out_raw)
print(out_clean)
print(out_manifest)
print(out_semantic_pixels)
doc.close()
";

            File.WriteAllText(scriptPath, script, Encoding.UTF8);
            return scriptPath;
        }

        private static void RunPythonExtraction(
            string scriptPath,
            string pdfPath,
            string rawJsonPath,
            string cleanJsonPath,
            string semanticReadyManifestPath,
            string semanticPixelsPath,
            string tilesDirectoryPath,
            int tileSizePt,
            int rasterDpi,
            int pageNumber)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "python",
                Arguments =
                    "\"" + scriptPath + "\" " +
                    "\"" + pdfPath + "\" " +
                    "\"" + rawJsonPath + "\" " +
                    "\"" + cleanJsonPath + "\" " +
                    "\"" + semanticReadyManifestPath + "\" " +
                    "\"" + semanticPixelsPath + "\" " +
                    "\"" + tilesDirectoryPath + "\" " +
                    tileSizePt + " " +
                    rasterDpi + " " +
                    pageNumber,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            var stdOut = new StringBuilder();
            var stdErr = new StringBuilder();

            using (var process = new Process { StartInfo = startInfo })
            {
                process.OutputDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                    {
                        stdOut.AppendLine(e.Data);
                    }
                };
                process.ErrorDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                    {
                        stdErr.AppendLine(e.Data);
                    }
                };

                if (!process.Start())
                {
                    throw new InvalidOperationException("Falha ao iniciar Python para extração vetorial.");
                }

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                const int timeoutMs = 180000;
                if (!process.WaitForExit(timeoutMs))
                {
                    try
                    {
                        process.Kill();
                    }
                    catch
                    {
                        // ignored
                    }

                    throw new InvalidOperationException("Timeout ao gerar JSON vetorial (180s).");
                }

                if (process.ExitCode != 0 ||
                    !File.Exists(rawJsonPath) ||
                    !File.Exists(cleanJsonPath) ||
                    !File.Exists(semanticReadyManifestPath) ||
                    !File.Exists(semanticPixelsPath) ||
                    !Directory.Exists(tilesDirectoryPath))
                {
                    throw new InvalidOperationException(
                        "Falha a gerar JSON vetorial do PDF.\n" +
                        "A extração agora gera raw/clean + semantic_ready_manifest + semantic_pixels + tiles.\n" +
                        "Confirma Python + PyMuPDF instalados (`python -m pip install pymupdf`).\n" +
                        "stderr: " + stdErr.ToString().Trim() + "\n" +
                        "stdout: " + stdOut.ToString().Trim());
                }
            }
        }

        private static string ReadPreview(string jsonPath, int maxChars)
        {
            using (var stream = new FileStream(jsonPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, leaveOpen: false))
            {
                var buffer = new char[maxChars];
                var read = reader.ReadBlock(buffer, 0, buffer.Length);
                var content = new string(buffer, 0, read);

                if (stream.Length > maxChars)
                {
                    content += Environment.NewLine + Environment.NewLine +
                               "... (preview truncado; usa \"Guardar JSON\" para obter o ficheiro completo)";
                }

                return content;
            }
        }
    }
}
