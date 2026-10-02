import { watch, nextTick } from 'vue';
import { SceneGraph } from '@open-pencil/scene-graph';
import { fontManager } from '@open-pencil/core/text';
import { getActiveStore } from '@/app/tabs';
import { onlineFontsEnabled } from '@/app/editor/fonts';
import { stamp, metadata, exportTree } from './lab-adapter.js';

onlineFontsEnabled.value = false;
let document = null, loading = false, timer, stopSelection;
const origin = window.location.origin;
const reply = data => window.parent.postMessage({channel: '7days-pencil', ...data}, origin);
const solid = c => ({type: 'SOLID', color: {r: c[0] / 255, g: c[1] / 255, b: c[2] / 255, a: c[3] / 255}, opacity: 1, visible: true});

async function importScene(input) {
  loading = true;
  clearTimeout(timer);
  try {
    const graph = new SceneGraph();
    const page = graph.getPages()[0] || graph.addPage('Game UI');
    const fonts = input.assets.filter(a => a.kind === 'font');
    fontManager.setHostFontLoader(async family => {
      const asset = fonts.find(a => family === 'game-' + a.id);
      if (!asset) return null;
      const response = await fetch(asset.url);
      if (!response.ok) throw new Error('Cannot load native font ' + asset.name);
      return response.arrayBuffer();
    });
    await Promise.all(fonts.map(a => fontManager.loadFont('game-' + a.id, 'Regular')));
    const controls = input.layout.controls;
    const images = new Map();
    await Promise.all(controls.filter(c => c.imageUrl).map(async c => {
      const hash = c.imageUrl;
      if (images.has(hash)) return;
      images.set(hash, null);
      const response = await fetch(hash);
      if (!response.ok) throw new Error('Cannot load game sprite ' + c.name);
      graph.images.set(hash, new Uint8Array(await response.arrayBuffer()));
    }));
    const rendered = new Map(), repeated = [];
    for (const c of controls.filter(c => !c.virtual)) {
      if (rendered.has(c.id)) repeated.push(c); else rendered.set(c.id, c);
    }
    function create(model, parent, parentBox, root = false) {
      const c = rendered.get(model.id);
      const virtuals = controls.filter(v => v.virtual && v.id === model.id);
      const box = c?.box || (root ? [0, 0, Math.max(1, input.layout.bounds[0] + input.layout.bounds[2]), Math.max(1, input.layout.bounds[1] + input.layout.bounds[3])] : virtuals[0]?.box || [parentBox[0], parentBox[1], Number(model.attrs.width) || 0, Number(model.attrs.height) || 0]);
      const type = root || model.children.length || ['rect', 'grid', 'window', 'panel'].includes(model.tag) ? 'FRAME' : model.tag === 'label' ? 'TEXT' : 'RECTANGLE';
      const color = c?.color || [255, 255, 255, 255];
      const fills = c?.imageUrl ? [{...solid([255, 255, 255, 255]), type: 'IMAGE', imageHash: c.imageUrl, imageScaleMode: 'FILL'}] : model.tag === 'label' ? [solid(color)] : [];
      const node = graph.createNode(type, parent.id, {name: model.attrs.name || model.tag,
        x: root ? 0 : box[0] - parentBox[0], y: root ? 0 : box[1] - parentBox[1], width: box[2], height: box[3],
        fills, clipsContent: false, visible: root || !!c || controls.some(v => v.id === model.id),
        text: c?.text || '', fontFamily: 'game-' + (c?.font || input.layout.font), fontSize: Number(c?.attrs.font_size) || 28,
        textAlignHorizontal: (c?.attrs.justify || 'left').toUpperCase(), textAlignVertical: 'CENTER', textAutoResize: 'NONE'});
      stamp(node, model, c || {attrs: model.attrs, box, color}, {root, gridManaged: metadata(parent)?.model.tag === 'grid'});
      for (const child of model.children) create(child, node, box);
      for (const virtual of virtuals) {
        const v = graph.createNode(virtual.tag === 'label' ? 'TEXT' : 'RECTANGLE', node.id, {
          name: 'Shared · ' + virtual.name, x: virtual.box[0] - box[0], y: virtual.box[1] - box[1], width: virtual.box[2], height: virtual.box[3], locked: true,
          fills: virtual.imageUrl ? [{...solid([255, 255, 255, 255]), type: 'IMAGE', imageHash: virtual.imageUrl, imageScaleMode: 'FILL'}] : virtual.tag === 'label' ? [solid(virtual.color)] : [],
          text: virtual.text || '', fontFamily: 'game-' + (virtual.font || input.layout.font), fontSize: Number(virtual.attrs.font_size) || 28,
          textAlignHorizontal: (virtual.attrs.justify || 'left').toUpperCase(), textAlignVertical: 'CENTER', textAutoResize: 'NONE'});
        stamp(v, model, virtual, {virtual: true});
      }
      // Native depth determines draw order, not XML element order. Keep source
      // order in metadata so a no-op save remains byte-for-byte semantic input.
      node.childIds.sort((a, b) => (metadata(graph.getNode(a))?.resolved.depth || 0) - (metadata(graph.getNode(b))?.resolved.depth || 0));
      stamp(node, model, c || {attrs: model.attrs, box, color}, {root,
        gridManaged: metadata(parent)?.model.tag === 'grid', importOrder: [...node.childIds]});
      return node;
    }
    const board = create(input.tree, page, [0, 0, 0, 0], true);
    // XUi repeats source controls without creating additional XML nodes. Keep
    // their actual rendered content as locked previews, not duplicate source.
    for (const c of repeated.filter(c => c.imageUrl || c.tag === 'label')) {
      const preview = graph.createNode(c.tag === 'label' ? 'TEXT' : 'RECTANGLE', board.id, {
        name: 'Repeated · ' + c.name, x: c.box[0], y: c.box[1], width: c.box[2], height: c.box[3], locked: true,
        fills: c.imageUrl ? [{...solid([255, 255, 255, 255]), type: 'IMAGE', imageHash: c.imageUrl, imageScaleMode: 'FILL'}] : [solid(c.color)],
        text: c.text || '', fontFamily: 'game-' + (c.font || input.layout.font), fontSize: Number(c.attrs.font_size) || 28,
        textAlignHorizontal: (c.attrs.justify || 'left').toUpperCase(), textAlignVertical: 'CENTER', textAutoResize: 'NONE'});
      stamp(preview, {id: c.id, tag: c.tag, attrs: c.attrs, children: []}, c, {virtual: true});
    }
    const boardData = metadata(board);
    board.pluginData[0].value = JSON.stringify({...boardData, nativeFont: 'game-' + input.layout.font, importOrder: [...board.childIds]});
    const store = document?.store || getActiveStore();
    stopSelection?.();
    store.replaceGraph(graph); store.undo.clear(); store.clearSelection();
    store.state.documentName = input.title;
    document = {store, boardId: board.id, key: input.key};
    for (const event of ['node:created', 'node:updated', 'node:deleted', 'node:reparented', 'node:reordered']) graph.emitter.on(event, schedule);
    graph.emitter.on('node:created', node => {
      if (node.type === 'TEXT' && !metadata(node)) graph.updateNode(node.id, {fontFamily: 'game-' + input.layout.font});
    });
    stopSelection = watch(() => [...store.state.selectedIds], ids => {
      const node = graph.getNode(ids[0]), data = node && metadata(node);
      reply({event: 'selection', id: data?.model.id || null});
    });
    await nextTick();
    await store.canvasReady;
    const firstText = [...graph.getAllNodes()].find(n => metadata(n)?.model.attrs.name === 'TextContent') || [...graph.getAllNodes()].find(n => n.type === 'TEXT' && !n.locked);
    store.select([firstText?.id || board.id]);
    fitScene();
    reply({event: 'imported', key: input.key, layers: graph.getNode(page.id).childIds.length});
  } finally { loading = false; }
}

function schedule() {
  if (loading) return;
  clearTimeout(timer); timer = setTimeout(() => {
    try { reply({event: 'changed', key: document.key, tree: exportTree(document.store.graph, document.boardId)}); }
    catch (error) { reply({event: 'incompatible', error: error.message}); }
  }, 250);
}

function fitScene() {
  const store = document.store, board = store.graph.getNode(document.boardId);
  store.zoomToFit();
  const canvas = [...window.document.querySelectorAll('canvas')].find(c => c.clientWidth > 100);
  if (canvas && board) store.setZoomAroundPoint(Math.min(4, canvas.clientWidth / (board.width + 80), canvas.clientHeight / (board.height + 80)), canvas.clientWidth / 2, canvas.clientHeight / 2);
}

window.addEventListener('message', async event => {
  if (event.origin !== origin || event.source !== window.parent || event.data?.channel !== '7days-pencil') return;
  const {request, action, input} = event.data;
  try {
    if (action === 'import') await importScene(input);
    else {
      if (!document || getActiveStore() !== document.store) throw new Error('Select the imported game document tab before saving mod XML.');
      if (action === 'export') return reply({request, result: exportTree(document.store.graph, document.boardId)});
      if (action === 'fit') fitScene();
    }
    reply({request, result: true});
  } catch (error) { reply({request, error: error.message}); }
});
reply({event: 'ready'});
