# AI writing for Reply, Reply All and Forward (11.0.0)

Version 11.0.0 implements Q85–Q87 / issues #113–#115 from the approved [AW1 design](designs/AW1-ai-writing.html) (approved 30 Oct 2026). It builds on the 10.0.0 AI replies ([FR1](FONTS-AI-REPLIES.md)).

| Item | What it does |
|---|---|
| Q85 · Forward | Settings → AI writing → **When I click Forward** (Off by default; its own consent per cloud connection, `AiFeature.ForwardDraft`). The note goes above the forwarded email; reason chips FYI only · Please handle · Please review · Introduce · Escalate · Custom plus a free-text note; optional 2-line summary and attachment mention (file names only). With no recipient yet the note starts "Hi,"; adding one changes only that greeting line locally (`AiService.WithRecipientGreeting`), no new request. The model reads only the forwarded email (`Draft.ForwardOfRow`). |
| Q86 · What the reply says | With **Show "What the reply says"** on, the first draft asks the model to start with `ASK / ANSWER / GIST / DRAFT:` lines (`ParseDraft` hides them, also while streaming). The suggestion fills the answer box; chips Yes · No · Yes, if… · Need details · Later · Custom set it (Yes / No / Need details / Later rewrite at once; Yes, if… and Custom put the cursor in the box). Typing switches the chip to Custom so a chip can never contradict the words. Rewrite (or Ctrl+Enter inside the panel — it never sends from there) follows the box exactly. Up to 5 versions per draft with ‹ ›. |
| Q87 · How it sounds | Tone chips (Formal/Informal moved out), a 5-step Formality scale, Length, Language (more languages), Purpose (replies). Culture & etiquette: English style, business etiquette, directness, greeting, form of address, emoji (only when Casual or Very casual). Per draft in the panel; changes are counted on the Rewrite button and sent together. **Save these choices as my defaults** writes them to Settings at once. **Remember my choices per contact** (Off): when a draft whose style differs from the defaults is used, that style is stored for the first To address (max 300, newest kept) and used for the next reply to them. |

## Rules kept from 10.0.0

Sending is always explicit; recipients never come from the model; signature and quoted/forwarded block are protected; Use draft asks Append / Replace when you have typed; email text is untrusted quoted data inside JSON; the user's answer is also JSON data, bounded to 500 characters; a context/account/AI change drops pending results. Per-draft choices are no longer part of the context fingerprint, so changing one keeps the shown version usable.

## Upgrade

A saved Tone of Formal becomes Professional + Formality Formal; Informal becomes Friendly + Casual. New fields get their defaults (Forward Off, What the reply says On, Indian English, Neutral etiquette, Balanced, Auto greeting/address, no emoji). Remembered contact styles are written by the composer as they happen and are not part of Settings Apply.

## Verification

| Check | Result |
|---|---|
| Core tests (`Release1100Tests` + updated `Release1000Tests`) | Run on Linux — see the PR |
| Windows build, DpDump, `xaml_check.py`, `--check-refs` | Run on Linux — see the PR |
| `windows/build/ProductivitySmoke` → `CheckAiWritingFlow` (fake local provider: suggestion, Yes → No, versions, collected changes, Save as defaults, Forward off / on, greeting update) | Runs in the Windows CI job |

## Not verified on a real device

- Real provider output with the header lines (Claude / OpenAI / Ollama). If a model ignores the header, the whole answer is used as the draft and the answer box stays as it was.
- The panel's look at 125–200 % DPI, keyboard order and screen readers; a small compose window (the panel scrolls, max 380 px, and collapses to one line).
- The Mac app: it shares the Core changes (prompt, settings, migration) but has no AI draft panel yet.
