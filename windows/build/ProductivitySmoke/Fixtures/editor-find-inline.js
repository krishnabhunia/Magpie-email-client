// Real DOM ranges and native browser edits; no replacement or Undo emulation.
(() => {
  let assertions = 0;
  const foldingSupport = quoteFoldingSupported;
  const bounded = value => String(value).slice(0, 3000);
  function check(condition, message) {
    if (!condition) throw new Error(message + '\nHTML: ' + bounded(ed.innerHTML));
    assertions++;
  }
  function find(query, matchCase, count, index, startNode, startOffset, endNode, endOffset, text) {
    const hit = findInEditor(query, matchCase), selection = window.getSelection();
    const range = selection.rangeCount ? selection.getRangeAt(0) : null;
    check(hit.count === count && hit.index === index, 'Cross-inline Find progression: ' + JSON.stringify({query, hit, count, index}));
    check(range && range.startContainer === startNode && range.startOffset === startOffset
      && range.endContainer === endNode && range.endOffset === endOffset && range.toString() === text,
      'Cross-inline Find must map both original UTF-16 endpoints: ' + JSON.stringify({query, startOffset, endOffset,
        actualStart: range?.startOffset, actualEnd: range?.endOffset, actualText: range?.toString()}));
  }
  function undoRedo(before, after, maximumSteps, label) {
    let steps = 0;
    // Replace All uses one native command per match; the browser may group adjacent commands.
    while (ed.innerHTML !== before && steps < maximumSteps) {
      check(fmt('undo'), label + ': native Undo must succeed'); steps++;
    }
    check(steps > 0 && ed.innerHTML === before, label + ': Undo must restore exact pre-edit HTML\nExpected: ' + bounded(before));
    for (let i = 0; i < steps; i++) check(fmt('redo'), label + ': native Redo must succeed');
    check(ed.innerHTML === after, label + ': Redo must restore the exact accepted edit\nExpected: ' + bounded(after));
  }
  try {
    setHtml('<p>Please review the <strong>annual</strong> report.</p>');
    let paragraph = ed.firstElementChild, before = ed.innerHTML;
    find('annual report', true, 1, 1, paragraph.querySelector('strong').firstChild, 0, paragraph.lastChild, 7, 'annual report');
    check(ed.innerHTML === before, 'Finding the supplied formatted phrase must not edit its markup');

    setHtml('<p>before:<strong>annual</strong> report:after</p>');
    paragraph = ed.firstElementChild; before = ed.innerHTML;
    find('annual report', true, 1, 1, paragraph.querySelector('strong').firstChild, 0, paragraph.lastChild, 7, 'annual report');
    check(replaceFoundText('ANNUAL REPORT', 'summary', false) && ed.textContent === 'before:summary:after',
      'Single Replace must replace the whole formatted phrase and preserve both surrounding strings');
    undoRedo(before, ed.innerHTML, 1, 'Cross-inline single Replace');

    setHtml('<p>pre:<strong>annual</strong><em> report</em>|<span>Annual </span><b>Report</b>:post</p>');
    paragraph = ed.firstElementChild; before = ed.innerHTML;
    const firstStart = paragraph.querySelector('strong').firstChild, firstEnd = paragraph.querySelector('em').firstChild;
    const secondStart = paragraph.querySelector('span').firstChild, secondEnd = paragraph.querySelector('b').firstChild;
    find('annual report', false, 2, 1, firstStart, 0, firstEnd, 7, 'annual report');
    find('annual report', false, 2, 2, secondStart, 0, secondEnd, 6, 'Annual Report');
    find('annual report', false, 2, 1, firstStart, 0, firstEnd, 7, 'annual report');
    const within = document.createRange(); within.setStart(firstStart, 2); within.collapse(true); selectEditorRange(within);
    find('annual report', false, 2, 2, secondStart, 0, secondEnd, 6, 'Annual Report');
    check(ed.innerHTML === before, 'Cross-inline caret progression and wrap must not edit the document');
    check(replaceAllInEditor('annual report', 'X', false) === 2 && ed.textContent === 'pre:X|X:post',
      'Replace All must replace both formatted phrases from the end without deleting neighboring text');
    undoRedo(before, ed.innerHTML, 2, 'Cross-inline Replace All');

    setHtml('<p>İ😀A<strong>b</strong><em>C</em> report!</p>');
    paragraph = ed.firstElementChild;
    find('abc report', false, 1, 1, paragraph.firstChild, 3, paragraph.lastChild, 7, 'AbC report');
    check(replaceFoundText('ABC REPORT', 'X', false) && ed.textContent === 'İ😀X!',
      'Split-node case-insensitive replacement must preserve dotted I and the preceding UTF-16 surrogate pair');

    setHtml('<p>İ😀<strong>\u{10400} </strong><em>report</em>!</p>');
    paragraph = ed.firstElementChild;
    find('\u{10428} report', false, 1, 1, paragraph.querySelector('strong').firstChild, 0, paragraph.querySelector('em').firstChild, 6, '\u{10400} report');
    check(replaceAllInEditor('\u{10428} report', 'X', false) === 1 && ed.textContent === 'İ😀X!',
      'Supplementary case-folded letters keep original DOM offsets across inline boundaries');

    const pixel = '<img alt="Kept image" width="16" height="16" src="data:image/gif;base64,R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7">';
    setHtml('<p><strong>annual</strong> report' + pixel + '</p>');
    paragraph = ed.firstElementChild; before = ed.innerHTML;
    const image = paragraph.querySelector('img'), imageHtml = image.outerHTML, parentSelection = document.createRange();
    parentSelection.selectNodeContents(paragraph); selectEditorRange(parentSelection);
    check(replaceFoundText('annual report', 'summary', true) && ed.textContent === 'summary'
      && ed.contains(image) && image.outerHTML === imageHtml,
      'A parent-endpoint selection replaces its eligible text and preserves the extra atomic content');
    undoRedo(before, ed.innerHTML, 1, 'Parent-endpoint single Replace');

    const barriers = [
      ['paragraph', '<p>annual</p><p> report</p>'],
      ['BR', '<p>annual<br> report</p>'],
      ['hidden attribute', '<p>annual<span hidden></span> report</p>'],
      ['display:none', '<p>annual<span style="display:none">secret</span> report</p>'],
      ['visibility:hidden', '<p>annual<span style="visibility:hidden">secret</span> report</p>'],
      ['noneditable', '<p>annual<span contenteditable="false">secret</span> report</p>'],
      ['CSS block', '<p>annual<span style="display:block"></span> report</p>'],
      ['table cells', '<table><tr><td>annual</td><td> report</td></tr></table>'],
      ['inline image', '<p>annual' + pixel + ' report</p>']
    ];
    for (const [name, html] of barriers) {
      setHtml(html); before = ed.innerHTML;
      check(editorMatches('annual report', false).length === 0 && replaceAllInEditor('annual report', 'wrong', false) === 0
        && ed.innerHTML === before, name + ' must split searchable runs without creating a match across it');
      const selected = document.createRange(); selected.selectNodeContents(ed); selectEditorRange(selected);
      check(!replaceFoundText('annual report', 'wrong', false) && ed.innerHTML === before,
        name + ' must also reject a manually selected cross-boundary replacement');
    }
    setHtml('<p><span style="visibility:hidden">hidden<span style="visibility:visible">annual <b>report</b></span></span></p>');
    check(editorMatches('annual report', true).length === 1,
      'A descendant that explicitly restores visibility remains searchable within its own inline run');
    setHtml('<p>annual<span style="display:contents"><b> report</b></span></p>');
    check(editorMatches('annual report', true).length === 1, 'Transparent inline wrappers do not create artificial separators');

    setHtml('<p>annual\nreport</p>'); setEditorPlainText(true, false);
    // Set the actual plain-text document, avoiding rich paragraph whitespace normalization.
    ed.textContent = 'annual\nreport';
    check(editorMatches('annual report', true).length === 0 && editorMatches('annual\nreport', true).length === 1,
      'Plain-text line breaks keep their original matching behavior and offsets');

    setQuoteFoldingSupported(true);
    setHtml('<p>Current writing</p><div class="magpie-quote"><p>annual <strong>report</strong></p></div>');
    check(editorMatches('annual report', true).length === 0, 'Folded quote content remains excluded');
    toggleQuoteFold();
    check(editorMatches('annual report', true).length === 1, 'Expanded quote text can be found across inline formatting');
    return {ok: true, assertions};
  } catch (error) {
    return {ok: false, assertions, error: String(error)};
  } finally {
    setQuoteFoldingSupported(foldingSupport);
    setHtml('<p><br></p>');
  }
})()
