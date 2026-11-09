// Execute production boundary-state/command functions without a browser. This checks dispatch and
// persistence inputs only; real DOM, selection and native Undo/Redo are exercised by ProductivitySmoke.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const source = fs.readFileSync(path.join(__dirname, '../src/Magpie.Core/Mail/EditorPage.cs'), 'utf8');
function between(start, end) {
  const from = source.indexOf(start), to = source.indexOf(end, from);
  assert.ok(from >= 0 && to > from); return source.slice(from, to);
}
let nativeCalls = 0, bodyCalls = 0, nativeSuccess = true;
const commands = [];
const range = {collapsed: false, toString: () => 'Selected writing', cloneRange() {return this;},
  setStart() {}, selectNodeContents() {}, collapse() {}};
const ed = {innerHTML: '', innerText: 'Selected writing\nRetained signature\nRetained quote',
  classList: {add() {}, remove() {}, toggle() {}}, querySelector: () => null, focus() {},
  replaceChildren(...nodes) {this.innerHTML = nodes.map(node => node.textContent).join('\n');},
  set textContent(value) {this.innerHTML = this.innerText = value;}};
const context = vm.createContext({ed, isPlainText: false, signatureBoundariesLost: false, quoteFoldingSupported: false,
  hideLinkBubble() {}, post() {}, restore: () => true, getNewText: () => ed.innerText,
  insertBody: () => {bodyCalls++; return true;}, inlineHtml: text => text,
  document: {createRange: () => range, createElement: () => ({}), execCommand(...args) {nativeCalls++; commands.push(args); return nativeSuccess;}},
  window: {getSelection: () => ({removeAllRanges() {}, addRange() {}})}});
vm.runInContext(between('function setHtml(', 'function getHtml(')
  + between('function getBodySectionsLost(', 'function replaceSignatureBlock(')
  + between('function setEditorPlainText(', 'function setQuoteFoldingSupported(')
  + between('function cmd(', "ed.addEventListener('input'"), context);
let checks = 0;
function rejected(stage) {
  const before = ed.innerHTML, bodyBefore = bodyCalls, nativeBefore = nativeCalls;
  for (const mode of ['replaceBody', 'appendBody', 'replyDraft']) {
    const payload = mode === 'replyDraft'
      ? JSON.stringify({text: 'Generated', expectedNewText: ed.innerText, mode: 'replaceBody'}) : 'Generated';
    assert.equal(context.cmd(mode, payload), false, stage + ': ' + mode);
    checks++;
  }
  assert.equal(ed.innerHTML, before);
  assert.equal(bodyCalls, bodyBefore); assert.equal(nativeCalls, nativeBefore);
}
context.setHtml('<p>Known writing</p>', false);
assert.equal(context.canReplaceBody(), true); checks++;
assert.equal(context.cmd('replaceBody', 'Generated'), true); checks++;
context.setEditorPlainText(true, false);
assert.equal(context.getBodySectionsLost(), true); checks++;
rejected('After plain conversion');
context.saved = range;
assert.equal(context.cmd('captureRewrite', 'Selected writing'), true);
assert.equal(context.cmd('replaceSelection', 'New <literal>\ttext\nline'), true);
assert.deepEqual(commands.at(-1), ['insertText', false, 'New <literal>\ttext\nline']);
assert.equal(nativeCalls, 1); checks++;
context.saved = range;
assert.equal(context.cmd('captureRewrite', 'Selected writing'), true);
nativeSuccess = false;
assert.equal(context.cmd('replaceSelection', 'Failed edit'), false, 'A native command refusal must not claim success'); checks++;
nativeSuccess = true;
context.setEditorPlainText(false, false);
rejected('After returning to rich');
context.setHtml('<p>Flattened quote and signature</p>', true);
rejected('Reopened draft');
context.setHtml('<p>Original rich revision</p>', false);
assert.equal(context.canReplaceBody(), true); checks++;
assert.equal(context.cmd('appendBody', 'Generated'), true); checks++;
console.log('EDITOR_BODY_SAFETY_PASSED checks=' + checks);
