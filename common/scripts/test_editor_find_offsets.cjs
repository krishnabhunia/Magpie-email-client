// Dependency-free offset checks against the actual EditorPage functions.
// Native WebView2 runs the complete DOM/editing fixture in ProductivitySmoke as well.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const sourcePath = process.argv[2] || path.join(__dirname, '../src/Magpie.Core/Mail/EditorPage.cs');
const source = fs.readFileSync(sourcePath, 'utf8');
const start = source.indexOf('function editorSearchPattern(') >= 0
  ? source.indexOf('function editorSearchPattern(') : source.indexOf('function editorMatches(');
const end = source.indexOf("document.addEventListener('selectionchange'", start);
assert.ok(start >= 0 && end > start, 'Production editor search functions must be present');
let nodes = [], edits = [];
class TextRange {
  setStart(node, offset) {
    assert.ok(offset >= 0 && offset <= node.nodeValue.length, 'Start is an original UTF-16 offset');
    this.startContainer = this.commonAncestorContainer = node; this.startOffset = offset;
  }
  setEnd(node, offset) {
    assert.ok(offset >= 0 && offset <= node.nodeValue.length, 'End is an original UTF-16 offset');
    this.endContainer = node; this.endOffset = offset;
  }
  get collapsed() { return this.startOffset === this.endOffset; }
  toString() { return this.startContainer.nodeValue.slice(this.startOffset, this.endOffset); }
}
const context = vm.createContext({
  NodeFilter: {SHOW_TEXT: 4}, saved: null, restore() {}, post() {},
  ed: {classList: {contains: () => false}, contains: node => nodes.includes(node), focus() {}},
  document: {
    createTreeWalker() {
      let index = -1;
      return {nextNode() { return ++index < nodes.length; }, get currentNode() { return nodes[index]; }};
    },
    createRange: () => new TextRange(),
    execCommand(...args) { edits.push(args); return true; },
  },
});
vm.runInContext(source.slice(start, end), context, {filename: sourcePath});
let checks = 0;
function matches(texts, query, matchCase, expected) {
  nodes = texts.map(nodeValue => ({nodeValue, parentElement: {closest: () => null}}));
  const ranges = context.editorMatches(query, matchCase);
  const actual = ranges == null ? null : Array.from(ranges, r => [nodes.indexOf(r.startContainer), r.startOffset, r.endOffset, r.toString()]);
  assert.deepEqual(actual, expected, 'Original offsets for ' + JSON.stringify({texts, query, matchCase}));
  checks++;
  return ranges;
}
matches(['İabcX'], 'abc', false, [[0, 1, 4, 'abc']]);
matches(['İabc'], 'ABC', false, [[0, 1, 4, 'abc']]);
matches(['İ I i İ'], 'İ', false, [[0, 0, 1, 'İ'], [0, 6, 7, 'İ']]);
matches(['İ😀\u{10400} X \u{10428}'], '\u{10428}', false, [[0, 3, 5, '\u{10400}'], [0, 8, 10, '\u{10428}']]);
matches(['İ', 'AbC', ' and ', 'İabc', ' ABC'], 'abc', false,
  [[1, 0, 3, 'AbC'], [3, 1, 4, 'abc'], [4, 1, 4, 'ABC']]);
matches(['İAbC abc'], 'abc', true, [[0, 5, 8, 'abc']]);
matches(['aaaa'], 'aa', true, [[0, 0, 2, 'aa'], [0, 2, 4, 'aa']]);
const literal = '[a].*+(x)?^$|\\';
matches(['İ' + literal], literal, false, [[0, 1, 1 + literal.length, literal]]);
matches(['İa '.repeat(2001)], 'A', false, null);
matches(['abc'], '', false, []);
matches(['abc'], 'a'.repeat(201), false, []);
vm.runInContext("String.prototype.toLocaleLowerCase=function(){throw Error('Search must not use locale-sensitive casing')}", context);
let ranges = matches(['I i İ ı'], 'i', false, [[0, 0, 1, 'I'], [0, 2, 3, 'i']]);
context.saved = ranges[0];
assert.equal(context.replaceFoundText('i', '$&\\end', false), true);
assert.deepEqual(edits.pop(), ['insertText', false, '$&\\end']); checks++;
ranges = matches(['ςΣσ'], 'σ', false, [[0, 0, 1, 'ς'], [0, 1, 2, 'Σ'], [0, 2, 3, 'σ']]);
context.saved = ranges[0];
assert.equal(context.replaceFoundText('σ', 'Z', false), true); checks++;
matches(['İabcX'], 'abc', false, [[0, 1, 4, 'abc']]);
const wrong = new TextRange(); wrong.setStart(nodes[0], 2); wrong.setEnd(nodes[0], 5); context.saved = wrong;
const before = edits.length;
assert.equal(context.replaceFoundText('abc', 'wrong', false), false);
assert.equal(edits.length, before); checks++;
console.log('EDITOR_FIND_OFFSETS_PASSED checks=' + checks);
