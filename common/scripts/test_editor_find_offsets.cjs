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
// Minimal DOM shape/ranges for offset and boundary tests, not a layout or native-edit emulator.
class TextNode {
  constructor(value) {this.nodeType = 3; this.nodeValue = value;}
}
class Element {
  constructor(tag, children = [], attributes = {}, style = {}) {
    this.nodeType = 1; this.localName = tag; this.childNodes = children; this.attributes = attributes; this.style = style;
    this.classList = {contains: name => (attributes.class || '').split(/\s+/).includes(name)};
    this.hidden = Object.hasOwn(attributes, 'hidden');
    for (const child of children) child.parentElement = this;
  }
  getAttribute(name) {return this.attributes[name] ?? null;}
  contains(node) {for (;node;node=node.parentElement) if (node === this) return true; return false;}
  focus() {}
  scrollIntoView() {}
}
const text = value => new TextNode(value);
const element = (tag, ...children) => new Element(tag, children.map(child => typeof child === 'string' ? text(child) : child));
const ed = element('div');
function load(children) {
  ed.childNodes = children;
  nodes = [];
  function collect(node) {
    if (node.nodeType === 3) nodes.push(node);
    else for (const child of node.childNodes) {child.parentElement = node; collect(child);}
  }
  collect(ed);
}
function position(target, offset) {
  let cursor = 0, result;
  function visit(node) {
    if (node.nodeType === 3) {
      if (node === target) result = cursor + offset;
      cursor += node.nodeValue.length + 1;
    } else {
      if (node === target && offset === 0) result = cursor;
      cursor++;
      node.childNodes.forEach((child, index) => {
        visit(child);
        if (node === target && offset === index + 1) result = cursor;
        cursor++;
      });
    }
  }
  visit(ed); assert.notEqual(result, undefined); return result;
}
class TextRange {
  setStart(node, offset) {
    assert.ok(offset >= 0 && offset <= (node.nodeType === 3 ? node.nodeValue.length : node.childNodes.length), 'Valid original start offset');
    this.startContainer = this.commonAncestorContainer = node; this.startOffset = offset;
  }
  setEnd(node, offset) {
    assert.ok(offset >= 0 && offset <= (node.nodeType === 3 ? node.nodeValue.length : node.childNodes.length), 'Valid original end offset');
    this.endContainer = node; this.endOffset = offset;
  }
  get collapsed() {return this.startContainer === this.endContainer && this.startOffset === this.endOffset;}
  cloneRange() {const clone = new TextRange(); Object.assign(clone, this); return clone;}
  selectNodeContents(node) {this.setStart(node, 0); this.setEnd(node, node.nodeType === 3 ? node.nodeValue.length : node.childNodes.length);}
  collapse(toStart) {
    if (toStart) this.setEnd(this.startContainer, this.startOffset);
    else this.setStart(this.endContainer, this.endOffset);
  }
  compareBoundaryPoints(how, other) {
    assert.ok(how === 0 || how === 2);
    const edge = how === 0 ? 'start' : 'end';
    return Math.sign(position(this[edge + 'Container'], this[edge + 'Offset']) - position(other[edge + 'Container'], other[edge + 'Offset']));
  }
  toString() {
    const start = position(this.startContainer, this.startOffset), end = position(this.endContainer, this.endOffset);
    return nodes.map(node => {
      const at = position(node, 0);
      return end <= at || start >= at + node.nodeValue.length ? ''
        : node.nodeValue.slice(Math.max(0, start - at), Math.min(node.nodeValue.length, end - at));
    }).join('');
  }
}
const selection = {removeAllRanges() {}, addRange(range) {this.range = range;}};
const context = vm.createContext({
  Node: {TEXT_NODE: 3, ELEMENT_NODE: 1}, Range: {START_TO_START: 0, END_TO_END: 2}, saved: null, restore: () => true, post() {}, ed,
  validEditorRange: range => !!range && ed.contains(range.startContainer) && ed.contains(range.endContainer),
  getComputedStyle(node) {
    const display = node.style.display || (['div', 'p', 'li', 'section', 'blockquote', 'table', 'tr', 'td'].includes(node.localName) ? 'block' : 'inline');
    let owner = node;
    while (owner && !owner.style.visibility) owner = owner.parentElement;
    return {display, visibility: owner?.style.visibility || 'visible'};
  },
  window: {getSelection: () => selection},
  document: {
    createRange: () => new TextRange(),
    execCommand(...args) {edits.push(args); return true;},
  },
});
vm.runInContext(source.slice(start, end), context, {filename: sourcePath});
let checks = 0;
function matches(texts, query, matchCase, expected) {
  load([element('p', ...texts)]);
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
function inlineMatches(children, query, expected) {
  load(children);
  const ranges = context.editorMatches(query, false);
  assert.deepEqual(Array.from(ranges, range => [nodes.indexOf(range.startContainer), range.startOffset,
    nodes.indexOf(range.endContainer), range.endOffset, range.toString()]), expected);
  checks++;
  return ranges;
}
let joined = inlineMatches([element('p', 'Please review the ', element('strong', 'annual'), ' report.')], 'annual report',
  [[1, 0, 2, 7, 'annual report']]);
context.saved = joined[0];
assert.equal(context.replaceFoundText('ANNUAL REPORT', 'summary', false), true); checks++;
assert.deepEqual(edits.pop(), ['insertText', false, 'summary']);
inlineMatches([element('p', 'İ😀', element('strong', 'Ab'), element('em', 'C re'), 'port!')], 'abc report',
  [[1, 0, 3, 4, 'AbC report']]);
inlineMatches([element('p', element('strong', 'ab'), element('em', 'cd'), element('span', 'ef'))], 'bcde',
  [[0, 1, 2, 1, 'bcde']]);
inlineMatches([element('p', element('strong', 'ab'), element('em', 'ab'))], 'ab', [[0,0,0,2,'ab'],[1,0,1,2,'ab']]);
inlineMatches([element('p', element('strong', 'a'), element('em', 'b'), ' ', element('strong', 'A'), element('em', 'B'))], 'ab',
  [[0,0,1,1,'ab'],[3,0,4,1,'AB']]);
assert.equal(context.replaceAllInEditor('ab', 'X', false), 2); checks++;
for (const divider of [element('br'), element('img'), new Element('span', [text('ignored')], {hidden: ''}),
  new Element('span', [text('ignored')], {}, {display: 'none'}),
  new Element('span', [text('ignored')], {}, {visibility: 'hidden'}),
  new Element('span', [text('ignored')], {contenteditable: 'false'})]) {
  inlineMatches([element('p', 'annual', divider, ' report')], 'annual report', []);
}
inlineMatches([element('p', 'annual'), element('p', ' report')], 'annual report', []);
inlineMatches([element('p', 'annual', new Element('span', [text('separator')], {}, {display:'block'}), ' report')], 'annual report', []);
inlineMatches([element('p', 'annual\nreport')], 'annual report', []);
inlineMatches([element('p', 'annual\nreport')], 'annual\nreport', [[0,0,0,13,'annual\nreport']]);
load([element('p', 'annual'), element('p', 'report')]);
const crossing = new TextRange(); crossing.setStart(nodes[0], 0); crossing.setEnd(nodes[1], 6); context.saved = crossing;
const editCount = edits.length;
assert.equal(context.replaceFoundText('annualreport', 'wrong', true), false);
assert.equal(edits.length, editCount); checks++;
load([element('p', element('strong', 'annual'), ' report', element('br'))]);
const parentSelection = new TextRange(); parentSelection.selectNodeContents(ed.childNodes[0]); context.saved = parentSelection;
assert.equal(context.replaceFoundText('annual report', 'summary', true), true);
assert.equal(selection.range.startContainer, nodes[0]); assert.equal(selection.range.endContainer, nodes[1]);
assert.equal(selection.range.endOffset, 7); checks++;
console.log('EDITOR_FIND_OFFSETS_PASSED checks=' + checks);
