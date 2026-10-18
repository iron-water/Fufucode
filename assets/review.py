"""Comparison boards only; reference images never enter production exports.

Run: python assets/review.py --reference C:/Projects/Fufucode/reference/UniGetUI
Each reference panel says whether it is a shipped screenshot, source render,
or the nearest semantic analogue. No source render is called a live UI capture.
"""
from pathlib import Path
from io import BytesIO
import argparse
import ast
import base64
import hashlib
import html
import json
import re
import xml.etree.ElementTree as ET
from PIL import Image, ImageDraw, ImageFont
import generate as g

ROOT = g.ROOT
OLD = ROOT/'review/previous'
OUT = ROOT/'review/compare'
FONT = 'C:/Windows/Fonts/msyh.ttc'
INDEX = []


def font(size=15):
    return ImageFont.truetype(FONT,size)


def label(img,xy,text,color='#626A73',size=14):
    ImageDraw.Draw(img).text(xy,text,font=font(size),fill=color)


def wrapped(img,xy,text,width=365,size=14,color='#626A73'):
    draw = ImageDraw.Draw(img)
    line, y = '',xy[1]
    for ch in text:
        if ch=='\n' or draw.textlength(line+ch,font=font(size))>width:
            label(img,(xy[0],y),line,color,size)
            line,y = '' if ch=='\n' else ch,y+22
        else:
            line += ch
    if line:
        label(img,(xy[0],y),line,color,size)


def image(path,w=None,h=None):
    p = Path(path)
    if p.suffix=='.svg':
        if not w:
            root = ET.parse(p).getroot()
            view = root.attrib.get('viewBox','0 0 24 24').split()
            w,h = int(float(view[-2])),int(float(view[-1]))
        return Image.open(BytesIO(g.render(p.read_text(encoding='utf-8-sig'),w,h))).convert('RGBA')
    return Image.open(p).convert('RGBA')


def panel(h=280,bg='#F3F3F3'):
    return Image.new('RGB',(400,h),bg)


def paste(img,art,box):
    x,y,w,h = box
    scale = min(w/art.width,h/art.height)
    sized = art.resize((max(1,round(art.width*scale)),max(1,round(art.height*scale))),Image.Resampling.LANCZOS)
    img.paste(sized,(x+(w-sized.width)//2,y+(h-sized.height)//2),sized)


def board(name,title,old,new,ref,ref_path,notes=''):
    OUT.mkdir(parents=True,exist_ok=True)
    height = max(old.height,new.height,ref.height)+150
    result = Image.new('RGB',(1280,height),'#FFFFFF')
    label(result,(20,14),title,'#242424',21)
    for x,text,content in [(20,'旧版',old),(440,'返工版',new),(860,'UniGetUI 参考',ref)]:
        label(result,(x,53),text,'#242424',16)
        result.paste(content,(x,84))
    label(result,(20,height-48),'参考列含 UniGetUI 素材/截图，仅限设计审阅；不进入生产资产。','#777777',13)
    label(result,(20,height-25),notes,'#777777',12)
    result.save(OUT/f'{name}.png')
    buf = BytesIO()
    result.save(buf,format='PNG')
    encoded = base64.b64encode(buf.getvalue()).decode()
    (OUT/f'{name}.svg').write_text(g.document(f'<image width="1280" height="{height}" href="data:image/png;base64,{encoded}"/>',1280,height),encoding='utf-8')
    INDEX.append(dict(asset=name,title=title,comparison=f'review/compare/{name}.png',reference=str(ref_path),note=notes))


def art_panel(path,theme='light',h=280,description='',box=None):
    p = panel(h,g.THEMES[theme]['canvas'])
    bounds = box or (20,10,360,h-65)
    if Path(path).suffix=='.svg':
        view = ET.parse(path).getroot().attrib['viewBox'].split()
        w,height = float(view[-2]),float(view[-1])
        factor = min(bounds[2]/w,bounds[3]/height)
        art = image(path,round(w*factor),round(height*factor))
    else:
        art = image(path)
    paste(p,art,bounds)
    if description:
        wrapped(p,(18,h-51),description,size=12,color='#828282' if theme=='dark' else '#737373')
    return p


def old_action_sources():
    tree = ast.parse((OLD/'generate.py').read_text(encoding='utf-8-sig'))
    glyphs = next(ast.literal_eval(n.value) for n in tree.body if isinstance(n,ast.Assign) and any(isinstance(t,ast.Name) and t.id=='ACTION_GLYPHS' for t in n.targets))
    return {k:g.document(f'<g fill="none" stroke="#315B68" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round">{v}</g>',24,24) for k,v in glyphs.items()}


def native_action_panel(name,old=False):
    p = panel(650,'#FAFAFA')
    for theme_index,theme in enumerate(g.THEMES):
        y = theme_index*300
        surface = g.THEMES[theme]['canvas']
        p.paste(Image.new('RGB',(400,284),surface),(0,y))
        textcolor = '#D4D4D4' if theme=='dark' else '#606060'
        label(p,(15,y+8),f'{theme} · 原生像素',textcolor,13)
        if old:
            source = old_action_sources()[name]
            for row,size in enumerate(g.SIZES):
                label(p,(16,y+46+row*53),f'{size} px',textcolor,12)
                art = Image.open(BytesIO(g.render(source,size,size))).convert('RGBA')
                p.paste(art,(90,y+44+row*53),art)
                # A nearest-neighbour enlargement reveals blur, rather than
                # painting a falsely sharp version of the old icon.
                zoom = art.resize((size*2,size*2),Image.Resampling.NEAREST)
                p.paste(zoom,(170,y+36+row*53),zoom)
            wrapped(p,(14,y+260),'旧 action 文件未落盘：由留档脚本中的原始路径渲染；只提供过常规态。',365,11,textcolor)
        else:
            for col,state in enumerate(g.STATES):
                label(p,(75+col*79,y+35),state,textcolor,10)
            for row,size in enumerate(g.SIZES):
                label(p,(14,y+69+row*50),f'{size}px',textcolor,12)
                for col,state in enumerate(g.STATES):
                    art = image(ROOT/f'actions/{theme}/{name}-{state}-{size}.png')
                    p.paste(art,(87+col*79+(32-size)//2,y+65+row*50+(32-size)//2),art)
    wrapped(p,(14,605),'此列只重现旧版原始路径和固定颜色，未补造旧版缺失的状态。' if old else '常规→悬停→按下由底板和边缘层次表达；禁用保留轮廓并降低前景对比。',370,12)
    return p


def action_reference(ref,name):
    p = panel(650)
    symbol_root = ref/'src/SharedAssets/Assets/Symbols'
    if name=='new-session':
        source = ref/'media/UniGetUI_1.png'
        shot = image(source).crop((753,116,986,158))
        paste(p,shot,(12,40,376,90))
        paste(p,image(symbol_root/'add_to.svg',48,48),(170,180,60,60))
        wrapped(p,(18,270),'实际工具栏：添加到包集合。参考 add_to.svg 的 1.5 px 线重；UniGetUI 没有“新建会话”同语义按钮。')
    elif name in ('expand','collapse'):
        source = ref/'media/UniGetUI_8.png'
        paste(p,image(source).crop((65,554,365,598)),(12,40,376,90))
        paste(p,image(symbol_root/f'{name}.svg',48,48),(170,180,60,60))
        wrapped(p,(18,270),'实际 Operations 面板展开/收起。参考列为该位置截图＋源图标渲染；Fufucode 的扩大/缩小会话窗另用双向箭头。')
    elif name in ('pause','resume','stop'):
        source = ref/'media/UniGetUI_8.png'
        paste(p,image(source).crop((72,592,1568,655)),(12,40,376,95))
        paste(p,image(source).crop((1462,609,1565,648)),(115,178,170,66))
        wrapped(p,(18,270),'实际任务卡片 Cancel 按钮。此快照没有独立 Pause / Resume / 方形 Stop 图标；这里只能对照同位置的任务控制风格，不能声称有逐图标同位实测。')
    else:
        source = ref/'media/UniGetUI_1.png'
        paste(p,image(source).crop((1378,1,1528,47)),(18,40,364,108))
        wrapped(p,(18,200),'实际标题栏最小化/最大化/关闭。源 MainWindow.axaml 使用 1 px 几何笔画（关闭为 1.25 px）。还原态未出现在仓库截图中，只能核对源码回退图形。')
    wrapped(p,(18,480),'截图来自参考仓库 media；未运行 UniGetUI。悬停/按下/禁用的运行态截图缺失，当前只按源码令牌对照。',365,13)
    return p,source


def generate_actions(ref):
    for name in g.PATHS:
        rp,source = action_reference(ref,name)
        board(f'action-{name}',f'动作图标 · {name}',native_action_panel(name,True),native_action_panel(name),rp,source,'16 / 20 / 24 / 32 px · light / dark · 四态；第三列注明同位参考边界')


def caption_panel(name,old=False):
    p = panel(380)
    for row,theme in enumerate(g.THEMES):
        y = row*150
        p.paste(Image.new('RGB',(400,140),g.THEMES[theme]['canvas']),(0,y))
        color = '#DDDDDD' if theme=='dark' else '#626262'
        label(p,(12,y+7),theme,color,13)
        for col,state in enumerate(g.STATES):
            label(p,(10+col*98,y+36),state,color,11)
            path = OLD/f'titlebar/{name}-{state}.png' if old else ROOT/f'titlebar/{theme}/{name}-{state}.png'
            if path.exists():
                art = image(path)
                p.paste(art,(25+col*98,y+65),art)
            else:
                label(p,(15+col*98,y+74),'缺失',color,12)
    wrapped(p,(15,315),'旧版没有深色/禁用变体。新版保留 46×32 槽位；1x/2x/3x 使用同一 SVG 源。',365,12)
    return p


def generate_captions(ref):
    for name in g.CAPTION:
        rp = panel(380)
        paste(rp,image(ref/'media/UniGetUI_1.png').crop((1378,1,1528,47)),(18,16,364,110))
        paste(rp,image(ref/'media/UniGetUI_2.png').crop((1360,1,1515,47)),(18,136,364,110))
        wrapped(rp,(18,270),'实际深/浅标题栏截图。还原按钮未有截图；四种交互态仅有源码定义。源码按钮高度为 44，Fufucode 兼容槽位为 32。',365,12)
        board(f'titlebar-{name}',f'窗口按钮 · {name}',caption_panel(name,True),caption_panel(name),rp,ref/'src/UniGetUI.Avalonia/Views/MainWindow.axaml','参考源：MainWindow.axaml 的 caption 样式与几何路径；截图只证明常规态')


def generate_art(ref):
    reference_icon = ref/'media/assets/devolutions-unigetui-icon.svg'
    board('brand-main','应用标识 · 原创折页代码图形',art_panel(OLD/'icon/icon.svg'),art_panel(ROOT/'icon/icon.svg'),art_panel(reference_icon,description='实际品牌图标源；只借鉴体块与折叠层次'),reference_icon,'同一图标槽位；未复用 UniGetUI 路径')
    # Native-size comparison, with nearest-neighbour enlarged cells.
    panels = []
    for source,caption in [(OLD/'icon/icon-small.svg','旧版小尺寸源'),(ROOT/'icon/icon-small.svg','新版光学校准源'),(reference_icon,'UniGetUI 实际品牌 SVG')]:
        p = panel(390)
        for row,size in enumerate(g.SIZES):
            art = image(source,size,size)
            label(p,(18,24+row*80),f'{size}px',size=13)
            p.paste(art,(86,23+row*80),art)
            zoom = art.resize((size*3,size*3),Image.Resampling.NEAREST)
            p.paste(zoom,(163,8+row*80),zoom)
        label(p,(18,360),caption,size=12)
        panels.append(p)
    board('brand-small','小尺寸标识 · 原生像素与 3×近邻放大',*panels,reference_icon,'原生 16 / 20 / 24 / 32；参考列由 SVG 渲染，非全部尺寸的原有 ICO 层')
    for theme in g.THEMES:
        shot = ref/f'media/assets/splash-{theme}.png'
        rp = art_panel(shot,theme,description='实际 Splash（含字标）；构图比对，并非 Fufucode 启动画面')
        board(f'brand-preview-{theme}',f'品牌与留白 · {theme}',art_panel(OLD/f'icon/preview-{theme}.png',theme),art_panel(ROOT/f'icon/preview-{theme}.png',theme),rp,shot,'主题独立校准；参考为实际 Splash 资产')
    for scene in ('conversation','extensions','interrupted','error'):
        for theme in g.THEMES:
            reference = ref/('src/SharedAssets/Assets/Images/alert_laptop.png' if scene in ('interrupted','error') else 'src/UniGetUI.Avalonia/Assets/Images/Empty_inbox.png')
            note = '参考的空集合插画实际绑定于 PackageBundlesPage，最大 180 px；UniGetUI 无对话/扩展空态同语义页面。' if scene not in ('interrupted','error') else 'alert_laptop 为仓库错误类素材；未找到该图在现快照的绑定，不能称为已运行的同位空态。'
            rp = art_panel(reference,theme,description=note,box=(95,12,210,195))
            old_scene = 'interrupted' if scene=='error' else scene
            board(f'empty-{scene}-{theme}',f'空态 · {scene} · {theme}',art_panel(OLD/f'empty/{old_scene}-{theme}.svg',theme),art_panel(ROOT/f'empty/{scene}-{theme}.svg',theme),rp,reference,'透明 SVG/PNG；参考是空态槽位/错误语义对应，非逐页运行截图')
    for name in ('wizard-side','wizard-small','complete'):
        reference = ref/('InstallerExtras/unigetui-256.png' if name=='wizard-small' else 'InstallerExtras/installer-banner.png')
        height = 650 if name=='wizard-side' else 360
        for theme in g.THEMES:
            panels = []
            for source in (OLD/f'installer/{name}.png',ROOT/f'installer/{name}-{theme}.svg',reference):
                p = art_panel(source,theme,h=height,box=(35,10,330,height-85))
                panels.append(p)
            color = '#999999' if theme=='dark' else '#626A73'
            wrapped(panels[2],(18,height-60),'实际 Inno 引用文件。完成页复用向导侧图，未有独立同位置 256×256 完成图。' if name=='complete' else '实际 Inno WizardImageFile / WizardSmallImageFile；参考 .iss 的深色动态图复用同文件。',365,12,color)
            board(f'installer-{name}-{theme}',f'安装器素材 · {name} · {theme}',*panels,reference,'透明原图；独立 light/dark 校准；PNG 1x / 2x / 3x')


def overview():
    names = ['brand-main','empty-conversation-light','empty-extensions-light','empty-interrupted-dark']
    canvas = Image.new('RGB',(1280,420*len(names)),'white')
    for i,name in enumerate(names):
        img = image(OUT/f'{name}.png')
        paste(canvas,img,(0,i*420,1280,420))
    canvas.save(OUT/'overview.png')
    buf = BytesIO()
    canvas.save(buf,format='PNG')
    (OUT/'overview.svg').write_text(g.document(f'<image width="1280" height="{canvas.height}" href="data:image/png;base64,{base64.b64encode(buf.getvalue()).decode()}"/>',1280,canvas.height),encoding='utf-8')


def gallery():
    cards = []
    for entry in INDEX:
        name = entry['asset']
        group = name.split('-')[0]
        title = html.escape(entry['title'])
        cards.append(f'<article data-group="{group}"><header><h2>{title}</h2><a href="compare/{name}.png">PNG</a><a href="compare/{name}.svg">SVG 对比源</a></header><img loading="lazy" src="compare/{name}.png" alt="{title}：旧版、返工版、UniGetUI参考"></article>')
    icons = ''.join(f'<div class="glyph" data-name="{name}"><img alt="{name}"/><code>{name}</code></div>' for name in g.PATHS)
    source = '''<!doctype html><html lang="zh-CN"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Fufucode 资产返工对照</title>
<style>
*{box-sizing:border-box}body{margin:0;background:#f3f3f3;color:#242424;font:14px/1.6 "Segoe UI","Microsoft YaHei",sans-serif}main{max-width:1320px;margin:auto;padding:32px 20px}h1{font-size:28px;margin:0 0 8px;font-weight:600}h2{font-size:17px;margin:0}p{color:#666;margin:8px 0 20px}a{color:#1c5fb8;text-decoration:none}a:hover{text-decoration:underline}nav{display:flex;gap:8px;position:sticky;top:0;padding:12px 0;background:#f3f3f3;z-index:1}button,select{font:inherit;background:white;border:1px solid #d9d9d9;border-radius:5px;padding:5px 14px;color:inherit}button{cursor:pointer}button:hover{background:#e9e9e9}button.active{background:#e1ebf5;color:#1c5fb8;border-color:#b8cee3}.controls{display:flex;gap:16px;align-items:center;flex-wrap:wrap;padding:16px 0}.native{display:grid;grid-template-columns:repeat(auto-fit,minmax(110px,1fr));gap:12px;border:1px solid #ddd;border-radius:8px;padding:24px 12px;margin-bottom:24px;background:#f3f3f3}.native.dark{background:#202020;color:#eee;border-color:#444}.glyph{min-height:90px;display:flex;flex-direction:column;gap:14px;align-items:center;justify-content:center}.glyph img{object-fit:contain}.glyph code{font-size:11px;color:inherit}article{background:white;border:1px solid #e1e1e1;border-radius:8px;overflow:hidden;margin:0 0 24px;box-shadow:0 1px 2px #00000004}article header{display:flex;align-items:center;gap:18px;padding:14px 20px;border-bottom:1px solid #eee}article header h2{flex:1}article>img{display:block;width:100%;height:auto}footer{color:#777;font-size:12px;margin-top:20px}
</style><main><h1>Fufucode 资产返工对照</h1><p>2026-09-30 · 各板从左至右为旧版、返工版、UniGetUI 参考。同位缺失、源码渲染和未实测部分均在板内注明。</p>
<h2>动作图标原生尺寸</h2><div class="controls"><label>主题 <select id="theme"><option value="light">浅色</option><option value="dark">深色</option></select></label><label>状态 <select id="state"><option value="normal">常规</option><option value="hover">悬停</option><option value="pressed">按下</option><option value="disabled">禁用</option></select></label><label>尺寸 <select id="size"><option>16</option><option>20</option><option selected>24</option><option>32</option></select></label></div><div class="native" id="native">__ICONS__</div>
<nav><button class="active" data-filter="all">全部</button><button data-filter="brand">品牌</button><button data-filter="empty">空态</button><button data-filter="action">动作</button><button data-filter="titlebar">标题栏</button><button data-filter="installer">安装器</button></nav>
__CARDS__
<footer>生产来源与尺寸见 ../manifest.md。review/ 为设计审阅资料，不应进入产品包。参考许可原文见 UNIGETUI-LICENSE.txt。</footer></main>
<script>
const theme=document.getElementById('theme'),state=document.getElementById('state'),size=document.getElementById('size');
function update(){document.getElementById('native').classList.toggle('dark',theme.value==='dark');document.querySelectorAll('.glyph').forEach(el=>{const img=el.querySelector('img');img.src='../actions/'+theme.value+'/'+el.dataset.name+'-'+state.value+'-'+size.value+'.png';img.width=Number(size.value);img.height=Number(size.value);});}
[theme,state,size].forEach(el=>el.addEventListener('change',update));update();
document.querySelectorAll('[data-filter]').forEach(button=>button.addEventListener('click',()=>{document.querySelectorAll('[data-filter]').forEach(el=>el.classList.toggle('active',el===button));document.querySelectorAll('article').forEach(el=>el.hidden=button.dataset.filter!=='all'&&el.dataset.group!==button.dataset.filter);}));
</script></html>'''
    (ROOT/'review/index.html').write_text(source.replace('__ICONS__',icons).replace('__CARDS__','\n'.join(cards)),encoding='utf-8')


def reference_sources(ref):
    names = [
        'LICENSE','UniGetUI.iss','media/UniGetUI_1.png','media/UniGetUI_2.png','media/UniGetUI_8.png',
        'media/assets/devolutions-unigetui-icon.svg','media/assets/splash-light.png','media/assets/splash-dark.png',
        'src/SharedAssets/Assets/Symbols/add_to.svg','src/SharedAssets/Assets/Symbols/expand.svg','src/SharedAssets/Assets/Symbols/collapse.svg',
        'src/SharedAssets/Assets/Images/alert_laptop.png','src/UniGetUI.Avalonia/Assets/Images/Empty_inbox.png',
        'src/UniGetUI.Avalonia/Views/MainWindow.axaml','src/UniGetUI.Avalonia/Views/SoftwarePages/AbstractPackagesPage.axaml',
        'src/UniGetUI.Avalonia/Views/SoftwarePages/PackageBundlesPage.cs','src/UniGetUI.Avalonia/Assets/Styles/Styles.Common.axaml',
        'InstallerExtras/installer-banner.png','InstallerExtras/unigetui-256.png',
    ]
    records = {name:dict(bytes=(ref/name).stat().st_size,sha256=hashlib.sha256((ref/name).read_bytes()).hexdigest()) for name in names}
    (ROOT/'review/reference-sources.json').write_text(json.dumps(records,indent=2),encoding='utf-8')


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--reference',type=Path,required=True)
    args = parser.parse_args()
    generate_art(args.reference)
    generate_actions(args.reference)
    generate_captions(args.reference)
    overview()
    gallery()
    reference_sources(args.reference)
    (ROOT/'review/comparisons.json').write_text(json.dumps(INDEX,ensure_ascii=False,indent=2),encoding='utf-8')
    print(f'Generated {len(INDEX)} old/new/reference comparison boards, plus overview.')


if __name__=='__main__':
    main()
