// Runs the editor page's production drag-and-drop handlers (EditorPage.cs) without a browser:
// files dropped on the message are handed to the Windows app with postMessageWithAdditionalObjects,
// other drops (text, links) are only cancelled, and the Mac page (no WebView2 bridge) never throws.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const source = fs.readFileSync(path.join(__dirname, '../src/Magpie.Core/Mail/EditorPage.cs'), 'utf8');
const from = source.indexOf('function hasFiles('), to = source.indexOf("post({t:'ready'});", from);
assert.ok(from >= 0 && to > from, 'drop handlers found in EditorPage.cs');
const script = source.slice(from, to);

function run(bridge) {
  const listeners = {};
  const sent = [];
  const window = bridge ? {chrome: {webview: {postMessageWithAdditionalObjects(message, objects) { sent.push([JSON.parse(message), objects]); }}}} : {};
  const context = vm.createContext({window, document: {addEventListener(type, fn) { listeners[type] = fn; }}, Array, JSON});
  vm.runInContext(script, context);
  return {listeners, sent};
}
function event(types, files) {
  return {prevented: false, preventDefault() { this.prevented = true; }, dataTransfer: {types, files, dropEffect: 'none'}};
}

let checks = 0;
const files = [{name: 'report.pdf'}, {name: 'photo.jpg'}];
files.length = 2;

const win = run(true);
let over = event(['Files'], files);
win.listeners.dragover(over);
assert.ok(over.prevented); assert.equal(over.dataTransfer.dropEffect, 'copy'); checks += 2;
let drop = event(['Files'], files);
win.listeners.drop(drop);
assert.ok(drop.prevented); checks++;
assert.equal(win.sent.length, 1); checks++;
assert.deepEqual(win.sent[0][0], {t: 'dropFiles'}); checks++;
assert.equal(win.sent[0][1], files, 'the dropped FileList itself is passed so WebView2 can give the app real paths'); checks++;

// Dragged text or a link: cancelled (the page never navigates or inserts), nothing posted.
over = event(['text/plain'], []);
over.dataTransfer.dropEffect = 'move';   // what the browser offers for text from another app
win.listeners.dragover(over);
assert.ok(over.prevented); assert.equal(over.dataTransfer.dropEffect, 'none'); checks += 2;
drop = event(['text/plain'], {length: 0});
win.listeners.drop(drop);
assert.ok(drop.prevented); assert.equal(win.sent.length, 1); checks += 2;

// Magpie for Mac shares the page: no WebView2 bridge, the drop is just cancelled.
const mac = run(false);
drop = event(['Files'], files);
assert.doesNotThrow(() => mac.listeners.drop(drop)); assert.ok(drop.prevented); checks += 2;

console.log('EDITOR_FILE_DROP_PASSED checks=' + checks);
