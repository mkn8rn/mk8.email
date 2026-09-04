#!/usr/bin/env python3
import argparse
import imaplib
import re
import smtplib
import ssl
import time
import uuid
from email.message import EmailMessage
from email.parser import BytesParser
from email.policy import default
from pathlib import Path


HOST = "127.0.0.1"


def require(condition: bool, message: str) -> None:
    if not condition:
        raise RuntimeError(message)


def tls_context() -> ssl.SSLContext:
    context = ssl.create_default_context()
    context.check_hostname = False
    context.verify_mode = ssl.CERT_NONE
    return context


def new_message(sender: str, recipient: str, marker: str) -> EmailMessage:
    value = EmailMessage()
    value["From"] = sender
    value["To"] = recipient
    value["Subject"] = f"mk8.email multi-domain smoke {marker}"
    value["X-Mk8-Multi-Domain-Test"] = marker
    value.set_content("Multi-domain delivery test.")
    return value


def require_recipient_rejected(recipient: str) -> None:
    with smtplib.SMTP(HOST, 25, timeout=20) as client:
        client.ehlo("probe.debian.org")
        require(client.mail("probe@debian.org")[0] == 250, "The test sender was rejected.")
        code, _ = client.rcpt(recipient)
        require(code in (550, 554), "Postfix accepted a recipient for an inactive domain.")


def require_login_rejected(account: str, password: str) -> None:
    try:
        with imaplib.IMAP4_SSL(HOST, 993, ssl_context=tls_context(), timeout=20) as client:
            client.login(account, password)
    except imaplib.IMAP4.error:
        return
    raise RuntimeError("Dovecot accepted a login for an inactive domain.")


def send_inbound(value: EmailMessage) -> None:
    with smtplib.SMTP(HOST, 25, timeout=30) as client:
        client.ehlo("probe.debian.org")
        client.send_message(value)


def send_submission(value: EmailMessage, account: str, password: str) -> None:
    with smtplib.SMTP(HOST, 587, timeout=30) as client:
        client.ehlo("probe.debian.org")
        client.starttls(context=tls_context())
        client.ehlo("probe.debian.org")
        client.login(account, password)
        client.send_message(value)


def wait_for_message(account: str, password: str, marker: str) -> bytes:
    deadline = time.monotonic() + 40
    while time.monotonic() < deadline:
        with imaplib.IMAP4_SSL(HOST, 993, ssl_context=tls_context(), timeout=20) as client:
            client.login(account, password)
            status, _ = client.select("INBOX")
            require(status == "OK", "IMAP could not select the test inbox.")
            status, data = client.uid(
                "SEARCH", None, "HEADER", "X-Mk8-Multi-Domain-Test", marker
            )
            require(status == "OK", "The IMAP test search failed.")
            identifiers = data[0].split()
            if identifiers:
                identifier = identifiers[-1]
                status, content = client.uid("FETCH", identifier, "(BODY.PEEK[])")
                require(status == "OK", "The IMAP test fetch failed.")
                raw = next(item[1] for item in content if isinstance(item, tuple))
                client.uid("STORE", identifier, "+FLAGS.SILENT", "(\\Deleted)")
                client.expunge()
                return raw
        time.sleep(1)
    raise RuntimeError("The expected multi-domain message was not delivered.")


def require_sender_mismatch_rejected(account: str, password: str) -> None:
    with smtplib.SMTP(HOST, 587, timeout=30) as client:
        client.ehlo("probe.debian.org")
        client.starttls(context=tls_context())
        client.ehlo("probe.debian.org")
        client.login(account, password)
        code, _ = client.mail("admin@mk8n.com")
        require(code in (550, 553), "Postfix accepted a sender from another hosted domain.")


def test_active(domain: str, account: str, password: str, selector: str) -> None:
    exact_marker = uuid.uuid4().hex
    send_inbound(new_message("probe@debian.org", account, exact_marker))
    wait_for_message(account, password, exact_marker)

    catchall_marker = uuid.uuid4().hex
    catchall_recipient = f"undefined-{catchall_marker}@{domain}"
    send_inbound(new_message("probe@debian.org", catchall_recipient, catchall_marker))
    wait_for_message(account, password, catchall_marker)

    submission_marker = uuid.uuid4().hex
    send_submission(new_message(account, account, submission_marker), account, password)
    raw = wait_for_message(account, password, submission_marker)
    parsed = BytesParser(policy=default).parsebytes(raw)
    signatures = " ".join(str(value) for value in parsed.get_all("DKIM-Signature", []))
    require(
        re.search(rf"(?:^|[;\s])d={re.escape(domain)}(?:;|\s)", signatures) is not None,
        "Rspamd did not sign with the second domain identity.",
    )
    require(
        re.search(rf"(?:^|[;\s])s={re.escape(selector)}(?:;|\s)", signatures) is not None,
        "Rspamd did not use the second domain selector.",
    )
    require_sender_mismatch_rejected(account, password)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("mode", choices=("pending", "active"))
    parser.add_argument("--domain", required=True)
    parser.add_argument("--account", required=True)
    parser.add_argument("--password-file", required=True)
    parser.add_argument("--selector")
    arguments = parser.parse_args()
    password = Path(arguments.password_file).read_text(encoding="ascii").rstrip("\r\n")
    require(bool(password), "The test password file is empty.")

    if arguments.mode == "pending":
        require_recipient_rejected(f"undefined@{arguments.domain}")
        require_login_rejected(arguments.account, password)
        print("The pending domain rejected SMTP and IMAP access.")
        return

    require(arguments.selector is not None, "The active test requires a DKIM selector.")
    test_active(arguments.domain, arguments.account, password, arguments.selector)
    print("The second domain passed delivery, catch-all, login, sender, and DKIM tests.")


if __name__ == "__main__":
    main()
