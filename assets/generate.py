"""Original Fluent-style SVG/PNG artwork. Run: python assets/generate.py.

resvg renders without Cairo. Small icons use native geometry, not resized PNGs.
"""
from pathlib import Path
import json
import re
import struct
import resvg_py

ROOT = Path(__file__).resolve().parent
SIZES = (16, 20, 24, 32)
STATES = ('normal', 'hover', 'pressed', 'disabled')
EXPORTS = []


def document(body, w, h):
    return f'<svg xmlns="http://www.w3.org/2000/svg" width="{w}" height="{h}" viewBox="0 0 {w} {h}">\n{body}\n</svg>'


def svg(path, body, w, h):
    source = document(body, w, h)
    p = ROOT / path
    p.parent.mkdir(parents=True, exist_ok=True)
    p.write_text(source, encoding='utf-8')
    EXPORTS.append(dict(path=path, size=[w, h], kind='svg', origin='original'))
    return source


def render(source, w, h):
    return resvg_py.svg_to_bytes(svg_string=source, width=w, height=h, skip_system_fonts=True)


def png(path, source, w, h):
    p = ROOT / path
    p.parent.mkdir(parents=True, exist_ok=True)
    p.write_bytes(render(source, w, h))
    EXPORTS.append(dict(path=path, size=[w, h], kind='png', origin='original'))


THEMES = {
    'light': dict(canvas='#F3F3F3', fg='#242424', hover_fg='#1B1B1B', pressed_fg='#404040', disabled_fg='#999999', hover='#E7E7E7', pressed='#D9D9D9', edge='#D0D0D0', top='#FFFFFF', face='#EEF1F4', side='#CDD4DC', bottom='#BEC8D2', outline='#ABB6C1', detail='#8593A1', accent='#387FA9', accent_light='#A6CADC', shadow='#172A3B', shadow_alpha='.075'),
    'dark': dict(canvas='#202020', fg='#EEEEEE', hover_fg='#FFFFFF', pressed_fg='#D1D1D1', disabled_fg='#737373', hover='#373737', pressed='#464646', edge='#555555', top='#505A66', face='#39434F', side='#29313B', bottom='#242D37', outline='#697785', detail='#9CAAB7', accent='#7DADC6', accent_light='#456C82', shadow='#000000', shadow_alpha='.23'),
}

# 24-unit design grid. Each native size gets snapped coordinates and a width
# of 1/1.25/1.5/2 px, respectively. All paths below are original constructions.
PATHS = {
    'new-session': 'M17.25 10V7.5Q17.25 5.25 15 5.25H6Q3.75 5.25 3.75 7.5V13.25Q3.75 15.5 6 15.5H7.5V18.75L11.5 15.5H12.25 M18.75 12.5V18.5 M15.75 15.5H21.75',
    'expand': 'M14.25 5.25H18.75V9.75 M18.75 5.25L13 11 M5.25 14.25V18.75H9.75 M5.25 18.75L11 13',
    'collapse': 'M13.75 5.25V10.25H18.75 M13.75 10.25L19.25 4.75 M5.25 13.75H10.25V18.75 M10.25 13.75L4.75 19.25',
    'pause': 'M8.25 6.25V17.75 M15.75 6.25V17.75',
    'resume': 'M8.75 5.75L18.75 12L8.75 18.25Z',
    'stop': 'M7.75 6.75H16.25Q17.25 6.75 17.25 7.75V16.25Q17.25 17.25 16.25 17.25H7.75Q6.75 17.25 6.75 16.25V7.75Q6.75 6.75 7.75 6.75Z',
    'window-minimize': 'M6.75 12H17.25',
    'window-maximize': 'M5.75 5.75H18.25V18.25H5.75Z',
    'window-restore': 'M8.75 6.25V4.75H19.25V15.25H17.75 M4.75 8.75H15.25V19.25H4.75Z',
    'window-close': 'M6.25 6.25L17.75 17.75 M17.75 6.25L6.25 17.75',
}


def snapped_path(name, size):
    def snap(match):
        value = float(match.group()) * size / 24
        if size == 16:
            value = round(value - .5) + .5
        elif size == 32:
            value = round(value)
        else:
            value = round(value * 4) / 4
        return f'{value:g}'
    return re.sub(r'\d+(?:\.\d+)?', snap, PATHS[name])


def action_body(name, size, theme='light', state='normal'):
    p = THEMES[theme]
    color = p['fg'] if state == 'normal' else p[f'{state}_fg']
    stroke = {16: 1, 20: 1.25, 24: 1.5, 32: 2}[size]
    surface = ''
    if state in ('hover', 'pressed'):
        inset = .5 if state == 'hover' else 1
        surface = f'<rect x="{inset}" y="{inset}" width="{size-2*inset}" height="{size-2*inset}" rx="{size/6:g}" fill="{p[state]}"/>'
        if state == 'pressed':
            surface += f'<rect x="1" y="1" width="{size-2}" height="{size-2}" rx="{size/6:g}" fill="none" stroke="{p["edge"]}" stroke-width=".5"/>'
    return surface + f'<path d="{snapped_path(name, size)}" fill="none" stroke="{color}" stroke-width="{stroke}" stroke-linecap="round" stroke-linejoin="round"/>'


def generate_actions():
    for name in PATHS:
        svg(f'actions/{name}.svg', action_body(name,24),24,24)
        for size in SIZES:
            source = svg(f'actions/{name}-{size}.svg',action_body(name,size),size,size)
            png(f'actions/{name}-{size}.png',source,size,size)
            for theme in THEMES:
                for state in STATES:
                    source = svg(f'actions/{theme}/{name}-{state}-{size}.svg',action_body(name,size,theme,state),size,size)
                    png(f'actions/{theme}/{name}-{state}-{size}.png',source,size,size)


CAPTION = {
    'minimize': '<path d="M18.5 16.5H27.5"/>',
    'maximize': '<path d="M18.5 11.5H27.5V20.5H18.5Z"/>',
    'restore': '<path d="M20.5 12V10.5H28.5V18.5H27 M17.5 13.5H25.5V21.5H17.5Z"/>',
    'close': '<path d="M18.5 11.5L27.5 20.5 M27.5 11.5L18.5 20.5"/>',
}


def caption_body(name,theme,state):
    p = THEMES[theme]
    color = p['fg'] if state == 'normal' else p[f'{state}_fg']
    background = ''
    if state in ('hover','pressed'):
        fill = p[state]
        if name == 'close':
            fill = '#C42B1C' if state == 'hover' else '#A9251A'
            color = '#FFFFFF' if state == 'hover' else '#F5DEDB'
        background = f'<rect width="46" height="32" fill="{fill}"/>'
        if state == 'pressed':
            background += '<path d="M0 .5H46" stroke="#000000" stroke-opacity=".10"/>'
    return background + f'<g fill="none" stroke="{color}" stroke-width="1" stroke-linecap="round" stroke-linejoin="miter">{CAPTION[name]}</g>'


def generate_captions():
    for name in CAPTION:
        for theme in THEMES:
            for state in STATES:
                source = svg(f'titlebar/{theme}/{name}-{state}.svg',caption_body(name,theme,state),46,32)
                for scale in (1,2,3):
                    suffix = '' if scale == 1 else f'@{scale}x'
                    png(f'titlebar/{theme}/{name}-{state}{suffix}.png',source,46*scale,32*scale)
                if theme == 'light':
                    source = svg(f'titlebar/{name}-{state}.svg',caption_body(name,theme,state),46,32)
                    for scale in (1,2,3):
                        suffix = '' if scale == 1 else f'@{scale}x'
                        png(f'titlebar/{name}-{state}{suffix}.png',source,46*scale,32*scale)


def brand_body(theme='light',small=False):
    if small:
        return '''<path d="M7 3H18L27 12V26Q27 29 24 29H7Q5 29 5 26V6Q5 3 7 3Z" fill="#347DA5"/>
<path d="M18 3V10Q18 12 20 12H27" fill="#8DC8DD"/>
<path d="M10 15L14.5 19L10 23 M18 23H22" fill="none" stroke="#FFFFFF" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"/>'''
    top,bottom,fold = ('#67BDD0','#2E749C','#B2DFEA') if theme == 'light' else ('#76BCCF','#4583A6','#B8DCE5')
    return f'''<defs>
<linearGradient id="sheet" x1="0" y1="0" x2=".75" y2="1"><stop stop-color="{top}"/><stop offset="1" stop-color="{bottom}"/></linearGradient>
<linearGradient id="fold" x1="0" y1="0" x2="1" y2="1"><stop stop-color="{fold}"/><stop offset="1" stop-color="#80B9D0"/></linearGradient>
</defs>
<g transform="translate(-6.45 -8.1) scale(1.1)">
<path d="M28 22H58L79 43V78Q79 85 72 85H28Q22 85 22 78V29Q22 22 28 22Z" fill="#215C7C" opacity=".82"/>
<path d="M27 17H54L75 38V75Q75 82 68 82H27Q20 82 20 75V24Q20 17 27 17Z" fill="url(#sheet)"/>
<path d="M54 17V31Q54 38 61 38H75" fill="url(#fold)"/>
<path d="M21 26V73" stroke="#FFFFFF" stroke-opacity=".18" stroke-width="1"/>
<path d="M34 48L44 57L34 66 M51 66H62" fill="none" stroke="#FFFFFF" stroke-width="4.5" stroke-linecap="round" stroke-linejoin="round"/></g>'''


def write_ico(frames):
    header = struct.pack('<HHH',0,1,len(frames))
    entries,payload,offset = [],b'',6+16*len(frames)
    for size,data in frames:
        entries.append(struct.pack('<BBBBHHII',0 if size==256 else size,0 if size==256 else size,0,0,1,32,len(data),offset))
        payload += data
        offset += len(data)
    (ROOT/'icon/fufucode.ico').write_bytes(header+b''.join(entries)+payload)
    EXPORTS.append(dict(path='icon/fufucode.ico',sizes=[s for s,_ in frames],kind='ico',origin='original'))


def generate_brand():
    source = svg('icon/icon.svg',brand_body(),96,96)
    small = svg('icon/icon-small.svg',brand_body(small=True),32,32)
    frames = []
    for size in (16,20,24,32,48,64,128,256):
        chosen = small if size<=32 else source
        png(f'icon/icon-{size}.png',chosen,size,size)
        frames.append((size,render(chosen,size,size)))
    png('icon/icon-small-32.png',small,32,32)
    write_ico(frames)
    for theme,p in THEMES.items():
        brand = svg(f'icon/icon-{theme}.svg',brand_body(theme),96,96)
        for size in (96,192,288):
            png(f'icon/icon-{theme}-{size}.png',brand,size,size)
        preview = svg(f'icon/preview-{theme}.svg',f'<rect width="320" height="200" fill="{p["canvas"]}"/><g transform="translate(112 52)">{brand_body(theme)}</g>',320,200)
        for scale in (1,2,3):
            suffix = '' if scale==1 else f'@{scale}x'
            png(f'icon/preview-{theme}{suffix}.png',preview,320*scale,200*scale)


def illustration_body(scene,theme):
    p = THEMES[theme]
    defs = f'''<defs>
<linearGradient id="paper" x1="0" y1="0" x2=".4" y2="1"><stop stop-color="{p['top']}"/><stop offset="1" stop-color="{p['face']}"/></linearGradient>
<radialGradient id="ground"><stop stop-color="{p['shadow']}" stop-opacity="{p['shadow_alpha']}"/><stop offset="1" stop-color="{p['shadow']}" stop-opacity="0"/></radialGradient>
</defs>'''
    ground = '<ellipse cx="161" cy="157" rx="88" ry="14" fill="url(#ground)"/>'
    if scene=='conversation':
        body = f'''<g transform="rotate(-8 134 90)">
<rect x="91" y="49" width="88" height="75" rx="8" fill="{p['side']}"/>
<rect x="88" y="45" width="88" height="75" rx="8" fill="url(#paper)" stroke="{p['outline']}" stroke-width=".8"/>
<path d="M103 63H144 M103 73H157 M103 83H144" stroke="{p['detail']}" stroke-opacity=".55" stroke-width="2.5" stroke-linecap="round"/>
<path d="M103 99H125" stroke="{p['accent']}" stroke-width="3" stroke-linecap="round"/>
</g>
<g transform="rotate(5 184 111)">
<path d="M137 73H226Q234 73 234 81V137Q234 145 226 145H165L152 152V145H137Q129 145 129 137V81Q129 73 137 73Z" fill="{p['side']}"/>
<path d="M134 69H223Q231 69 231 77V133Q231 141 223 141H163L149 148V141H134Q126 141 126 133V77Q126 69 134 69Z" fill="url(#paper)" stroke="{p['outline']}" stroke-width=".8"/>
<path d="M127 89H230" stroke="{p['outline']}" stroke-opacity=".5" stroke-width=".8"/>
<circle cx="139" cy="80" r="2" fill="{p['accent']}"/><circle cx="147" cy="80" r="2" fill="{p['detail']}" opacity=".45"/>
<path d="M143 104L153 112L143 120 M165 120H177" stroke="{p['accent']}" stroke-width="2.3" fill="none" stroke-linecap="round" stroke-linejoin="round"/>
<path d="M187 105H213 M187 115H205 M143 130H186" stroke="{p['detail']}" stroke-opacity=".5" stroke-width="2" stroke-linecap="round"/>
</g>'''
    elif scene=='extensions':
        body = f'''<path d="M88 132L164 156L245 121L169 98Z" fill="{p['side']}" opacity=".45"/>
<path d="M88 127L164 151L245 116L169 93Z" fill="url(#paper)" stroke="{p['outline']}" stroke-width=".6"/>
<path d="M108 78L145 90V133L108 121Z" fill="{p['face']}"/>
<path d="M145 90L171 76V119L145 133Z" fill="{p['side']}"/>
<path d="M108 78L134 64L171 76L145 90Z" fill="{p['top']}"/>
<path d="M108 78L145 90L171 76 M145 90V133" stroke="{p['outline']}" stroke-width=".8" fill="none"/>
<path d="M118 103L135 109 M126.5 98V114" stroke="{p['accent']}" stroke-width="2" stroke-linecap="round"/>
<path d="M181 62L211 72V105L181 95Z" fill="{p['accent_light']}"/>
<path d="M211 72L231 61V94L211 105Z" fill="{p['accent']}" opacity=".85"/>
<path d="M181 62L201 51L231 61L211 72Z" fill="{p['accent_light']}"/>
<path d="M181 62L211 72L231 61 M211 72V105" stroke="{p['accent']}" stroke-opacity=".6" stroke-width=".8" fill="none"/>
<path d="M164 107L190 116V140L164 131Z" fill="{p['face']}"/>
<path d="M190 116L208 106V130L190 140Z" fill="{p['side']}"/>
<path d="M164 107L182 97L208 106L190 116Z" fill="{p['top']}"/>
<path d="M164 107L190 116L208 106 M190 116V140" stroke="{p['outline']}" stroke-width=".7" fill="none"/>
<path d="M159 68L171 64 M173 50V43" stroke="{p['detail']}" stroke-opacity=".5" stroke-linecap="round" stroke-width="1.4"/>'''
    else:
        marker = p['accent'] if scene=='interrupted' else ('#AE5559' if theme=='light' else '#D49798')
        sign = 'M202 121V141 M214 121V141' if scene=='interrupted' else 'M208 119V132 M208 141V142'
        body = f'''<g transform="rotate(-7 159 101)">
<rect x="106" y="44" width="103" height="111" rx="8" fill="{p['side']}"/>
<rect x="103" y="40" width="103" height="111" rx="8" fill="url(#paper)" stroke="{p['outline']}" stroke-width=".8"/>
<path d="M104 62H205" stroke="{p['outline']}" stroke-opacity=".55" stroke-width=".8"/>
<circle cx="116" cy="51" r="2" fill="{p['accent']}"/>
<path d="M118 80H170 M118 91H189 M118 102H174" stroke="{p['detail']}" stroke-opacity=".5" stroke-width="2.5" stroke-linecap="round"/>
<path d="M118 119H137 M147 119H175" stroke="{p['detail']}" stroke-opacity=".35" stroke-width="2.5" stroke-linecap="round"/>
</g>
<circle cx="211" cy="135" r="25" fill="{p['side']}"/>
<circle cx="208" cy="131" r="25" fill="url(#paper)" stroke="{p['outline']}" stroke-width=".8"/>
<path d="{sign}" stroke="{marker}" stroke-width="3" stroke-linecap="round"/>'''
    return defs+ground+body


def generate_empty():
    for scene in ('conversation','extensions','interrupted','error'):
        for theme in THEMES:
            source = svg(f'empty/{scene}-{theme}.svg',illustration_body(scene,theme),320,200)
            for scale in (1,2,3):
                suffix = '' if scale==1 else f'@{scale}x'
                png(f'empty/{scene}-{theme}{suffix}.png',source,320*scale,200*scale)


def generate_installer():
    for theme,p in THEMES.items():
        bodies = {
            'wizard-side': ('<g transform="translate(61 174) scale(2.16)">'+brand_body(theme)+'</g>',330,630),
            'wizard-small': ('<g transform="translate(24 24) scale(2.1666667)">'+brand_body(theme)+'</g>',256,256),
            'complete': ('<g transform="translate(60 30) scale(1.45)">'+brand_body(theme)+'</g>'+f'<circle cx="179" cy="176" r="33" fill="{p["face"]}" stroke="{p["outline"]}" stroke-width="1"/><path d="M164 176L175 187L194 165" fill="none" stroke="{p["accent"]}" stroke-width="4" stroke-linecap="round" stroke-linejoin="round"/>',256,256),
        }
        for name,(body,w,h) in bodies.items():
            source = svg(f'installer/{name}-{theme}.svg',body,w,h)
            for scale in (1,2,3):
                suffix = '' if scale==1 else f'@{scale}x'
                png(f'installer/{name}-{theme}{suffix}.png',source,w*scale,h*scale)
            if theme=='light':
                source = svg(f'installer/{name}.svg',body,w,h)
                for scale in (1,2,3):
                    suffix = '' if scale==1 else f'@{scale}x'
                    png(f'installer/{name}{suffix}.png',source,w*scale,h*scale)


def main():
    generate_brand()
    generate_empty()
    generate_actions()
    generate_captions()
    generate_installer()
    (ROOT/'exports.json').write_text(json.dumps(EXPORTS,indent=2),encoding='utf-8')
    print(f'Generated {len(EXPORTS)} production files with resvg.')


if __name__=='__main__':
    main()
