# AI provider discovery and setup

The AI path remains `Settings → AiService → AiProviderFactory → IAiProvider`.
Windows owns the provider editor. Both desktop apps share the Core providers; the
Mac app does not yet expose AI settings. OpenAI, Ollama and custom endpoints retain
their existing setup, feature toggles and per-feature cloud consent.

## Setup

1. Add or edit an AI connection in Settings → AI features; select Anthropic.
2. Enter the API key; the endpoint is under **Server (advanced)**. HTTPS is required remotely; HTTP is allowed on
   loopback. Credentials, query strings and fragments are refused in endpoint URLs.
3. Choose **Refresh Claude models**. This reads Anthropic's paginated
   `GET /v1/models` API with `x-api-key` and `anthropic-version: 2023-06-01`.
   No email content or generation prompt is sent during discovery.
4. Enable models to publish them into this connection's dropdown. New models start
   unchecked. Select an enabled model, or type an exact model ID in the Model field.
5. **Test connection** sends only a synthetic `Say OK` prompt to that model. It may
   incur provider charges. An empty reply is a failed test. Testing does not save.
6. **Apply** saves model choices and stores changed credentials in SecretVault.
   Closing without Apply discards edits. Changing provider clears the editor's key
   so it cannot be accidentally reused at another provider.

A refresh preserves enable choices and never changes the selected model. Models
missing from a later list remain recorded as unavailable, disappear from the
dropdown and regain their previous enable choice if they return. Manual IDs and
existing selections are retained, including when a provider cannot list models.

Catalogs are scoped to each connection and normalized endpoint. Key changes
invalidate the old workspace catalog until refreshed. A complete successful refresh
replaces the cache; partial pages, errors and cancellation retain the last good
list. Catalogs, timestamps and enable flags round-trip through settings and clones;
keys are never serialized with metadata. Old single-provider settings retain their
original model and legacy vault slot.

Editing the provider, endpoint, model or key, selecting another connection, or
closing Settings cancels pending work. Identity and cancellation checks reject late
results even when a server completes after cancellation. Test and refresh cannot
overlap. Requests have a 45-second editor deadline and a Cancel request action.

## Parameters, errors and retry policy

- Anthropic uses `max_tokens`. Only recognized older Claude families receive the
  selected creativity value (default `temperature: 0.4`); Claude 4.7+, Mythos, and unknown/future model IDs omit
  temperature. No `top_p`, `top_k` or thinking settings are injected.
- OpenAI reasoning model families (`o1`, `o3`, `o4`, `gpt-5` variants) omit sampling
  and use `max_completion_tokens`. Existing GPT-4 and local/custom model defaults
  remain unchanged.
- `AiException` preserves the legacy `Auth` flag and adds error kind, HTTP status,
  provider error type, request ID, retry delay, retry eligibility and partial-reply
  state. Plain-language messages avoid echoing untrusted response bodies, prompts,
  keys or transport exception details.
- Anthropic handles text SSE, structured stream error events, message completion,
  interrupted streams and non-streaming JSON responses.
- Model-list GETs retry explicit rate-limit/server rejections at most twice with
  exponential delay and jitter. `Retry-After` is honored; waits over five seconds
  return to the user instead of retrying early. Cancellation interrupts backoff.
- Only an explicit HTTP 400 refusal of an optional setting can retry before a reply starts (at most three changes, never the same change twice). Successful shapes are remembered per model and endpoint; testing keeps them unsaved until Apply. Anthropic always retains `max_tokens`. Value errors do not remove parameters.
- Transport, timeout, rate-limit/server and mid-stream generation failures are **never automatically replayed**. This avoids duplicate text and possible charges.
- The engine uses a separate AI HTTP client with redirects disabled. An API-key
  header must not follow a redirect; enter the final endpoint explicitly instead.

## Verification

Core tests use fake HTTP and a loopback redirect server, with no live provider
credentials. The native Windows smoke uses temporary settings, a reversible test
protector, synthetic keys and fake HTTP; it creates the actual settings window in
light and dark themes. It never starts mail sync or edits the user's profile.

```powershell
dotnet test common/tests/Magpie.Core.Tests -c Release --filter "Category!=Integration"
dotnet run --project windows/build/AiSettingsSmoke -c Release -- smoke-captures
dotnet build windows/Magpie.App -c Release
```

The smoke is part of the existing single build workflow. It checks dropdown
bindings, enabling/disabling, unsaved edits, vault isolation, provider switches,
stale async results, errors and reopening saved catalogs. It renders both themes.
The existing XAML and missing-assembly checks remain required. Live Anthropic
credentials, real provider billing and the Mac UI require separate manual checks.

Protocol references:

- [Anthropic Models API](https://platform.claude.com/docs/en/api/models/list)
- [API errors and request IDs](https://platform.claude.com/docs/en/api/errors)
- [Model and parameter deprecations](https://platform.claude.com/docs/en/about-claude/model-deprecations)

## Relationship to PR #99

This is a separate implementation based directly on main (`5b7274b`), with no merge
or change to #99. The API listing, request shaping, field order and creativity
control overlap with #99. `RequestShape` reuses its parameter-shaping logic and slider styling, with
more conservative Claude temperature defaults and a stricter refusal check.
The Mac Pkcs dependency override is retained so the existing Mac CI restore works.

This change adds the explicit enable-to-publish model workflow, per-connection
catalog metadata and lifecycle/output limits, complete-page commits, candidate
setup/test/save, cancellation and stale-result protection, safe structured errors,
no credential redirects, and a real Windows settings smoke test. Existing OpenAI,
Ollama and custom setup is preserved; discovery in this PR is Anthropic-specific.
New discovered models are unchecked; refresh never switches the selected model.
Provider refresh is an explicit action. #99's all-provider automatic daily refresh
and switch suggestions are separate behavior and are not claimed by this PR.

Local checks on 23 Oct 2026: 507 Core unit tests passed (integration excluded),
48 native Windows AI settings smoke assertions passed, both theme captures visually
reviewed, Windows build/publish succeeded with no warnings, XAML 18 files / zero
problems, assembly references 238 / zero missing, and Mac build plus 26 headless
tests passed. No live provider credentials, API billing or actual Mac UI was used.
