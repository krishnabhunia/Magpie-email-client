# Local productivity workspace

Approved by Krishna on 8 October 2026; implementation follows issue [Q70 / #83](https://github.com/krishnabhunia/Magpie-email-client/issues/83).

## Inbox organisation

**Important (pinned)** shows inbox conversations with a pinned message. **Other inbox** shows inbox conversations without a pinned message. They partition the unsnoozed inbox; this is explicit user priority, not an AI importance score.

Use **Save view…** below search, or **Commands → New inbox view**, to create a custom split. Choose a name, query, account and whether the view covers inbox folders only. Uncheck that option to create a saved search across mail folders. Views persist in mail.db on this PC. Editing keeps the view identity; removing a view removes no email. A missing account stays scoped to that account and yields an empty view.

Examples:

| Query | Match |
|---|---|
| `from:anita` | Sender name/address contains anita |
| `domain:example.com` | Exact sender domain, case insensitive; excludes subdomains |
| `to:team` | To or Cc contains team |
| `subject:invoice has:attachment` | Subject contains invoice and has an attachment |
| `label:"Team Work"` | Exact local label, case insensitive |
| `domain:example.com is:unread` | Unread email from that domain |

Every predicate is applied in SQLite before pagination. Additional text entered while a saved view is open is combined with the saved query using AND. Up to 32 views are allowed, with unique names of up to 80 characters and queries of up to 2048 characters.

## Search

Search runs locally over stored headers and downloaded bodies. Attachment filenames are available after the body containing attachment metadata downloads. The 90-day body download window remains governed by the existing per-account download settings; headers outside that window can still be searched when stored. This feature does not promise that undownloaded body text is searchable.

Attachments and Pinned buttons add their conditions to the current search without duplicating them. The existing Unread toggle narrows the current view. Ctrl+F focuses search; Escape in search clears it. Save view persists the query rather than fetching results from a server.

## Reminders

**Follow up** retains waiting and due reminders; **Reminders due** shows due reminders only. Commands → Remind me opens Magpie's existing time picker. An outgoing message uses “if nobody replies”; an incoming message uses “bring this back”.

The timer reconciles waiting reminders against synced local replies before the deadline. Another person's later reply in the same account and thread completes a no-reply reminder. Own messages and another account's thread do not cancel it. Unconditional reminders remain active. Due transitions are claimed once and refresh the existing inbox/reminder UI. Offline reply detection waits for sync; no cloud background scheduler is introduced.

## Drafting

Commands → Compose opens the existing locally autosaved draft editor. Commands → Draft with AI opens its draft assistant when an account and Draft AI feature are enabled. It does not generate text automatically. Existing provider availability, feature-level permission, preview and Insert/Replace review gates remain in place. Sending remains a separate action. Ctrl+Shift+A in Compose toggles the enabled AI panel; rewriting remains available in that panel.

## Navigation

A Commands button in the title bar and Ctrl+K open a native command palette. Type an action, folder, saved view or search alias; every word must match. Arrow keys choose, Enter runs and Escape closes. Unavailable actions remain visible with a reason. Reader actions require an open mail conversation and no bulk selection. Calendar context disables mail actions.

The palette reuses existing compose, reply, archive, Trash/Undo, snooze, reminder, pin, sync and navigation commands. It provides no direct send or permanent-delete command. Letter shortcuts are suppressed in WPF text/password/editable-combo fields and the reading WebView.

Opening a conversation continues to reuse SQLite bodies, the bounded body/page memory caches and prepared neighbouring conversations. Neighbour preparation snapshots the targets and checks cancellation and rendering context immediately after background reads, so an obsolete selection/theme cannot proceed with expensive HTML preparation.

## Validation and remaining device checks

Core tests cover persistence, edit/delete, duplicate names, capacity, future schema compatibility, exact domain/label matching, account scope before pagination, priority partitioning, reminder lifecycle and palette matching. Windows Actions validates compilation, static XAML, assembly references and packaging.

Before merging, try Ctrl+K, typing in search, light/dark palettes, saved views across a restart, reminder reply cancellation and AI preview on a Windows desktop. Local command execution in the current ChatGPT session fails before startup, so interactive UI behaviour and rendering latency on Krishna's device have not been measured.
