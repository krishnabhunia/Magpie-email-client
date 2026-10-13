# Test SMTP server: accepts AUTH PLAIN/LOGIN (any user with password "secret"), delivers each
# message into the recipient's Dovecot INBOX via IMAP APPEND, and logs to /opt/imaptest/smtp.log.
import asyncio, imaplib, time
from aiosmtpd.controller import Controller
from aiosmtpd.smtp import AuthResult, LoginPassword

LOG = open('/opt/imaptest/smtp.log', 'a', buffering=1)

def authenticator(server, session, envelope, mechanism, auth_data):
    ok = isinstance(auth_data, LoginPassword) and auth_data.password == b'secret'
    LOG.write(f"AUTH {mechanism} ok={ok}\n")
    return AuthResult(success=ok)

class Handler:
    async def handle_DATA(self, server, session, envelope):
        for rcpt in envelope.rcpt_tos:
            try:
                m = imaplib.IMAP4('127.0.0.1', 1143)
                m.login(rcpt, 'secret')
                m.append('INBOX', '', imaplib.Time2Internaldate(time.time()), envelope.content)
                m.logout()
                LOG.write(f"DELIVERED to {rcpt} from {envelope.mail_from}\n")
            except Exception as e:
                LOG.write(f"DELIVERY FAILED {rcpt}: {e}\n")
        return '250 OK'

c = Controller(Handler(), hostname='127.0.0.1', port=1025, authenticator=authenticator,
               auth_require_tls=False, auth_required=True)
c.start()
LOG.write("started\n")
import signal
signal.pause()
