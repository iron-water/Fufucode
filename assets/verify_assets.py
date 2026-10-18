"""Check delivered files, clipping, native line weight and ICO frame fidelity.

No application build is claimed. Evidence goes to review/verification.json.
"""
from pathlib import Path
from collections import Counter
import hashlib
import json
import xml.etree.ElementTree as ET
from PIL import Image, ImageChops, ImageColor
import generate as g

ROOT = g.ROOT


def luminance(color):
    values = [x/255 for x in ImageColor.getrgb(color)]
    values = [x/12.92 if x<=.04045 else ((x+.055)/1.055)**2.4 for x in values]
    return sum(a*b for a,b in zip(values,(.2126,.7152,.0722)))


def contrast(a,b):
    x,y = sorted((luminance(a),luminance(b)))
    return (y+.05)/(x+.05)


def main():
    exports = json.loads((ROOT/'exports.json').read_text(encoding='utf-8'))
    errors = []
    seen = set()
    for e in exports:
        path = ROOT/e['path']
        if e['path'] in seen:
            errors.append(f'Duplicate manifest path: {e["path"]}')
        seen.add(e['path'])
        if not path.is_file():
            errors.append(f'Missing file: {e["path"]}')
            continue
        if e['kind']=='svg':
            root = ET.parse(path).getroot()
            if list(map(int,root.attrib['viewBox'].split()[-2:]))!=e['size']:
                errors.append(f'SVG dimensions: {e["path"]}')
            if root.findall('.//{http://www.w3.org/2000/svg}image') or root.findall('.//{http://www.w3.org/2000/svg}text'):
                errors.append(f'Production SVG must have native paths and no bitmap/font: {e["path"]}')
        elif e['kind']=='png':
            with Image.open(path) as img:
                if list(img.size)!=e['size']:
                    errors.append(f'PNG dimensions: {e["path"]}')
                img.verify()

    bounds, weight = {},{}
    for size in g.SIZES:
        weight[size] = round(sum(Image.open(ROOT/f'actions/pause-{size}.png').getchannel('A').crop((0,size//2,size,size//2+1)).get_flattened_data())/255/2,3)
        for name in g.PATHS:
            alpha = Image.open(ROOT/f'actions/{name}-{size}.png').getchannel('A')
            box = alpha.getbbox()
            bounds[f'{name}-{size}'] = box
            if not box or box[0]<1 or box[1]<1 or box[2]>size-1 or box[3]>size-1:
                errors.append(f'Glyph clipped or not padded: {name}-{size} {box}')
            # Independently verify that every theme/state file physically exists.
            for theme in ('light','dark'):
                for state in ('normal','hover','pressed','disabled'):
                    for ext in ('svg','png'):
                        if not (ROOT/f'actions/{theme}/{name}-{state}-{size}.{ext}').is_file():
                            errors.append(f'Missing native theme/state: {name}/{theme}/{state}/{size}/{ext}')

    ico = Image.open(ROOT/'icon/fufucode.ico')
    ico_sizes = sorted(ico.ico.sizes())
    if ico_sizes!=[(n,n) for n in (16,20,24,32,48,64,128,256)]:
        errors.append(f'ICO sizes: {ico_sizes}')
    for size,_ in ico_sizes:
        actual = ico.ico.getimage((size,size)).convert('RGBA')
        expected = Image.open(ROOT/f'icon/icon-{size}.png').convert('RGBA')
        if any(ImageChops.difference(actual,expected).getchannel(c).getbbox() for c in 'RGBA'):
            errors.append(f'ICO native frame mismatch: {size}')

    theme_contrasts = {}
    for theme,p in g.THEMES.items():
        c = {'normal':round(contrast(p['fg'],p['canvas']),2),
             'hover':round(contrast(p['hover_fg'],p['hover']),2),
             'pressed':round(contrast(p['pressed_fg'],p['pressed']),2),
             'disabled':round(contrast(p['disabled_fg'],p['canvas']),2),
             'hover_surface':round(contrast(p['hover'],p['canvas']),3),
             'pressed_surface':round(contrast(p['pressed'],p['canvas']),3)}
        theme_contrasts[theme] = c
        if min(c[x] for x in ('normal','hover','pressed'))<3:
            errors.append(f'Active icon contrast below 3:1: {theme}')
        if not (1<c['hover_surface']<c['pressed_surface']):
            errors.append(f'State surface contrast does not progress: {theme}')
        if c['disabled']>=c['normal']:
            errors.append(f'Disabled state is not muted: {theme}')

    before = json.loads((ROOT/'review/inventory-before.json').read_text(encoding='utf-8'))
    for path,record in before.items():
        old = ROOT/'review/previous'/path
        if hashlib.sha256(old.read_bytes()).hexdigest()!=record['sha256']:
            errors.append(f'Old archive was changed: {path}')
    comparisons = json.loads((ROOT/'review/comparisons.json').read_text(encoding='utf-8'))
    for entry in comparisons:
        for ext in ('png','svg'):
            path = ROOT/entry['comparison']
            if not path.with_suffix('.'+ext).is_file():
                errors.append(f'Missing comparison {entry["asset"]}/{ext}')
    result = dict(status='PASS' if not errors else 'FAIL',
                  scope='Asset files only; not an application or installer acceptance test',
                  counts=dict(Counter(e['kind'] for e in exports)),
                  production_bytes=sum((ROOT/e['path']).stat().st_size for e in exports),
                  native_pause_stroke_width_px=weight,
                  native_icon_bounds=bounds,ico_sizes=ico_sizes,
                  ico_native_frames_match_png=True if not any('ICO' in e for e in errors) else False,
                  state_contrast_ratios=theme_contrasts,
                  previous_files_preserved=len(before),comparison_boards=len(comparisons),
                  errors=errors,
                  unverified=['User aesthetic acceptance','D runtime integration','F installer integration',
                              'UniGetUI runtime hover/pressed/disabled captures','Unavailable exact semantic references'])
    (ROOT/'review/verification.json').write_text(json.dumps(result,indent=2),encoding='utf-8')
    print(json.dumps({k:result[k] for k in ('status','counts','native_pause_stroke_width_px','ico_sizes','state_contrast_ratios','comparison_boards','errors')},indent=2))
    if errors:
        raise SystemExit(1)


if __name__=='__main__':
    main()
