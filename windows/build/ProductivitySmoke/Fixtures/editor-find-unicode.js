// Run against the real EditorPage DOM in WebView2 or a local headless browser.
(() => {
  let assertions = 0;
  function check(condition, message) {
    if (!condition) throw new Error(message);
    assertions++;
  }
  function find(query, matchCase, count, index, text, start, end, parent = 'P') {
    const found = findInEditor(query, matchCase);
    const range = window.getSelection().getRangeAt(0);
    check(found.count === count && found.index === index, 'Unexpected match count or progression for ' + query);
    check(range.toString() === text && range.startOffset === start && range.endOffset === end
      && range.startContainer.parentElement.tagName === parent, 'Search must select exact original UTF-16 offsets for ' + query);
  }
  const localeLower = String.prototype.toLocaleLowerCase;
  try {
    setHtml('<p>İabcX</p>');
    find('abc', false, 1, 1, 'abc', 1, 4);
    check(replaceFoundText('AbC', 'word', false), 'Case-insensitive Replace must accept the found original text');
    check(ed.textContent === 'İwordX', 'Single replacement must preserve both surrounding characters');

    setHtml('<p>İabc</p>');
    find('ABC', false, 1, 1, 'abc', 1, 4);
    check(replaceAllInEditor('ABC', 'end', false) === 1 && ed.textContent === 'İend',
      'A match at the end must not overflow the original text node');

    setHtml('<p>İ I i İ</p>');
    find('İ', false, 2, 1, 'İ', 0, 1);
    find('İ', false, 2, 2, 'İ', 6, 7);
    check(replaceAllInEditor('İ', 'Z', false) === 2 && ed.textContent === 'Z I i Z',
      'A length-changing lowercase query must still replace only its original matches');

    // Searching and replacing must not depend on Windows/macOS language or locale casing.
    String.prototype.toLocaleLowerCase = function() { throw new Error('Editor search used locale-sensitive casing'); };
    setHtml('<p>I i İ ı</p>');
    find('i', false, 2, 1, 'I', 0, 1);
    check(replaceFoundText('i', 'Z', false), 'Single replace uses the same locale-independent comparison as Find');
    check(replaceAllInEditor('i', 'Z', false) === 1 && ed.textContent === 'Z Z İ ı',
      'ASCII I matching stays consistent regardless of the host locale');

    setHtml('<p>ςΣσ</p>');
    find('σ', false, 3, 1, 'ς', 0, 1);
    check(replaceFoundText('σ', 'Z', false) && ed.textContent === 'ZΣσ',
      'Single Replace agrees with Unicode case folding, including final sigma');

    setHtml('<p>İ😀\u{10400} X \u{10428}</p>');
    find('\u{10428}', false, 2, 1, '\u{10400}', 3, 5);
    find('\u{10428}', false, 2, 2, '\u{10428}', 8, 10);
    check(replaceAllInEditor('\u{10428}', 'Z', false) === 2 && ed.textContent === 'İ😀Z X Z',
      'Supplementary letters and emoji retain their original UTF-16 boundaries');

    setHtml('<p>İ<strong>AbC</strong> and <em>İabc</em> ABC</p>');
    find('abc', false, 3, 1, 'AbC', 0, 3, 'STRONG');
    find('abc', false, 3, 2, 'abc', 1, 4, 'EM');
    find('abc', false, 3, 3, 'ABC', 1, 4);
    find('abc', false, 3, 1, 'AbC', 0, 3, 'STRONG');
    check(replaceAllInEditor('aBc', 'Z', false) === 3, 'Replace all must process every formatted text node');
    check(ed.innerHTML === '<p>İ<strong>Z</strong> and <em>İZ</em> Z</p>',
      'Replace all must preserve formatting and nonmatching prefixes across text nodes');

    const literal = '[a].*+(x)?^$|\\';
    setHtml('<p>İ' + literal + ' ' + literal.replace('a', 'A') + '</p>');
    find(literal, false, 2, 1, literal, 1, 1 + literal.length);
    check(replaceAllInEditor(literal, '$&\\end', false) === 2 && ed.textContent === 'İ$&\\end $&\\end',
      'Search metacharacters and replacement dollar/backslash characters remain literal');

    setHtml('<p>İAbC abc</p>');
    find('abc', true, 1, 1, 'abc', 5, 8);
    check(replaceAllInEditor('abc', 'Z', true) === 1 && ed.textContent === 'İAbC Z',
      'Match case still restricts replacement to the exact requested spelling');

    setHtml('<p>İabcX</p>');
    const wrong = document.createRange(), node = ed.firstElementChild.firstChild;
    wrong.setStart(node, 2); wrong.setEnd(node, 5); selectEditorRange(wrong);
    check(!replaceFoundText('abc', 'wrong', false) && ed.textContent === 'İabcX',
      'Single replacement rejects a selection containing neighboring, nonmatching text');

    setHtml('<p>' + 'İa '.repeat(2001) + '</p>');
    const before = ed.innerHTML;
    check(replaceAllInEditor('A', 'Z', false) === -1 && ed.innerHTML === before,
      'Too many matches must leave the entire document unchanged');
    check(editorMatches('', false).length === 0 && editorMatches('a'.repeat(201), false).length === 0,
      'Empty and overlong searches remain rejected');
    return { ok: true, assertions };
  } catch (error) {
    return { ok: false, assertions, error: String(error) };
  } finally {
    String.prototype.toLocaleLowerCase = localeLower;
    setHtml('<p><br></p>');
  }
})()
