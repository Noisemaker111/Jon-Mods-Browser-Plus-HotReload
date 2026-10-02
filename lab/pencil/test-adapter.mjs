import assert from 'node:assert/strict';
import { pathToFileURL } from 'node:url';
import { exportTree, stamp } from './adapter.js';

// Run against the real pinned editor graph, not a replacement design engine.
const source = process.argv[2];
if (!source) throw new Error('Usage: bun lab/pencil/test-adapter.mjs <OpenPencil source directory>');
const { SceneGraph } = await import(pathToFileURL(source + '/packages/scene-graph/src/index.ts').href);
const graph = new SceneGraph(), page = graph.getPages()[0];
const original = {id: 'root', tag: 'party_entry', attrs: {controller: 'Party'}, children: [
  {id: 'root/0', tag: 'label', attrs: {name: 'name', text: '{name}', pos: '80,-3', width: '112', height: '22', controller: 'Custom', custom_attribute: 'retain', pivot: 'center'}, children: []},
  {id: 'root/1', tag: 'texture', attrs: {name: 'portrait', visible: '{showportrait}'}, children: []}
]};
const board = graph.createNode('FRAME', page.id, {width: 310, height: 76, fills: [], clipsContent: false});
stamp(board, original, {attrs: original.attrs}, {root: true});
const text = graph.createNode('TEXT', board.id, {name: 'name', x: 24, y: -8, width: 112, height: 22, text: 'Preview Jon', fontSize: 20});
stamp(text, original.children[0], {attrs: original.children[0].attrs, color: [255, 255, 255, 255]});
const hidden = graph.createNode('RECTANGLE', board.id, {name: 'portrait', visible: false});
stamp(hidden, original.children[1], {attrs: original.children[1].attrs});
assert.deepEqual(exportTree(graph, board.id), original, 'no-op preserves bindings, controllers, hidden controls and unknown attributes');
graph.updateNode(text.id, {x: text.x + 10, y: text.y + 4, width: 132});
let output = exportTree(graph, board.id);
assert.equal(output.children[0].attrs.pos, '100,-7', 'pivot-aware resize and inverted native Y');
assert.equal(output.children[0].attrs.text, '{name}', 'geometry must not bake preview text');
graph.updateNode(text.id, {text: 'Explicit edit', fontSize: 24});
output = exportTree(graph, board.id);
assert.equal(output.children[0].attrs.text, 'Explicit edit');
assert.equal(output.children[0].attrs.font_size, '24');
assert.equal(output.children[0].attrs.controller, 'Custom');
graph.updateNode(text.id, {rotation: 15});
assert.throws(() => exportTree(graph, board.id), /rotation.*not an XUi-compatible/);
graph.updateNode(text.id, {rotation: 0});
const duplicate = graph.cloneTree(text.id, board.id);
output = exportTree(graph, board.id);
assert.notEqual(output.children[0].id, output.children[2].id, 'duplicates get independent XML identities');
graph.deleteNode(duplicate.id);
const ellipse = graph.createNode('ELLIPSE', board.id);
assert.throws(() => exportTree(graph, board.id), /no safe XUi representation/);
graph.deleteNode(ellipse.id);
const outside = graph.createNode('RECTANGLE', page.id);
assert.throws(() => exportTree(graph, board.id), /outside the game scene/);
graph.deleteNode(outside.id);
graph.deleteNode(hidden.id);
assert.equal(exportTree(graph, board.id).children.length, 1, 'deletion removes a native control');
console.log('PASS real OpenPencil graph: no-op XML, hidden bindings, native geometry, text edits, duplicates/deletes, unsupported transforms/shapes and scene boundaries');
