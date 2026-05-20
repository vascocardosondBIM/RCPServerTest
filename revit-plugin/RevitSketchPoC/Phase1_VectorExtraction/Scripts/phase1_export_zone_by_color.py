"""
Exporta PNG por cor dentro de uma zona (bbox em espaço desrotado / clean).

Modo pixel_mask (opção B): render fiel do PDF na zona (get_pixmap clip) e máscara
por tolerância RGB — inclui texto, imagens, hachuras e qualquer conteúdo visível.

Uso:
  python phase1_export_zone_by_color.py <pdf> <out_dir> <page> <x0> <y0> <x1> <y1> <rotation> <width_pt> <height_pt> [dpi] [tolerance]

Requer: pip install pymupdf
"""
import json
import os
import shutil
import sys
import datetime

import fitz

DEFAULT_DPI = 300
DEFAULT_TOLERANCE = 32
MAX_COLOR_GROUPS = 48
DEFAULT_PRESET = 'balanced'
DEFAULT_WHITE_THRESHOLD = 248
DEFAULT_WHITE_LUMA_THRESHOLD = 242
DEFAULT_WHITE_CHROMA_SPREAD = 18
DEFAULT_MIN_COLOR_PIXELS = 64
DEFAULT_MIN_COLOR_COVERAGE = 0.00035
DEFAULT_MIN_JSON_ENTITIES = 1

COLOR_PRESETS = {
    'conservative': {
        'white_threshold': 250,
        'white_luma_threshold': 247,
        'white_chroma_spread': 14,
        'min_color_pixels': 36,
        'min_color_coverage': 0.0002,
        'min_json_entities': 1,
    },
    'balanced': {
        'white_threshold': DEFAULT_WHITE_THRESHOLD,
        'white_luma_threshold': DEFAULT_WHITE_LUMA_THRESHOLD,
        'white_chroma_spread': DEFAULT_WHITE_CHROMA_SPREAD,
        'min_color_pixels': DEFAULT_MIN_COLOR_PIXELS,
        'min_color_coverage': DEFAULT_MIN_COLOR_COVERAGE,
        'min_json_entities': DEFAULT_MIN_JSON_ENTITIES,
    },
    'aggressive': {
        'white_threshold': 245,
        'white_luma_threshold': 236,
        'white_chroma_spread': 22,
        'min_color_pixels': 120,
        'min_color_coverage': 0.0007,
        'min_json_entities': 2,
    },
}


def r(v):
    return round(float(v), 4)


def color_to_rgb_key(c):
    if c is None:
        return None
    if isinstance(c, (list, tuple)) and len(c) >= 3:
        vals = [float(c[0]), float(c[1]), float(c[2])]
        scale = 255.0 if max(vals) <= 1.0 + 1e-6 and min(vals) >= 0 else 1.0
        ri = int(max(0, min(255, round(vals[0] * scale))))
        gi = int(max(0, min(255, round(vals[1] * scale))))
        bi = int(max(0, min(255, round(vals[2] * scale))))
        return '%02X%02X%02X' % (ri, gi, bi)
    if isinstance(c, (int, float)):
        g = int(max(0, min(255, round(float(c) * 255 if c <= 1 else c))))
        return '%02X%02X%02X' % (g, g, g)
    return None


def hex_to_rgb(hex_key):
    h = hex_key.strip().lstrip('#')
    if len(h) != 6:
        return 0, 0, 0
    return int(h[0:2], 16), int(h[2:4], 16), int(h[4:6], 16)


def rgb_dist_sq(r1, g1, b1, r2, g2, b2):
    return (r1 - r2) ** 2 + (g1 - g2) ** 2 + (b1 - b2) ** 2


def is_near_white(r, g, b, threshold):
    return r >= threshold and g >= threshold and b >= threshold


def is_background_like_color(r, g, b, white_threshold, white_luma_threshold, white_chroma_spread):
    if is_near_white(r, g, b, white_threshold):
        return True
    # Treat high-luma low-chroma tones as background-ish (off-white paper/noise).
    luma = int(0.2126 * r + 0.7152 * g + 0.0722 * b)
    spread = max(r, g, b) - min(r, g, b)
    return luma >= white_luma_threshold and spread <= white_chroma_spread


def derotated_to_raw(x, y, rotation, width, height):
    rot = int(rotation) % 360
    if rot == 90:
        return height - float(y), float(x)
    if rot == 180:
        return width - float(x), height - float(y)
    if rot == 270:
        return float(y), width - float(x)
    return float(x), float(y)


def derotated_bbox_to_raw_rect(bbox, rotation, width, height):
    x0, y0, x1, y1 = [float(b) for b in bbox]
    pts = [(x0, y0), (x1, y0), (x1, y1), (x0, y1)]
    raw_pts = [derotated_to_raw(px, py, rotation, width, height) for px, py in pts]
    xs = [p[0] for p in raw_pts]
    ys = [p[1] for p in raw_pts]
    return fitz.Rect(min(xs), min(ys), max(xs), max(ys))


def color_keys_for_drawing(drawing):
    keys = set()
    stroke_key = color_to_rgb_key(drawing.get('color'))
    fill_key = color_to_rgb_key(drawing.get('fill'))
    if stroke_key:
        keys.add(stroke_key)
    if fill_key:
        keys.add(fill_key)
    if not keys:
        keys.add('000000')
    return keys


def render_zone_pixmap(page, clip_rect, dpi):
    clip = clip_rect & page.rect
    if clip.is_empty:
        clip = page.rect
    return page.get_pixmap(dpi=dpi, clip=clip, alpha=False)


def collect_colors_from_pixmap(
    pix,
    tolerance,
    max_colors,
    white_threshold,
    white_luma_threshold,
    white_chroma_spread,
):
    """Agrupa cores visíveis no render da zona (amostragem + clustering)."""
    w, h, n = pix.width, pix.height, pix.n
    if n < 3:
        return []

    tol_sq = tolerance * tolerance
    clusters = []

    def assign(r, g, b):
        for idx, cluster in enumerate(clusters):
            cr, cg, cb, count = cluster
            if rgb_dist_sq(r, g, b, cr, cg, cb) <= tol_sq:
                count += 1
                nr = (cr * (count - 1) + r) // count
                ng = (cg * (count - 1) + g) // count
                nb = (cb * (count - 1) + b) // count
                clusters[idx] = [nr, ng, nb, count]
                return
        if len(clusters) < max_colors:
            clusters.append([r, g, b, 1])

    step = 2 if max(w, h) > 2000 else 1
    samples = pix.samples
    for y in range(0, h, step):
        row = y * w
        for x in range(0, w, step):
            i = (row + x) * n
            r, g, b = samples[i], samples[i + 1], samples[i + 2]
            if is_background_like_color(r, g, b, white_threshold, white_luma_threshold, white_chroma_spread):
                continue
            assign(r, g, b)

    clusters.sort(key=lambda c: -c[3])
    return ['%02X%02X%02X' % (c[0], c[1], c[2]) for c in clusters]


def merge_color_keys(hex_keys, tolerance):
    """Funde tons próximos numa lista de cores representativas."""
    keys = []
    seen = set()
    for k in hex_keys:
        if not k or k in seen:
            continue
        seen.add(k)
        keys.append(k)

    groups = []
    used = set()
    tol_sq = tolerance * tolerance

    for k in keys:
        if k in used:
            continue
        r, g, b = hex_to_rgb(k)
        members = [k]
        used.add(k)
        for k2 in keys:
            if k2 in used:
                continue
            r2, g2, b2 = hex_to_rgb(k2)
            if rgb_dist_sq(r, g, b, r2, g2, b2) <= tol_sq:
                members.append(k2)
                used.add(k2)
        rs = sum(hex_to_rgb(m)[0] for m in members) // len(members)
        gs = sum(hex_to_rgb(m)[1] for m in members) // len(members)
        bs = sum(hex_to_rgb(m)[2] for m in members) // len(members)
        rep = '%02X%02X%02X' % (rs, gs, bs)
        groups.append({'rep': rep, 'members': members})

    groups.sort(key=lambda g: -len(g['members']))
    return groups


def save_color_mask_png(source_pix, target_rgb, tolerance, dest_path, bg=(255, 255, 255)):
    tr, tg, tb = target_rgb
    tol_sq = tolerance * tolerance
    w, h, n = source_pix.width, source_pix.height, source_pix.n
    if n < 3:
        raise ValueError('Pixmap sem RGB')

    src = source_pix.samples
    out = bytearray(w * h * 3)
    br, bgc, bb = bg
    matched = 0

    for y in range(h):
        row_off = y * w
        for x in range(w):
            i = (row_off + x) * n
            r, g, b = src[i], src[i + 1], src[i + 2]
            o = (row_off + x) * 3
            if rgb_dist_sq(r, g, b, tr, tg, tb) <= tol_sq:
                out[o] = r
                out[o + 1] = g
                out[o + 2] = b
                matched += 1
            else:
                out[o] = br
                out[o + 1] = bgc
                out[o + 2] = bb

    mask_pix = fitz.Pixmap(fitz.Colorspace(fitz.CS_RGB), w, h, out, False)
    os.makedirs(os.path.dirname(dest_path) or '.', exist_ok=True)
    mask_pix.save(dest_path)
    mask_pix = None
    return matched


def compute_color_priority_score(coverage, json_count):
    coverage_score = min(1.0, max(0.0, coverage) * 900.0)
    json_score = min(1.0, max(0, json_count) / 40.0)
    return round(coverage_score * 0.65 + json_score * 0.35, 6)


def resolve_runtime_settings(
    preset_name,
    min_color_pixels,
    min_color_coverage,
    min_json_entities,
    white_threshold,
    white_luma_threshold,
    white_chroma_spread,
):
    key = (preset_name or DEFAULT_PRESET).strip().lower()
    if key == 'conservador':
        key = 'conservative'
    elif key == 'balanceado':
        key = 'balanced'
    elif key == 'agressivo':
        key = 'aggressive'
    if key not in COLOR_PRESETS:
        key = DEFAULT_PRESET

    cfg = dict(COLOR_PRESETS[key])
    if min_color_pixels is not None:
        cfg['min_color_pixels'] = max(1, int(min_color_pixels))
    if min_color_coverage is not None:
        cfg['min_color_coverage'] = max(0.0, float(min_color_coverage))
    if min_json_entities is not None:
        cfg['min_json_entities'] = max(0, int(min_json_entities))
    if white_threshold is not None:
        cfg['white_threshold'] = max(0, min(255, int(white_threshold)))
    if white_luma_threshold is not None:
        cfg['white_luma_threshold'] = max(0, min(255, int(white_luma_threshold)))
    if white_chroma_spread is not None:
        cfg['white_chroma_spread'] = max(0, min(255, int(white_chroma_spread)))

    cfg['preset'] = key
    return cfg


def color_matches_hex_key(stroke_key, fill_key, hex_key, tolerance):
    tr, tg, tb = hex_to_rgb(hex_key)
    tol_sq = tolerance * tolerance
    if stroke_key:
        r, g, b = hex_to_rgb(stroke_key)
        if rgb_dist_sq(r, g, b, tr, tg, tb) <= tol_sq:
            return True
    if fill_key:
        r, g, b = hex_to_rgb(fill_key)
        if rgb_dist_sq(r, g, b, tr, tg, tb) <= tol_sq:
            return True
    return False


def filter_json_entities_by_color(geometry_dir, hex_key, member_keys, tolerance, out_dir):
    if not os.path.isdir(geometry_dir):
        return 0
    member_set = set(member_keys)
    count = 0
    for root, _, files in os.walk(geometry_dir):
        for fn in files:
            if not fn.endswith('.json'):
                continue
            src = os.path.join(root, fn)
            rel = os.path.relpath(src, geometry_dir)
            try:
                with open(src, 'r', encoding='utf-8') as f:
                    doc = json.load(f)
            except Exception:
                continue
            key_name = None
            arr = None
            if isinstance(doc.get('entities'), list):
                key_name, arr = 'entities', doc['entities']
            else:
                for k, v in doc.items():
                    if isinstance(v, list) and k not in ('bbox_norm', 'bbox_pt'):
                        key_name, arr = k, v
                        break
            if not arr:
                continue
            filtered = []
            for ent in arr:
                if not isinstance(ent, dict):
                    continue
                st = ent.get('style') or {}
                sk = color_to_rgb_key(st.get('stroke_color'))
                fk = color_to_rgb_key(st.get('fill_color'))
                if sk in member_set or fk in member_set:
                    filtered.append(ent)
                    continue
                if color_matches_hex_key(sk, fk, hex_key, tolerance):
                    filtered.append(ent)
            if not filtered:
                continue
            doc[key_name] = filtered
            doc['color_filter'] = hex_key
            doc['color_filter_tolerance'] = tolerance
            dest = os.path.join(out_dir, 'geometry', rel)
            os.makedirs(os.path.dirname(dest), exist_ok=True)
            with open(dest, 'w', encoding='utf-8') as f:
                json.dump(doc, f, ensure_ascii=False, indent=2)
            count += len(filtered)
    return count


def main():
    if len(sys.argv) < 11:
        print(
            'Uso: phase1_export_zone_by_color.py <pdf> <out_dir> <page> '
            '<x0> <y0> <x1> <y1> <rotation> <width_pt> <height_pt> [dpi] [tolerance] '
            '[preset] [min_pixels] [min_coverage] [min_json_entities] '
            '[white_threshold] [white_luma] [white_chroma]',
            file=sys.stderr,
        )
        sys.exit(2)

    pdf_path = sys.argv[1]
    out_dir = sys.argv[2]
    page_num = int(sys.argv[3])
    bbox = [float(sys.argv[4]), float(sys.argv[5]), float(sys.argv[6]), float(sys.argv[7])]
    rotation = int(sys.argv[8])
    width_pt = float(sys.argv[9])
    height_pt = float(sys.argv[10])
    dpi = int(sys.argv[11]) if len(sys.argv) > 11 else DEFAULT_DPI
    tolerance = int(sys.argv[12]) if len(sys.argv) > 12 else DEFAULT_TOLERANCE
    preset = sys.argv[13] if len(sys.argv) > 13 else DEFAULT_PRESET
    min_color_pixels = int(sys.argv[14]) if len(sys.argv) > 14 else None
    min_color_coverage = float(sys.argv[15]) if len(sys.argv) > 15 else None
    min_json_entities = int(sys.argv[16]) if len(sys.argv) > 16 else None
    white_threshold = int(sys.argv[17]) if len(sys.argv) > 17 else None
    white_luma_threshold = int(sys.argv[18]) if len(sys.argv) > 18 else None
    white_chroma_spread = int(sys.argv[19]) if len(sys.argv) > 19 else None

    settings = resolve_runtime_settings(
        preset,
        min_color_pixels,
        min_color_coverage,
        min_json_entities,
        white_threshold,
        white_luma_threshold,
        white_chroma_spread,
    )

    os.makedirs(out_dir, exist_ok=True)
    doc = fitz.open(pdf_path)
    if page_num < 1 or page_num > doc.page_count:
        doc.close()
        raise ValueError('page fora do intervalo')

    page = doc.load_page(page_num - 1)
    zone_raw = derotated_bbox_to_raw_rect(bbox, rotation, width_pt, height_pt)

    zone_pix = render_zone_pixmap(page, zone_raw, dpi)
    zone_full_path = os.path.join(out_dir, 'zone_full_render.png')
    zone_pix.save(zone_full_path)

    drawing_keys = set()
    for d in page.get_drawings():
        bb = None
        try:
            bb = fitz.Rect(d.get('rect', (0, 0, 0, 0)))
        except Exception:
            pass
        if bb is None or bb.is_empty or bb.intersects(zone_raw):
            for k in color_keys_for_drawing(d):
                drawing_keys.add(k)

    pixmap_keys = collect_colors_from_pixmap(
        zone_pix,
        tolerance,
        MAX_COLOR_GROUPS,
        settings['white_threshold'],
        settings['white_luma_threshold'],
        settings['white_chroma_spread'],
    )
    pixmap_rgbs = [hex_to_rgb(k) for k in pixmap_keys]
    drawing_filtered = []
    draw_link_tol_sq = (max(1, tolerance) * 2) ** 2
    for key in drawing_keys:
        dr, dg, db = hex_to_rgb(key)
        if is_background_like_color(
            dr, dg, db,
            settings['white_threshold'],
            settings['white_luma_threshold'],
            settings['white_chroma_spread'],
        ):
            continue
        if not pixmap_rgbs:
            drawing_filtered.append(key)
            continue
        if any(rgb_dist_sq(dr, dg, db, pr, pg, pb) <= draw_link_tol_sq for pr, pg, pb in pixmap_rgbs):
            drawing_filtered.append(key)

    all_keys = list(pixmap_keys) + [k for k in drawing_filtered if k not in pixmap_keys]
    color_groups = merge_color_keys(all_keys, tolerance)

    manifest = {
        'schema': 'phase1.zone_by_color.v3',
        'render_mode': 'pixel_mask',
        'color_assignment': 'stroke_or_fill_plus_pixmap',
        'color_preset': settings['preset'],
        'generated_at_utc': datetime.datetime.utcnow().isoformat() + 'Z',
        'source_pdf': pdf_path,
        'page': page_num,
        'bbox_pt_derotated': [r(x) for x in bbox],
        'bbox_pt_raw': [r(zone_raw.x0), r(zone_raw.y0), r(zone_raw.x1), r(zone_raw.y1)],
        'rotation_degrees': rotation,
        'dpi': dpi,
        'tolerance_rgb': tolerance,
        'min_color_pixels': settings['min_color_pixels'],
        'min_color_coverage': settings['min_color_coverage'],
        'min_json_entities': settings['min_json_entities'],
        'white_threshold': settings['white_threshold'],
        'white_luma_threshold': settings['white_luma_threshold'],
        'white_chroma_spread': settings['white_chroma_spread'],
        'zone_full_render': 'zone_full_render.png',
        'colors': [],
    }

    region_geom = os.path.join(os.path.dirname(out_dir), 'geometry')

    for group in color_groups:
        hex_key = group['rep']
        members = group['members']
        tr, tg, tb = hex_to_rgb(hex_key)
        if is_background_like_color(
            tr, tg, tb,
            settings['white_threshold'],
            settings['white_luma_threshold'],
            settings['white_chroma_spread'],
        ):
            continue
        color_dir = os.path.join(out_dir, hex_key)
        os.makedirs(color_dir, exist_ok=True)
        png_path = os.path.join(color_dir, 'page.png')
        matched_pixels = save_color_mask_png(zone_pix, (tr, tg, tb), tolerance, png_path)
        coverage = matched_pixels / float(max(1, zone_pix.width * zone_pix.height))
        json_count = filter_json_entities_by_color(
            region_geom, hex_key, members, tolerance, color_dir)
        keep_due_pixels = matched_pixels >= settings['min_color_pixels']
        keep_due_coverage = coverage >= settings['min_color_coverage']
        keep_due_entities = json_count >= settings['min_json_entities']
        if not (keep_due_pixels or keep_due_coverage or keep_due_entities):
            shutil.rmtree(color_dir, ignore_errors=True)
            continue
        score = compute_color_priority_score(coverage, json_count)
        manifest['colors'].append({
            'hex': '#' + hex_key,
            'member_keys': ['#' + m for m in members],
            'json_entities': json_count,
            'matched_pixels': matched_pixels,
            'coverage_ratio': round(coverage, 6),
            'score': score,
            'png': os.path.join(hex_key, 'page.png').replace('\\', '/'),
            'method': 'pixel_mask',
        })

    manifest['colors'].sort(
        key=lambda c: (
            float(c.get('score', 0.0)),
            float(c.get('coverage_ratio', 0.0)),
            int(c.get('json_entities', 0)),
            int(c.get('matched_pixels', 0)),
        ),
        reverse=True,
    )

    manifest_path = os.path.join(out_dir, 'by_color_manifest.json')
    with open(manifest_path, 'w', encoding='utf-8') as f:
        json.dump(manifest, f, ensure_ascii=False, indent=2)

    doc.close()
    zone_pix = None
    print(manifest_path)


if __name__ == '__main__':
    main()
