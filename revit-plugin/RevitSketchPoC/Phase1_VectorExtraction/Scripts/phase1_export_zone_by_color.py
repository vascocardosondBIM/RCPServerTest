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
import sys
import datetime

import fitz

DEFAULT_DPI = 300
DEFAULT_TOLERANCE = 32
MAX_COLOR_GROUPS = 48
WHITE_THRESHOLD = 248


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


def is_near_white(r, g, b, threshold=WHITE_THRESHOLD):
    return r >= threshold and g >= threshold and b >= threshold


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


def collect_colors_from_pixmap(pix, tolerance, max_colors=MAX_COLOR_GROUPS):
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
            if is_near_white(r, g, b):
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
            else:
                out[o] = br
                out[o + 1] = bgc
                out[o + 2] = bb

    mask_pix = fitz.Pixmap(fitz.Colorspace(fitz.CS_RGB), w, h, out, False)
    os.makedirs(os.path.dirname(dest_path) or '.', exist_ok=True)
    mask_pix.save(dest_path)
    mask_pix = None


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
            '<x0> <y0> <x1> <y1> <rotation> <width_pt> <height_pt> [dpi] [tolerance]',
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

    pixmap_keys = collect_colors_from_pixmap(zone_pix, tolerance)
    all_keys = list(drawing_keys) + [k for k in pixmap_keys if k not in drawing_keys]
    color_groups = merge_color_keys(all_keys, tolerance)

    manifest = {
        'schema': 'phase1.zone_by_color.v3',
        'render_mode': 'pixel_mask',
        'color_assignment': 'stroke_or_fill_plus_pixmap',
        'generated_at_utc': datetime.datetime.utcnow().isoformat() + 'Z',
        'source_pdf': pdf_path,
        'page': page_num,
        'bbox_pt_derotated': [r(x) for x in bbox],
        'bbox_pt_raw': [r(zone_raw.x0), r(zone_raw.y0), r(zone_raw.x1), r(zone_raw.y1)],
        'rotation_degrees': rotation,
        'dpi': dpi,
        'tolerance_rgb': tolerance,
        'zone_full_render': 'zone_full_render.png',
        'colors': [],
    }

    region_geom = os.path.join(os.path.dirname(out_dir), 'geometry')

    for group in color_groups:
        hex_key = group['rep']
        members = group['members']
        tr, tg, tb = hex_to_rgb(hex_key)
        color_dir = os.path.join(out_dir, hex_key)
        os.makedirs(color_dir, exist_ok=True)
        png_path = os.path.join(color_dir, 'page.png')
        save_color_mask_png(zone_pix, (tr, tg, tb), tolerance, png_path)
        json_count = filter_json_entities_by_color(
            region_geom, hex_key, members, tolerance, color_dir)
        manifest['colors'].append({
            'hex': '#' + hex_key,
            'member_keys': ['#' + m for m in members],
            'json_entities': json_count,
            'png': os.path.join(hex_key, 'page.png').replace('\\', '/'),
            'method': 'pixel_mask',
        })

    manifest_path = os.path.join(out_dir, 'by_color_manifest.json')
    with open(manifest_path, 'w', encoding='utf-8') as f:
        json.dump(manifest, f, ensure_ascii=False, indent=2)

    doc.close()
    zone_pix = None
    print(manifest_path)


if __name__ == '__main__':
    main()
