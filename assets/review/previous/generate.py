"""Generate the original Fufucode vector and raster assets."""
from pathlib import Path
from io import BytesIO
import cairosvg
from PIL import Image

ROOT = Path(__file__).resolve().parent


def save_svg(name, body, width, height):
    svg = f'''<svg xmlns="http://www.w3.org/2000/svg" width="{width}" height="{height}" viewBox="0 0 {width} {height}">
{body}
</svg>'''
    path = ROOT / name
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(svg, encoding="utf-8")
    return svg


def png(svg, name, width, height):
    path = ROOT / name
    path.parent.mkdir(parents=True, exist_ok=True)
    cairosvg.svg2png(bytestring=svg.encode(), write_to=str(path), output_width=width, output_height=height)


DEFS = '''<defs>
 <linearGradient id="base" x1="0" y1="0" x2="1" y2="1" objectBoundingBox="true"><stop stop-color="#155474"/><stop offset="1" stop-color="#102A3A"/></linearGradient>
 <linearGradient id="aqua" x1="0" y1="0" x2="1" y2="1" objectBoundingBox="true"><stop stop-color="#72E7DF"/><stop offset="1" stop-color="#087F99"/></linearGradient>
</defs>'''

# Distinctive mark: an open speech corner and a code insertion cursor.
MARK = '''<path d="M25 29 Q25 23 31 23 H70 Q77 23 77 30 V58 Q77 65 70 65 H47 L35 75 V65 H31 Q25 65 25 59 Z" fill="none" stroke="#62D8DB" stroke-width="7" stroke-linejoin="round"/>
<path d="M47 39 L39 47 L47 55 M58 38 V56 M66 39 L74 47 L66 55" fill="none" stroke="#F7FAF8" stroke-width="5.5" stroke-linecap="round" stroke-linejoin="round"/>'''
icon = save_svg('icon/icon.svg', DEFS + '<rect x="4" y="4" width="88" height="88" rx="23" fill="url(#base)"/>' + MARK, 96, 96)
small_mark = '''<rect x="3" y="3" width="90" height="90" rx="22" fill="#103B50"/>
<path d="M24 26 H73 V64 H47 L36 75 V64 H24 Z" fill="none" stroke="#66DDE0" stroke-width="9" stroke-linejoin="round"/>
<path d="M48 39 L39 47 L48 55 M61 37 V57" fill="none" stroke="#FFFFFF" stroke-width="7" stroke-linecap="round" stroke-linejoin="round"/>'''
small = save_svg('icon/icon-small.svg', small_mark, 96, 96)
for theme, bg in [('light', '#EFF7F7'), ('dark', '#0B1D27')]:
    preview = save_svg(f'icon/preview-{theme}.svg', f'<rect width="320" height="200" fill="{bg}"/><g transform="translate(112 52)">{DEFS}<rect x="4" y="4" width="88" height="88" rx="23" fill="url(#base)"/>{MARK}</g>', 320, 200)
    png(preview, f'icon/preview-{theme}.png', 320, 200)
png(icon, 'icon/icon-256.png', 256, 256)
png(small, 'icon/icon-small-32.png', 32, 32)
frames = []
for size in [16, 24, 32, 48, 64, 128, 256]:
    source = small if size <= 32 else icon
    data = cairosvg.svg2png(bytestring=source.encode(), output_width=size, output_height=size)
    frames.append(Image.open(BytesIO(data)).convert('RGBA'))
frames[-1].save(ROOT / 'icon' / 'fufucode.ico', format='ICO', append_images=frames[:-1], sizes=[(s, s) for s in [16, 24, 32, 48, 64, 128, 256]])

SCENES = {
 'conversation': '''<rect x="65" y="35" width="148" height="94" rx="20" fill="{panel}"/><path d="M88 129 V149 L114 129" fill="{panel}"/>
<rect x="106" y="75" width="132" height="82" rx="18" fill="{surface}" stroke="{line}" stroke-width="4"/>
<path d="M134 106 L122 116 L134 126 M151 103 V130 M171 106 L183 116 L171 126" fill="none" stroke="{line}" stroke-width="5" stroke-linecap="round" stroke-linejoin="round"/>
<circle cx="220" cy="49" r="7" fill="{accent}"/>''',
 'extensions': '''<rect x="73" y="37" width="173" height="126" rx="24" fill="{panel}"/>
<path d="M101 71 H145 V110 H101 Z M174 71 H218 V110 H174 Z M101 128 H145 V145 H101 Z" fill="{surface}" stroke="{line}" stroke-width="4" stroke-linejoin="round"/>
<path d="M181 130 H211 M196 115 V145" stroke="{accent}" stroke-width="7" stroke-linecap="round"/>''',
 'interrupted': '''<rect x="73" y="39" width="174" height="122" rx="23" fill="{panel}"/>
<path d="M104 70 H216 M104 96 H185" stroke="{line}" stroke-width="5" stroke-linecap="round"/>
<circle cx="205" cy="125" r="26" fill="{surface}" stroke="{accent}" stroke-width="5"/>
<path d="M205 112 V128 M205 138 V139" stroke="{accent}" stroke-width="6" stroke-linecap="round"/>'''
}
PALETTES = {
 'light': dict(panel='#E9F3F4', surface='#FFFFFF', line='#315B68', accent='#087F99'),
 'dark': dict(panel='#173743', surface='#102A3A', line='#B5DEE0', accent='#47D9DA')
}
for scene, markup in SCENES.items():
    for theme, palette in PALETTES.items():
        svg = save_svg(f'empty/{scene}-{theme}.svg', markup.format(**palette), 320, 200)
        png(svg, f'empty/{scene}-{theme}.png', 320, 200)
        png(svg, f'empty/{scene}-{theme}@2x.png', 640, 400)

# Dialog title-bar and window-control glyphs. These are original, stroke-based
# Fluent-style symbols designed for 16-32 px buttons; UI supplies hover/disabled
# colors and accessible localized names.
ACTION_GLYPHS = {
    'new-session': '<path d="M5 6.5A2.5 2.5 0 0 1 7.5 4h9A2.5 2.5 0 0 1 19 6.5v7a2.5 2.5 0 0 1-2.5 2.5H11l-4 3v-3.2a2.5 2.5 0 0 1-2-2.3z"/><path d="M20 8v8m-4-4h8"/>',
    'expand': '<path d="M4 9V4h5M20 9V4h-5M4 15v5h5M20 15v5h-5"/><path d="M9 4 4 9m11-5 5 5M4 15l5 5m11-5-5 5"/>',
    'collapse': '<path d="M9 4v5H4m11-5v5h5M9 20v-5H4m11 5v-5h5"/><path d="m4 9 5-5m11 5-5-5M4 15l5 5m11-5-5 5"/>',
    'pause': '<path d="M8 5h3v14H8zM15 5h3v14h-3z"/>',
    'resume': '<path d="M7 4.8a1 1 0 0 1 1.5-.86l10 6.2a2.2 2.2 0 0 1 0 3.72l-10 6.2A1 1 0 0 1 7 19.2z"/>',
    'stop': '<rect x="5" y="5" width="14" height="14" rx="2"/>',
    'window-minimize': '<path d="M5 17.5h14"/>',
    'window-maximize': '<rect x="5" y="5" width="14" height="14" rx="1.5"/>',
    'window-restore': '<path d="M8 8V5.5A1.5 1.5 0 0 1 9.5 4h9A1.5 1.5 0 0 1 20 5.5v9a1.5 1.5 0 0 1-1.5 1.5H16"/><rect x="4" y="8" width="12" height="12" rx="1.5"/>' ,
    'window-close': '<path d="m6 6 12 12M18 6 6 18"/>',
}
ACTION_COLOR = '#315B68'
ACTION_SVG_STYLE = f'<g fill="none" stroke="{ACTION_COLOR}" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round">{{glyph}}</g>'
for action, glyph in ACTION_GLYPHS.items():
    svg = save_svg(f'actions/{action}.svg', ACTION_SVG_STYLE.format(glyph=glyph), 24, 24)
    for size in (16, 20, 24, 32):
        png(svg, f'actions/{action}-{size}.png', size, size)

# Original custom-title-bar controls. Each control has a transparent resting
# state plus Fluent-like hover/pressed surfaces. The close control uses the
# familiar destructive red treatment on hover and press. PNGs are exported at
# 1x/2x/3x for a 46x32 DIP title-bar slot; the consuming UI owns hit testing,
# localization, and keyboard/accessibility behavior.
TITLEBAR_GLYPHS = {
    'minimize': '<path d="M18 16h10"/>',
    'maximize': '<rect x="18.5" y="10.5" width="9" height="9" rx=".5"/>',
    'restore': '<path d="M19 11h9v9M16 14h9v9h-9z"/>',
    'close': '<path d="m18.5 11.5 9 9m0-9-9 9"/>',
}
TITLEBAR_STATES = {
    'normal': (None, '#315B68'),
    'hover': ('#E9F3F4', '#102A3A'),
    'pressed': ('#D3E7E8', '#102A3A'),
}
for control, glyph in TITLEBAR_GLYPHS.items():
    for state, (surface, color) in TITLEBAR_STATES.items():
        if control == 'close' and state != 'normal':
            surface = '#C42B1C' if state == 'hover' else '#A4262C'
            color = '#FFFFFF'
        background = f'<rect width="46" height="32" fill="{surface}"/>' if surface else ''
        body = f'{background}<g fill="none" stroke="{color}" stroke-width="1.35" stroke-linecap="round" stroke-linejoin="round">{glyph}</g>'
        svg = save_svg(f'titlebar/{control}-{state}.svg', body, 46, 32)
        for scale in (1, 2, 3):
            suffix = '' if scale == 1 else f'@{scale}x'
            png(svg, f'titlebar/{control}-{state}{suffix}.png', 46 * scale, 32 * scale)

banner = save_svg('installer/wizard-side.svg', '''<defs><linearGradient id="bg" x2="0" y2="1"><stop stop-color="#12374A"/><stop offset="1" stop-color="#061B28"/></linearGradient></defs>
<rect width="330" height="630" fill="url(#bg)"/>
<path d="M-80 415 C125 310 100 600 390 410" fill="none" stroke="#225D6C" stroke-width="80" opacity=".5"/>
<path d="M-100 500 C120 390 175 660 390 520" fill="none" stroke="#35A5AA" stroke-width="32" opacity=".26"/>
<g transform="translate(96 106) scale(1.44)"><rect x="4" y="4" width="88" height="88" rx="23" fill="#12516C"/>'''+MARK+'''</g>''', 330, 630)
png(banner, 'installer/wizard-side.png', 330, 630)
header = save_svg('installer/wizard-small.svg', '<rect width="256" height="256" rx="48" fill="#102A3A"/><g transform="translate(7 7) scale(2.52)">'+MARK+'</g>', 256, 256)
png(header, 'installer/wizard-small.png', 256, 256)
finish = save_svg('installer/complete.svg', '''<rect width="256" height="256" rx="52" fill="#102A3A"/>
<circle cx="128" cy="128" r="77" fill="#17697B"/>
<path d="M82 128 L113 158 L177 94" fill="none" stroke="#F7FAF8" stroke-width="17" stroke-linecap="round" stroke-linejoin="round"/>''', 256, 256)
png(finish, 'installer/complete.png', 256, 256)
