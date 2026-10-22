// SpaceUI spike B: a web shell (WebKitGTK in a layer-shell surface, see host.py).
// Same scope and shared code as the native spike in ../native/shell.qml.
import * as S from "../shared/suggest.mjs";
import { tokens, layout as L } from "../shared/tokens.mjs";
import { fragmentSource, UNIFORMS } from "./plasma-shader.mjs";

const tk = tokens.baseline;
const C = tk.colors;
const params = new URLSearchParams(location.search);
const reduced = params.get("reduced") === "1" || matchMedia("(prefers-reduced-motion: reduce)").matches;
const host = window.webkit?.messageHandlers?.nexus;
const post = msg => host?.postMessage(JSON.stringify(msg));
const $ = id => document.getElementById(id);

for (const [k, v] of Object.entries({ primary: C.primary, focus: C.focus, border: C.border, "shell-ink": C.shellInk,
  "content-ink": C.contentInk, "content-secondary-ink": C.contentSecondaryInk, "window-border": C.windowBorder,
  "muted-surface": C.mutedSurface, destructive: C.destructive }))
  document.documentElement.style.setProperty(`--${k}`, v);

// ---- the plasma core: shared shader, premultiplied alpha, still-image fallback ----
const ASSET = "../shared/nexus-plasma-512.png";
const VERTEX = `#version 300 es
in vec2 p; out vec2 v_uv;
void main() { v_uv = vec2(p.x * 0.5 + 0.5, 0.5 - p.y * 0.5); gl_Position = vec4(p, 0.0, 1.0); }`;

class PlasmaCore {
  constructor(el, size, { strands = true } = {}) {
    Object.assign(el.style, { width: `${size}px`, height: `${size}px` });
    this.el = el; this.size = size; this.strands = strands;
    this.u = { u_time: 0, u_energy: 0, u_awake: 0, u_envelope: 0, u_strands: strands ? 1 : 0 };
    this.target = { energy: 0, awake: 0 };
    const canvas = document.createElement("canvas");
    canvas.width = canvas.height = Math.round(size * devicePixelRatio);
    el.append(canvas);
    canvas.addEventListener("webglcontextlost", e => { e.preventDefault(); this.fallback(); });
    try { this.init(canvas); } catch (err) { console.error("SPIKE core fallback:", err.message); this.fallback(); }
  }
  init(canvas) {
    const gl = canvas.getContext("webgl2", { premultipliedAlpha: true, alpha: true, antialias: false });
    if (!gl) throw new Error("WebGL2 unavailable");
    const sh = (type, src) => {
      const s = gl.createShader(type); gl.shaderSource(s, src); gl.compileShader(s);
      if (!gl.getShaderParameter(s, gl.COMPILE_STATUS)) throw new Error(gl.getShaderInfoLog(s));
      return s;
    };
    const prog = gl.createProgram();
    gl.attachShader(prog, sh(gl.VERTEX_SHADER, VERTEX));
    gl.attachShader(prog, sh(gl.FRAGMENT_SHADER, fragmentSource));
    gl.linkProgram(prog);
    if (!gl.getProgramParameter(prog, gl.LINK_STATUS)) throw new Error(gl.getProgramInfoLog(prog));
    gl.useProgram(prog);
    gl.bindBuffer(gl.ARRAY_BUFFER, gl.createBuffer());
    gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1, -1, 1, -1, -1, 1, 1, 1]), gl.STATIC_DRAW);
    const loc = gl.getAttribLocation(prog, "p");
    gl.enableVertexAttribArray(loc);
    gl.vertexAttribPointer(loc, 2, gl.FLOAT, false, 0, 0);
    this.loc = Object.fromEntries(UNIFORMS.map(n => [n, gl.getUniformLocation(prog, n)]));
    const img = new Image();
    img.onload = () => {
      gl.bindTexture(gl.TEXTURE_2D, gl.createTexture());
      gl.pixelStorei(gl.UNPACK_PREMULTIPLY_ALPHA_WEBGL, true);   // the asset rule: premultiply before filtering
      gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, gl.RGBA, gl.UNSIGNED_BYTE, img);
      gl.generateMipmap(gl.TEXTURE_2D);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR_MIPMAP_LINEAR);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
      this.gl = gl; this.draw();
    };
    img.onerror = () => this.fallback();
    img.src = ASSET;
  }
  fallback() {
    this.gl = null;
    this.el.replaceChildren(Object.assign(new Image(), { src: ASSET, alt: "" }));
  }
  step(dt, visible) {
    const ease = (a, b, ms) => a + (b - a) * Math.min(1, dt * 1000 / ms);
    this.u.u_energy = ease(this.u.u_energy, this.target.energy, 500 / 3);
    this.u.u_awake = ease(this.u.u_awake, this.target.awake, 400 / 3);
    if (!reduced) this.u.u_time += dt;
    this.u.u_envelope = this.target.energy >= 1 ? S.envelope(this.u.u_time) : 0;
    if (visible) this.draw();
  }
  draw() {
    const gl = this.gl;
    if (!gl) return;
    gl.viewport(0, 0, gl.drawingBufferWidth, gl.drawingBufferHeight);
    gl.clearColor(0, 0, 0, 0); gl.clear(gl.COLOR_BUFFER_BIT);
    for (const n of UNIFORMS) gl.uniform1f(this.loc[n], this.u[n]);
    gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
  }
}

// ---- state ----
const st = { expanded: false, showAll: false, suggested: [], replyApps: [], mode: "idle",
  reply: "", shown: "", provider: "", error: "", hoverOpen: false, toplevels: [] };

const zone = $("launcher-zone"), mini = $("mini"), stage = $("stage"), big = $("big");
const miniCore = new PlasmaCore(mini, L.launcher.diameter * 1.42, { strands: false });
const bigCore = new PlasmaCore(big, tk.nexus.hitDiameter * 1.42);
const place = (el, x, y) => Object.assign(el.style, { left: `${x}px`, top: `${y}px` });

// Launcher zone mirrors the native input mask: 120x100, or 300x190 when widened.
function layoutZone() {
  const w = st.hoverOpen ? 300 : 120, h = st.hoverOpen ? 190 : 100;
  Object.assign(zone.style, { width: `${w}px`, height: `${h}px` });
  place(zone, L.launcher.x - w / 2, L.launcher.y - h + 44);
  zone.classList.toggle("open", st.hoverOpen);
  const ox = w / 2, oy = h - 44;      // launcher centre inside the zone
  place(mini, ox - miniCore.size / 2, oy - miniCore.size / 2);
  const planets = $("planets");
  planets.replaceChildren(...st.toplevels.map((t, i) => {
    const p = S.launcherPlanet(i, st.toplevels.length, st.hoverOpen);
    const el = document.createElement("button");
    el.className = "planet";
    el.setAttribute("aria-label", `Switch to ${t.title}`);
    Object.assign(el.style, { width: `${p.size}px`, height: `${p.size}px` });
    place(el, ox + p.x - p.size / 2, oy + p.y - p.size / 2);
    const app = (t.appId || "").replace("nexus-spike-", "");
    el.innerHTML = `<img alt="" src="../shared/icons/${S.APP_IDS.includes(app) ? app : "files"}.svg"><span></span>`;
    el.querySelector("span").textContent = t.title.replace(" (spike stand-in)", "");
    el.onclick = () => st.hoverOpen && post({ type: "activate", id: t.id });
    return el;
  }));
  reportRegion();
}

function reportRegion() {
  const r = st.expanded ? [0, 0, L.surface.width, L.surface.height]
                        : [zone.offsetLeft, zone.offsetTop, zone.offsetWidth, zone.offsetHeight];
  post({ type: "region", expanded: st.expanded, rect: r });
}

let hoverTimer;
zone.addEventListener("mouseenter", () => { hoverTimer = setTimeout(() => { st.hoverOpen = true; layoutZone(); }, L.launcherHoverDelay); });
zone.addEventListener("mouseleave", () => { clearTimeout(hoverTimer); st.hoverOpen = false; layoutZone(); });
mini.addEventListener("mouseenter", () => { miniCore.target.awake = 1; });
mini.addEventListener("mouseleave", () => { miniCore.target.awake = 0; });
mini.addEventListener("click", () => setExpanded(true));

function setExpanded(v) {
  st.expanded = v;
  zone.hidden = v; stage.hidden = !v;
  if (v) setTimeout(() => $("prompt").focus(), 0);
  reportRegion();
}

function collapse() {
  Object.assign(st, { showAll: false, suggested: [], replyApps: [], reply: "", shown: "", error: "", mode: "idle" });
  $("prompt").value = "";
  renderReply();
  setExpanded(false);
}

// ---- expanded stage ----
const core = L.core;
place(big, core.x - bigCore.size / 2, core.y - bigCore.size / 2);
place($("core-title"), core.x - 150, core.y + 98);
$("orbit-lines").innerHTML =
  `<circle cx="${core.x}" cy="${core.y}" r="${tk.appOrbit.radius}" fill="none" stroke="rgba(135,117,230,${tk.appOrbit.outerStrokeOpacity})"/>` +
  `<circle cx="${core.x}" cy="${core.y}" r="${tk.appOrbit.innerRadius}" fill="none" stroke="rgba(135,117,230,.3)" stroke-dasharray="${tk.appOrbit.innerDash}"/>`;
big.addEventListener("mouseenter", () => { bigCore.target.awake = 1; });
big.addEventListener("mouseleave", () => { bigCore.target.awake = 0; });
big.addEventListener("click", e => { e.stopPropagation(); st.showAll = !st.showAll; });
stage.addEventListener("click", e => { if (e.target === stage) collapse(); });
addEventListener("keydown", e => { if (e.key === "Escape" && st.expanded) collapse(); });

const m = tk.appOrbit.motion;
const nodes = S.APP_IDS.map((id, index) => {
  const el = document.createElement("div");
  el.className = "app";
  el.innerHTML = `<button disabled aria-label="Open ${S.APP_LABELS[id]}"><img alt="" src="../shared/icons/${id}.svg"></button><span>${S.APP_LABELS[id]}</span>`;
  const btn = el.querySelector("button"), label = el.querySelector("span");
  Object.assign(btn.style, { width: `${tk.appOrbit.buttonDiameter}px`, height: `${tk.appOrbit.buttonDiameter}px` });
  label.style.top = `${tk.appOrbit.buttonDiameter / 2 + tk.appOrbit.labelGap}px`;
  btn.addEventListener("click", e => { e.stopPropagation(); post({ type: "launch", app: id }); collapse(); });
  $("apps").append(el);
  return { id, index, el, btn, shown: false, progress: 0, from: 0, to: 0, start: 0, duration: 0 };
});

function visibleApps() {
  if (st.showAll) return S.APP_IDS;
  const all = st.suggested.concat(st.replyApps);
  return S.APP_IDS.filter(id => all.includes(id));
}

function stepNodes(now) {
  const want = st.expanded ? visibleApps() : [];
  for (const n of nodes) {
    const shown = want.includes(n.id);
    if (shown !== n.shown) {
      n.shown = shown; n.from = n.progress; n.to = shown ? 1 : 0;
      n.start = now + (shown && !reduced ? n.index * m.entryStagger : 0);
      n.duration = reduced ? m.reducedEntryExit : shown ? m.entry : m.exit;
    }
    if (n.progress !== n.to && now >= n.start) {
      const k = Math.min(1, (now - n.start) / n.duration);
      n.progress = n.from + (n.to - n.from) * k;
    }
    const eased = n.shown ? S.entryEase(n.progress) : 1 - S.exitEase(1 - n.progress);
    const at = reduced ? { ...S.slot(n.id), scale: 1, opacity: n.progress } : S.emergence(n.id, eased);
    n.el.style.display = n.progress > 0 ? "" : "none";
    n.el.style.transform = `translate(${core.x + at.x}px, ${core.y + at.y}px) scale(${at.scale})`;
    n.el.style.opacity = at.opacity;
    const settled = n.shown && n.progress >= 1;
    n.btn.disabled = !settled;
    n.el.style.pointerEvents = settled ? "auto" : "none";
  }
}

// ---- chat: real answers from Nexus Core, via the host ----
let debounce;
$("prompt").addEventListener("input", e => {
  st.mode = e.target.value ? "typing" : "idle";
  clearTimeout(debounce);
  debounce = setTimeout(() => { st.suggested = S.suggestApps(e.target.value); kick(); }, m.intentDebounce);
});
$("chat").addEventListener("submit", e => {
  e.preventDefault();
  const prompt = $("prompt").value.trim();
  if (!prompt) return;
  Object.assign(st, { error: "", reply: "", shown: "", mode: "thinking" });
  renderReply();
  post({ type: "ask", prompt });
});
$("chat").addEventListener("click", e => e.stopPropagation());

let typer;
window.nexusHost = {
  answer(r) {
    if (r.error) { Object.assign(st, { error: r.error, mode: "idle" }); renderReply(); return; }
    Object.assign(st, { reply: r.answer, provider: r.provider, mode: "responding", replyApps: S.suggestApps($("prompt").value) });
    clearInterval(typer);
    // Demo pacing from the design pack: Core does not stream yet.
    typer = setInterval(() => {
      st.shown = reduced ? st.reply : st.reply.slice(0, st.shown.length + tk.demoOnlyTiming.characterBatch);
      renderReply(); kick();
      if (st.shown.length >= st.reply.length) { clearInterval(typer); setTimeout(() => { st.mode = "idle"; kick(); }, tk.demoOnlyTiming.idleAfterNormalReply); }
    }, reduced ? 1 : tk.demoOnlyTiming.characterTick);
  },
  toplevels(list) { st.toplevels = list; layoutZone(); kick(); },
};

function renderReply() {
  const box = $("reply"), text = $("reply-text");
  box.hidden = !st.shown && !st.error;
  text.className = st.error ? "error" : "";
  text.textContent = st.error ? `Nexus Core could not answer: ${st.error}` : st.shown;
  const done = !st.error && st.reply && st.shown.length >= st.reply.length;
  $("chips").replaceChildren(...(done ? [`Answer · Nexus Core (${st.provider} provider)`, "Apps · keyword match, not AI (prototype)"] : [])
    .map(t => Object.assign(document.createElement("span"), { textContent: t })));
}

// ---- layout ----
Object.assign($("chat").style, { left: `${L.chat.x}px`, top: `${L.chat.y}px`, width: `${L.chat.width}px`,
  height: `${L.chat.height}px`, borderRadius: `${tk.radii.chat}px` });
Object.assign($("send").style, { width: `${tk.chat.sendDiameter}px`, height: `${tk.chat.sendDiameter}px` });
Object.assign($("reply").style, { left: `${L.reply.x}px`, top: `${L.reply.y}px`, width: `${L.reply.width}px` });
const STATUS = { idle: "Ready when you are", typing: "Listening", thinking: "Asking Nexus Core…", responding: "Responding" };

// ---- frame loop, with the same per-second frame report as the native shell ----
// Under reduced motion nothing drifts, so the loop runs only while something
// changes (as the native shell's FrameAnimation stops), and kick() restarts it.
let last = performance.now(), frames = 0, lastReport = last, running = false, lastSig = "";
const signature = () => JSON.stringify([st.mode, st.expanded, st.showAll, st.suggested, st.replyApps, st.shown,
  miniCore.target, bigCore.target, nodes.map(n => n.progress)]);
function kick() {
  if (!running) { running = true; last = performance.now(); requestAnimationFrame(frame); }
}
for (const ev of ["pointermove", "pointerdown", "keydown", "input"]) addEventListener(ev, kick, true);
function frame(now) {
  const dt = Math.min(0.1, (now - last) / 1000);
  last = now;
  bigCore.target.energy = { responding: 1, thinking: 0.55, typing: 0.25, idle: 0 }[st.mode];
  miniCore.step(dt, !st.expanded);
  bigCore.step(dt, st.expanded);
  stepNodes(now);
  $("core-status").textContent = STATUS[st.mode];
  frames++;
  if (now - lastReport >= 1000) {
    post({ type: "fps", fps: frames });
    frames = 0; lastReport = now;
  }
  const sig = signature();
  if (!reduced || sig !== lastSig || nodes.some(n => n.progress !== n.to)) requestAnimationFrame(frame);
  else running = false;
  lastSig = sig;
}
layoutZone();
renderReply();
kick();
post({ type: "ready" });
