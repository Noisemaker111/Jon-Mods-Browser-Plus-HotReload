// Only the XUi translation layer is ours. Selection, rendering, tools, property
// controls, snapping, history and document editing are the upstream editor.
export const namespace = '7days-mod-lab';
const copy = value => structuredClone(value);
const equal = (a, b) => JSON.stringify(a) === JSON.stringify(b);
const round = value => String(Math.round(value * 1000) / 1000);
const pivots = {topleft: [0, 0], top: [.5, 0], topright: [1, 0], left: [0, .5], center: [.5, .5], right: [1, .5], bottomleft: [0, 1], bottom: [.5, 1], bottomright: [1, 1]};
const protectedFields = ['type', 'rotation', 'strokes', 'effects', 'cornerRadius', 'independentCorners', 'topLeftRadius', 'topRightRadius', 'bottomLeftRadius', 'bottomRightRadius', 'layoutMode', 'blendMode', 'fontFamily', 'fontWeight', 'italic', 'textDecoration', 'textCase', 'letterSpacing', 'lineHeight', 'isMask', 'clipsContent', 'textAutoResize', 'textStyleRuns', 'boundVariables', 'componentProperties', 'relativeTransform', 'layoutSizingHorizontal', 'layoutSizingVertical', 'layoutGrow', 'layoutPositioning', 'opacity', 'textDirection', 'horizontalConstraint', 'verticalConstraint', 'minWidth', 'minHeight', 'maxWidth', 'maxHeight', 'cornerSmoothing', 'textTruncation', 'maxLines', 'openTypeFeatures', 'strokeWeight', 'strokeAlign', 'strokeDashes'];

export function metadata(node) {
  const item = node.pluginData?.find(p => p.pluginId === namespace && p.key === 'xui');
  return item ? JSON.parse(item.value) : null;
}

export function baseline(node) {
  return Object.fromEntries(['name', 'x', 'y', 'width', 'height', 'text', 'fontSize', 'textAlignHorizontal', 'textAlignVertical', 'visible', 'opacity', 'fills', ...protectedFields].map(k => [k, copy(node[k])]));
}

export function stamp(node, model, resolved, extra = {}) {
  node.pluginData = [{pluginId: namespace, key: 'xui', value: JSON.stringify({
    model: copy(model), resolved: copy(resolved), baseline: baseline(node), ...extra
  })}];
}

function changed(node, data, key) { return !equal(node[key], data.baseline[key]); }
function guard(node, data) {
  for (const key of protectedFields.filter(key => key !== 'opacity')) if (changed(node, data, key)) {
    throw new Error(`${node.name}: ${key} is not an XUi-compatible edit. Undo it before saving XML; design-file export still works.`);
  }
  if (data.virtual && !equal(baseline(node), data.baseline)) {
    throw new Error(`${node.name}: template/repeated preview is read-only. Edit its shared native source instead.`);
  }
}

function apply(node, data) {
  guard(node, data);
  const model = copy(data.model), attrs = model.attrs, b = data.baseline;
  if (changed(node, data, 'name')) attrs.name = node.name;
  const dw = node.width - b.width, dh = node.height - b.height;
  if (dw) attrs.width = round(node.width);
  if (dh) attrs.height = round(node.height);
  const [pivotX, pivotY] = pivots[(data.resolved.attrs.pivot || 'topleft').toLowerCase()] || [0, 0];
  const dx = data.root ? 0 : node.x - b.x + dw * pivotX, dy = data.root ? 0 : node.y - b.y + dh * pivotY;
  if (dx || dy) {
    if (data.gridManaged || Object.keys(data.resolved.attrs).some(k => k.startsWith('anchor_'))) {
      throw new Error(`${node.name}: position is managed by a native grid/anchor. Edit the native attributes instead.`);
    }
    const pos = (attrs.pos || '0,0').split(',').map(Number);
    if (!pos.every(Number.isFinite)) throw new Error(`${node.name}: bound position must be edited in native attributes.`);
    attrs.pos = `${round(pos[0] + dx)},${round(pos[1] - dy)}`;
  }
  if (changed(node, data, 'visible')) attrs.visible = node.visible ? 'true' : 'false';
  if (model.tag === 'label') {
    if (changed(node, data, 'text')) {
      attrs.text = node.text;
      // An explicit content edit replaces localization; untouched bindings and
      // localization keys are preserved, including all controllers/unknown attrs.
      delete attrs.text_key;
    }
    if (changed(node, data, 'fontSize')) attrs.font_size = round(node.fontSize);
    if (changed(node, data, 'textAlignHorizontal')) attrs.justify = node.textAlignHorizontal.toLowerCase();
    if (changed(node, data, 'textAlignVertical')) throw new Error(`${node.name}: vertical text alignment needs native attributes.`);
  }
  if (changed(node, data, 'fills')) {
    const fills = node.fills.filter(f => f.visible);
    if (model.tag !== 'label' || fills.length !== 1 || fills[0].type !== 'SOLID') {
      throw new Error(`${node.name}: use the game asset chooser/native color for sprite fills. Arbitrary design fills cannot be written to XUi.`);
    }
    const f = fills[0];
    attrs.color = [f.color.r, f.color.g, f.color.b, (f.color.a ?? 1) * f.opacity * node.opacity].map(v => Math.round(v * 255)).join(',');
  } else if (changed(node, data, 'opacity')) {
    const color = data.resolved.color || [255, 255, 255, 255];
    attrs.color = [...color.slice(0, 3), Math.round(color[3] * node.opacity / (b.opacity || 1))].join(',');
  }
  return model;
}

export function exportTree(graph, boardId) {
  const board = graph.getNode(boardId);
  if (!board || !metadata(board)?.root) throw new Error('The game scene root was deleted. Reload its source before saving.');
  const page = graph.getNode(board.parentId);
  if (page.childIds.some(id => id !== boardId)) throw new Error('Design layers outside the game scene cannot be saved to mod XML. Move them inside the game frame or export a design file.');
  const seen = new Set();
  function walk(node) {
    const data = metadata(node);
    if (data?.virtual) { guard(node, data); return null; }
    let model;
    if (data) {
      model = apply(node, data);
      if (seen.has(model.id)) {
        model.id = 'new-' + node.id.replaceAll(':', '-');
        model.attrs.name = node.name + '_copy_' + model.id.slice(-5);
      }
      seen.add(model.id);
    } else {
      if (!['TEXT', 'FRAME', 'GROUP', 'RECTANGLE'].includes(node.type) || node.rotation || node.strokes.length || node.effects.length || node.cornerRadius || node.cornerSmoothing || node.independentCorners || node.layoutMode !== 'NONE' || ['minWidth', 'minHeight', 'maxWidth', 'maxHeight'].some(key => node[key] != null)) {
        throw new Error(`${node.name}: this design feature has no safe XUi representation. Export the design file or use a text/frame/solid rectangle.`);
      }
      const attrs = {name: node.name, pos: `${round(node.x)},${round(-node.y)}`, width: round(node.width), height: round(node.height), visible: String(node.visible)};
      let tag = 'rect';
      if (node.type === 'TEXT' || node.type === 'RECTANGLE') {
        const fills = node.fills.filter(f => f.visible);
        if (fills.length !== 1 || fills[0].type !== 'SOLID') throw new Error(`${node.name}: new game layers require one solid fill; choose real sprites through the game bridge.`);
        const f = fills[0];
        attrs.color = [f.color.r, f.color.g, f.color.b, (f.color.a ?? 1) * f.opacity * node.opacity].map(v => Math.round(v * 255)).join(',');
        if (node.type === 'TEXT') {
          if (metadata(board)?.nativeFont && node.fontFamily !== metadata(board).nativeFont) throw new Error(`${node.name}: new text must use the scene's native game font before saving XML.`);
          tag = 'label'; Object.assign(attrs, {text: node.text, font_size: round(node.fontSize), justify: node.textAlignHorizontal.toLowerCase()});
        } else {
          tag = 'sprite'; Object.assign(attrs, {sprite: 'menu_empty', atlas: 'UIAtlas'});
        }
      } else if (node.fills.some(f => f.visible)) throw new Error(`${node.name}: native containers must have no fill; add a rectangle inside instead.`);
      model = {id: 'new-' + node.id.replaceAll(':', '-'), tag, attrs, children: []};
    }
    const children = node.childIds.map(id => walk(graph.getNode(id))).filter(Boolean);
    if (data?.importOrder) {
      const previous = data.importOrder.filter(id => node.childIds.includes(id));
      const current = node.childIds.filter(id => data.importOrder.includes(id));
      if (!equal(previous, current)) throw new Error(`${node.name}: XUi uses explicit depth. Change native depth attributes rather than reorder design layers before saving XML.`);
      const order = new Map(data.model.children.map((child, index) => [child.id, index]));
      children.sort((a, b) => (order.get(a.id) ?? Infinity) - (order.get(b.id) ?? Infinity));
    }
    // Virtual template previews are not XML children of their invocation.
    model.children = children;
    return model;
  }
  return walk(board);
}
