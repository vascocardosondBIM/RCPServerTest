"""
Phase 1 — pipeline JSON modular: raw.json legado + pastas metadata/geometry/text/topology/raster/semantic/raw/clean.
Uso: python phase1_extract.py <pdf_path> <output_dir> <page_number>
Requer: pip install pymupdf

Fase 1 (estável): extração, normalização, estruturação leve, bbox, IDs, raster sync, manifests.
Inferência pesada está DESLIGADA por defeito (ver PHASE1_FEATURE_FLAGS).

TODO Fase 2: topologia — interseções, grafo de adjacência, foundations de geometria.
TODO Fase 3: semântica — salas, paredes, inferência de retângulos/hachuras, raciocínio espacial.
"""
import fitz
import json
import sys
import datetime
import os
import math
import hashlib
from collections import defaultdict


# -----------------------------------------------------------------------------
# Fase 1 — flags de desempenho (inferência pesada desactivada por defeito).
# Activar pontualmente para experimentação; não recomendado dentro do Revit em PDFs grandes.
# -----------------------------------------------------------------------------
PHASE1_FEATURE_FLAGS = {
    'enable_topology_foundation': False,
    'enable_rectangle_grid_inference': False,
    'enable_rectangle_polyline_inference': False,
    'enable_heavy_hatch_clusters': False,
    # Preenchimentos explícitos do parser PyMuPDF (path com fill + rect) — O(n) no nº de drawings.
    'emit_native_pdf_hatch_fills': True,
}


def r(v):
    return round(float(v), 4)


def pxy(p):
    return {'x': r(p.x), 'y': r(p.y)}


def rect_to_bbox(rect):
    return [r(rect.x0), r(rect.y0), r(rect.x1), r(rect.y1)]


def stable_id(prefix, *parts):
    h = hashlib.sha256()
    for p in parts:
        h.update(str(p).encode('utf-8'))
    return prefix + '_' + h.hexdigest()[:10]


def color_to_rgb(c):
    if c is None:
        return None
    if isinstance(c, (list, tuple)) and len(c) >= 3:
        return [r(c[0]), r(c[1]), r(c[2])]
    return None


def line_len_pf(a, b):
    return math.hypot(b['x'] - a['x'], b['y'] - a['y'])


def norm_point(pt, rotation, width, height):
    x, y = float(pt['x']), float(pt['y'])
    if rotation == 90:
        nx, ny = y, height - x
    elif rotation == 180:
        nx, ny = width - x, height - y
    elif rotation == 270:
        nx, ny = width - y, x
    else:
        nx, ny = x, y
    nx = max(0.0, min(width, nx))
    ny = max(0.0, min(height, ny))
    return {'x': r(nx), 'y': r(ny)}


def norm_bbox(b, rotation, width, height):
    pts = [
        {'x': b[0], 'y': b[1]}, {'x': b[2], 'y': b[1]},
        {'x': b[2], 'y': b[3]}, {'x': b[0], 'y': b[3]},
    ]
    npts = [norm_point(p, rotation, width, height) for p in pts]
    xs = [p['x'] for p in npts]
    ys = [p['y'] for p in npts]
    return [r(min(xs)), r(min(ys)), r(max(xs)), r(max(ys))]


def bbox_dict_from_pts(pts):
    if not pts:
        return {'x_min': 0.0, 'y_min': 0.0, 'x_max': 0.0, 'y_max': 0.0}
    xs = [float(p['x']) for p in pts]
    ys = [float(p['y']) for p in pts]
    return {'x_min': r(min(xs)), 'y_min': r(min(ys)), 'x_max': r(max(xs)), 'y_max': r(max(ys))}


def bbox_dict_from_xyxy(x0, y0, x1, y1):
    return {
        'x_min': r(min(x0, x1)),
        'y_min': r(min(y0, y1)),
        'x_max': r(max(x0, x1)),
        'y_max': r(max(y0, y1)),
    }


def line_angle_deg_unnorm(p0, p1):
    dx = float(p1['x']) - float(p0['x'])
    dy = float(p1['y']) - float(p0['y'])
    if abs(dx) < 1e-12 and abs(dy) < 1e-12:
        return 0.0
    return r(math.degrees(math.atan2(dy, dx)))


def orientation_label(angle_deg):
    a = abs(angle_deg) % 180
    tol = 2.0
    if a < tol or abs(a - 180) < tol:
        return 'horizontal'
    if abs(a - 90) < tol:
        return 'vertical'
    return 'diagonal'


def entity_style_record(stroke_width_pt, stroke_color, fill_color, dash_pattern, opacity=1.0):
    return {
        'stroke_width_pt': r(stroke_width_pt or 0),
        'stroke_color': stroke_color if stroke_color is not None else [0, 0, 0],
        'fill_color': fill_color,
        'dash_pattern': [r(x) for x in (dash_pattern or [])],
        'opacity': r(opacity),
    }


def empty_topology():
    return {
        'intersections': [],
        'adjacent_entities': [],
        'contained_in': [],
    }


def wrap_geometry_document(schema, run_id, page_num, entities, normalized=True):
    return {
        'schema': schema,
        'run_id': run_id,
        'page': page_num,
        'generated_at_utc': datetime.datetime.utcnow().isoformat() + 'Z',
        'coordinate_system': {
            'space': 'pdf_page_space',
            'units': 'pdf_points',
            'normalized': bool(normalized),
        },
        'entities': entities,
    }


def seg_intersect(a1, a2, b1, b2):
    x1, y1 = a1['x'], a1['y']
    x2, y2 = a2['x'], a2['y']
    x3, y3 = b1['x'], b1['y']
    x4, y4 = b2['x'], b2['y']
    den = (x1 - x2) * (y3 - y4) - (y1 - y2) * (x3 - x4)
    if abs(den) < 1e-9:
        return None
    t = ((x1 - x3) * (y3 - y4) - (y1 - y3) * (x3 - x4)) / den
    u = -((x1 - x2) * (y1 - y3) - (y1 - y2) * (x1 - x3)) / den
    if 0 <= t <= 1 and 0 <= u <= 1:
        return {'x': r(x1 + t * (x2 - x1)), 'y': r(y1 + t * (y2 - y1))}
    return None


def setup_dirs(root):
    for rel in (
        'metadata', 'geometry', 'text', 'topology',
        'raster/preview', 'raster/ai', 'raster/ocr', 'raster/tiles',
        'semantic', 'raw', 'clean'
    ):
        os.makedirs(os.path.join(root, rel), exist_ok=True)


def extract_text_rich(page, page_num, run_id):
    raw = page.get_text('dict')
    blocks_out = []
    spans_flat = []
    for bi, block in enumerate(raw.get('blocks', [])):
        if block.get('type') != 0:
            continue
        bl_id = stable_id('blk', run_id, bi, block.get('bbox', (0, 0, 0, 0))[0])
        lines_o = []
        for li, line in enumerate(block.get('lines', [])):
            ln_id = stable_id('ln', run_id, bi, li, line.get('bbox', (0, 0, 0, 0))[0])
            spans_meta = []
            for si, span in enumerate(line.get('spans', [])):
                bbox = span.get('bbox', [0, 0, 0, 0])
                text = span.get('text', '')
                if not str(text).strip():
                    continue
                sp_id = stable_id('spn', run_id, bi, li, si, text[:24], bbox[0])
                sp_obj = {
                    'id': sp_id,
                    'parent_block_id': bl_id,
                    'parent_line_id': ln_id,
                    'page': page_num,
                    'text': text,
                    'bbox_pt': [r(b) for b in bbox],
                    'font': span.get('font', ''),
                    'size_pt': r(span.get('size', 0)),
                    'flags': int(span.get('flags', 0)),
                }
                spans_meta.append({'id': sp_id})
                spans_flat.append(sp_obj)
            if spans_meta:
                lb = line.get('bbox', [0, 0, 0, 0])
                lines_o.append({
                    'id': ln_id,
                    'parent_block_id': bl_id,
                    'page': page_num,
                    'bbox_pt': [r(b) for b in lb],
                    'spans': spans_meta,
                })
        if lines_o:
            bb = block.get('bbox', [0, 0, 0, 0])
            blocks_out.append({
                'id': bl_id,
                'page': page_num,
                'bbox_pt': [r(b) for b in bb],
                'lines': lines_o,
            })
    return blocks_out, spans_flat


def parse_drawings(drawings, run_id):
    lines, beziers, rectangles, quads, polylines, hatches, others = [], [], [], [], [], [], []

    for di, drawing in enumerate(drawings):
        fill = drawing.get('fill')
        color = drawing.get('color')
        width = r(drawing.get('width', 0) or 0)
        dashes = drawing.get('dashes')
        if dashes is None:
            dash_list = []
        elif isinstance(dashes, (list, tuple)):
            dash_list = list(dashes)
        else:
            dash_list = []
        stroke_c = color_to_rgb(color) or [0, 0, 0]
        fill_c = color_to_rgb(fill)
        style = {
            'stroke_width_pt': width,
            'stroke_color': stroke_c,
            'fill_color': fill_c,
            'dash_pattern': [r(x) for x in dash_list],
            'drawing_index': di,
        }
        items = drawing.get('items', [])
        chain = []

        for item in items:
            if not item:
                continue
            op = item[0]
            op_s = op if isinstance(op, str) else str(op)
            if op_s == 'l' and len(item) >= 3:
                fid = stable_id('ln', run_id, di, item[1].x, item[1].y, item[2].x, item[2].y)
                lines.append({
                    'id': fid,
                    'from': pxy(item[1]),
                    'to': pxy(item[2]),
                    'stroke_width_pt': width,
                    'drawing_index': di,
                    'polyline_id': None,
                    'style': dict(style),
                })
                chain.append(fid)
            elif op_s == 'c' and len(item) >= 5:
                bid = stable_id('bez', run_id, di, item[1].x, item[4].x)
                beziers.append({
                    'id': bid,
                    'kind': 'cubic_bezier',
                    'p1': pxy(item[1]), 'p2': pxy(item[2]),
                    'p3': pxy(item[3]), 'p4': pxy(item[4]),
                    'stroke_width_pt': width,
                    'drawing_index': di,
                    'style': dict(style),
                })
                chain = []
            elif op_s == 're' and len(item) >= 2:
                rid = stable_id('rect', run_id, di, item[1].x0, item[1].y0)
                rectangles.append({
                    'id': rid,
                    'bbox_pt': rect_to_bbox(item[1]),
                    'stroke_width_pt': width,
                    'drawing_index': di,
                    'style': dict(style),
                    'source_kind': 'pdf_operator',
                })
                chain = []
            elif op_s == 'qu' and len(item) >= 2:
                quad = item[1]
                pts = []
                for attr in ('ul', 'ur', 'll', 'lr'):
                    if hasattr(quad, attr):
                        pts.append(pxy(getattr(quad, attr)))
                qid = stable_id('quad', run_id, di, len(pts))
                quads.append({
                    'id': qid, 'points': pts,
                    'stroke_width_pt': width, 'drawing_index': di, 'style': dict(style),
                })
                chain = []
            else:
                others.append({'id': stable_id('op', run_id, di, str(op_s)), 'opcode': str(op_s), 'drawing_index': di})
                chain = []

        if len(chain) >= 2:
            pid = stable_id('pl', run_id, di, len(chain), chain[0], chain[-1])
            polylines.append({
                'id': pid,
                'segment_line_ids': list(chain),
                'drawing_index': di,
                'style': dict(style),
            })
            idset = set(chain)
            for ln in lines:
                if ln['drawing_index'] == di and ln['id'] in idset:
                    ln['polyline_id'] = pid

        if fill is not None:
            hb = None
            for item in items:
                if not item or (item[0] if isinstance(item[0], str) else str(item[0])) != 're' or len(item) < 2:
                    continue
                hb = rect_to_bbox(item[1])
                break
            if hb:
                hatches.append({
                    'id': stable_id('hatch', run_id, di),
                    'bbox_pt': hb,
                    'drawing_index': di,
                    'fill_color': fill_c,
                    'kind': 'solid_fill_pdf',
                })

    return lines, beziers, rectangles, quads, polylines, hatches, others


def _polyline_ordered_points(segment_ids, line_by_id):
    if not segment_ids:
        return []
    segs = [line_by_id[sid] for sid in segment_ids if sid in line_by_id]
    if not segs:
        return []
    pts = [dict(segs[0]['from'])]
    cur = dict(segs[0]['to'])
    pts.append(cur)
    for s in segs[1:]:
        a, b = dict(s['from']), dict(s['to'])
        if line_len_pf(cur, a) <= line_len_pf(cur, b):
            if line_len_pf(cur, a) > 1e-5:
                pts.append(a)
            cur = b
            pts.append(b)
        else:
            if line_len_pf(cur, b) > 1e-5:
                pts.append(b)
            cur = a
            pts.append(a)
    out = [pts[0]]
    for p in pts[1:]:
        if line_len_pf(out[-1], p) > 1e-4:
            out.append(p)
    return out


def _internal_angles_deg(pts_closed):
    n = len(pts_closed)
    angs = []
    for i in range(n):
        p0 = pts_closed[(i - 1) % n]
        p1 = pts_closed[i]
        p2 = pts_closed[(i + 1) % n]
        v1 = (p0['x'] - p1['x'], p0['y'] - p1['y'])
        v2 = (p2['x'] - p1['x'], p2['y'] - p1['y'])
        n1 = math.hypot(v1[0], v1[1]) or 1e-12
        n2 = math.hypot(v2[0], v2[1]) or 1e-12
        cos = max(-1.0, min(1.0, (v1[0] * v2[0] + v1[1] * v2[1]) / (n1 * n2)))
        angs.append(math.degrees(math.acos(cos)))
    return angs


def _is_near_perpendicular(angles, tol_deg=12.0):
    return all(abs(a - 90) < tol_deg for a in angles)


def infer_rectangles_from_polylines(polyline_defs, line_by_id, run_id, rot, w, h):
    # Fase 2+: inferência a partir de polilinhas fechadas; desactivada na Fase 1 (ver PHASE1_FEATURE_FLAGS).
    if not PHASE1_FEATURE_FLAGS.get('enable_rectangle_polyline_inference'):
        return []
    out = []
    seen = set()
    for pl in polyline_defs:
        seg_ids = pl.get('segment_line_ids') or []
        raw_pts = _polyline_ordered_points(seg_ids, line_by_id)
        if len(raw_pts) < 4:
            continue
        tol_close = 0.75
        closed = line_len_pf(raw_pts[0], raw_pts[-1]) <= tol_close
        pts = raw_pts
        if closed and line_len_pf(raw_pts[0], raw_pts[-1]) <= tol_close:
            pts = raw_pts[:-1]
        if len(pts) != 4:
            continue
        pts4 = pts + [pts[0]]
        angs = _internal_angles_deg(pts4)
        if not _is_near_perpendicular(angs):
            continue
        npts = [norm_point(p, rot, w, h) for p in pts]
        x0 = min(p['x'] for p in npts)
        x1 = max(p['x'] for p in npts)
        y0 = min(p['y'] for p in npts)
        y1 = max(p['y'] for p in npts)
        key = (r(x0), r(y0), r(x1), r(y1))
        if key in seen:
            continue
        seen.add(key)
        cid = stable_id('rect', run_id, 'pl', key[0], key[1], key[2], key[3])
        out.append({
            'id': cid,
            'bbox_pt': [x0, y0, x1, y1],
            'stroke_width_pt': 0,
            'drawing_index': pl.get('drawing_index', -1),
            'style': dict(pl.get('style') or {}),
            'source_kind': 'inferred_closed_polyline',
            'source_polyline_id': pl.get('id'),
        })
    return out


def _aa_line(ln):
    p0, p1 = ln['from'], ln['to']
    ang = abs(line_angle_deg_unnorm(p0, p1)) % 180
    return ang < 2.0 or abs(ang - 90) < 2.0 or abs(ang - 180) < 2


def infer_rectangles_from_line_grid(clean_lines, run_id, max_rects=400):
    # Fase 2+: grelha H×V (custo O(H²×V²)); desactivada na Fase 1.
    if not PHASE1_FEATURE_FLAGS.get('enable_rectangle_grid_inference'):
        return []
    aa = [ln for ln in clean_lines if _aa_line(ln)]
    if len(aa) < 4:
        return []
    horiz = []
    vert = []
    for ln in aa:
        p0, p1 = ln['from'], ln['to']
        ang = abs(line_angle_deg_unnorm(p0, p1)) % 180
        if ang < 2.0 or abs(ang - 180) < 2:
            y = r((p0['y'] + p1['y']) / 2)
            xma, xmi = max(p0['x'], p1['x']), min(p0['x'], p1['x'])
            horiz.append((y, xmi, xma, ln['id']))
        elif abs(ang - 90) < 2:
            x = r((p0['x'] + p1['x']) / 2)
            yma, ymi = max(p0['y'], p1['y']), min(p0['y'], p1['y'])
            vert.append((x, ymi, yma, ln['id']))
    rects = []
    seen = set()
    horiz_sorted = sorted(horiz, key=lambda t: (t[0], t[1]))
    vert_sorted = sorted(vert, key=lambda t: (t[0], t[1]))
    corner_tol = 2.5
    for i, (y1, x1a, x1b, _) in enumerate(horiz_sorted):
        for y2, x2a, x2b, _ in horiz_sorted[i + 1:]:
            if abs(y1 - y2) < corner_tol:
                continue
            x_left = max(min(x1a, x1b), min(x2a, x2b))
            x_right = min(max(x1a, x1b), max(x2a, x2b))
            if x_right - x_left < corner_tol * 2:
                continue
            for j, (xv1, yv1a, yv1b, _) in enumerate(vert_sorted):
                if xv1 < x_left - corner_tol or xv1 > x_right + corner_tol:
                    continue
                for xv2, yv2a, yv2b, _ in vert_sorted[j + 1:]:
                    if xv2 < x_left - corner_tol or xv2 > x_right + corner_tol:
                        continue
                    if abs(xv1 - xv2) < corner_tol * 2:
                        continue
                    y_lo = max(min(yv1a, yv1b), min(yv2a, yv2b))
                    y_hi = min(max(yv1a, yv1b), max(yv2a, yv2b))
                    if y_hi - y_lo < corner_tol * 2:
                        continue
                    if not (y_lo <= y1 + corner_tol <= y_hi and y_lo <= y2 + corner_tol <= y_hi):
                        continue
                    if not (x_left <= xv1 + corner_tol <= x_right and x_left <= xv2 + corner_tol <= x_right):
                        continue
                    bb = [r(x_left), r(y_lo), r(x_right), r(y_hi)]
                    key = (bb[0], bb[1], bb[2], bb[3])
                    if key in seen:
                        continue
                    seen.add(key)
                    if len(rects) >= max_rects:
                        return rects
                    rects.append({
                        'id': stable_id('rect', run_id, 'grid', key[0], key[1], key[2], key[3]),
                        'bbox_pt': bb,
                        'stroke_width_pt': 0,
                        'drawing_index': -1,
                        'style': {},
                        'source_kind': 'inferred_orthogonal_line_cycle',
                    })
    return rects


class HatchInferenceService:
    """Heurísticas para regiões tipo hatch (preenchimento, franjas paralelas)."""

    @staticmethod
    def merge_pdf_hatches(hatches, run_id, rot, pw, ph, tile_ids):
        """Fase 1: apenas regiões fill+re já identificadas pelo parser (sem clustering)."""
        entities = []
        for h0 in hatches:
            bb = h0['bbox_pt']
            nbb = norm_bbox(bb, rot, pw, ph)
            x0, y0, x1, y1 = nbb
            bd = bbox_dict_from_xyxy(x0, y0, x1, y1)
            corners = [
                {'x': x0, 'y': y0},
                {'x': x1, 'y': y0},
                {'x': x1, 'y': y1},
                {'x': x0, 'y': y1},
            ]
            fill_c = h0.get('fill_color')
            kind = h0.get('kind') or 'solid_fill_pdf'
            pat = 'solid_fill' if kind == 'solid_fill_pdf' else 'unknown'
            st = entity_style_record(0, [0, 0, 0], fill_c, [], opacity=1.0 if fill_c else 0.35)
            eid = stable_id('hat', run_id, 'fill', h0.get('drawing_index', 0), x0, y0, x1, y1)
            entities.append({
                'id': eid,
                'entity_type': 'hatch',
                'geometry': {
                    'boundary': corners,
                    'pattern_type': pat,
                },
                'bbox': bd,
                'style': st,
                'semantic_hints': {'is_structural_fill_candidate': False},
                'topology': empty_topology(),
                'spatial': {'tile_ids': list(tile_ids)},
                'source': {'drawing_index': int(h0.get('drawing_index', -1))},
            })
        return entities

    @staticmethod
    def parallel_line_clusters(clean_lines, run_id, tile_ids, min_lines=8, angle_bin=3.0):
        # Fase 3+: agrupamento heurístico de linhas paralelas densas; desactivado na Fase 1.
        if not PHASE1_FEATURE_FLAGS.get('enable_heavy_hatch_clusters'):
            return []
        buckets = defaultdict(list)
        for ln in clean_lines:
            ang = line_angle_deg_unnorm(ln['from'], ln['to'])
            key = round(ang / angle_bin) * angle_bin
            buckets[key].append(ln)
        entities = []
        for ang_key, segs in buckets.items():
            if len(segs) < min_lines:
                continue
            pts_all = []
            for ln in segs:
                pts_all.extend([ln['from'], ln['to']])
            bd = bbox_dict_from_pts(pts_all)
            if (bd['x_max'] - bd['x_min']) * (bd['y_max'] - bd['y_min']) < 400:
                continue
            hid = stable_id('hat', run_id, 'par', ang_key, len(segs), bd['x_min'], bd['y_min'])
            corners = [
                {'x': bd['x_min'], 'y': bd['y_min']},
                {'x': bd['x_max'], 'y': bd['y_min']},
                {'x': bd['x_max'], 'y': bd['y_max']},
                {'x': bd['x_min'], 'y': bd['y_max']},
            ]
            entities.append({
                'id': hid,
                'entity_type': 'hatch',
                'geometry': {
                    'boundary': corners,
                    'pattern_type': 'parallel_lines',
                    'dominant_angle_deg': r(ang_key),
                    'line_count': len(segs),
                },
                'bbox': bd,
                'style': entity_style_record(0, [0.5, 0.5, 0.5], None, [], opacity=0.2),
                'semantic_hints': {'is_structural_fill_candidate': False},
                'topology': empty_topology(),
                'spatial': {'tile_ids': list(tile_ids)},
                'source': {'drawing_index': -1},
            })
        return entities


def topology_foundation(line_entities, max_pairs=4000):
    # Fase 2+: interseções + proximidade de extremos; desactivado na Fase 1 por defeito.
    if not PHASE1_FEATURE_FLAGS.get('enable_topology_foundation'):
        return [], []
    ids = [ln['id'] for ln in line_entities]
    intersections = []
    adj = {i: set() for i in ids}
    tol = 0.35
    pairs = 0
    n = len(line_entities)
    for i in range(n):
        for j in range(i + 1, n):
            if pairs >= max_pairs:
                break
            pairs += 1
            a, b = line_entities[i], line_entities[j]
            pt = seg_intersect(a['from'], a['to'], b['from'], b['to'])
            if pt:
                iid = stable_id('x', a['id'], b['id'], pt['x'], pt['y'])
                intersections.append({
                    'id': iid,
                    'entity_a_id': a['id'],
                    'entity_b_id': b['id'],
                    'point': [pt['x'], pt['y']],
                })
                adj[a['id']].add(b['id'])
                adj[b['id']].add(a['id'])
        if pairs >= max_pairs:
            break
    for i in range(min(n, 800)):
        for j in range(i + 1, min(n, 800)):
            a, b = line_entities[i], line_entities[j]
            for pa in (a['from'], a['to']):
                for pb in (b['from'], b['to']):
                    if line_len_pf(pa, pb) <= tol:
                        adj[a['id']].add(b['id'])
                        adj[b['id']].add(a['id'])
    adjacency = [{'entity_id': k, 'neighbor_ids': sorted(v)} for k, v in adj.items() if v]
    return intersections, adjacency


def wrap_file(schema, run_id, page_num, entities_key, entities):
    return {
        'schema': schema,
        'run_id': run_id,
        'page': page_num,
        'generated_at_utc': datetime.datetime.utcnow().isoformat() + 'Z',
        'units': {'coordinates': 'pdf_points'},
        entities_key: entities,
    }


def _index_intersections_by_line(intersections):
    """Fase 2+: indexar interseções por ID de linha para popular entidades (não usado na Fase 1)."""
    m = defaultdict(list)
    for it in intersections:
        m[it['entity_a_id']].append(it['id'])
        m[it['entity_b_id']].append(it['id'])
    return dict(m)


def _index_neighbors(adjacency):
    """Fase 2+: mapear vizinhos a partir da lista de adjacency global."""
    return {a['entity_id']: list(a.get('neighbor_ids') or []) for a in adjacency}


def _style_from_line_map(ln_id, style_by_id):
    s = style_by_id.get(ln_id) or {}
    return entity_style_record(
        s.get('stroke_width_pt', 0),
        s.get('stroke_color'),
        s.get('fill_color'),
        s.get('dash_pattern') or [],
        opacity=1.0,
    )


def build_spatial_line_entities(clean_lines_full, style_by_line_id, tile_ids):
    """Topology em entidades: apenas placeholders na Fase 1 (sem interseções calculadas)."""
    entities = []
    for ln in clean_lines_full:
        p0, p1 = ln['from'], ln['to']
        L = line_len_pf(p0, p1)
        ang = line_angle_deg_unnorm(p0, p1)
        bb = bbox_dict_from_xyxy(p0['x'], p0['y'], p1['x'], p1['y'])
        st = _style_from_line_map(ln['id'], style_by_line_id)
        st['stroke_width_pt'] = r(ln.get('stroke_width_pt', st['stroke_width_pt']))
        lid = ln['id']
        entities.append({
            'id': lid,
            'entity_type': 'line',
            'geometry': {
                'start': {'x': r(p0['x']), 'y': r(p0['y'])},
                'end': {'x': r(p1['x']), 'y': r(p1['y'])},
                'length': r(L),
                'angle_deg': r(ang),
            },
            'bbox': bb,
            'style': st,
            'relationships': {'polyline_id': ln.get('polyline_id')},
            'semantic_hints': {
                'orientation': orientation_label(ang),
                'is_closed': False,
                'is_structural_candidate': False,
            },
            'topology': empty_topology(),
            'spatial': {'tile_ids': list(tile_ids)},
            'source': {'drawing_index': int(ln.get('drawing_index', -1))},
        })
    return entities


def build_spatial_polyline_entities(polylines, line_by_id, clean_line_ids, rotation, pw, ph, tile_ids):
    entities = []
    for pl in polylines:
        seg_ids = pl.get('segment_line_ids') or []
        raw_pts = _polyline_ordered_points(seg_ids, line_by_id)
        if len(raw_pts) < 2:
            continue
        npts = [norm_point(p, rotation, pw, ph) for p in raw_pts]
        tol_c = 0.75
        closed = len(npts) >= 3 and line_len_pf(npts[0], npts[-1]) <= tol_c
        plen_f = 0.0
        for i in range(len(npts) - 1):
            plen_f += line_len_pf(npts[i], npts[i + 1])
        if closed and len(npts) >= 2:
            plen_f += line_len_pf(npts[-1], npts[0])
        bb = bbox_dict_from_pts(npts)
        seg_refs = [s for s in seg_ids if s in clean_line_ids]
        st_src = pl.get('style') or {}
        st = entity_style_record(
            st_src.get('stroke_width_pt', 0),
            st_src.get('stroke_color'),
            st_src.get('fill_color'),
            st_src.get('dash_pattern') or [],
        )
        pid = pl['id']
        entities.append({
            'id': pid,
            'entity_type': 'polyline',
            'geometry': {
                'points': [{'x': r(p['x']), 'y': r(p['y'])} for p in npts],
                'closed': closed,
                'length': r(plen_f),
            },
            'bbox': bb,
            'segment_refs': seg_refs,
            'style': st,
            'semantic_hints': {
                # Fase 3+: heurísticas de limite de divisão; Fase 1 mantém placeholder.
                'is_room_boundary_candidate': False,
            },
            'topology': empty_topology(),
            'spatial': {'tile_ids': list(tile_ids)},
            'source': {'drawing_index': int(pl.get('drawing_index', -1))},
        })
    return entities


def build_spatial_bezier_entities(beziers, rotation, pw, ph, tile_ids):
    out = []
    for b in beziers:
        p1 = norm_point(b['p1'], rotation, pw, ph)
        p2 = norm_point(b['p2'], rotation, pw, ph)
        p3 = norm_point(b['p3'], rotation, pw, ph)
        p4 = norm_point(b['p4'], rotation, pw, ph)
        pts = [p1, p2, p3, p4]
        bb = bbox_dict_from_pts(pts)
        st_src = b.get('style') or {}
        st = entity_style_record(
            b.get('stroke_width_pt', st_src.get('stroke_width_pt', 0)),
            st_src.get('stroke_color'),
            st_src.get('fill_color'),
            st_src.get('dash_pattern') or [],
        )
        out.append({
            'id': b['id'],
            'entity_type': 'cubic_bezier',
            'geometry': {
                'p1': {'x': r(p1['x']), 'y': r(p1['y'])},
                'p2': {'x': r(p2['x']), 'y': r(p2['y'])},
                'p3': {'x': r(p3['x']), 'y': r(p3['y'])},
                'p4': {'x': r(p4['x']), 'y': r(p4['y'])},
            },
            'bbox': bb,
            'style': st,
            'semantic_hints': {'is_structural_candidate': False},
            'topology': empty_topology(),
            'spatial': {'tile_ids': list(tile_ids)},
            'source': {'drawing_index': int(b.get('drawing_index', -1))},
        })
    return out


def build_spatial_rectangle_entities(rect_manifest, rect_style_by_id, tile_ids):
    """rect_manifest: list of {id, bbox_pt, stroke_width_pt, drawing_index, inference?}"""
    ents = []
    for row in rect_manifest:
        x0, y0, x1, y1 = row['bbox_pt']
        corners = [
            {'x': r(x0), 'y': r(y0)},
            {'x': r(x1), 'y': r(y0)},
            {'x': r(x1), 'y': r(y1)},
            {'x': r(x0), 'y': r(y1)},
        ]
        bd = bbox_dict_from_xyxy(x0, y0, x1, y1)
        rs = rect_style_by_id.get(row['id']) or {}
        st = entity_style_record(
            row.get('stroke_width_pt', rs.get('stroke_width_pt', 0)),
            rs.get('stroke_color'),
            rs.get('fill_color'),
            rs.get('dash_pattern') or [],
        )
        src = {'drawing_index': int(row.get('drawing_index', -1))}
        inf = row.get('inference')
        if inf:
            src['inference'] = inf
        ents.append({
            'id': row['id'],
            'entity_type': 'rectangle',
            'geometry': {
                'corners': corners,
                'width': r(abs(x1 - x0)),
                'height': r(abs(y1 - y0)),
            },
            'bbox': bd,
            'style': st,
            'semantic_hints': {
                'is_column_candidate': False,
                'is_room_candidate': False,
            },
            'topology': empty_topology(),
            'spatial': {'tile_ids': list(tile_ids)},
            'source': src,
        })
    return ents


def main():
    if len(sys.argv) < 4:
        print('Uso: phase1_extract.py <pdf_path> <output_dir> <page_number>', file=sys.stderr)
        sys.exit(2)

    pdf_path = sys.argv[1]
    output_dir = sys.argv[2]
    page_num = int(sys.argv[3])
    setup_dirs(output_dir)

    doc = fitz.open(pdf_path)
    if page_num < 1 or page_num > doc.page_count:
        doc.close()
        raise ValueError('pdfPageNumber fora do intervalo: %s / %s' % (page_num, doc.page_count))

    safe = os.path.splitext(os.path.basename(pdf_path))[0]
    for c in '<>:"/\\|?*':
        safe = safe.replace(c, '_')
    run_base = '%s_page%s' % (safe, page_num)
    run_id = stable_id('run', output_dir, run_base)

    page = doc.load_page(page_num - 1)
    drawings = page.get_drawings()
    words = page.get_text('words')

    lines, beziers, rectangles, quads, polylines, hatches, others = parse_drawings(drawings, run_id)
    line_by_id = {ln['id']: ln for ln in lines}
    style_by_line_id = {ln['id']: ln.get('style', {}) for ln in lines}
    rect_style_by_id = {rc['id']: rc.get('style', {}) for rc in rectangles}

    text_words = []
    word_objs = []
    for w in words:
        x0, y0, x1, y1, text, block_no, line_no, word_no = w
        wid = stable_id('wd', run_id, block_no, line_no, word_no, text[:12], x0)
        wo = {
            'id': wid,
            'text': text,
            'bbox_pt': [r(x0), r(y0), r(x1), r(y1)],
            'block_no': int(block_no),
            'line_no': int(line_no),
            'word_no': int(word_no),
            'page': page_num,
        }
        text_words.append({
            'text': text,
            'bbox_pt': [r(x0), r(y0), r(x1), r(y1)],
            'block_no': int(block_no),
            'line_no': int(line_no),
            'word_no': int(word_no),
        })
        word_objs.append(wo)

    blocks, spans_flat = extract_text_rich(page, page_num, run_id)

    rotation = int(page.rotation)
    width = float(page.rect.width)
    height = float(page.rect.height)
    min_len = 0.5

    clean_lines_full = []
    for ln in lines:
        nln = {
            'id': ln['id'],
            'from': norm_point(ln['from'], rotation, width, height),
            'to': norm_point(ln['to'], rotation, width, height),
            'stroke_width_pt': ln['stroke_width_pt'],
            'drawing_index': ln['drawing_index'],
            'polyline_id': ln.get('polyline_id'),
        }
        if line_len_pf(nln['from'], nln['to']) < min_len:
            continue
        clean_lines_full.append(nln)

    matcher_lines = [{
        'from': x['from'],
        'to': x['to'],
        'stroke_width_pt': x['stroke_width_pt'],
        'drawing_index': x['drawing_index'],
        'id': x['id'],
    } for x in clean_lines_full]

    clean_rects = []
    for rc in rectangles:
        clean_rects.append({
            'id': rc['id'],
            'bbox_pt': norm_bbox(rc['bbox_pt'], rotation, width, height),
            'stroke_width_pt': rc['stroke_width_pt'],
            'drawing_index': rc['drawing_index'],
            'inference': None,
        })

    # Fase 2+: inferência de retângulos (polilinha / grelha); desligada — ver PHASE1_FEATURE_FLAGS.
    inferred_pl = infer_rectangles_from_polylines(polylines, line_by_id, run_id, rotation, width, height)
    inferred_grid = infer_rectangles_from_line_grid(clean_lines_full, run_id, max_rects=120)
    seen_bb = {tuple(r(x) for x in cr['bbox_pt']) for cr in clean_rects}
    for inf in inferred_pl + inferred_grid:
        k = tuple(r(x) for x in inf['bbox_pt'])
        if k in seen_bb:
            continue
        seen_bb.add(k)
        clean_rects.append({
            'id': inf['id'],
            'bbox_pt': inf['bbox_pt'],
            'stroke_width_pt': inf.get('stroke_width_pt', 0),
            'drawing_index': inf['drawing_index'],
            'inference': inf.get('source_kind'),
        })

    ints, adjs = topology_foundation(clean_lines_full)

    preview_dpi = 150
    preview_path = os.path.join(output_dir, 'raster', 'preview', 'page.png')
    pix = page.get_pixmap(dpi=preview_dpi)
    pix.save(preview_path)

    scale = preview_dpi / 72.0
    tw = int(pix.width)
    th = int(pix.height)
    tile_full = {
        'tile_id': 'tile_full_page',
        'row': 0,
        'col': 0,
        'bbox_pt': [0, 0, r(width), r(height)],
        'bbox_px': [0, 0, tw, th],
        'image_path': preview_path,
        'image_width_px': tw,
        'image_height_px': th,
    }

    manifest = {
        'schema': 'semantic_ready_manifest.v1',
        'run_id': run_id,
        'source_pdf': pdf_path,
        'generated_at_utc': datetime.datetime.utcnow().isoformat() + 'Z',
        'selected_page_number': page_num,
        'tile_strategy': {
            'mode': 'full_page_preview',
            'preview_dpi': preview_dpi,
            'scale_px_per_pt': r(scale),
        },
        'page': {'width_pt': r(width), 'height_pt': r(height), 'rotation_degrees': 0},
        'tiles': [tile_full],
    }

    semantic_pixels = {
        'schema': 'semantic_pixels.v1',
        'generated_at_utc': datetime.datetime.utcnow().isoformat() + 'Z',
        'source_pdf': pdf_path,
        'page': page_num,
        'contract': {
            'required_fields': ['type', 'confidence', 'bbox', 'page', 'tile_id'],
            'bbox_format': 'pixels relative to tile image',
            'confidence_range': [0.0, 1.0],
        },
        'detections': [],
    }

    raw_result = {
        'schema': 'phase1_raw_min.v1',
        'run_id': run_id,
        'source_pdf': pdf_path,
        'generated_at_utc': datetime.datetime.utcnow().isoformat() + 'Z',
        'units': {'coordinates': 'pdf_points'},
        'summary': {
            'pages': doc.page_count,
            'selected_page_number': page_num,
            'is_vector_page': len(drawings) > 0,
            'drawings_count': len(drawings),
            'text_words_count': len(text_words),
        },
        'page': {
            'page_index': page_num - 1,
            'page_number': page_num,
            'width_pt': r(width),
            'height_pt': r(height),
            'rotation_degrees': rotation,
            'geometry': {
                'lines': [{'from': x['from'], 'to': x['to'], 'stroke_width_pt': x['stroke_width_pt'],
                          'drawing_index': x['drawing_index']} for x in lines],
                'beziers': [{k: v for k, v in b.items() if k != 'style'} for b in beziers],
                'rectangles': [{k: v for k, v in rc.items() if k != 'style'} for rc in rectangles],
                'quads': quads,
                'other_path_commands': others,
            },
            'text_words': text_words,
        },
    }

    clean_doc = {
        'schema': 'phase1_clean.v1',
        'run_id': run_id,
        'source_pdf': pdf_path,
        'generated_at_utc': datetime.datetime.utcnow().isoformat() + 'Z',
        'units': {'coordinates': 'pdf_points'},
        'normalization': {
            'applied': True,
            'rotation_degrees_original': rotation,
            'target_coordinate_system': 'derotated_page_space',
            'line_filter': {'min_length_pt': min_len},
        },
        'artifact_refs': {
            'geometry_lines': 'geometry/lines.json',
            'geometry_polylines': 'geometry/polylines.json',
            'geometry_beziers': 'geometry/beziers.json',
            'geometry_rectangles': 'geometry/rectangles.json',
            'geometry_hatches': 'geometry/hatches.json',
            'text_words': 'text/words.json',
        },
        'summary': raw_result['summary'],
        'page': {
            'page_index': page_num - 1,
            'page_number': page_num,
            'width_pt': r(width),
            'height_pt': r(height),
            'rotation_degrees': 0,
            'geometry': {
                'lines': matcher_lines,
                'rectangles': [{'id': x['id'], 'bbox_pt': x['bbox_pt'], 'stroke_width_pt': x['stroke_width_pt'],
                               'drawing_index': x['drawing_index']} for x in clean_rects],
                'beziers': [],
                'quads': [],
            },
        },
    }

    project = {
        'schema': 'phase1_project.v1',
        'run_id': run_id,
        'file': os.path.basename(pdf_path),
        'pages': doc.page_count,
        'selected_page': page_num,
        'units': 'pdf_points',
        'created_at_utc': datetime.datetime.utcnow().isoformat() + 'Z',
        'output_layout': 'phase1_modular_json.v1',
    }

    index = {
        'schema': 'phase1_index.v1',
        'run_id': run_id,
        'run_base': run_base,
        'generated_at_utc': datetime.datetime.utcnow().isoformat() + 'Z',
        'paths': {
            'raw_json': 'raw.json',
            'raw_packaged': os.path.join('raw', run_base + '_raw.json').replace('\\', '/'),
            'clean_json': os.path.join('clean', run_base + '_clean.json').replace('\\', '/'),
            'metadata_project': 'metadata/project.json',
            'geometry_lines': 'geometry/lines.json',
            'geometry_polylines': 'geometry/polylines.json',
            'geometry_beziers': 'geometry/beziers.json',
            'geometry_rectangles': 'geometry/rectangles.json',
            'geometry_hatches': 'geometry/hatches.json',
            'text_words': 'text/words.json',
            'text_blocks': 'text/blocks.json',
            'text_spans': 'text/spans.json',
            'topology_intersections': 'topology/intersections.json',
            'topology_adjacency': 'topology/adjacency.json',
            'raster_preview': 'raster/preview/page.png',
            'semantic_manifest': 'semantic/semantic_ready_manifest.json',
            'semantic_pixels': 'semantic/semantic_pixels.json',
        },
    }

    tile_ids = ['tile_full_page']
    clean_ids = {ln['id'] for ln in clean_lines_full}
    line_ents = build_spatial_line_entities(clean_lines_full, style_by_line_id, tile_ids)
    pl_ents = build_spatial_polyline_entities(
        polylines, line_by_id, clean_ids, rotation, width, height, tile_ids)
    bez_ents = build_spatial_bezier_entities(beziers, rotation, width, height, tile_ids)
    rect_ents = build_spatial_rectangle_entities(clean_rects, rect_style_by_id, tile_ids)
    hatch_ents = []
    if PHASE1_FEATURE_FLAGS.get('emit_native_pdf_hatch_fills'):
        hatch_ents = HatchInferenceService.merge_pdf_hatches(
            hatches, run_id, rotation, width, height, tile_ids)
    if PHASE1_FEATURE_FLAGS.get('enable_heavy_hatch_clusters'):
        hatch_ents.extend(HatchInferenceService.parallel_line_clusters(
            clean_lines_full, run_id, tile_ids))

    def writep(rel_path, obj):
        p = os.path.join(output_dir, *rel_path.replace('\\', '/').split('/'))
        os.makedirs(os.path.dirname(p), exist_ok=True)
        with open(p, 'w', encoding='utf-8') as f:
            json.dump(obj, f, ensure_ascii=False, indent=2)

    writep('geometry/lines.json', wrap_geometry_document('phase1.geometry.lines.v2', run_id, page_num, line_ents))
    writep('geometry/polylines.json', wrap_geometry_document('phase1.geometry.polylines.v2', run_id, page_num, pl_ents))
    writep('geometry/beziers.json', wrap_geometry_document('phase1.geometry.beziers.v2', run_id, page_num, bez_ents))
    writep('geometry/rectangles.json', wrap_geometry_document('phase1.geometry.rectangles.v2', run_id, page_num, rect_ents))
    writep('geometry/hatches.json', wrap_geometry_document('phase1.geometry.hatches.v2', run_id, page_num, hatch_ents))

    writep('text/words.json', wrap_file('phase1.text.words.v1', run_id, page_num, 'entities', word_objs))
    writep('text/blocks.json', wrap_file('phase1.text.blocks.v1', run_id, page_num, 'entities', blocks))
    writep('text/spans.json', wrap_file('phase1.text.spans.v1', run_id, page_num, 'entities', spans_flat))

    writep('topology/intersections.json', wrap_file('phase1.topology.intersections.v1', run_id, page_num, 'intersections', ints))
    writep('topology/adjacency.json', wrap_file('phase1.topology.adjacency.v1', run_id, page_num, 'adjacency', adjs))

    writep('metadata/project.json', project)
    writep('semantic/semantic_ready_manifest.json', manifest)
    writep('semantic/semantic_pixels.json', semantic_pixels)

    raw_packaged = dict(raw_result)
    raw_packaged['modular_index'] = index['paths']

    writep('raw/%s_raw.json' % run_base, raw_packaged)
    writep('clean/%s_clean.json' % run_base, clean_doc)

    root_raw = os.path.join(output_dir, 'raw.json')
    with open(root_raw, 'w', encoding='utf-8') as f:
        json.dump(raw_result, f, ensure_ascii=False, indent=2)

    idx_path = os.path.join(output_dir, 'phase1_index.json')
    with open(idx_path, 'w', encoding='utf-8') as f:
        json.dump(index, f, ensure_ascii=False, indent=2)

    doc.close()
    print(idx_path)


if __name__ == '__main__':
    try:
        main()
    except Exception as e:
        print(str(e), file=sys.stderr)
        sys.exit(1)
