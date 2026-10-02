const $ = selector => document.querySelector(selector);
const clone = value => structuredClone(value);
function el(tag, className = '', text = '') { const n = document.createElement(tag); n.className = className; n.textContent = text; return n; }
function flat(node, result = []) { result.push(node); for (const child of node.children) flat(child, result); return result; }
function find(tree, id) { return tree && flat(tree).find(node => node.id === id); }
const number = value => Number(value) || 0;
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
  ishovered: false, islocked: false, iconcolor: '255,255,255,255', currencyamount: '200',
  creativewindowopen: false, userlockmode: false, userlockedslot: false, lootingorvehiclestorage: false
};
const states = {
  healthy: {}, low: {healthfill: '.15', healthcurrentwithmax: '12/100', staminafill: '.1', jonxpfill: '.2'},
  dead: {healthfill: '0', healthcurrentwithmax: '0/100', staminafill: '0', showarrow: 'false', voicevisible: 'false'},
  muted: {voicevisible: 'false'}, far: {distance: '412m', arrowcolor: '255,180,60,255'}
};

export async function initStudio({api, startJob, fail, loadGallery}) {
  const s = {catalog: [], assets: [], assetMap: new Map(), fonts: new Map(), scene: null, tree: null, graph: null,
    selected: null, dirty: false, bindings: {...defaultBindings}, draftTimer: null, assetPage: 0, pickerPage: 0, pickerKind: 'sprite', detail: null};
  // Imports, binding changes and source writes must never interleave: otherwise
  // one scene's late render could be paired with another scene's XML tree.
  let operation = Promise.resolve();
  const safe = fn => (...args) => {
    operation = operation.then(async () => {
      frame.inert = true;
      try { return await fn(...args); } finally { frame.inert = false; }
    }).catch(fail);
    return operation;
  };
  const status = (text, kind = '') => { $('#studio-status').textContent = text; $('#studio-status').className = 'badge ' + kind; };
  const payload = () => ({key: s.scene.key, revision: s.scene.revision, owner: $('#studio-owner').value, tree: s.tree, values: s.bindings});
  const frame = $('#studio-pencil'), pending = new Map();
  let resolveReady, imported = false, incompatible = false;
  const ready = new Promise(resolve => { resolveReady = resolve; });
  window.addEventListener('message', event => {
    if (event.origin !== location.origin || event.source !== frame.contentWindow || event.data?.channel !== '7days-pencil') return;
    const message = event.data;
    if (message.request && pending.has(message.request)) {
      const {resolve, reject, timer} = pending.get(message.request); pending.delete(message.request); clearTimeout(timer);
      message.error ? reject(new Error(message.error)) : resolve(message.result);
    }
    if (message.event === 'ready') resolveReady();
    if (message.event === 'selection') { s.selected = message.id; inspect(); }
    if (message.event === 'changed' && imported && message.key === s.scene?.key) {
      s.tree = message.tree; incompatible = false; s.dirty = true; inspect();
      status('Design edits · draft saving', 'running');
      clearTimeout(s.draftTimer); s.draftTimer = setTimeout(() => saveDraft().catch(fail), 700);
    }
    if (message.event === 'incompatible') { incompatible = true; status('Design-only feature · XML save blocked', 'failed'); $('#studio-save-info').textContent = message.error; }
  });
  async function pencil(action, input) {
    await ready;
    const request = crypto.randomUUID();
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => { pending.delete(request); reject(new Error('OpenPencil did not respond. Reload the editor; mod XML has not been written.')); }, 60000);
      pending.set(request, {resolve, reject, timer});
      frame.contentWindow.postMessage({channel: '7days-pencil', request, action, input}, location.origin);
    });
  }
  async function sync() {
    if (!imported) throw new Error('Wait for the game scene to load into OpenPencil.');
    s.tree = await pencil('export'); incompatible = false;
  }
  async function saveDraft() {
    if (!s.scene || incompatible) return;
    await api('/api/studio/draft', payload()); s.draftTimer = null;
    if (s.dirty) status('Draft saved · mod XML unchanged', 'running');
  }
  async function render() {
    imported = false;
    const graph = await api('/api/studio/render', {...payload(), pencil: true}); s.graph = graph;
    $('#studio-xml').textContent = graph.patch;
    const warnings = $('#studio-warnings'); warnings.replaceChildren(); warnings.hidden = !graph.warnings.length;
    if (graph.warnings.length) {
      warnings.append(el('strong', 'text-danger', `${graph.warnings.length} runtime/layout limitations — not silently approximated`));
      for (const warning of graph.warnings.slice(0, 12)) warnings.append(el('div', '', warning));
    }
    $('#studio-render-info').textContent = `${Math.round(graph.bounds[2])} × ${Math.round(graph.bounds[3])} native units · actual game fonts, tinted/nine-sliced sprites · Unity runtime widgets still require game validation`;
    await pencil('import', {key: s.scene.key, title: s.scene.name, tree: s.tree, layout: graph, assets: s.assets});
    imported = true; incompatible = false; inspect();
  }
  function sceneOptions() {
    const filter = $('#studio-scene-search').value.toLowerCase(), selected = s.scene?.key || 'XUi_InGame:templates.xml:party_entry';
    const choices = s.catalog.filter(scene => `${scene.name} ${scene.scope}`.toLowerCase().includes(filter));
    $('#studio-scene').replaceChildren(...choices.map(scene => new Option(`${scene.title} · ${scene.scope.replace('XUi_', '')} ${scene.kind}`, scene.key)));
    if (choices.some(c => c.key === selected)) $('#studio-scene').value = selected;
  }
  async function loadScene() {
    if (imported) { await sync(); if (s.dirty) await saveDraft(); }
    const key = $('#studio-scene').value; if (!key) return;
    const [scene, draft] = await Promise.all([api('/api/studio/scene?key=' + encodeURIComponent(key)), api('/api/studio/draft?key=' + encodeURIComponent(key))]);
    s.scene = scene; s.tree = clone(scene.tree); s.dirty = false; s.selected = null; $('#studio-owner').value = scene.owner;
    if (draft && draft.revision === scene.revision) { s.tree = draft.tree; s.bindings = {...s.bindings, ...draft.values}; s.dirty = JSON.stringify(draft.tree) !== JSON.stringify(scene.tree); if (draft.owner) $('#studio-owner').value = draft.owner; }
    $('#studio-source').textContent = scene.source + (scene.patches.length ? ' + ' + scene.patches.join(', ') : ' · unmodified game source');
    $('#studio-save-info').textContent = draft && draft.revision !== scene.revision ? 'Source XML changed: an older recovery draft has not been applied or deleted.' : 'Design files can use all OpenPencil tools. Only XUi-compatible edits can save to mod XML, with exact backups and revision checks.';
    $('#studio-values').value = JSON.stringify(s.bindings, null, 2);
    await render(); status(s.dirty ? 'Recovered draft · unsaved to mod' : 'OpenPencil · game source loaded');
  }
  async function nativeChange(change) { await sync(); change(); s.dirty = true; await saveDraft(); await render(); }
  function inspect() {
    const inspector = $('#studio-inspector'); inspector.replaceChildren();
    const n = find(s.tree, s.selected); if (!n) { inspector.append(el('p', 'text-xs text-muted', 'Select a native layer in OpenPencil to edit game-only attributes or choose an asset.')); return; }
    $('#studio-selected-type').textContent = n.tag;
    inspector.append(el('p', 'text-xs text-muted', `${n.attrs.name || n.tag} · bindings/controllers remain native XML. Geometry and text can be edited in OpenPencil’s properties panel.`));
    const template = s.catalog.find(scene => scene.name === n.tag && scene.scope === s.scene.scope && scene.kind === 'template');
    if (template && n.id !== 'root') {
      const open = el('button', 'quiet', 'Edit shared template ↗'); open.onclick = safe(async () => { $('#studio-scene-search').value = ''; sceneOptions(); $('#studio-scene').value = template.key; await loadScene(); }); inspector.append(open);
    }
    if (['sprite', 'filledsprite', 'button', 'texture'].includes(n.tag)) {
      const button = el('button', 'quiet', 'Choose real game asset'); button.onclick = () => openPicker(n.tag === 'texture' ? 'texture' : 'sprite'); inspector.append(button);
    }
    const area = el('textarea', 'studio-code mt-3'); area.rows = 8; area.value = JSON.stringify(n.attrs, null, 2); area.setAttribute('aria-label', 'Native game attributes');
    const apply = el('button', 'quiet', 'Apply native attributes'); apply.onclick = safe(async () => {
      const attrs = JSON.parse(area.value), id = n.id;
      if (!attrs || Array.isArray(attrs) || typeof attrs !== 'object') throw new Error('Expected an attribute object');
      await nativeChange(() => { find(s.tree, id).attrs = Object.fromEntries(Object.entries(attrs).map(([key, value]) => [key, String(value)])); });
    }); inspector.append(area, apply);
  }
  function showExport(result, label) {
    const link = el('a', 'font-semibold underline', 'Open ' + label + ' ↗'); link.href = result.url; link.target = '_blank'; link.rel = 'noopener';
    $('#notice').replaceChildren(document.createTextNode(`Saved ${result.path} · `), link); $('#notice').hidden = false;
  }
  $('#studio-fit').onclick = safe(() => pencil('fit'));
  $('#studio-native-preview').onclick = safe(async () => { await sync(); await saveDraft(); await render(); status('Native preview refreshed · design undo history restarted'); });
  $('#studio-scene-search').oninput = sceneOptions; $('#studio-scene').onchange = safe(loadScene);
  $('#studio-owner').onchange = safe(async () => { await sync(); s.dirty = true; await saveDraft(); });
  $('#studio-reload').onclick = safe(async () => {
    if (s.dirty && !confirm('Reload source XML? Your private draft remains as a recovery copy.')) return;
    clearTimeout(s.draftTimer);
    s.scene = await api('/api/studio/scene?key=' + encodeURIComponent(s.scene.key)); s.tree = clone(s.scene.tree); s.dirty = false;
    await render(); status('Source reloaded');
  });
  $('#studio-save').onclick = safe(async () => {
    await sync(); clearTimeout(s.draftTimer); status('Saving real mod XML…', 'running');
    const saved = await api('/api/studio/save', payload()); s.scene = saved.scene; s.tree = clone(saved.scene.tree); s.dirty = false;
    await saveDraft(); await render(); status('Saved to mod XML', 'passed');
    $('#studio-save-info').textContent = `Saved ${saved.path}. Exact original backed up at weblab/${saved.backup}. Nothing installed into your normal game.`;
  });
  $('#studio-export').onclick = safe(async () => { await sync(); showExport(await api('/api/studio/export', payload()), 'game XML patch'); });
  $('#studio-png').onclick = safe(async () => { await sync(); showExport(await api('/api/studio/png', payload()), 'native-asset PNG'); await loadGallery(); });
  async function updateBindings() {
    await sync(); s.bindings = {...s.bindings, ...defaultBindings, ...states[$('#studio-state').value], name: $('#studio-player-name').value,
      distance: $('#studio-state').value === 'far' ? '412m' : $('#studio-distance').value};
    $('#studio-values').value = JSON.stringify(s.bindings, null, 2); await render(); await saveDraft();
  }
  $('#studio-state').onchange = safe(updateBindings); $('#studio-player-name').onchange = safe(updateBindings); $('#studio-distance').onchange = safe(updateBindings);
  $('#studio-apply-values').onclick = safe(async () => {
    await sync(); const values = JSON.parse($('#studio-values').value);
    if (!values || Array.isArray(values) || typeof values !== 'object') throw new Error('Binding values must be a JSON object');
    s.bindings = values; await render(); await saveDraft();
  });

  async function font(id) {
    if (!s.fonts.has(id)) { const face = new FontFace('game-' + id, `url("${s.assetMap.get(id).url}")`); s.fonts.set(id, face.load().then(loaded => document.fonts.add(loaded))); }
    return s.fonts.get(id);
  }
  async function library() {
    const manifest = await api('/api/assets'); s.assets = manifest.assets; s.assetMap = new Map(s.assets.map(a => [a.id, a]));
    $('#asset-summary').textContent = `${s.assets.length.toLocaleString()} real installed-game assets · private local cache${manifest.warnings.length ? ` · ${manifest.warnings.length} extraction warnings` : ''}`; showAssets();
  }
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
  async function useAsset(asset) {
    const id = s.selected;
    await nativeChange(() => {
      const n = find(s.tree, id); if (!n) throw new Error('Select a native layer in OpenPencil first');
      if (['sprite', 'filledsprite', 'button'].includes(n.tag) && ['sprite', 'item'].includes(asset.kind)) {
        n.attrs.sprite = asset.name; n.attrs.atlas = asset.atlas || 'UIAtlas';
      } else if (n.tag === 'texture' && ['texture', 'capture', 'prefab', 'item'].includes(asset.kind)) {
        if (asset.nativePath && n.attrs.name !== 'jonPortrait') n.attrs.texture = asset.nativePath;
        else s.bindings['texture:' + (n.attrs.name || n.id)] = asset.id;
      } else throw new Error('This asset does not match the selected layer type');
    });
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
  sceneOptions(); await library();
  const build = await fetch('/pencil/build.json');
  if (!build.ok) { $('#pencil-setup').hidden = false; status('OpenPencil build required', 'failed'); return; }
  frame.src = '/pencil/';
  await safe(loadScene)();
}
