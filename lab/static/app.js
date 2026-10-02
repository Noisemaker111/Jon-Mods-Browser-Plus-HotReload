import {screenPoint, worldPoint, latestAt, fitPoints} from './model.mjs';
import {initStudio} from './studio-editor.js';

const $ = selector => document.querySelector(selector);
const all = selector => [...document.querySelectorAll(selector)];
const state = {token: '', suites: {}, jobs: [], selected: null, active: null, artifacts: [], previewHandled: new Set()};
const title = kind => state.suites[kind]?.title || (kind === 'preview' ? 'UI preview' : kind);
const jobTitle = job => job.kind === 'scenario' ? job.params.name || title(job.kind) : title(job.kind);
const timeText = seconds => `${Math.max(0, seconds).toFixed(1)}s`;
function node(tag, className, text) { const el = document.createElement(tag); if (className) el.className = className; if (text != null) el.textContent = text; return el; }
function fail(error) { $('#error').textContent = String(error.message || error); $('#error').dataset.kind = error instanceof TypeError ? 'network' : 'request'; $('#error').hidden = false; }
function action(fn) { return (...args) => Promise.resolve().then(() => fn(...args)).catch(fail); }
async function api(path, data, retry = true) {
  const response = await fetch(path, data === undefined ? {} : {method: 'POST', headers: {'Content-Type': 'application/json', 'X-Lab-Token': state.token}, body: JSON.stringify(data)});
  const body = await response.json();
  if (response.status === 403 && data !== undefined && retry) {
    // A local dev-server restart rotates the mutation token. Refresh it once
    // instead of making every open browser tab require a manual reload.
    state.token = (await api('/api/status')).token;
    return api(path, data, false);
  }
  if (!response.ok) throw new Error(body.error || `Request failed (${response.status})`);
  return body;
}

// Navigation and theme share the same components across all workspaces.
function navigate(tab) {
  if (!['tests', 'ui', 'assets', 'map', 'board'].includes(tab)) tab = 'tests';
  all('nav button').forEach(b => b.classList.toggle('active', b.dataset.tab === tab));
  all('.page').forEach(p => p.classList.toggle('active', p.id === tab));
  $('#page-title').textContent = {tests: 'Test bench', ui: 'UI studio', assets: 'Asset library', map: 'World & paths', board: 'Design board'}[tab];
  $('#page-description').textContent = {
    tests: 'Build with confidence. Test your mods against the real game.',
    ui: 'Real game assets. Editable layers. Changes saved to your mod’s XML.',
    assets: 'Browse the actual sprites, textures, fonts, items and POI images from your game install.',
    map: 'Replay a follow run, inspect the planner, and see where it went wrong.',
    board: 'A clean canvas for designs, diagrams, and better test setups.',
  }[tab];
  location.hash = tab;
  if (tab === 'map') drawMap();
  if (tab === 'board') drawBoard();
}
all('nav button').forEach(b => b.onclick = () => navigate(b.dataset.tab));
window.addEventListener('hashchange', () => navigate(location.hash.slice(1)));
function applyTheme(theme) {
  document.documentElement.dataset.theme = theme;
  localStorage.setItem('lab-theme-v2', theme);
  $('#theme').textContent = theme === 'light' ? 'Dark theme' : 'Light theme';
  drawMap(); drawBoard();
}
$('#theme').onclick = () => applyTheme(document.documentElement.dataset.theme === 'light' ? 'dark' : 'light');

// Test jobs are asynchronous, streamed into the page, and persisted by the server.
function suiteCards() {
  $('#suites').replaceChildren();
  for (const [kind, suite] of Object.entries(state.suites)) {
    const card = node('div', 'panel suite');
    const heading = node('div', 'suite-heading'), icon = node('span', 'suite-icon');
    const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg'), use = document.createElementNS('http://www.w3.org/2000/svg', 'use');
    svg.classList.add('icon'); use.setAttribute('href', '#icon-' + {sim: 'code', static: 'check', chain: 'layers', probe: 'box', engine: 'map', scenario: 'plus'}[kind]); svg.append(use); icon.append(svg);
    heading.append(icon, node('h2', '', suite.title), node('span', 'badge', suite.engine ? 'Engine' : 'Offline'));
    card.append(heading, node('p', '', suite.detail));
    const button = node('button', suite.engine ? 'quiet' : 'primary', suite.engine ? 'Start engine check' : 'Run checks');
    button.dataset.job = kind;
    button.onclick = kind === 'scenario' ? () => { $('#scenario-builder').open = true; $('#scenario-builder').scrollIntoView({behavior: 'smooth'}); } : action(() => startJob(kind));
    if (kind === 'scenario') button.textContent = 'Build a test';
    card.append(button); $('#suites').append(card);
  }
}
async function startJob(kind, params = {}) {
  $('#error').hidden = true;
  const job = await api('/api/jobs', {kind, params});
  state.active = job.id;
  if (kind !== 'preview') state.selected = job.id;
  await refreshJobs();
  return job;
}
function stat(value, label) { const el = node('div', 'stat'); el.append(node('strong', '', value), node('small', '', label)); return el; }
function displayResult(job) {
  if (!job) return;
  $('#result-title').textContent = jobTitle(job);
  $('#result-status').textContent = job.status; $('#result-status').className = `badge ${job.status}`;
  const checks = job.checks || [];
  const elapsed = (job.finished || Date.now() / 1000) - job.started;
  $('#result-summary').replaceChildren(stat(checks.filter(c => c.status === 'pass').length, 'Explicit passes'), stat(checks.filter(c => c.status === 'fail').length, 'Explicit failures'), stat(checks.filter(c => c.status === 'skip').length, 'Skipped'), stat(timeText(elapsed), 'Elapsed'));
  $('#checks').replaceChildren();
  for (const check of checks) { const row = node('div', `check-row ${check.status}`); row.append(node('span', '', {pass: '✓', fail: '✕', skip: '–'}[check.status]), node('div', '', check.label)); $('#checks').append(row); }
  const output = $('#output'), atBottom = output.scrollTop + output.clientHeight >= output.scrollHeight - 30;
  output.textContent = job.output || 'Starting process…'; if (atBottom) output.scrollTop = output.scrollHeight;
}
async function refreshJobs() {
  state.jobs = await api('/api/jobs');
  if ($('#error').dataset.kind === 'network') $('#error').hidden = true;
  const active = state.jobs.find(j => j.status === 'running'); state.active = active?.id || null;
  all('[data-job], [data-source-save]').forEach(b => b.disabled = !!active);
  const history = $('#history'); history.replaceChildren();
  $('#run-count').textContent = `${state.jobs.length} runs`;
  if (!state.jobs.length) history.append(node('p', 'muted small', 'No runs yet. Nothing is marked passed until a check actually runs.'));
  if (!state.selected && state.jobs.length) state.selected = state.jobs.find(j => j.kind !== 'preview')?.id;
  for (const job of state.jobs) {
    const button = node('button', job.id === state.selected ? 'selected' : '');
    button.append(node('span', `badge ${job.status}`, job.status), node('strong', '', jobTitle(job)), node('small', '', new Date(job.started * 1000).toLocaleString()));
    button.onclick = () => { state.selected = job.id; displayResult(job); all('#history button').forEach(b => b.classList.remove('selected')); button.classList.add('selected'); };
    history.append(button);
  }
  displayResult(state.jobs.find(j => j.id === state.selected));
}
const scenarioQueries = ['hr doctor', 'pois', 'gettime', 'getgamestats', 'listplayers', 'version'];
function addScenarioStep(spec = {tel: 'hr doctor', pattern: 'doctor: \\d+ pass, 0 fail'}) {
  const row = node('div', 'scenario-step');
  const queryLabel = node('label', '', 'Console query'), query = node('select'); query.append(...scenarioQueries.map(q => new Option(q, q))); query.value = spec.tel; query.className = 'scenario-query'; queryLabel.append(query);
  const patternLabel = node('label', '', 'Expected output (regex)'), pattern = node('input'); pattern.value = spec.pattern; pattern.maxLength = 250; pattern.className = 'scenario-pattern'; patternLabel.append(pattern);
  const forbidLabel = node('label', 'check-label'), forbid = node('input'); forbid.type = 'checkbox'; forbid.checked = !!spec.forbid; forbid.className = 'scenario-forbid'; forbidLabel.append(forbid, document.createTextNode('Must be absent'));
  const remove = node('button', 'quiet', 'Remove'); remove.onclick = () => { row.remove(); persistScenario(); };
  row.append(queryLabel, patternLabel, forbidLabel, remove); row.addEventListener('input', persistScenario); $('#scenario-steps').append(row);
}
function scenarioParams() { return {name: $('#scenario-name').value, steps: all('.scenario-step').map(row => ({expect_console: {tel: row.querySelector('.scenario-query').value, pattern: row.querySelector('.scenario-pattern').value, forbid: row.querySelector('.scenario-forbid').checked}}))}; }
function persistScenario() { localStorage.setItem('lab-scenario', JSON.stringify(scenarioParams())); }
$('#scenario-add').onclick = () => { if (all('.scenario-step').length < 20) { addScenarioStep(); persistScenario(); } };
$('#scenario-name').oninput = persistScenario;
$('#scenario-run').onclick = action(() => startJob('scenario', scenarioParams()));
async function loadGallery() { state.artifacts = await api('/api/previews'); filterGallery(); }
function filterGallery() {
  const filter = $('#artifact-filter').value.toLowerCase(); $('#gallery').replaceChildren();
  for (const item of state.artifacts.filter(p => `${p.name} ${p.path}`.toLowerCase().includes(filter))) {
    const button = node('button', 'thumb'), image = node('img'); image.src = item.url; image.alt = item.name; image.loading = 'lazy';
    const label = node('span', '', item.name); label.append(node('small', '', item.path)); button.append(image, label);
    button.onclick = () => { $('#lightbox-title').textContent = item.name; $('#lightbox-image').src = item.url; $('#lightbox').showModal(); };
    $('#gallery').append(button);
  }
  if (!$('#gallery').children.length) $('#gallery').append(node('p', 'muted', 'No matching images. Render a preview or run a test that produces evidence.'));
}
$('#artifact-filter').oninput = filterGallery; $('#refresh-gallery').onclick = action(loadGallery);
$('#close-lightbox').onclick = () => $('#lightbox').close();

// Real-world map, with an independent world-coordinate camera (no stretched zoom).
const map = {world: null, image: null, recording: null, view: {x: 0, z: 0, scale: .1}, cursor: 0,
  shapes: [], draft: null, drag: null, playing: false, lastFrame: 0, generation: 0, dirty: false};
const layerSpec = [
  ['biomes', 'Biome overview', '#6f8d65'], ['grid', 'Coordinate grid', '#7a8880'], ['spawns', 'Spawn points', '#e6bd7b'],
  ['prefabs', 'Building markers', '#a8b3bc'], ['walked', 'Follower / leader trails', '#6ad3aa'], ['planned', 'Latest planned route', '#76b6ff'],
  ['walls', 'Planner wall cells', '#c79b86'], ['notes', 'Notes / blocked samples', '#f28d8d'], ['sketch', 'Your annotations', '#debf78'],
];
const layers = Object.fromEntries(layerSpec.map(([key]) => [key, true]));
for (const [key, label, color] of layerSpec) {
  const el = node('label', 'layer'), input = node('input'), swatch = node('i'); input.type = 'checkbox'; input.checked = true; swatch.style.background = color;
  input.onchange = () => { layers[key] = input.checked; drawMap(); }; el.append(input, swatch, document.createTextNode(label)); $('#layers').append(el);
}
function canvasContext(canvas) {
  const width = canvas.clientWidth, height = canvas.clientHeight; if (!width || !height) return null;
  const dpr = window.devicePixelRatio || 1;
  if (canvas.width !== Math.round(width * dpr) || canvas.height !== Math.round(height * dpr)) { canvas.width = Math.round(width * dpr); canvas.height = Math.round(height * dpr); }
  const ctx = canvas.getContext('2d'); ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  return {ctx, width, height};
}
function colors() { const style = getComputedStyle(document.documentElement); return {bg: style.getPropertyValue('--stage').trim(), ink: style.getPropertyValue('--ink').trim(), line: style.getPropertyValue('--line').trim()}; }
function pathLine(ctx, points, convert, color, width = 2, dashed = false) {
  if (!points.length) return; ctx.beginPath(); points.forEach((p, i) => { const [x, y] = convert(p); i ? ctx.lineTo(x, y) : ctx.moveTo(x, y); });
  ctx.strokeStyle = color; ctx.lineWidth = width; ctx.setLineDash(dashed ? [6, 4] : []); ctx.stroke(); ctx.setLineDash([]);
}
function marker(ctx, point, convert, color, label = '') {
  if (!point) return; const [x, y] = convert(point); ctx.fillStyle = color; ctx.beginPath(); ctx.arc(x, y, 4, 0, 2 * Math.PI); ctx.fill();
  if (label) { ctx.font = '11px Segoe UI'; ctx.fillText(label, x + 8, y - 7); }
}
function sampleAt(t) { return latestAt(map.recording?.samples || [], t); }
function drawMap() {
  const dimensions = canvasContext($('#world-canvas')); if (!dimensions) return;
  const {ctx, width, height} = dimensions, theme = colors(); ctx.fillStyle = theme.bg; ctx.fillRect(0, 0, width, height);
  if (!map.world) { ctx.fillStyle = theme.ink; ctx.fillText('Loading world data…', 25, 35); return; }
  const xy = p => screenPoint(p[0], p[1], map.view, width, height), v3 = p => xy([p[0], p[2]]);
  if (layers.biomes && map.image?.complete && map.image.naturalWidth) {
    const [sx, sz] = map.world.size, [x, y] = xy([-sx / 2, sz / 2]);
    ctx.globalAlpha = .55; ctx.drawImage(map.image, x, y, sx * map.view.scale, sz * map.view.scale); ctx.globalAlpha = 1;
  }
  if (layers.grid) {
    const [minX, maxZ] = worldPoint(0, 0, map.view, width, height), [maxX, minZ] = worldPoint(width, height, map.view, width, height);
    const raw = 95 / map.view.scale, step = 10 ** Math.ceil(Math.log10(raw)); ctx.strokeStyle = theme.line; ctx.fillStyle = theme.ink; ctx.font = '9px Consolas'; ctx.lineWidth = 1;
    for (let x = Math.ceil(minX / step) * step; x < maxX; x += step) { const [px] = xy([x, 0]); ctx.beginPath(); ctx.moveTo(px, 0); ctx.lineTo(px, height); ctx.stroke(); ctx.fillText(String(Math.round(x)), px + 3, 13); }
    for (let z = Math.ceil(minZ / step) * step; z < maxZ; z += step) { const [, py] = xy([0, z]); ctx.beginPath(); ctx.moveTo(0, py); ctx.lineTo(width, py); ctx.stroke(); ctx.fillText(String(Math.round(z)), 4, py - 3); }
  }
  if (layers.prefabs) { ctx.fillStyle = '#a8b3bc'; for (const p of map.world.prefabs) { const [x, y] = xy([p.x, p.z]); if (x < -4 || y < -4 || x > width + 4 || y > height + 4) continue; ctx.fillRect(x - 1.5, y - 1.5, 3, 3); } }
  if (layers.spawns) for (const p of map.world.spawns) marker(ctx, p, v3, '#e6bd7b');
  const recording = map.recording;
  if (recording) {
    const t = recording.start + map.cursor, visible = recording.samples.filter(s => s.t <= t), route = latestAt(recording.routes, t), current = sampleAt(t);
    if (layers.walls && route) {
      ctx.fillStyle = '#c79b86';
      for (const wall of route.walls || []) { const [x, y] = xy([wall[0], wall[1] + 1]); ctx.fillRect(x, y, Math.max(2, map.view.scale), Math.max(2, map.view.scale)); }
    }
    if (layers.planned && route) {
      pathLine(ctx, [route.p, ...(route.pts || [])].filter(Boolean), v3, route.found ? '#76b6ff' : '#f28d8d', 2, true);
      marker(ctx, route.g, v3, '#76b6ff', route.found ? 'Goal' : 'No route');
    }
    if (layers.walked) {
      pathLine(ctx, visible.map(s => s.l), v3, '#f28d8d'); pathLine(ctx, visible.map(s => s.p), v3, '#6ad3aa', 2.5);
      marker(ctx, recording.events.find(e => e.e === 'start')?.p || recording.samples[0]?.p, v3, '#e6bd7b', 'Start');
      if (current) { marker(ctx, current.p, v3, '#6ad3aa', 'Follower'); marker(ctx, current.l, v3, '#f28d8d', 'Leader'); if (current.w) pathLine(ctx, [current.p, current.w], v3, '#6ad3aa', 1, true); }
    }
    if (layers.notes) {
      for (const s of visible.filter(s => s.mode === 'blocked')) marker(ctx, s.p, v3, '#f28d8d');
      for (const e of recording.notes.filter(e => e.t <= t && e.e === 'note')) { const s = sampleAt(e.t); if (s) marker(ctx, s.p, v3, '#e6bd7b', e.what); }
    }
    updateSample(current, route);
  }
  if (layers.sketch) for (const shape of [...map.shapes, ...(map.draft ? [map.draft] : [])]) drawShape(ctx, shape, xy);
  drawTrace();
}
function drawTrace() {
  const dimensions = canvasContext($('#trace-chart')); if (!dimensions) return;
  const {ctx, width, height} = dimensions, theme = colors(); ctx.fillStyle = theme.bg; ctx.fillRect(0, 0, width, height);
  const r = map.recording; if (!r?.samples.length) return;
  const gaps = r.samples.map(s => Math.hypot(s.p[0] - s.l[0], s.p[2] - s.l[2])), maxGap = Math.max(1, ...gaps), duration = Math.max(.1, r.end - r.start);
  const convert = p => [38 + p[0] / duration * (width - 55), height - 24 - p[1] / maxGap * (height - 45)];
  ctx.fillStyle = theme.ink; ctx.font = '10px Segoe UI'; ctx.fillText(`Measured leader gap · max ${maxGap.toFixed(1)}m`, 15, 15);
  ctx.fillText('0s', 38, height - 7); ctx.fillText(timeText(duration), width - 50, height - 7);
  pathLine(ctx, r.samples.map((s, i) => [s.t - r.start, gaps[i]]), convert, '#6ad3aa', 1.5);
  const [x] = convert([map.cursor, 0]); ctx.strokeStyle = '#e6bd7b'; ctx.beginPath(); ctx.moveTo(x, 22); ctx.lineTo(x, height - 22); ctx.stroke();
}
function updateSample(sample, route) {
  $('#time-label').textContent = `${timeText(map.cursor)} / ${timeText(map.recording.end - map.recording.start)}`;
  $('#sample-stats').replaceChildren();
  if (!sample) return;
  const gap = Math.hypot(sample.p[0] - sample.l[0], sample.p[2] - sample.l[2]);
  $('#sample-stats').append(stat(sample.mode || '—', 'Movement'), stat(`${gap.toFixed(1)}m`, 'Leader gap'), stat(sample.stam == null ? '—' : sample.stam.toFixed(0), 'Stamina'), stat(route ? route.cells ?? '—' : '—', 'Planner cells'));
}
function fitWorld() {
  if (!map.world) return;
  const canvas = $('#world-canvas'), [sx, sz] = map.world.size;
  map.view = {x: 0, z: 0, scale: Math.min((canvas.clientWidth || 800) / sx, (canvas.clientHeight || 570) / sz) * .92}; drawMap();
}
function fitRoute() {
  const recording = map.recording; if (!recording) return;
  const points = [];
  for (const sample of recording.samples) for (const p of [sample.p, sample.l]) if (p) points.push([p[0], p[2]]);
  for (const route of recording.routes) for (const p of [route.p, route.g, ...(route.pts || [])]) if (p) points.push([p[0], p[2]]);
  const view = fitPoints(points, $('#world-canvas').clientWidth || 800, $('#world-canvas').clientHeight || 570); if (view) map.view = view; drawMap();
}
async function loadWorld() {
  if (map.dirty) await saveMap();
  const generation = ++map.generation, name = $('#world').value;
  const [world, drawing] = await Promise.all([api('/api/world/' + encodeURIComponent(name)), api('/api/drawings/map-' + name)]);
  if (generation !== map.generation) return;
  map.world = world; map.shapes = drawing.shapes || []; map.dirty = false;
  const image = new Image(); map.image = image; image.onload = drawMap; image.src = '/worlds/' + encodeURIComponent(name) + '/biomes.png';
  fitWorld(); if (map.recording) fitRoute();
}
async function refreshRecordings() {
  const previous = $('#recording').value || localStorage.getItem('lab-recording'), recordings = await api('/api/telemetry'); $('#recording').replaceChildren(new Option('No recording — overview only', ''));
  for (const r of recordings) $('#recording').append(new Option(`${r.name} · ${r.source}`, r.id));
  $('#recording').value = recordings.some(r => r.id === previous) ? previous : recordings[0]?.id || '';
  await loadRecording();
}
async function loadRecording() {
  map.playing = false; $('#play').textContent = '▶ Play'; const id = $('#recording').value;
  map.recording = id ? await api('/api/telemetry/' + id) : null;
  localStorage.setItem('lab-recording', id);
  $('#events').replaceChildren();
  if (map.recording) {
    const r = map.recording; map.cursor = r.end - r.start; $('#time').max = Math.max(.1, map.cursor); $('#time').value = map.cursor;
    $('#recording-info').textContent = `${r.samples.length} samples · ${r.routes.length} route plans. World is not recorded; choose the matching world above.${r.warnings.length ? ` ${r.warnings.length} incomplete or malformed lines skipped.` : ''}`;
    const interesting = r.events.filter(e => ['route', 'note', 'end'].includes(e.e));
    for (const e of interesting) {
      const text = e.e === 'route' ? `${e.found ? 'Route found' : 'No route'} · ${e.cells} cells` : e.what || e.why || e.e;
      const b = node('button', '', `${timeText(e.t - r.start)} · ${text}`); b.onclick = () => { map.cursor = e.t - r.start; $('#time').value = map.cursor; drawMap(); }; $('#events').append(b);
    }
    fitRoute();
  } else {
    $('#recording-info').textContent = 'No recording selected. Recordings from isolated lab clients appear here automatically.'; $('#sample-stats').replaceChildren(); $('#time-label').textContent = 'No recording';
  }
  drawMap();
}
$('#world').onchange = action(loadWorld); $('#recording').onchange = action(loadRecording); $('#refresh-recordings').onclick = action(refreshRecordings);
$('#fit-world').onclick = fitWorld; $('#fit-route').onclick = fitRoute;
$('#time').oninput = () => { map.cursor = Number($('#time').value); map.playing = false; $('#play').textContent = '▶ Play'; drawMap(); };
$('#play').onclick = () => { if (!map.recording) return; if (map.cursor >= map.recording.end - map.recording.start) map.cursor = 0; map.playing = !map.playing; map.lastFrame = performance.now(); $('#play').textContent = map.playing ? 'Ⅱ Pause' : '▶ Play'; };
function replayFrame(now) {
  if (map.playing && map.recording) {
    map.cursor = Math.min(map.recording.end - map.recording.start, map.cursor + (now - map.lastFrame) / 1000); map.lastFrame = now; $('#time').value = map.cursor;
    if (map.cursor >= map.recording.end - map.recording.start) { map.playing = false; $('#play').textContent = '▶ Play'; } drawMap();
  }
  requestAnimationFrame(replayFrame);
}
requestAnimationFrame(replayFrame);
function pointerPosition(e, canvas) { const r = canvas.getBoundingClientRect(); return [e.clientX - r.left, e.clientY - r.top]; }
const worldCanvas = $('#world-canvas');
function zoomCenter(factor) { map.view.scale = Math.max(.015, Math.min(80, map.view.scale * factor)); drawMap(); }
$('#zoom-in').onclick = () => zoomCenter(1.5); $('#zoom-out').onclick = () => zoomCenter(1 / 1.5);
worldCanvas.onkeydown = e => {
  if (e.key === '+' || e.key === '=') zoomCenter(1.5);
  else if (e.key === '-') zoomCenter(1 / 1.5);
  else if (e.key === 'ArrowLeft') map.view.x -= 60 / map.view.scale;
  else if (e.key === 'ArrowRight') map.view.x += 60 / map.view.scale;
  else if (e.key === 'ArrowUp') map.view.z += 60 / map.view.scale;
  else if (e.key === 'ArrowDown') map.view.z -= 60 / map.view.scale;
  else return;
  e.preventDefault(); drawMap();
};
worldCanvas.addEventListener('wheel', e => {
  e.preventDefault(); const [x, y] = pointerPosition(e, worldCanvas), before = worldPoint(x, y, map.view, worldCanvas.clientWidth, worldCanvas.clientHeight);
  map.view.scale = Math.max(.015, Math.min(80, map.view.scale * Math.exp(-e.deltaY * .0015)));
  const after = worldPoint(x, y, map.view, worldCanvas.clientWidth, worldCanvas.clientHeight);
  map.view.x += before[0] - after[0]; map.view.z += before[1] - after[1]; drawMap();
}, {passive: false});
worldCanvas.onpointerdown = e => {
  if (!map.world) return; const [x, y] = pointerPosition(e, worldCanvas), point = worldPoint(x, y, map.view, worldCanvas.clientWidth, worldCanvas.clientHeight), tool = $('#map-tool').value;
  if (tool === 'text') { if (!$('#map-note').value.trim()) return; map.shapes.push({type: 'text', points: [point], text: $('#map-note').value, color: '#e6bd7b'}); mapChanged(); return; }
  worldCanvas.setPointerCapture(e.pointerId);
  map.drag = {x, y, view: {...map.view}, tool};
  if (tool !== 'pan') map.draft = {type: tool, points: [point, point], color: '#e6bd7b'};
};
worldCanvas.onpointermove = e => {
  const [x, y] = pointerPosition(e, worldCanvas), point = worldPoint(x, y, map.view, worldCanvas.clientWidth, worldCanvas.clientHeight);
  $('#coordinates').textContent = `X ${point[0].toFixed(1)} · Z ${point[1].toFixed(1)} · ${map.view.scale.toFixed(2)} px/block`;
  if (map.drag?.tool === 'pan') { map.view.x = map.drag.view.x - (x - map.drag.x) / map.view.scale; map.view.z = map.drag.view.z + (y - map.drag.y) / map.view.scale; }
  else if (map.draft) { if (map.draft.type === 'pen') map.draft.points.push(point); else map.draft.points[1] = point; }
  if (map.drag) drawMap();
};
worldCanvas.onpointerup = () => { if (map.draft) { map.shapes.push(map.draft); map.draft = null; mapChanged(); } map.drag = null; };
worldCanvas.onpointercancel = () => { map.draft = null; map.drag = null; drawMap(); };
function mapChanged() { map.dirty = true; $('#map-save-state').textContent = 'Unsaved sketch · click Save sketch to keep it.'; drawMap(); }
$('#map-undo').onclick = () => { map.shapes.pop(); mapChanged(); };
async function saveMap() { if (!map.world) return; const result = await api('/api/drawings/map-' + map.world.name, {shapes: map.shapes, world: map.world.name}); map.dirty = false; $('#map-save-state').textContent = 'Saved · ' + result.path; }
$('#map-save').onclick = action(saveMap);

// Design board and map use the same serializable drawing shapes. Agents can write
// drawing JSON through the same local API; no markup or arbitrary code is executed.
const board = {shapes: [], draft: null, dirty: false};
function drawShape(ctx, shape, convert) {
  const points = shape.points || []; if (!points.length) return;
  ctx.strokeStyle = shape.color || '#e6bd7b'; ctx.fillStyle = shape.color || '#e6bd7b'; ctx.lineWidth = 2;
  const a = convert(points[0]), b = convert(points.at(-1));
  if (shape.type === 'text') { ctx.font = '14px Segoe UI'; ctx.fillText(shape.text || '', a[0], a[1]); }
  else if (shape.type === 'rect') { ctx.strokeRect(a[0], a[1], b[0] - a[0], b[1] - a[1]); }
  else {
    pathLine(ctx, points, convert, shape.color || '#e6bd7b', 2);
    if (shape.type === 'arrow') {
      const angle = Math.atan2(b[1] - a[1], b[0] - a[0]); ctx.beginPath(); ctx.moveTo(b[0], b[1]); ctx.lineTo(b[0] - 12 * Math.cos(angle - .4), b[1] - 12 * Math.sin(angle - .4)); ctx.moveTo(b[0], b[1]); ctx.lineTo(b[0] - 12 * Math.cos(angle + .4), b[1] - 12 * Math.sin(angle + .4)); ctx.stroke();
    }
  }
}
function drawBoard() {
  const dimensions = canvasContext($('#board-canvas')); if (!dimensions) return;
  const {ctx, width, height} = dimensions, theme = colors(); ctx.fillStyle = theme.bg; ctx.fillRect(0, 0, width, height);
  ctx.fillStyle = theme.line;
  for (let x = 16; x < width; x += 24) for (let y = 16; y < height; y += 24) ctx.fillRect(x, y, 1, 1);
  const convert = p => [p[0] / 1200 * width, p[1] / 700 * height];
  for (const shape of [...board.shapes, ...(board.draft ? [board.draft] : [])]) drawShape(ctx, shape, convert);
}
function boardPoint(e) { const c = $('#board-canvas'), [x, y] = pointerPosition(e, c); return [x / c.clientWidth * 1200, y / c.clientHeight * 700]; }
function boardChanged() { board.dirty = true; $('#board-state').textContent = 'Unsaved changes'; drawBoard(); }
$('#board-canvas').onpointerdown = e => {
  const point = boardPoint(e), type = $('#board-tool').value, color = $('#board-color').value;
  if (type === 'text') { if ($('#board-text').value.trim()) { board.shapes.push({type, color, points: [point], text: $('#board-text').value}); boardChanged(); } return; }
  e.target.setPointerCapture(e.pointerId); board.draft = {type, color, points: [point, point]}; drawBoard();
};
$('#board-canvas').onpointermove = e => { if (!board.draft) return; const p = boardPoint(e); if (board.draft.type === 'pen') board.draft.points.push(p); else board.draft.points[1] = p; drawBoard(); };
$('#board-canvas').onpointerup = () => { if (board.draft) { board.shapes.push(board.draft); board.draft = null; boardChanged(); } };
$('#board-canvas').onpointercancel = () => { board.draft = null; drawBoard(); };
$('#board-undo').onclick = () => { board.shapes.pop(); boardChanged(); };
$('#board-save').onclick = action(async () => { const result = await api('/api/drawings/design-board', {shapes: board.shapes, size: [1200, 700]}); board.dirty = false; $('#board-state').textContent = 'Saved · ' + result.path; });
async function exportCanvas(canvas, kind) {
  const result = await api('/api/exports', {kind, png: canvas.toDataURL('image/png')});
  const notice = $('#notice'), link = node('a', 'font-semibold underline', 'Open saved PNG ↗');
  link.href = result.url; link.target = '_blank'; link.rel = 'noopener';
  notice.replaceChildren(document.createTextNode(`Saved to ${result.path} · `), link); notice.hidden = false;
  await loadGallery();
}
$('#board-export').onclick = action(() => exportCanvas($('#board-canvas'), 'design')); $('#map-export').onclick = action(() => exportCanvas(worldCanvas, 'map'));
window.addEventListener('beforeunload', e => { if (map.dirty || board.dirty) { e.preventDefault(); e.returnValue = ''; } });
new ResizeObserver(drawMap).observe(worldCanvas); new ResizeObserver(drawBoard).observe($('#board-canvas'));

async function init() {
  $('#address').textContent = location.host;
  // The first browser workspace used dark green styling; start the redesigned
  // Tailwind workspace in light mode unless this version's preference is saved.
  applyTheme(localStorage.getItem('lab-theme-v2') || 'light'); navigate(location.hash.slice(1) || 'tests');
  const status = await api('/api/status'); state.token = status.token; state.suites = status.suites; $('#branch').textContent = status.branch; suiteCards();
  let scenario = null;
  try { scenario = JSON.parse(localStorage.getItem('lab-scenario')); } catch { /* ignore obsolete browser state */ }
  if (scenario?.steps?.length) { $('#scenario-name').value = scenario.name; scenario.steps.forEach(s => addScenarioStep(s.expect_console)); } else addScenarioStep();
  await refreshJobs();
  // Start test polling before loading any world/artifact data. A broken map or a
  // slow disk scan must not freeze an unrelated running test.
  let ticks = 0, refreshing = false;
  setInterval(async () => {
    if (refreshing) return; refreshing = true;
    try {
      await refreshJobs(); ticks++;
    } catch (error) { fail(error); } finally { refreshing = false; }
  }, 1000);
  window.addEventListener('focus', () => refreshJobs().catch(fail));
  await initStudio({api, startJob, fail, loadGallery});
  const worlds = await api('/api/worlds'); $('#world').replaceChildren(...worlds.map(w => new Option(w.name, w.name)));
  if (worlds.length) { $('#world').value = worlds.some(w => w.name === 'Navezgane') ? 'Navezgane' : worlds[0].name; await loadWorld(); }
  board.shapes = (await api('/api/drawings/design-board')).shapes || [];
  await Promise.all([refreshRecordings(), loadGallery()]); drawBoard();
}
init().catch(fail);
