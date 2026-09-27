# One-time sign-in setup (Google and Microsoft)

Magpie signs in to Gmail and Outlook with **your own** free app registration, so no third party ever holds your
mail tokens. You do this once; it takes about 5 minutes per provider. Then paste the IDs into
**Settings → Accounts → Sign-in apps** (or use *Import Google client JSON…*).

> Not ready for this yet? Gmail works with an **app password** (Google Account → Security → 2-Step Verification →
> App passwords), and any other mailbox works with its normal IMAP password.

## Google (Gmail)

| # | Step |
|---|------|
| 1 | Open <https://console.cloud.google.com/> → create a project, e.g. `magpie-mail`. |
| 2 | **APIs & Services → OAuth consent screen** → User type **External** → app name *Magpie*, your email as support + developer contact. |
| 3 | **Scopes → Add** `https://mail.google.com/` (plus `openid`, `email`, `profile`). |
| 4 | **Test users → Add** every Gmail address you'll use in Magpie. |
| 5 | **Credentials → Create credentials → OAuth client ID → Application type: Desktop app** → name *Magpie*. |
| 6 | **Download JSON** → in Magpie: Settings → Accounts → *Import Google client JSON…* |
| 7 | Recommended: on the consent screen click **Publish app** (status *In production*). In *Testing* status Google expires sign-ins every 7 days. As a personal app you'll see a one-time "Google hasn't verified this app" screen → *Advanced → Go to Magpie*. |

Magpie uses the loopback redirect `http://127.0.0.1:<random port>` with PKCE — nothing to configure for it.

## Microsoft (Outlook.com, Hotmail, Microsoft 365)

| # | Step |
|---|------|
| 1 | Open <https://portal.azure.com/> → **Microsoft Entra ID → App registrations → New registration**. |
| 2 | Name *Magpie*; Supported account types: **Accounts in any organizational directory and personal Microsoft accounts**. |
| 3 | Redirect URI: platform **Public client/native (mobile & desktop)**, value `http://localhost` → Register. |
| 4 | **Authentication → Advanced settings → Allow public client flows = Yes** → Save. |
| 5 | **API permissions → Add → APIs my organization uses → Office 365 Exchange Online → Delegated**: `IMAP.AccessAsUser.All`, `SMTP.Send` (plus Microsoft Graph `offline_access`, `openid`, `email`, `profile`). |
| 6 | Copy the **Application (client) ID** from Overview → paste into Magpie → *Microsoft client ID*. |

Work / school accounts: your IT admin may need to allow the app and have IMAP + authenticated SMTP enabled for your mailbox.
