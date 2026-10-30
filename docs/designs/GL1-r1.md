# GL1-r1 — collapsible whole Gmail Labels section

Design for [Q84 / issue #112](https://github.com/krishnabhunia/Magpie-email-client/issues/112), requested 30 October 2026. Krishna selected **Only the whole Labels section**. That selection defines scope; implementation approval is still pending.

[Open the self-contained HTML](GL1-r1-gmail-label-section.html). All sample account names, messages, labels and counts are fictional. Primary is the default example; optional Example account demonstrates independent state, without accessing another real mailbox. No external assets or mail/provider/network requests. Only this browser tab's demo expansion choices use session storage.

## Recommended behavior

- Add one full-width **Labels** heading button, chevron and custom-label count inside each Gmail account. Its custom labels form one collapsible section; standard folders retain their existing placement outside it.
- Click anywhere on the heading, including its arrow/count, to show/hide the entire list. Enter/Space uses the same native button. Expose expanded state and visible keyboard focus.
- Keep existing label rows, ordering, names, icons, per-label counts, selection and nested-branch controls/behavior. Individual label-branch changes are explicitly outside scope.
- The heading count is a number of labels, never an aggregate unread/email count: Gmail messages may belong to multiple labels.
- Collapse only the sidebar section: retain the selected label, message list and reader. When the selected label is hidden, show **Viewing: label name** and **Show labels**. Revealing the list does not navigate.
- Save a distinct local Labels-section expanded preference by account ID, separate from real folder paths and existing account/folder expansion. Missing preference defaults to expanded for existing installations.
- Restore state across restart, sync, theme changes and sidebar rebuild. Accounts keep independent choices; preserve preferences for temporarily unavailable accounts and follow existing removal cleanup.
- Identify standard/custom folders using existing provider and folder-role metadata, never English display-name guesses. Preserve non-Gmail accounts' current navigation, Gmail label memberships/server paths, mail selection, counts and sync behavior.
- For zero custom labels, show a small empty-state explanation. Preserve long-name trimming/tooltips and a scrollable sidebar for large lists.

## Preview controls and alternative

Controls above the mock window are for design review, not product UI: compare Proposed/Current, select a label, collapse/expand Labels, Show labels, Light/Dark, refresh, simulate restart, second fictional account and reset. Current layout is a code-based illustration, not a screenshot from a real mailbox. The New message/reading toolbar provide decorative context only.

Alternative: collapse the existing account header, which hides standard folders together with labels. A separate Labels heading is recommended because those folders remain available. No sidebar redesign, nested-label repair, separate Settings page, Gmail API write, Mac navigation change or Android work is included.

## Discovery and version

Base main `8290cad731061da59cbb4344a7f090b3273750e2`, version 10.0.0. `MainWindow.xaml` currently lists account folders directly within each expandable account. `SidebarState` persists account/individual-folder expansion, but has no independent whole Labels section. Existing FolderTree/ToggleFolder logic was inspected; nested-label behavior was not selected scope.

Proposed **10.1.0**, provisional: **Changed** existing Windows label navigation. After approval, repository release automation must recalculate against current main. No version/app source changes occur during this design phase. The HTML shows 10.0.0 only as the example installed version.

## Approval and new PR

Branch `chat-gpt-gmail-label-collapse` contains administrative design/queue files only, with signed commits. No implementation PR is open. After GL1-r1 approval, create a **NEW** draft PR from this branch; do not reuse/modify another PR. App implementation, dependencies, builds/tests and release preparation wait for approval.

## Planned app checks — not run

- Only the owning Gmail account's custom labels collapse; standard folders/account toggles/nested controls retain behavior.
- No navigation/count/message changes when collapsing; Show labels reveals without navigation.
- Settings migration, JSON/backup, account separation and save/restart/sync rebuild preserve state; default expanded.
- Empty/large lists, long names, rapid toggles and missing/removed accounts remain usable.
- Keyboard/screen reader, 125%/150%/200% DPI and light/dark WPF observation.
- Core regressions, Windows publish/XAML/reference/theme/native checks, supported Mac CI, version/package validation.

HTML preview checks are design-only. Real Gmail data, WPF, assistive technology/DPI, real settings persistence/network sync and app/Mac regressions are not verified by the HTML.

The three referenced source policies `D:/Projects/Skills/Github Skills/software app.md`, `versions_management.md` and `workflows.md` remain unavailable. Supplied user requirements/current repository rules apply; no compliance with unread text is claimed.


## Queue allocation audit

This design was initially allocated Q83. A refresh before the administrative commit found Q83 reserved by the parallel issue #110 / design PR #111. This unapproved record was renumbered Q84; issue #112 and its durable request key are unchanged. The parallel proposal includes nested-label changes and is not the selected GL1-r1 scope. Issue #110 and PR #111 remain untouched.
