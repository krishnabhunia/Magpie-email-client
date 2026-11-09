# Scheduling clarity and undoing AI changes

These complete the original compose checklist's timezone/offline clarity and review/apply/Undo paths. They keep the existing local outbox and native editor histories.

## Scheduling

- Every Send later preset shows the resolved local time zone and UTC offset for that date. The date/time picker updates the same label when its selection changes.
- The picker rejects a wall time skipped by a forward clock change or repeated by a backward clock change. It asks for another time instead of silently selecting one of two possible instants.
- The preset menu, custom picker and Scheduled view explain that messages stay on this PC and need Magpie running with access to the mail server. An overdue message can be delayed by an offline or powered-off PC.
- Scheduled rows and confirmation toasts show the local zone and offset. Stored Unix timestamps remain absolute instants; changing the display time zone does not reschedule a message.
- The existing six-attempt automatic retry limit is unchanged. Failed rows and the status bar say when automatic retries have paused and point to **Send now** in Scheduled. They no longer promise another automatic retry after that limit.

## AI apply and Undo

- A reviewed rich-editor AI insertion or replacement is one native browser editing transaction over the writable body only. The entire retained tail, including nested editable descendants and unmarked text after the quote, is protected during that command. Its live nodes, exact markup and typography-reset records remain intact. Loose tail text receives an unstyled persistent span when loaded; temporary edit guards restore every original attribute. The author body stays editable; native paragraph separation keeps appended text apart from its existing formatting. A signature-only or quote-only draft starts with a persistent blank author paragraph.
- Native Undo returns the exact preceding document; Redo reapplies the accepted result. Later manual typing has its own undo entry. Signature/quote markup, folding and typography-reset records are retained.
- The native text-box fallback groups a selected-text replacement instead of assigning its `Text` property, preserving its real Undo/Redo history. Its toolbar Undo and Redo actions now reach that history too.
- Whole-body AI application requires known signature and quote boundaries. When plain-text conversion or imported content makes those boundaries unavailable, Magpie keeps the message unchanged and offers selected-text rewriting. This restriction survives saving, history, scheduling and Undo Send; restoring a known rich revision restores body editing. A live fallback window can still use its exact original protected tail.
- Selection rewrites continue through the existing native editing command. AI results remain previews until explicitly accepted, and applying a result never sends mail.

## Verification

| Coverage | Fixture |
|---|---|
| Indian UTC+05:30 conversion; exact absolute queue persistence | `ScheduleTimeTests.cs` |
| Forward/backward clock-change rejection; correct daylight/standard offsets | `ScheduleTimeTests.cs` with isolated synthetic zones |
| Six-attempt exhaustion and explicit manual retry | `ScheduleTimeTests.cs` with a fake sender |
| Preset/dialog labels, accessible time-zone text and exhausted-retry copy | `ScheduleClaritySmoke.cs`, light and dark themes |
| Rich AI append/replace, exact protected markup and node identity, native Undo/Redo and later typing; 14 boundary fixtures covering nested/inherited-style raw tails, tails outside wrappers, empty bodies, inline images, trailing breaks and explicitly editable tail descendants; inherited tail typography and save/reopen idempotence | `AiApplyUndoSmoke.cs`: ordinary rich cases in light/dark modes, 28 boundary cases in actual WebView2 |
| Plain WebView2 and native fallback AI Undo/Redo; earlier typing preserved | `AiApplyUndoSmoke.cs` |

The Linux environment can execute core tests, check editor JavaScript syntax and compile the native fixtures. Actual WebView2/WPF execution is required in Windows CI; cross-compilation does not establish that those UI regressions passed.
