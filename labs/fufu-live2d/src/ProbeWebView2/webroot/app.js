/* FUFU-02 Probe A: Live2D Cubism Web Core → WebGL canvas（最小渲染管线，与 Probe B 的 C# 管线等价）
 * 数据流：model3.json → moc → Model.fromMoc → 每帧参数管线(motion/pose/eyeblink/breath)
 *         → model.update() → drawables 网格 → WebGL drawElements（模板缓冲做裁剪蒙版）。
 * 桥：window.chrome.webview.postMessage 与 C# 双向通信。
 */
'use strict';

const errEl = document.getElementById('err');
const post = m => { try { window.chrome.webview.postMessage(m); } catch (e) {} };
const log = s => post({ type: 'log', msg: s });

let Core = null, model = null, spec = null, canvasW = 0, canvasH = 0, originX = 0, originY = 0, ppu = 1;
let gl = null, prog = null, texList = [], texturesByIndex = {};
let active = [], pose = null, clock = 0, lastT = 0;
let frames = 0, lastMetrics = performance.now();
const hitDrawableCache = new Map();

// ---------- 与 C# 侧同一套最小管线 ----------
const EyeBlink = (() => {
  let st = 'interval', nextBlink = 2, stStart = 0, userTime = 0;
  const I = 4, TC = 0.1, TCL = 0.05, TO = 0.15;
  return { eval(dt) {
    userTime += dt; const t = userTime - stStart;
    if (st === 'closing') { if (t >= TC) { st = 'closed'; stStart = userTime; return 0; } return 1 - t / TC; }
    if (st === 'closed') { if (t >= TCL) { st = 'opening'; stStart = userTime; return 0; } return 0; }
    if (st === 'opening') { if (t >= TO) { st = 'interval'; stStart = userTime; nextBlink = userTime + Math.random() * (2 * I - 1); return 1; } return t / TO; }
    if (userTime >= nextBlink) { st = 'closing'; stStart = userTime; }
    return 1;
  }};
})();
const BREATH = { t: 0, ids: ['ParamAngleX','ParamAngleY','ParamAngleZ','ParamBodyAngleX','ParamBreath'], cycle: 6.5345, peak: 3.5345 };

function easingSin(x) { return x <= 0 ? 0 : x >= 1 ? 1 : 0.5 - 0.5 * Math.cos(x * Math.PI); }

// motion3.json 解析（与 C# Motion3Json 等价）
function parseMotion(o) {
  const meta = o.Meta || {};
  const m = { duration: meta.Duration || 0, loop: !!meta.Loop, fps: meta.Fps || 30,
              fadeIn: meta.FadeInTime ?? 1, fadeOut: meta.FadeOutTime ?? 1, curves: [], points: [] };
  for (const c of (o.Curves || [])) {
    const cur = { type: c.Target === 'Model' ? 0 : c.Target === 'PartOpacity' ? 2 : 1,
                  id: c.Id, fadeIn: c.FadeInTime ?? -1, fadeOut: c.FadeOutTime ?? -1, segs: [] };
    const s = c.Segments;
    m.points.push([s[0], s[1]]);
    cur.base = m.points.length - 1;
    let i = 2;
    while (i < s.length) {
      const t = s[i], n = t === 1 ? 3 : 1;
      const base = m.points.length - 1;
      for (let k = 1; k <= n; k++) m.points.push([s[i + 2 * k - 1], s[i + 2 * k]]);
      cur.segs.push({ t, base, n });
      i += 1 + 2 * n;
    }
    m.curves.push(cur);
  }
  return m;
}

function evalCurve(m, c, t) {
  let target = -1;
  for (let s = 0; s < c.segs.length; s++) {
    const g = c.segs[s];
    if (m.points[g.base + g.n][0] > t) { target = s; break; }
  }
  if (target < 0) return m.points[c.segs[c.segs.length - 1].base + c.segs[c.segs.length - 1].n][1];
  const g = c.segs[target], p0 = m.points[g.base];
  switch (g.t) {
    case 0: {
      const p1 = m.points[g.base + 1];
      return p1[0] - p0[0] < 1e-8 ? p0[1] : p0[1] + (p1[1] - p0[1]) * (t - p0[0]) / (p1[0] - p0[0]);
    }
    case 1: {
      const p1 = m.points[g.base + 1], p2 = m.points[g.base + 2], p3 = m.points[g.base + 3];
      const tt = p3[0] - p0[0] < 1e-8 ? 0 : (t - p0[0]) / (p3[0] - p0[0]);
      const L = (a, b) => a + (b - a) * tt;
      const a1 = [L(p0[0], p1[0]), L(p0[1], p1[1])], b1 = [L(p1[0], p2[0]), L(p1[1], p2[1])], c1 = [L(p2[0], p3[0]), L(p2[1], p3[1])];
      const a2 = [L(a1[0], b1[0]), L(a1[1], b1[1])], b2 = [L(b1[0], c1[0]), L(b1[1], c1[1])];
      return a2[1] + (b2[1] - a2[1]) * tt;
    }
    case 2: return p0[1];
    case 3: return m.points[g.base + 1][1];
    default: return p0[1];
  }
}

// pose3.json（与 C# Pose3 等价）
function buildPose(root) {
  if (!root || !root.Groups) return null;
  return { fade: root.FadeInTime > 0 ? root.FadeInTime : 0.5, groups: root.Groups };
}

function poseUpdate(pose, model, partIdx, dt) {
  const EPS = 0.001, PHI = 0.5, BOT = 0.15;
  for (const g of pose.groups) {
    let vis = -1, newOp = 1;
    for (let i = 0; i < g.length; i++) {
      const pi = partIdx[g[i].Id];
      if (pi === undefined || pi < 0) continue;
      if (model.parts.opacities[pi] > EPS) {
        if (vis >= 0) break;
        vis = i;
        newOp = pose.fade > 0 ? Math.min(1, model.parts.opacities[pi] + dt / pose.fade) : 1;
      }
    }
    if (vis < 0) { vis = 0; newOp = 1; }
    for (let i = 0; i < g.length; i++) {
      const pi = partIdx[g[i].Id];
      if (pi === undefined || pi < 0) continue;
      if (i === vis) { model.parts.opacities[pi] = newOp; continue; }
      let a1 = newOp < PHI ? newOp * (PHI - 1) / PHI + 1 : (1 - newOp) * PHI / (1 - PHI);
      const bo = (1 - a1) * (1 - newOp);
      if (bo > BOT) a1 = 1 - BOT / (1 - newOp);
      model.parts.opacities[pi] = Math.min(model.parts.opacities[pi], a1);
    }
    for (const it of g) {
      const pi = partIdx[it.Id];
      if (pi === undefined || pi < 0 || !it.Link) continue;
      for (const lid of it.Link) {
        const li = partIdx[lid];
        if (li !== undefined && li >= 0) model.parts.opacities[li] = model.parts.opacities[pi];
      }
    }
  }
}

function paramIdx(id) { const i = model.parameters.ids.indexOf(id); return i; }

async function loadModel(name) {
  const base = `/models/${name}/`;
  const res = await fetch(base + `${name}.model3.json`);
  spec = await res.json();
  const fr = spec.FileReferences;
  const mocBuf = await (await fetch(base + fr.Moc)).arrayBuffer();
  Core = Live2DCubismCore;
  const moc = Core.Moc.fromArrayBuffer(mocBuf);
  const m = Core.Model.fromMoc(moc);
  const ci = m.canvasinfo;
  canvasW = ci.CanvasWidth; canvasH = ci.CanvasHeight;
  originX = ci.CanvasOriginX; originY = ci.CanvasOriginY; ppu = ci.PixelsPerUnit;
  texList = fr.Textures || [];
  for (let i = 0; i < texList.length; i++) {
    const img = new Image();
    await new Promise((res2, rej) => { img.onload = res2; img.onerror = rej; img.src = base + texList[i]; });
    texturesByIndex[i] = uploadTex(img);
  }
  pose = fr.Pose ? buildPose(await (await fetch(base + fr.Pose)).json()) : null;
  if (pose) for (const g of pose.groups) for (let i = 0; i < g.length; i++) {
    const pi = partIdxOf(m, g[i].Id); if (pi >= 0) m.parts.opacities[pi] = i === 0 ? 1 : 0;
  }
  const partIds = m.parts.ids;
  window.__partIdx = {}; partIds.forEach((id, i) => window.__partIdx[id] = i);
  hitDrawableCache.clear();
  (spec.HitAreas || []).forEach(h => {
    const di = m.drawables.ids.indexOf(h.Id);
    if (di >= 0) hitDrawableCache.set(di, h.Name);
  });
  return m;
}
function partIdxOf(m, id) { return m.parts.ids.indexOf(id); }

function playMotionGroup(group) {
  const mo = spec.Motions || {};
  let list = mo[group];
  if (!list) list = Object.values(mo).flat();
  if (!list || !list.length) return false;
  const pick = list[Math.floor(Math.random() * list.length)];
  fetch(`/models/${Program.name}/${pick.File}`).then(r => r.json()).then(o => {
    for (const a of active) a.finished = true;
    const mm = parseMotion(o);
    active.push({ m: mm, start: clock, end: mm.loop ? -1 : clock + mm.duration + (pick.FadeInTime ?? mm.fadeIn), fadeIn: pick.FadeInTime ?? mm.fadeIn, fadeOut: pick.FadeOutTime ?? mm.fadeOut, loop: mm.loop, finished: false });
    post({ type: 'motion', file: pick.File });
  });
  return true;
}

// ---------- WebGL ----------
const VS = `attribute vec2 a_pos; attribute vec2 a_uv; uniform vec2 u_scale; uniform vec2 u_off;
varying vec2 v_uv; void main(){ v_uv=a_uv; gl_Position=vec4(a_pos*u_scale+u_off,0,1); }`;
const FS = `precision mediump float; varying vec2 v_uv; uniform sampler2D u_tex; uniform float u_alpha;
void main(){ vec4 c=texture2D(u_tex,v_uv); gl_FragColor=vec4(c.rgb*c.a*u_alpha, c.a*u_alpha); }`;

function uploadTex(img) {
  const t = gl.createTexture();
  gl.bindTexture(gl.TEXTURE_2D, t);
  gl.pixelStorei(gl.UNPACK_PREMULTIPLY_ALPHA_WEBGL, true);
  gl.pixelStorei(gl.UNPACK_FLIP_Y_WEBGL, true);  // Cubism UV v=0 在贴图底部
  gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, gl.RGBA, gl.UNSIGNED_BYTE, img);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
  return t;
}

function initGl() {
  const cv = document.getElementById('cv');
  gl = cv.getContext('webgl', { alpha: true, premultipliedAlpha: true, stencil: true, preserveDrawingBuffer: false });
  const vs = gl.createShader(gl.VERTEX_SHADER), fs = gl.createShader(gl.FRAGMENT_SHADER);
  gl.shaderSource(vs, VS); gl.compileShader(vs);
  gl.shaderSource(fs, FS); gl.compileShader(fs);
  if (!gl.getShaderParameter(vs, gl.COMPILE_STATUS)) errEl.textContent += gl.getShaderInfoLog(vs);
  if (!gl.getShaderParameter(fs, gl.COMPILE_STATUS)) errEl.textContent += gl.getShaderInfoLog(fs);
  prog = gl.createProgram();
  gl.attachShader(prog, vs); gl.attachShader(prog, fs); gl.linkProgram(prog); gl.useProgram(prog);
  prog.a_pos = gl.getAttribLocation(prog, 'a_pos'); prog.a_uv = gl.getAttribLocation(prog, 'a_uv');
  prog.u_scale = gl.getUniformLocation(prog, 'u_scale'); prog.u_off = gl.getUniformLocation(prog, 'u_off');
  prog.u_tex = gl.getUniformLocation(prog, 'u_tex'); prog.u_alpha = gl.getUniformLocation(prog, 'u_alpha');
  gl.enableVertexAttribArray(prog.a_pos); gl.enableVertexAttribArray(prog.a_uv);
  gl.clearColor(0, 0, 0, 0);
}

// 顶点(model units) → 画布单位空间 [0,uW]×[0,uH]（左上原点，Y 向下）
function unitPt(x, y) { return [x + originX / ppu, originY / ppu - y]; }
// 顶点(model units) → 屏幕像素（letterbox 居中，供命中测试调试用）
function toScreen(x, y, W, H) {
  const uW = canvasW / ppu, uH = canvasH / ppu;
  const s = Math.min(W / uW, H / uH);
  const offX = (W - uW * s) / 2, offY = (H - uH * s) / 2;
  const [uX, uY] = unitPt(x, y);
  return [offX + uX * s, offY + uY * s];
}

function drawFrame() {
  if (!model || !gl) return;
  const cv = gl.canvas, W = cv.width, H = cv.height;
  const D = model.drawables;
  gl.viewport(0, 0, W, H);
  gl.clear(gl.COLOR_BUFFER_BIT);
  gl.enable(gl.BLEND);
  const uW = canvasW / ppu, uH = canvasH / ppu;
  const s = Math.min(W / uW, H / uH);
  const offX = (W - uW * s) / 2, offY = (H - uH * s) / 2;
  // 单位空间 → clip：clipX = uX*(2s/W) + (2*offX/W - 1)；clipY = uY*(-2s/H) + (1 - 2*offY/H)
  gl.uniform2f(prog.u_scale, 2 * s / W, -2 * s / H);
  gl.uniform2f(prog.u_off, 2 * offX / W - 1, 1 - 2 * offY / H);

  const order = [...Array(D.count).keys()].sort((a, b) => D.renderOrders[a] - D.renderOrders[b]);
  for (const i of order) {
    if (!(D.dynamicFlags[i] & 1)) continue;
    const alpha = Math.min(1, Math.max(0, D.opacities[i]));
    const vp = D.vertexPositions[i], uv = D.vertexUvs[i], idx = D.indices[i];
    const n = D.vertexCounts[i];
    const pos = new Float32Array(n * 2), tc = new Float32Array(n * 2);
    for (let k = 0; k < n; k++) {
      const [uX, uY] = unitPt(vp[2 * k], vp[2 * k + 1]);
      pos[2 * k] = uX; pos[2 * k + 1] = uY;
      tc[2 * k] = uv[2 * k]; tc[2 * k + 1] = uv[2 * k + 1];
    }

    const maskCnt = D.maskCounts[i];
    let useStencil = false;
    if (maskCnt > 0) {
      useStencil = true;
      gl.enable(gl.STENCIL_TEST);
      gl.clearStencil(0);
      gl.clear(gl.STENCIL_BUFFER_BIT);
      gl.colorMask(false, false, false, false);
      gl.stencilFunc(gl.ALWAYS, 1, 0xff);
      gl.stencilOp(gl.KEEP, gl.KEEP, gl.REPLACE);
      for (let mi = 0; mi < maskCnt; mi++) {
        const m = D.masks[i][mi];
        const mvp = D.vertexPositions[m], mn = D.vertexCounts[m], midx = D.indices[m];
        const mp = new Float32Array(mn * 2);
        for (let k = 0; k < mn; k++) {
          const [uX, uY] = unitPt(mvp[2 * k], mvp[2 * k + 1]);
          mp[2 * k] = uX; mp[2 * k + 1] = uY;
        }
        fillVerts(mp, midx, 1);
      }
      gl.colorMask(true, true, true, true);
      gl.stencilFunc(gl.EQUAL, 1, 0xff);
      gl.stencilOp(gl.KEEP, gl.KEEP, gl.KEEP);
    }

    const cflags = D.constantFlags[i];
    gl.blendFuncSeparate(gl.ONE, gl.ONE_MINUS_SRC_ALPHA, gl.ONE, gl.ONE_MINUS_SRC_ALPHA);
    gl.uniform1f(prog.u_alpha, alpha);
    gl.bindTexture(gl.TEXTURE_2D, texturesByIndex[D.textureIndices[i]] || null);
    fillVerts(pos, idx, 0, tc);

    if (useStencil) gl.disable(gl.STENCIL_TEST);
  }
  frames++;
}

const _vb = { pos: null, uv: null, ib: null };
function fillVerts(pos, idx, stencilOnly, uv) {
  if (!_vb.pos) { _vb.pos = gl.createBuffer(); _vb.uv = gl.createBuffer(); _vb.ib = gl.createBuffer(); }
  gl.bindBuffer(gl.ARRAY_BUFFER, _vb.pos);
  gl.bufferData(gl.ARRAY_BUFFER, pos, gl.DYNAMIC_DRAW);
  gl.vertexAttribPointer(prog.a_pos, 2, gl.FLOAT, false, 0, 0);
  gl.bindBuffer(gl.ARRAY_BUFFER, _vb.uv);
  gl.bufferData(gl.ARRAY_BUFFER, uv || new Float32Array(pos.length), gl.DYNAMIC_DRAW);
  gl.vertexAttribPointer(prog.a_uv, 2, gl.FLOAT, false, 0, 0);
  gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, _vb.ib);
  gl.bufferData(gl.ELEMENT_ARRAY_BUFFER, idx, gl.DYNAMIC_DRAW);
  if (stencilOnly) { gl.uniform1f(prog.u_alpha, 0); gl.drawElements(gl.TRIANGLES, idx.length, gl.UNSIGNED_SHORT, 0); }
  else gl.drawElements(gl.TRIANGLES, idx.length, gl.UNSIGNED_SHORT, 0);
}

function tick(tms) {
  const cv = document.getElementById('cv');
  const W = cv.clientWidth, H = cv.clientHeight;
  if (cv.width !== W || cv.height !== H) { cv.width = W; cv.height = H; }
  const dt = lastT ? Math.min(0.1, (tms - lastT) / 1000) : 1 / 60;
  lastT = tms;
  if (model) {
    clock += dt;
    // motion 管线
    const pv = model.parameters.values;
    for (const a of active) {
      if (a.finished) continue;
      const t = clock - a.start;
      const w = a.m.fadeIn <= 0 ? 1 : easingSin(t / a.fadeIn);
      const wOut = a.end < 0 ? 1 : easingSin((a.end - clock) / a.fadeOut);
      const fw = w * wOut;
      const et = a.loop && a.m.duration > 0 ? t % a.m.duration : Math.min(t, a.m.duration);
      let ebv = null, lsv = null;
      for (const c of a.m.curves) {
        const v = evalCurve(a.m, c, et);
        if (c.type === 0) {
          if (c.id === 'Opacity') {} else if (c.id === 'EyeBlink') ebv = v; else if (c.id === 'LipSync') lsv = v;
        } else if (c.type === 1) {
          const pi = paramIdx(c.id);
          if (pi < 0) continue;
          let vv = v;
          if (ebv !== null && (window.__ebIds || []).includes(c.id)) vv *= ebv;
          if (lsv !== null && (window.__lsIds || []).includes(c.id)) vv += lsv;
          pv[pi] += (vv - pv[pi]) * fw;
        } else {
          const pi = model.parts.ids.indexOf(c.id);
          if (pi >= 0) model.parts.opacities[pi] += (v - model.parts.opacities[pi]) * fw;
        }
      }
      if (!a.loop && t >= a.m.duration + a.fadeIn) a.finished = true;
    }
    active = active.filter(a => !a.finished);
    const blink = EyeBlink.eval(dt);
    (window.__ebIdx || []).forEach(pi => pv[pi] *= blink);
    BREATH.t += dt;
    const bt = BREATH.t % BREATH.cycle;
    const bv = BREATH.peak * Math.sin(Math.PI * bt / BREATH.cycle) * 0.5;
    (window.__brIdx || []).forEach(pi => pv[pi] += bv);
    if (pose) poseUpdate(pose, model, window.__partIdx, dt);
    model.update();
    drawFrame();
  }
  const now = performance.now();
  if (now - lastMetrics >= 1000) {
    post({ type: 'metrics', fps: frames * 1000 / (now - lastMetrics) });
    frames = 0; lastMetrics = now;
  }
  requestAnimationFrame(tick);
}

function hitTest(px, py) {
  if (!model) return null;
  const cv = gl.canvas, W = cv.width, H = cv.height;
  const D = model.drawables;
  const inTri = (x, y, a, b, c) => {
    const s1 = (x - b[0]) * (a[1] - b[1]) - (a[0] - b[0]) * (y - b[1]);
    const s2 = (x - c[0]) * (b[1] - c[1]) - (b[0] - c[0]) * (y - c[1]);
    const s3 = (x - a[0]) * (c[1] - a[1]) - (c[0] - a[0]) * (y - a[1]);
    const neg = s1 < 0 || s2 < 0 || s3 < 0, pos = s1 > 0 || s2 > 0 || s3 > 0;
    return !(neg && pos);
  };
  const uW = canvasW / ppu, uH = canvasH / ppu;
  const s = Math.min(W / uW, H / uH);
  const testDrawable = (di) => {
    const vp = D.vertexPositions[di], idx = D.indices[di];
    for (let t = 0; t < idx.length; t += 3) {
      const a = toScreen(vp[2 * idx[t]], vp[2 * idx[t] + 1], W, H);
      const b = toScreen(vp[2 * idx[t + 1]], vp[2 * idx[t + 1] + 1], W, H);
      const c = toScreen(vp[2 * idx[t + 2]], vp[2 * idx[t + 2] + 1], W, H);
      if (inTri(px, py, a, b, c)) return true;
    }
    return false;
  };
  for (const [di, name] of hitDrawableCache) if (testDrawable(di)) return name;
  for (let i = 0; i < D.count; i++)
    if ((D.dynamicFlags[i] & 1) && testDrawable(i)) return 'Model';
  return null;
}

const Program = { name: 'Hiyori' };

window.chrome.webview.addEventListener('message', e => {
  const m = e.data;
  if (m.type === 'load') { Program.name = m.name; init().catch(x => post({ type: 'err', msg: String(x) })); }
  else if (m.type === 'motion') playMotionGroup(m.group);
  else if (m.type === 'hitTest') {
    const cv = gl.canvas, r = cv.getBoundingClientRect();
    post({ type: 'hit', id: m.id, name: hitTest(m.x * (cv.width / r.width), m.y * (cv.height / r.height)) });
  }
});

async function init() {
  initGl();
  model = await loadModel(Program.name);
  // 眼眨/口型/呼吸参数索引
  const g = (spec.Groups || []);
  const eb = g.find(x => x.Name === 'EyeBlink'), ls = g.find(x => x.Name === 'LipSync');
  window.__ebIds = eb ? eb.Ids : [];
  window.__lsIds = ls ? ls.Ids : [];
  window.__ebIdx = window.__ebIds.map(paramIdx).filter(i => i >= 0);
  window.__brIdx = BREATH.ids.map(paramIdx).filter(i => i >= 0);
  post({ type: 'loaded', drawables: model.drawables.count, canvas: [canvasW, canvasH], ppu });
  playMotionGroup('Idle');
  requestAnimationFrame(tick);
}

// 页面就绪：通知宿主可发 load（模型加载完成再发 loaded）
post({ type: 'ready' });
