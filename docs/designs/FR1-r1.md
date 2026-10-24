# Fonts and AI replies: FR1-r1

Approved behavior contract for Krishna's 24 October 2026 request; implementation is in PR #109. The overall UI/UX redesign from the previous discussion is set aside and is not part of this work.

Open [the self-contained interactive HTML](FR1-r1-fonts-ai-replies.html) in a browser. It has no external scripts, fonts or assets and uses a content-security policy that prohibits network requests. The named cloud provider and model are examples. All message content is fictional; Primary is the sole sample mailbox and Career is not processed.

## Requests

| Queue | Issue | Design screen | Scope |
| --- | --- | --- | --- |
| Q80 | [#103](https://github.com/krishnabhunia/Magpie-email-client/issues/103) | 01 / Fonts & text | Independent family and size settings for 53 built-in text roles, with search, live sample, reset and validation |
| Q81 | [#104](https://github.com/krishnabhunia/Magpie-email-client/issues/104) | 03 / Reply & Reply All | Clicking a button starts an AI draft when enabled; preview before explicit insertion; ordinary manual reply remains available |
| Q82 | [#105](https://github.com/krishnabhunia/Magpie-email-client/issues/105) | 02 / AI reply settings | Master/feature/trigger gates; tone, purpose, length, language, context and custom instructions; temporary per-draft overrides |

Request keys are recorded in the roadmap and issues. Krishna directly instructed implementation, pushing, and ready-for-review only after Actions pass, then explicitly requested a fresh PR after #108 was closed. This direct authorization is recorded as such, without claiming a literal approval keyword. [PR #109](https://github.com/krishnabhunia/Magpie-email-client/pull/109) is the fresh implementation PR created by this chat; #108 remains closed.

## Typography contract

Text areas: app/navigation (10 roles), inbox (5), reading pane (11), writing/signatures (6), AI text (4), settings/dialogs (8), calendar (5), and in-app notifications/status (4). The HTML contains the complete role catalogue and proposed example values.

Each role has a font family and size, independent of other roles. In the implementation, missing overrides fall back to the existing built-in style rather than globally changing all typography. Existing emphasis is retained. UI labels are not changed by choosing a message-body font. Installed/bundled fonts are used; missing fonts fall back safely without installing or downloading fonts.

Font sizes use Windows logical pixels. 18px is 13.5pt. The accepted range is 8–72px in half-pixel steps. Invalid edits must not persist, and large values must wrap or scroll without hiding essential controls. Reset can target a role, an area, or every font override.

The confirmed Gmail writing default is Arial 18px, normal weight, with explicit family and size in outgoing HTML. It applies to generated text when inserted into the composer as well. User changes to font settings are explicit overrides, not silent changes to the saved preference. The signature is retained exactly once. Existing styled drafts must not be reformatted unexpectedly; the new defaults apply to new composed/inserted text. Reader overrides affect display, not stored message content or what is sent.

Magpie-owned text includes its custom menus, hover cards, dialog contents and in-app notices. Windows-owned notification balloons, native file pickers, permission/sign-in UI and other operating-system surfaces remain governed by Windows. Current Magpie uses `NotifyIcon.ShowBalloonTip`; its API accepts title/text/icon, without a font override. See [Microsoft's API reference](https://learn.microsoft.com/en-us/dotnet/api/system.windows.forms.notifyicon.showballoontip?view=windowsdesktop-9.0). This is an implementation boundary inferred from the current code and documented API, not a claim that Windows notification fonts can be edited in Magpie.

Settings are staged until Apply. Apply and Close saves and closes, and Close discards unapplied settings, consistent with the existing settings contract. Font changes and AI-reply preferences must round-trip through settings serialization, cloning, normalization, change tracking and the existing password-protected backup path. Preview edits in this HTML exist only in memory.

## AI reply contract

The HTML opens with an explicitly labelled AI-enabled example. The actual new AI-replies feature starts Off until enabled. Existing feature choices are retained on upgrade. The existing AI master, the new AI-replies feature switch, the specific Reply/Reply All trigger, provider readiness and feature/connection/endpoint consent must all permit each request, including Retry and Regenerate.

Tone is distinct from purpose. For example, Formal + Make a request, or Polite + Give an instruction/order. Additional defaults include length, language, latest-message/current-conversation context and custom instructions. A temporary draft override never silently changes saved settings.

Clicking Reply/Reply All opens the correctly addressed composer and starts one request without another Generate click when permitted. The model generates body text, never recipients. Reuse the existing sending account and correct Reply-To/To/Cc rules, deduplicate recipients, and exclude own addresses. Keep Primary and Career context separate.

The selected account/thread text and reply instructions are the context. Attachments and unrelated accounts/threads are excluded. Reuse the existing bounded thread extraction and disclose truncation. Treat received mail as quoted, untrusted context; it cannot override instructions or request extra data/tool actions. Do not invent facts, dates or commitments. Include user identity only as already used by the existing draft feature, and preserve the signature once outside generated text.

For cloud providers, show the existing consent flow adapted to the new feature before sending context. A changed connection/endpoint asks again. Consent handling for local-only endpoints must retain the existing provider rules. The preview's consent, provider state and errors are simulated; no model requests occur.

Generation appears in a preview. Use draft inserts it as editable content using composer typography. If the editor contains text, ask whether to keep editing, append the draft or replace the body. Preserve signature and quotes. Sending is always explicit. Generation, retries and regeneration must preserve existing typed text, remain cancellable, and ignore late/stale results when the account/thread/composer changes or the feature is switched off.

The HTML includes Ready, first cloud use, AI Off, missing setup and provider error scenarios. Reply remains usable manually in every case. Existing summaries, prompt drafting, rewrites and suggested-reply chips retain distinct feature switches and behaviour.

## Version and platforms

Fresh base: `origin/main` at `07cebc1`; the exact SHA is recorded in FR1-r1-evidence.json. The source version is 8.1.0. The proposed feature version is 9.0.0 because the inspected repository release automation maps a New entry to a major increment. The repository release automation has now calculated 9.0.0 for this implementation, from the current 8.1.0 base. Version files and release notes agree.

Windows is the requested platform. Shared changes must remain compatible with the Mac client and its settings loading. This request does not authorize a Mac redesign, Android implementation, merging, tagging or publishing releases.

## Review and verification

FR1-r1-preview-checks.json records 25 HTML-only preview checks: independent fonts, size validation and reset, role search, Arial 18px normal, tone/purpose and staged settings, Reply/Reply All recipients, generated preview, non-empty editor insertion, single signature, AI Off, consent rejection, missing setup, provider error, cancellation and master-switch regeneration gating. The three screens also fit at 1440px and 1024px without horizontal document overflow. No JavaScript console errors were captured. Screenshots are examples of these HTML states.

Implementation verification is documented in [FONTS-AI-REPLIES.md](../FONTS-AI-REPLIES.md). Local Core, native WPF and real WebView2 editor checks cover settings/backup, account boundaries, generation gates and protected editor content. Current-head CI and beta/package evidence are tracked in PR #109. Live accounts/providers, DPI, assistive technology and native Mac observation remain device checks.

The source policy files at `D:/Projects/Skills/Github Skills/software app.md`, `versions_management.md`, and `workflows.md` were unavailable. The supplied user instructions and current repository rules informed the design; their current full text should be reconciled before implementation when available.

## Approval

Krishna explicitly authorized implementation for Q80–Q82 and selected a fresh PR in the trusted conversation on 24 October 2026. Requested scope changes still require review of their revised design. The HTML artifact remains unchanged at its recorded hash. The selected approach is generation on click followed by preview and explicit insertion. The direct-fill alternative shown in screen 04 is not selected by approval of this revision.

## Queue allocation audit

Issues #103/#104/#105 were initially allocated Q77/Q78/Q79 at 15:12 UTC on 24 October 2026. A parallel design session then created #106/#107 using Q77/Q78 at 15:17 UTC, with overlapping feature scope. Before approval, this proposal's allocations were changed to Q80/Q81/Q82 to avoid ambiguity. Issue IDs and durable request keys are unchanged. The original allocations remain in this audit record and GitHub issue history; no request was rejected or removed.

Related parallel designs: [#106](https://github.com/krishnabhunia/Magpie-email-client/issues/106), [#107](https://github.com/krishnabhunia/Magpie-email-client/issues/107). They were read only; their content and lifecycle have not been changed. Their design choices are not approval for FR1-r1. This implementation follows the explicitly authorized FR1-r1 preview-and-insert behavior; the parallel designs remain outside this PR.
