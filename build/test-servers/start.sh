#!/usr/bin/env bash
# Starts a local Dovecot IMAP (127.0.0.1:1143) + test SMTP (127.0.0.1:1025) for the integration tests (Linux).
# Needs: apt install dovecot-imapd ; pip install aiosmtpd
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
id mailtest >/dev/null 2>&1 || useradd -m -s /usr/sbin/nologin mailtest
mkdir -p /opt/imaptest/run /opt/imaptest/state /opt/imaptest/mail
cp "$HERE/dovecot.conf" "$HERE/smtp.py" /opt/imaptest/
touch /opt/imaptest/users
chown -R mailtest:mailtest /opt/imaptest/mail
dovecot -c /opt/imaptest/dovecot.conf || true
(cd /opt/imaptest && setsid nohup python3 smtp.py > smtp.out 2>&1 &)
echo "IMAP 127.0.0.1:1143, SMTP 127.0.0.1:1025 — run: dotnet test tests/Magpie.Core.Tests --filter Category=Integration"
