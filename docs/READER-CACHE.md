# Reader caching

Email bodies, text, attachment descriptions, calendar parts and embedded pictures are persisted in `mail.db`.
The account download window defaults to 90 days and continues in background rounds until available bodies are saved.
Existing choices, including longer windows and downloading all mail, are preserved. Trash, Spam and Drafts remain outside automatic background body downloads; opening them still saves their bodies.

The reader uses three bounded memory layers:

| Layer | Maximum entries | Byte budget | Reuse |
|---|---:|---:|---|
| Saved body snapshots per store | 256 | 64 MiB | Avoid SQLite reads and image/attachment JSON decoding. |
| Prepared HTML per reader | 64 | 64 MiB | Avoid cleaning and constructing the same conversation again. |
| Connected browser views per reader | 8 | 32 MiB of HTML | Preserve rendered iframes, decoded pictures and scroll position. |

Byte budgets measure retained text and metadata, rather than the browser's total process memory. DOM and decoded image memory are additionally bounded by the eight-view limit. A single oversized active page is displayed but is not retained in the managed caches; the browser drops it when another view is selected.

The browser navigates once to a persistent local shell. Later selections show the existing DOM or insert a prepared body. Cache hits send only identifiers and fingerprints through WebView2, not the complete HTML again. Email HTML still passes through the existing sanitizer and stays inside iframes without `allow-scripts`.

Read badges update in place, so marking an opened email read does not destroy its rendered view. Expanded messages and scroll position are preserved on return. Large embedded pictures recovered from a local MIME file are retained within the body memory budget; unresolved pictures are not repeatedly parsed on every selection.

Body snapshots returned to callers have independent dictionaries and attachment lists. Body saves invalidate affected entries; message/folder/account deletion invalidates body memory. Prepared pages validate headers, full body content, embedded images, permissions, theme, hover delay and relative-time bucket. Body content digests are computed once on a disk read and used only when the rendered snapshot still matches that content.

The next eight neighbouring conversations are prepared in the background. Cold body reads and HTML cleaning run off the UI thread. Interactive downloads share one operation per message: changing selection stops that reader's wait while the download finishes saving to disk. Account shutdown cancels the shared work. A saved complete body is checked before either a MIME-file read or an IMAP connection.

The first download and first render still take time. Cached mail remains readable after restart from disk; memory and rendered browser views are rebuilt as needed. Remote web images retain the existing permission policy and are not downloaded as part of the 90-day body window.
