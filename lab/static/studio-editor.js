const $ = selector => document.querySelector(selector);
const clone = value => structuredClone(value);
function el(tag, className = '', text = '') { const n = document.createElement(tag); n.className = className; n.textContent = text; return n; }
function flat(node, depth = 0, result = []) { result.push({node, depth}); for (const child of node.children) flat(child, depth + 1, result); return result; }
function find(tree, id) { return flat(tree).find(entry => entry.node.id === id)?.node; }
function parentOf(tree, id) { return flat(tree).find(entry => entry.node.children.some(n => n.id === id))?.node; }
function number(value, fallback = 0) { const n = Number(value); return Number.isFinite(n) ? n : fallback; }
function colorCss(c) { return `rgba(${c[0]},${c[1]},${c[2]},${c[3] / 255})`; }

const defaultBindings = {
  name: 'NoisemakerJon', jonlevel: 'Lv 42', distance: '128m', healthmodifiedmax: '.78', healthfill: '.62',
  healthcurrentwithmax: '78/100', staminamodifiedmax: '.9', staminafill: '.55', jondeficitfill: '.35', jonxpfill: '.6',
  arrowcolor: '255,255,255,255', showarrow: 'true', showicon1: 'true', showicon2: 'true', icon1: 'ui_game_symbol_quest',
  icon2: 'ui_game_symbol_stealth', voicevisible: 'true', partyvisible: 'true', healthcolor: '255,255,255,255',
  distancecolor: '255,255,255,255', icon1color: '255,255,255,255', icon2color: '255,255,255,255',
  itemicon: 'gunHandgunT1Pistol', recipeicon: 'meleeToolAxeT1IronFireaxe', recipename: 'Iron Fireaxe', count: '8',
  itemicontint: '255,255,255,255', recipeicontint: '255,255,255,255', hasentry: 'true', hasitemtypeicon: 'false',
  btn_enabled: true, btn_hovered: false, btn_selected: false, hold_to_activate_state: 0,
  hasdurability: false, haspermadurability: false, isfavorite: false, isassemblelocked: false, isQuickSwap: false,
  ishovered: false, islocked: false, iconcolor: '255,255,255,255', backgroundcolor: '96,96,96,255',
  selectionbordercolor: '128,128,128,255', itemcount: '8', repeat_i: 0, locktypeicon: '',
  is_controller_input: false, is_console: false,
  currencyamount: '200', creativewindowopen: false, userlockmode: false, userlockedslot: false, lootingorvehiclestorage: false,
};
const states = {
  healthy: {}, low: {healthfill: '.15', healthcurrentwithmax: '12/100', staminafill: '.1', jonxpfill: '.2'},
  dead: {healthfill: '0', healthcurrentwithmax: '0/100', staminafill: '0', showarrow: 'false', voicevisible: 'false'},
  muted: {voicevisible: 'false'}, far: {distance: '412m', arrowcolor: '255,180,60,255'},
};

export async function initStudio({api, startJob, fail, loadGallery}) {
  const s = {catalog: [], assets: [], assetMap: new Map(), scene: null, tree: null, graph: null, selected: 'root/0',
    images: new Map(), fonts: new Map(), tint: new Map(), undo: [], redo: [], dirty: false, generation: 0,
    view: {x: 0, y: 0, scale: 1}, needFit: true, drag: null, bindings: {...defaultBindings}, renderTimer: null, draftTimer: null,
    assetPage: 0, pickerPage: 0, pickerKind: 'sprite', detail: null};
  const safe = fn => (...args) => Promise.resolve().then(() => fn(...args)).catch(fail);
  const status = (text, kind = '') => { $('#studio-status').textContent = text; $('#studio-status').className = 'badge ' + kind; };
  const payload = () => ({key: s.scene.key, revision: s.scene.revision, owner: $('#studio-owner').value, tree: s.tree, values: s.bindings});

  async function library() {
    s.images.clear(); s.tint.clear(); resolvedImages.clear();
    const manifest = await api('/api/assets'); s.assets = manifest.assets; s.assetMap = new Map(s.assets.map(a => [a.id, a]));
    $('#asset-summary').textContent = `${s.assets.length.toLocaleString()} real game assets · ${s.assets.filter(a => a.kind === 'sprite').length} atlas sprites · ${s.assets.filter(a => a.kind === 'item').length.toLocaleString()} item icons${manifest.warnings.length ? ` · ${manifest.warnings.length} extraction warnings` : ''}`;
    showAssets();
    if (!s.assets.length) $('#studio-render-info').textContent = 'Asset library is empty. Use Reindex game assets to extract it locally.';
    if (s.graph) await render();
  }
  function image(id) {
    if (!id || !s.assetMap.has(id)) return Promise.resolve(null);
    if (!s.images.has(id)) {
      const promise = new Promise((resolve, reject) => { const img = new Image(); img.onload = () => resolve(img); img.onerror = () => reject(new Error('Cannot load game asset ' + s.assetMap.get(id).name)); img.src = s.assetMap.get(id).url; });
      s.images.set(id, promise);
    }
    return s.images.get(id);
  }
  async function font(id) {
    if (!id || !s.assetMap.has(id)) return;
    if (!s.fonts.has(id)) {
      const face = new FontFace('game-' + id, `url("${s.assetMap.get(id).url}")`);
      s.fonts.set(id, face.load().then(loaded => { document.fonts.add(loaded); return loaded; }));
    }
    return s.fonts.get(id);
  }

  function sceneOptions() {
    const filter = $('#studio-scene-search').value.toLowerCase(), selected = s.scene?.key || 'XUi_InGame:templates.xml:party_entry';
    const choices = s.catalog.filter(scene => `${scene.name} ${scene.scope}`.toLowerCase().includes(filter));
    $('#studio-scene').replaceChildren(...choices.map(scene => new Option(`${scene.title} · ${scene.scope.replace('XUi_', '')} ${scene.kind}`, scene.key)));
    if (choices.some(c => c.key === selected)) $('#studio-scene').value = selected;
  }
  async function loadScene() {
    if (s.dirty && s.scene) await saveDraft();
    const key = $('#studio-scene').value; if (!key) return;
    const [scene, draft] = await Promise.all([api('/api/studio/scene?key=' + encodeURIComponent(key)), api('/api/studio/draft?key=' + encodeURIComponent(key))]);
    s.scene = scene; s.tree = clone(scene.tree); s.undo = []; s.redo = []; s.dirty = false; s.selected = 'root/0';
    $('#studio-owner').value = scene.owner;
    if (draft && draft.revision === scene.revision) { s.tree = draft.tree; s.bindings = {...s.bindings, ...draft.values}; s.dirty = JSON.stringify(draft.tree) !== JSON.stringify(scene.tree) || (draft.owner && draft.owner !== scene.owner); if (draft.owner) $('#studio-owner').value = draft.owner; }
    $('#studio-source').textContent = scene.source + (scene.patches.length ? ' + ' + scene.patches.join(', ') : ' · unmodified game source');
    $('#studio-save-info').textContent = draft && draft.revision !== scene.revision ? 'An older recovery draft exists, but source XML changed. It has not been applied or deleted.' : 'Drafts are private. Save to mod XML writes a real patch into the selected mod, with a byte-for-byte backup and conflict protection.';
    $('#studio-values').value = JSON.stringify(s.bindings, null, 2);
    layerList(); await render(); fit(); inspect(); status(s.dirty ? 'Recovered draft · unsaved to mod' : 'Source loaded');
  }
  function layerList() {
    $('#studio-layers').replaceChildren(); if (!s.tree) return;
    const filter = $('#studio-layer-search').value.toLowerCase(), entries = flat(s.tree);
    $('#studio-layer-count').textContent = entries.length;
    for (const {node, depth} of entries) {
      const name = node.attrs.name || node.tag; if (!`${name} ${node.tag}`.toLowerCase().includes(filter)) continue;
      const button = el('button', 'studio-layer' + (node.id === s.selected ? ' selected' : ''));
      button.style.paddingLeft = `${8 + Math.min(depth, 6) * 8}px`; button.append(el('span', 'font-mono text-[9px] opacity-50', {label: 'T', sprite: '◇', rect: '□', texture: '▧', grid: '▦'}[node.tag] || '·'), el('span', '', name));
      button.title = `${node.tag} · ${node.id}`; button.onclick = () => select(node.id); $('#studio-layers').append(button);
    }
  }
  function select(id) { s.selected = id; layerList(); inspect(); draw(); }
  function remember() { s.undo.push(clone(s.tree)); if (s.undo.length > 60) s.undo.shift(); s.redo = []; }
  function changed(rebuildInspector = false) {
    s.dirty = true; status('Draft · unsaved to mod', 'running'); layerList();
    if (rebuildInspector) inspect();
    clearTimeout(s.renderTimer); s.renderTimer = setTimeout(() => render().catch(fail), 90);
    clearTimeout(s.draftTimer); s.draftTimer = setTimeout(() => saveDraft().catch(fail), 700);
  }
  async function saveDraft() {
    if (!s.scene || !s.tree) return;
    await api('/api/studio/draft', payload());
    s.draftTimer = null;
    if (s.dirty) status('Draft saved · mod XML unchanged', 'running');
  }
  async function render() {
    if (!s.scene) return; const generation = ++s.generation;
    const graph = await api('/api/studio/render', payload());
    await Promise.all([...new Set(graph.controls.map(c => c.asset).filter(Boolean))].map(image));
    await Promise.all([...new Set(graph.controls.map(c => c.font).filter(Boolean))].map(font));
    if (generation !== s.generation) return;
    s.graph = graph;
    $('#studio-xml').textContent = graph.patch;
    $('#studio-warnings').replaceChildren(); $('#studio-warnings').hidden = !graph.warnings.length;
    if (graph.warnings.length) {
      $('#studio-warnings').append(el('strong', 'text-danger', `${graph.warnings.length} runtime/layout limitations — not silently approximated`));
      for (const warning of graph.warnings.slice(0, 12)) $('#studio-warnings').append(el('div', '', warning));
    }
    const native = s.assetMap.get(graph.font)?.nativeName || 'font not extracted';
    $('#studio-render-info').textContent = `${Math.round(graph.bounds[2])} × ${Math.round(graph.bounds[3])} native units · ${native}`;
    draw();
  }
  const canvas = $('#studio-canvas');
  function dimensions() {
    const w = canvas.clientWidth, h = canvas.clientHeight; if (!w || !h) return null;
    const dpr = window.devicePixelRatio || 1;
    if (canvas.width !== Math.round(w * dpr) || canvas.height !== Math.round(h * dpr)) { canvas.width = Math.round(w * dpr); canvas.height = Math.round(h * dpr); }
    const ctx = canvas.getContext('2d'); ctx.setTransform(dpr, 0, 0, dpr, 0, 0); return {ctx, w, h};
  }
  function fit() {
    if (!s.graph) return; const [x, y, w, h] = s.graph.bounds;
    if (!canvas.clientWidth || !canvas.clientHeight) { s.needFit = true; return; }
    s.needFit = false;
    s.view = {x: x + w / 2, y: y + h / 2, scale: Math.min(4, Math.max(.05, Math.min((canvas.clientWidth - 80) / Math.max(1, w), (canvas.clientHeight - 80) / Math.max(1, h))))}; draw();
  }
  function point(x, y) { return [canvas.clientWidth / 2 + (x - s.view.x) * s.view.scale, canvas.clientHeight / 2 + (y - s.view.y) * s.view.scale]; }
  function nativePoint(x, y) { return [s.view.x + (x - canvas.clientWidth / 2) / s.view.scale, s.view.y + (y - canvas.clientHeight / 2) / s.view.scale]; }
  function nineSlice(ctx, img, box, border, fillCenter) {
    const [x, y, w, h] = box, [l, t, r, b] = border;
    const dx = [x, x + Math.min(l, w / 2), x + w - Math.min(r, w / 2), x + w], dy = [y, y + Math.min(t, h / 2), y + h - Math.min(b, h / 2), y + h];
    const sx = [0, l, img.width - r, img.width], sy = [0, t, img.height - b, img.height];
    for (let row = 0; row < 3; row++) for (let col = 0; col < 3; col++) {
      if (!fillCenter && row === 1 && col === 1) continue;
      if (sx[col + 1] > sx[col] && sy[row + 1] > sy[row] && dx[col + 1] > dx[col] && dy[row + 1] > dy[row]) ctx.drawImage(img, sx[col], sy[row], sx[col + 1] - sx[col], sy[row + 1] - sy[row], dx[col], dy[row], dx[col + 1] - dx[col], dy[row + 1] - dy[row]);
    }
  }
  function tinted(id, img, c) {
    if (!c || (c[0] === 255 && c[1] === 255 && c[2] === 255)) return img;
    const key = `${id}:${c.slice(0, 3)}`;
    if (!s.tint.has(key)) {
      const temp = document.createElement('canvas'); temp.width = img.width; temp.height = img.height;
      const ctx = temp.getContext('2d'); ctx.drawImage(img, 0, 0); ctx.globalCompositeOperation = 'multiply'; ctx.fillStyle = `rgb(${c[0]},${c[1]},${c[2]})`; ctx.fillRect(0, 0, img.width, img.height); ctx.globalCompositeOperation = 'destination-in'; ctx.drawImage(img, 0, 0); s.tint.set(key, temp);
    }
    return s.tint.get(key);
  }
  let resolvedImages = new Map();
  async function draw() {
    const d = dimensions(); if (!d) return;
    const {ctx, w, h} = d; ctx.fillStyle = '#282b32'; ctx.fillRect(0, 0, w, h);
    ctx.fillStyle = '#343740'; for (let x = 0; x < w; x += 20) for (let y = 0; y < h; y += 20) if ((x / 20 + y / 20) % 2 === 0) ctx.fillRect(x, y, 20, 20);
    if (!s.graph) { ctx.fillStyle = '#a1a6b5'; ctx.font = '13px Segoe UI'; ctx.fillText('Loading actual game assets…', 25, 35); return; }
    const graph = s.graph;
    const ids = [...new Set(graph.controls.map(c => c.asset).filter(Boolean))];
    if (ids.some(id => !resolvedImages.has(id))) {
      await Promise.all(ids.map(async id => { if (!resolvedImages.has(id)) resolvedImages.set(id, await image(id)); }));
      if (s.graph !== graph) return;
    }
    ctx.save(); ctx.translate(w / 2, h / 2); ctx.scale(s.view.scale, s.view.scale); ctx.translate(-s.view.x, -s.view.y);
    for (const c of graph.controls) {
      const [x, y, cw, ch] = c.box; if (cw <= 0 || ch <= 0) continue;
      const attrs = c.attrs, img = resolvedImages.get(c.asset), color = c.color || [255, 255, 255, 255];
      ctx.save(); ctx.globalAlpha = color[3] / 255;
      if (img && ['sprite', 'filledsprite', 'button', 'texture'].includes(c.tag)) {
        const picture = tinted(c.asset, img, color);
        if (attrs.type === 'filled' || c.tag === 'filledsprite') { ctx.beginPath(); ctx.rect(x, y, cw * c.fill, ch); ctx.clip(); }
        if (attrs.type === 'sliced' && c.border?.some(Boolean)) nineSlice(ctx, picture, c.box, c.border, attrs.fillcenter !== 'false');
        else ctx.drawImage(picture, x, y, cw, ch);
      } else if (c.tag === 'label' && c.font) {
        ctx.beginPath(); ctx.rect(x, y, cw, ch); ctx.clip();
        const fs = number(attrs.font_size, 28); ctx.font = `${fs}px "game-${c.font}"`; ctx.textBaseline = 'middle'; ctx.textAlign = attrs.justify === 'right' ? 'right' : attrs.justify === 'center' ? 'center' : 'left';
        const tx = attrs.justify === 'right' ? x + cw : attrs.justify === 'center' ? x + cw / 2 : x;
        if (['outline', 'shadow'].includes(attrs.effect)) { ctx.fillStyle = '#000'; ctx.fillText(c.text, tx + 1, y + ch / 2 + 1); }
        ctx.fillStyle = colorCss(color); ctx.globalAlpha = 1; ctx.fillText(c.text, tx, y + ch / 2);
      }
      ctx.restore();
    }
    ctx.restore();
    if ($('#studio-outlines').checked) {
      const selected = graph.controls.find(c => c.id === s.selected && !c.virtual) || graph.controls.find(c => c.id === s.selected);
      if (selected) { const [x, y] = point(selected.box[0], selected.box[1]), cw = selected.box[2] * s.view.scale, ch = selected.box[3] * s.view.scale; ctx.strokeStyle = '#a298ff'; ctx.lineWidth = 1.5; ctx.strokeRect(x, y, cw, ch); ctx.fillStyle = '#a298ff'; ctx.fillRect(x + cw - 4, y + ch - 4, 8, 8); }
    }
    $('#studio-zoom-label').textContent = Math.round(s.view.scale * 100) + '%';
  }

  function inspect() {
    $('#studio-inspector').replaceChildren(); if (!s.tree) return;
    const n = find(s.tree, s.selected); if (!n) return;
    $('#studio-selected-type').textContent = n.tag;
    const current = s.graph?.controls.find(c => c.id === n.id && !c.virtual) || s.graph?.controls.find(c => c.id === n.id);
    function field(label, key, fallback = '', type = 'text', options = null) {
      const wrap = el('label', '', label), input = options ? el('select') : el('input'); input.dataset.property = key;
      if (options) input.append(...options.map(value => new Option(value, value))); else input.type = type;
      input.value = n.attrs[key] ?? fallback;
      input.onchange = () => { remember(); n.attrs[key] = input.value; changed(key === 'name'); };
      wrap.append(input); return wrap;
    }
    const inspector = $('#studio-inspector'); inspector.append(field('Name', 'name', n.tag));
    const template = s.catalog.find(scene => scene.kind === 'template' && scene.name === n.tag && scene.scope === s.scene.scope)
      || s.catalog.find(scene => scene.kind === 'template' && scene.name === n.tag);
    if (template && n.id !== 'root' && !['rect', 'sprite', 'filledsprite', 'texture', 'label', 'grid', 'button', 'window'].includes(n.tag)) {
      const open = el('button', 'quiet', 'Edit shared template ↗'); open.onclick = safe(async () => { $('#studio-scene-search').value = ''; sceneOptions(); $('#studio-scene').value = template.key; await loadScene(); });
      inspector.append(el('p', 'text-[10px] leading-relaxed text-muted', 'This is a shared game template instance. Edit its attributes here, or open the template to edit its inner layers.'), open);
      for (const key of ['caption', 'caption_key', 'cell_size']) if (key in n.attrs || n.tag === 'mainmenubutton' && key !== 'cell_size') inspector.append(field(key.replaceAll('_', ' '), key, ''));
    }
    const gridManaged = parentOf(s.tree, n.id)?.tag === 'grid';
    if (gridManaged) inspector.append(el('p', 'text-[10px] leading-relaxed text-muted', 'The game grid positions this layer. Adjust the parent grid’s cells instead of dragging this instance.'));
    const position = el('div', 'grid grid-cols-2 gap-2');
    for (const [index, label] of [[0, 'X'], [1, 'Y (XUi)']]) {
      const wrap = el('label', '', label), input = el('input'); input.type = 'number'; input.step = '1'; input.value = (n.attrs.pos || '0,0').split(',')[index]?.trim() || '0'; input.dataset.property = label.startsWith('X') ? 'x' : 'y';
      input.disabled = gridManaged;
      input.onchange = () => { remember(); const p = (n.attrs.pos || '0,0').split(','); p[index] = input.value; n.attrs.pos = p.join(','); changed(); }; wrap.append(input); position.append(wrap);
    }
    inspector.append(position);
    if (n.tag === 'grid') for (const key of ['rows', 'cols', 'cell_width', 'cell_height']) inspector.append(field(key.replaceAll('_', ' '), key, current?.attrs[key] || '1', 'number'));
    const size = el('div', 'grid grid-cols-2 gap-2'); size.append(field('Width', 'width', current?.box[2] ?? 100, 'number'), field('Height', 'height', current?.box[3] ?? 24, 'number')); inspector.append(size);
    const color = el('div', 'grid grid-cols-[42px_minmax(0,1fr)] gap-2 items-end'), picker = el('input'); picker.type = 'color'; picker.className = 'h-10 w-10 rounded border border-border';
    const rgb = n.attrs.color?.split(',').map(Number) || [255, 255, 255, 255]; picker.value = '#' + rgb.slice(0, 3).map(v => Number.isFinite(v) ? Math.max(0, Math.min(255, v)).toString(16).padStart(2, '0') : 'ff').join('');
    picker.onchange = () => { remember(); const hex = picker.value.slice(1); n.attrs.color = [0, 2, 4].map(i => parseInt(hex.slice(i, i + 2), 16)).join(',') + ',' + (rgb[3] ?? 255); changed(true); };
    color.append(picker, field('Color / binding (RGBA)', 'color', '255,255,255,255')); inspector.append(color);
    if (n.tag === 'label') {
      inspector.append(field('Text / binding', 'text', ''), field('Font size', 'font_size', current?.attrs.font_size || 28, 'number'), field('Alignment', 'justify', 'left', 'text', ['left', 'center', 'right']));
    }
    if (['sprite', 'filledsprite', 'button', 'texture'].includes(n.tag)) {
      const asset = s.assetMap.get(current?.asset); const button = el('button', 'quiet w-full', asset ? 'Asset: ' + asset.name : 'Choose real asset'); button.onclick = () => openPicker(n.tag === 'texture' ? 'texture' : 'sprite'); inspector.append(button);
      if (n.tag === 'texture') inspector.append(el('p', 'text-[10px] leading-relaxed text-muted', n.attrs.name === 'jonPortrait' ? 'This texture is generated by the game. The editor uses a recorded portrait sample; runtime binding is preserved in the saved XML.' : 'Choose a native texture path, or a captured image for preview only.'));
      if (n.tag !== 'texture') inspector.append(field('Sprite / binding', 'sprite', current?.attrs.sprite || 'menu_empty'), field('Atlas', 'atlas', current?.attrs.atlas || 'UIAtlas'), field('Render type', 'type', 'simple', 'text', ['simple', 'sliced', 'filled']), field('Fill / binding', 'fill', '1'));
      else inspector.append(field('Texture path / binding', 'texture', ''));
    }
    inspector.append(field('Depth', 'depth', '0', 'number'), field('Pivot', 'pivot', current?.attrs.pivot || 'topleft', 'text', ['topleft', 'top', 'topright', 'left', 'center', 'right', 'bottomleft', 'bottom', 'bottomright']), field('Visible / binding', 'visible', 'true'));
    const details = el('details'), summary = el('summary', '', 'All native attributes'), area = el('textarea', 'studio-code mt-3'); area.rows = 8; area.value = JSON.stringify(n.attrs, null, 2);
    const apply = el('button', 'quiet mt-2', 'Apply attributes'); apply.onclick = safe(() => { const attrs = JSON.parse(area.value); if (!attrs || Array.isArray(attrs) || typeof attrs !== 'object') throw new Error('Expected an attribute object'); remember(); n.attrs = Object.fromEntries(Object.entries(attrs).map(([key, value]) => [key, String(value)])); changed(true); }); details.append(summary, area, apply); inspector.append(details);
  }

  function eventPoint(event) { const rect = canvas.getBoundingClientRect(); return [event.clientX - rect.left, event.clientY - rect.top]; }
  canvas.onpointerdown = event => {
    if (!s.graph) return; canvas.focus(); const [sx, sy] = eventPoint(event), [nx, ny] = nativePoint(sx, sy);
    const selected = s.graph.controls.find(c => c.id === s.selected && !c.virtual);
    const box = selected?.box; const handle = box ? point(box[0] + box[2], box[1] + box[3]) : null;
    const resize = handle && Math.hypot(sx - handle[0], sy - handle[1]) < 10;
    const hit = resize ? selected : [...s.graph.controls].reverse().find(c => c.id !== 'root' && ['label', 'sprite', 'filledsprite', 'button', 'texture', 'rect'].includes(c.tag) && nx >= c.box[0] && ny >= c.box[1] && nx <= c.box[0] + c.box[2] && ny <= c.box[1] + c.box[3]);
    if (!hit) return; select(hit.id); const n = find(s.tree, hit.id); if (!n) return;
    if (parentOf(s.tree, n.id)?.tag === 'grid') { status('Grid-managed position · edit the parent grid'); return; }
    canvas.setPointerCapture(event.pointerId);
    const [x, y] = (n.attrs.pos || '0,0').split(',').map(v => number(v));
    s.drag = {id: n.id, sx, sy, x, y, box: [...hit.box], resize, moved: false};
  };
  canvas.onpointermove = event => {
    if (!s.drag) return; const [x, y] = eventPoint(event), dx = (x - s.drag.sx) / s.view.scale, dy = (y - s.drag.sy) / s.view.scale, n = find(s.tree, s.drag.id);
    if (!n) return;
    if (!s.drag.moved) { if (Math.hypot(x - s.drag.sx, y - s.drag.sy) < 2) return; remember(); s.drag.moved = true; }
    if (s.drag.resize) { n.attrs.width = String(Math.max(1, Math.round(s.drag.box[2] + dx))); n.attrs.height = String(Math.max(1, Math.round(s.drag.box[3] + dy))); }
    else n.attrs.pos = `${Math.round(s.drag.x + dx)},${Math.round(s.drag.y - dy)}`;
    changed();
  };
  canvas.onpointerup = () => { if (s.drag) { s.drag = null; inspect(); } };
  canvas.onpointercancel = () => { s.drag = null; };
  canvas.onkeydown = event => {
    if (!s.tree || !['ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown'].includes(event.key)) return;
    const n = find(s.tree, s.selected); if (!n || n.id === 'root' || parentOf(s.tree, n.id)?.tag === 'grid') return; event.preventDefault(); remember();
    const [x, y] = (n.attrs.pos || '0,0').split(',').map(v => number(v)), step = event.shiftKey ? 10 : 1;
    n.attrs.pos = `${x + (event.key === 'ArrowRight' ? step : event.key === 'ArrowLeft' ? -step : 0)},${y + (event.key === 'ArrowUp' ? step : event.key === 'ArrowDown' ? -step : 0)}`; changed(true);
  };
  canvas.addEventListener('wheel', event => { event.preventDefault(); s.view.scale = Math.min(12, Math.max(.02, s.view.scale * Math.exp(-event.deltaY * .001))); draw(); }, {passive: false});
  new ResizeObserver(() => { if (s.needFit && s.graph && canvas.clientWidth) fit(); else draw(); }).observe(canvas);
  window.addEventListener('beforeunload', event => { if (s.dirty && s.draftTimer) { event.preventDefault(); event.returnValue = ''; } });
  $('#studio-fit').onclick = fit; $('#studio-outlines').onchange = draw;
  $('#studio-zoom-in').onclick = () => { s.view.scale = Math.min(12, s.view.scale * 1.4); draw(); }; $('#studio-zoom-out').onclick = () => { s.view.scale = Math.max(.02, s.view.scale / 1.4); draw(); };
  $('#studio-scene-search').oninput = sceneOptions; $('#studio-scene').onchange = safe(loadScene);
  $('#studio-layer-search').oninput = layerList;
  $('#studio-owner').onchange = () => { s.dirty = true; saveDraft().catch(fail); };
  $('#studio-reload').onclick = safe(async () => {
    if (s.dirty && !confirm('Reload current source XML? Your private draft is kept as a recovery copy.')) return;
    const scene = await api('/api/studio/scene?key=' + encodeURIComponent(s.scene.key)); s.scene = scene; s.tree = clone(scene.tree); s.undo = []; s.redo = []; s.dirty = false; layerList(); await render(); fit(); inspect(); status('Source reloaded');
  });
  $('#studio-undo').onclick = () => { if (!s.undo.length) return; s.redo.push(clone(s.tree)); s.tree = s.undo.pop(); changed(true); };
  $('#studio-redo').onclick = () => { if (!s.redo.length) return; s.undo.push(clone(s.tree)); s.tree = s.redo.pop(); changed(true); };
  $('#studio-add').onclick = () => {
    if (!s.tree) return; remember(); const type = $('#studio-add-type').value, id = 'new-' + crypto.randomUUID(), name = type + '_' + id.slice(-5);
    let parent = find(s.tree, s.selected); if (!parent || !['rect', 'grid', 'window'].includes(parent.tag)) parent = parentOf(s.tree, s.selected) || s.tree;
    const attrs = {name, pos: '10,-10', width: '100', height: type === 'label' ? '24' : '50', depth: '10'};
    if (type === 'label') Object.assign(attrs, {text: 'New label', font_size: '20', color: '255,255,255,255'});
    if (type === 'sprite') Object.assign(attrs, {sprite: 'ui_game_symbol_quest', atlas: 'UIAtlas', color: '255,255,255,255'});
    parent.children.push({id, tag: type, attrs, children: []}); s.selected = id; changed(true);
  };
  $('#studio-delete').onclick = () => { if (!s.tree || s.selected === 'root') return; const parent = parentOf(s.tree, s.selected); if (!parent) return; remember(); parent.children = parent.children.filter(n => n.id !== s.selected); s.selected = parent.id; changed(true); };
  $('#studio-duplicate').onclick = () => {
    if (!s.tree || s.selected === 'root') return; const n = find(s.tree, s.selected), parent = parentOf(s.tree, s.selected); if (!n || !parent) return;
    remember(); const copy = clone(n);
    for (const {node} of flat(copy)) { node.id = 'new-' + crypto.randomUUID(); if (node.attrs.name) node.attrs.name += '_copy_' + node.id.slice(-4); }
    const [x, y] = (copy.attrs.pos || '0,0').split(',').map(v => number(v)); copy.attrs.pos = `${x + 10},${y - 10}`; parent.children.push(copy); s.selected = copy.id; changed(true);
  };
  $('#studio-save').onclick = safe(async () => {
    clearTimeout(s.draftTimer); status('Saving real mod XML…', 'running');
    const saved = await api('/api/studio/save', payload()); const selectedName = find(s.tree, s.selected)?.attrs.name;
    s.scene = saved.scene; s.tree = clone(saved.scene.tree); s.dirty = false; s.undo = []; s.redo = [];
    s.selected = flat(s.tree).find(({node}) => node.attrs.name === selectedName)?.node.id || 'root/0';
    await saveDraft(); layerList(); await render(); inspect(); status('Saved to mod XML', 'passed');
    $('#studio-save-info').textContent = `Saved ${saved.path}. Original bytes backed up at weblab/${saved.backup}. Nothing was installed into your normal game.`;
  });
  function showExport(result, label) {
    const link = el('a', 'font-semibold underline', 'Open ' + label + ' ↗'); link.href = result.url; link.target = '_blank'; link.rel = 'noopener';
    $('#notice').replaceChildren(document.createTextNode(`Saved ${result.path} · `), link); $('#notice').hidden = false;
  }
  $('#studio-export').onclick = safe(async () => showExport(await api('/api/studio/export', payload()), 'game XML patch'));
  $('#studio-png').onclick = safe(async () => { showExport(await api('/api/studio/png', payload()), 'native-asset PNG'); await loadGallery(); });
  function updateBindings() {
    s.bindings = {...s.bindings, ...defaultBindings, ...states[$('#studio-state').value], name: $('#studio-player-name').value,
      distance: $('#studio-state').value === 'far' ? '412m' : $('#studio-distance').value};
    $('#studio-values').value = JSON.stringify(s.bindings, null, 2); render().catch(fail);
  }
  $('#studio-state').onchange = updateBindings; $('#studio-player-name').oninput = updateBindings; $('#studio-distance').oninput = updateBindings;
  $('#studio-apply-values').onclick = safe(async () => { const values = JSON.parse($('#studio-values').value); if (!values || Array.isArray(values) || typeof values !== 'object') throw new Error('Binding values must be a JSON object'); s.bindings = values; await render(); await saveDraft(); });

  // A searchable, paginated library of actual game files. No made-up icons.
  function assetCard(asset, choose) {
    const card = el('button', 'asset-card'), preview = el('div', 'asset-card-image'), label = el('span', 'asset-card-label', asset.name);
    if (asset.kind === 'font') { const sample = el('span', 'text-2xl text-white', '7 Days'); font(asset.id).then(() => { sample.style.fontFamily = 'game-' + asset.id; }).catch(fail); preview.append(sample); }
    else { const img = el('img'); img.src = asset.url; img.alt = asset.name; img.loading = 'lazy'; preview.append(img); }
    label.append(el('small', '', asset.atlas || asset.kind)); card.append(preview, label); card.onclick = () => choose(asset); return card;
  }
  function showAssets() {
    const query = $('#asset-search').value.toLowerCase(), kind = $('#asset-kind').value;
    const matches = s.assets.filter(a => (!kind || a.kind === kind) && `${a.name} ${a.atlas || ''}`.toLowerCase().includes(query));
    const pages = Math.max(1, Math.ceil(matches.length / 60)); s.assetPage = Math.min(s.assetPage, pages - 1);
    $('#asset-grid').replaceChildren(...matches.slice(s.assetPage * 60, (s.assetPage + 1) * 60).map(a => assetCard(a, details)));
    $('#asset-page').textContent = `${matches.length.toLocaleString()} matches · page ${s.assetPage + 1} / ${pages}`;
    $('#asset-prev').disabled = !s.assetPage; $('#asset-next').disabled = s.assetPage >= pages - 1;
  }
  function details(asset) {
    s.detail = asset; $('#asset-detail-title').textContent = asset.name; $('#asset-detail-preview').replaceChildren();
    if (asset.kind === 'font') { const sample = el('span', 'text-4xl text-white', 'NoisemakerJon · 12/100'); font(asset.id).then(() => { sample.style.fontFamily = 'game-' + asset.id; }); $('#asset-detail-preview').append(sample); }
    else { const img = el('img'); img.src = asset.url; img.alt = asset.name; $('#asset-detail-preview').append(img); }
    $('#asset-detail-meta').textContent = JSON.stringify({kind: asset.kind, atlas: asset.atlas, size: asset.size, border: asset.border, nativePath: asset.nativePath, source: asset.source, note: asset.note}, null, 2);
    $('#asset-detail-open').href = asset.url; $('#asset-detail').showModal();
  }
  function useAsset(asset) {
    if (!s.tree) throw new Error('Open a scene and select a sprite or texture layer first');
    const n = find(s.tree, s.selected); if (!n) throw new Error('Select a layer first');
    if (['sprite', 'filledsprite', 'button'].includes(n.tag) && ['sprite', 'item'].includes(asset.kind)) {
      remember(); n.attrs.sprite = asset.name; n.attrs.atlas = asset.atlas || 'UIAtlas'; changed(true);
    } else if (n.tag === 'texture' && ['texture', 'capture', 'prefab', 'item'].includes(asset.kind)) {
      if (asset.nativePath && n.attrs.name !== 'jonPortrait') { remember(); n.attrs.texture = asset.nativePath; changed(true); }
      else { s.bindings['texture:' + (n.attrs.name || n.id)] = asset.id; render().catch(fail); saveDraft().catch(fail); }
    } else throw new Error('This asset does not match the selected layer type');
    if ($('#asset-detail').open) $('#asset-detail').close(); if ($('#asset-picker').open) $('#asset-picker').close();
  }
  function openPicker(kind) { s.pickerKind = kind; s.pickerPage = 0; $('#asset-picker-search').value = ''; showPicker(); $('#asset-picker').showModal(); }
  function showPicker() {
    const query = $('#asset-picker-search').value.toLowerCase(), kinds = s.pickerKind === 'texture' ? ['texture', 'capture', 'prefab', 'item'] : ['sprite', 'item'];
    const matches = s.assets.filter(a => kinds.includes(a.kind) && `${a.name} ${a.atlas || ''}`.toLowerCase().includes(query));
    const pages = Math.max(1, Math.ceil(matches.length / 42)); s.pickerPage = Math.min(s.pickerPage, pages - 1);
    $('#asset-picker-grid').replaceChildren(...matches.slice(s.pickerPage * 42, (s.pickerPage + 1) * 42).map(a => assetCard(a, safe(useAsset))));
    $('#asset-picker-page').textContent = `${matches.length} matches · ${s.pickerPage + 1} / ${pages}`;
    $('#asset-picker-prev').disabled = !s.pickerPage; $('#asset-picker-next').disabled = s.pickerPage >= pages - 1;
  }
  $('#asset-search').oninput = () => { s.assetPage = 0; showAssets(); }; $('#asset-kind').onchange = () => { s.assetPage = 0; showAssets(); };
  $('#asset-prev').onclick = () => { s.assetPage--; showAssets(); }; $('#asset-next').onclick = () => { s.assetPage++; showAssets(); };
  $('#asset-refresh').onclick = safe(library); $('#asset-rebuild').onclick = safe(() => startJob('assets'));
  $('#asset-detail-close').onclick = () => $('#asset-detail').close(); $('#asset-use').onclick = safe(() => useAsset(s.detail));
  $('#asset-picker-close').onclick = () => $('#asset-picker').close(); $('#asset-picker-search').oninput = () => { s.pickerPage = 0; showPicker(); };
  $('#asset-picker-prev').onclick = () => { s.pickerPage--; showPicker(); }; $('#asset-picker-next').onclick = () => { s.pickerPage++; showPicker(); };

  let capture = null, cropDrag = null;
  const captureCanvas = $('#capture-canvas');
  function captureView() {
    const width = captureCanvas.clientWidth, height = captureCanvas.clientHeight;
    const scale = capture ? Math.min(width / capture.width, height / capture.height) : 1;
    return {width, height, scale, x: capture ? (width - capture.width * scale) / 2 : 0, y: capture ? (height - capture.height * scale) / 2 : 0};
  }
  function drawCapture() {
    const {width, height, scale, x, y} = captureView(); if (!width || !height) return;
    captureCanvas.width = width; captureCanvas.height = height; const ctx = captureCanvas.getContext('2d'); ctx.fillStyle = '#282b32'; ctx.fillRect(0, 0, width, height);
    if (!capture) return; ctx.drawImage(capture, x, y, capture.width * scale, capture.height * scale);
    ctx.strokeStyle = '#b7a9ff'; ctx.lineWidth = 2; ctx.strokeRect(x + number($('#capture-x').value) * scale, y + number($('#capture-y').value) * scale, number($('#capture-w').value) * scale, number($('#capture-h').value) * scale);
  }
  $('#asset-import').onclick = () => { $('#capture-import').showModal(); drawCapture(); };
  $('#capture-close').onclick = () => $('#capture-import').close();
  $('#capture-file').onchange = safe(async () => {
    const file = $('#capture-file').files[0]; if (!file) return;
    if (file.size > 11000000) throw new Error('Use a screenshot smaller than 11 MB');
    const url = URL.createObjectURL(file); capture = new Image();
    await new Promise((resolve, reject) => { capture.onload = resolve; capture.onerror = reject; capture.src = url; }); URL.revokeObjectURL(url);
    if (capture.width * capture.height > 16000000) { capture = null; throw new Error('Screenshot exceeds 16 million pixels'); }
    $('#capture-w').value = capture.width; $('#capture-h').value = capture.height; $('#capture-x').value = 0; $('#capture-y').value = 0; drawCapture();
  });
  function capturePoint(event) { const view = captureView(), rect = captureCanvas.getBoundingClientRect(); return [Math.max(0, Math.min(capture.width, Math.round((event.clientX - rect.left - view.x) / view.scale))), Math.max(0, Math.min(capture.height, Math.round((event.clientY - rect.top - view.y) / view.scale)))]; }
  captureCanvas.onpointerdown = event => { if (!capture) return; cropDrag = capturePoint(event); captureCanvas.setPointerCapture(event.pointerId); };
  captureCanvas.onpointermove = event => { if (!cropDrag) return; const [x, y] = capturePoint(event); $('#capture-x').value = Math.min(x, cropDrag[0]); $('#capture-y').value = Math.min(y, cropDrag[1]); $('#capture-w').value = Math.max(1, Math.abs(x - cropDrag[0])); $('#capture-h').value = Math.max(1, Math.abs(y - cropDrag[1])); drawCapture(); };
  captureCanvas.onpointerup = captureCanvas.onpointercancel = () => { cropDrag = null; };
  for (const id of ['x', 'y', 'w', 'h']) $('#capture-' + id).oninput = drawCapture;
  $('#capture-save').onclick = safe(async () => {
    const file = $('#capture-file').files[0]; if (!file || !capture) throw new Error('Choose a screenshot first');
    const encoded = await new Promise((resolve, reject) => { const reader = new FileReader(); reader.onload = () => resolve(reader.result); reader.onerror = reject; reader.readAsDataURL(file); });
    const result = await api('/api/assets/import', {name: $('#capture-name').value, image: encoded, crop: ['x', 'y', 'w', 'h'].map(id => Number($('#capture-' + id).value))});
    $('#capture-import').close(); await library(); $('#asset-kind').value = 'capture'; $('#asset-search').value = result.name; s.assetPage = 0; showAssets(); details(result);
  });

  const catalog = await api('/api/studio/catalog'); s.catalog = catalog.scenes;
  $('#studio-owner').replaceChildren(...catalog.owners.map(owner => new Option(owner.title, owner.id)));
  sceneOptions(); await library(); await loadScene();
}
