#!/usr/bin/env python3
import argparse
import imaplib
import re
import smtplib
import ssl
import subprocess
import time
import uuid
from email.message import EmailMessage
from email.parser import BytesParser
from email.policy import default
from pathlib import Path


LOCAL_HOST = "127.0.0.1"
INBOUND_HOST = "@@MK8_SERVER_IPV4@@"


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
    with smtplib.SMTP(INBOUND_HOST, 25, timeout=20) as client:
        client.ehlo("probe.debian.org")
        require(client.mail("probe@debian.org")[0] == 250, "The test sender was rejected.")
        code, _ = client.rcpt(recipient)
        require(code in (550, 554), "mk8.email accepted a recipient for an inactive domain.")


def require_login_rejected(account: str, password: str) -> None:
    try:
        with imaplib.IMAP4_SSL(LOCAL_HOST, 993, ssl_context=tls_context(), timeout=20) as client:
            client.login(account, password)
    except imaplib.IMAP4.error:
        return
    raise RuntimeError("mk8.email accepted an IMAP login for an inactive domain.")


def send_inbound(value: EmailMessage) -> None:
    with smtplib.SMTP(INBOUND_HOST, 25, timeout=30) as client:
        client.ehlo("probe.debian.org")
        client.send_message(value)


def send_submission(value: EmailMessage, account: str, password: str) -> None:
    with smtplib.SMTP(LOCAL_HOST, 587, timeout=30) as client:
        client.ehlo("probe.debian.org")
        client.starttls(context=tls_context())
        client.ehlo("probe.debian.org")
        client.login(account, password)
        client.send_message(value)


def find_message_identifiers(client: imaplib.IMAP4_SSL, marker: str) -> list[bytes]:
    status, data = client.uid(
        "SEARCH", None, "HEADER", "X-Mk8-Multi-Domain-Test", marker
    )
    require(status == "OK", "The IMAP test search failed.")
    return data[0].split()


def wait_for_message(
    account: str,
    password: str,
    marker: str,
    delete: bool = True,
) -> bytes:
    deadline = time.monotonic() + 40
    while time.monotonic() < deadline:
        with imaplib.IMAP4_SSL(LOCAL_HOST, 993, ssl_context=tls_context(), timeout=20) as client:
            client.login(account, password)
            status, _ = client.select("INBOX")
            require(status == "OK", "IMAP could not select the test inbox.")
            identifiers = find_message_identifiers(client, marker)
            if identifiers:
                identifier = identifiers[-1]
                status, content = client.uid("FETCH", identifier, "(BODY.PEEK[])")
                require(status == "OK", "The IMAP test fetch failed.")
                raw = next(item[1] for item in content if isinstance(item, tuple))
                if delete:
                    client.uid("STORE", identifier, "+FLAGS.SILENT", "(\\Deleted)")
                    client.expunge()
                return raw
        time.sleep(1)
    raise RuntimeError("The expected multi-domain message was not delivered.")


def database_value(query: str) -> str:
    result = subprocess.run(
        [
            "runuser",
            "-u",
            "postgres",
            "--",
            "psql",
            "--dbname=mk8email",
            "--no-psqlrc",
            "--quiet",
            "--tuples-only",
            "--no-align",
            "--command",
            query,
        ],
        check=True,
        capture_output=True,
        text=True,
        timeout=15,
    )
    return result.stdout.strip()


def quota_state(account: str, marker: str) -> tuple[int, int, int]:
    require(
        re.fullmatch(r"[a-z0-9][a-z0-9@._-]*", account) is not None,
        "The quota test account is not safe.",
    )
    require(marker.isascii() and marker.isalnum(), "The quota marker is not safe.")
    value = database_value(
        "SELECT u.quota_bytes || '|' || "
        "COALESCE((SELECT SUM(e.size_bytes) FROM emails e "
        "JOIN folders f ON f.id = e.folder_id "
        "JOIN inboxes i ON i.id = f.inbox_id WHERE i.owner_id = u.id), 0) || '|' || "
        "COALESCE((SELECT e.size_bytes FROM emails e "
        "JOIN folders f ON f.id = e.folder_id "
        "JOIN inboxes i ON i.id = f.inbox_id "
        f"WHERE i.owner_id = u.id AND e.raw_headers LIKE '%{marker}%' "
        "ORDER BY e.received_at DESC LIMIT 1), -1) "
        f"FROM users u WHERE u.username = '{account}'"
    )
    parts = value.split("|")
    require(
        len(parts) == 3 and all(part.isdecimal() for part in parts),
        "The quota state is not valid.",
    )
    return int(parts[0]), int(parts[1]), int(parts[2])


def set_user_quota(account: str, quota_bytes: int) -> None:
    require(quota_bytes >= 0, "The test quota is not valid.")
    result = database_value(
        f"UPDATE users SET quota_bytes = {quota_bytes} WHERE username = '{account}'; "
        f"SELECT quota_bytes FROM users WHERE username = '{account}'"
    )
    require(result == str(quota_bytes), "The test quota did not change.")


def delete_marker(account: str, password: str, folder: str, marker: str) -> None:
    with imaplib.IMAP4_SSL(LOCAL_HOST, 993, ssl_context=tls_context(), timeout=20) as client:
        client.login(account, password)
        status, _ = client.select(folder)
        require(status == "OK", f"IMAP could not select {folder} for cleanup.")
        for identifier in find_message_identifiers(client, marker):
            client.uid("STORE", identifier, "+FLAGS.SILENT", "(\\Deleted)")
        client.expunge()


def test_copy_quota(account: str, password: str) -> None:
    marker = uuid.uuid4().hex
    original_quota = None
    try:
        send_inbound(new_message("probe@debian.org", account, marker))
        source_content = wait_for_message(account, password, marker, delete=False)
        original_quota, used_bytes, source_bytes = quota_state(account, marker)
        require(source_bytes > 0 and used_bytes >= source_bytes, "The source size is not valid.")

        with imaplib.IMAP4_SSL(LOCAL_HOST, 993, ssl_context=tls_context(), timeout=20) as client:
            client.login(account, password)
            require(client.select("INBOX")[0] == "OK", "IMAP could not select the copy source.")
            source_identifiers = find_message_identifiers(client, marker)
            require(len(source_identifiers) == 1, "The copy source is not unique.")
            source_identifier = source_identifiers[0]

            set_user_quota(account, used_bytes)
            status, response = client.uid("COPY", source_identifier, "Trash")
            response_text = b" ".join(item for item in response if isinstance(item, bytes))
            require(
                status == "NO" and b"[OVERQUOTA]" in response_text,
                "UID COPY did not reject the over-quota request.",
            )

            set_user_quota(account, used_bytes + source_bytes)
            status, _ = client.uid("COPY", source_identifier, "Trash")
            require(status == "OK", "UID COPY rejected the exact quota boundary.")

            require(client.select("Trash")[0] == "OK", "IMAP could not select the copy target.")
            destination_identifiers = find_message_identifiers(client, marker)
            require(len(destination_identifiers) == 1, "The copied message is not unique.")
            status, content = client.uid(
                "FETCH", destination_identifiers[0], "(BODY.PEEK[])"
            )
            require(status == "OK", "The copied message could not be fetched.")
            copied_content = next(item[1] for item in content if isinstance(item, tuple))
            require(copied_content == source_content, "UID COPY changed the message content.")
    finally:
        if original_quota is not None:
            set_user_quota(account, original_quota)
        delete_marker(account, password, "Trash", marker)
        delete_marker(account, password, "INBOX", marker)


def require_sender_mismatch_rejected(account: str, password: str) -> None:
    with smtplib.SMTP(LOCAL_HOST, 587, timeout=30) as client:
        client.ehlo("probe.debian.org")
        client.starttls(context=tls_context())
        client.ehlo("probe.debian.org")
        client.login(account, password)
        code, _ = client.mail("admin@mk8n.com")
        if code < 400:
            code, _ = client.rcpt(account)
        require(code in (550, 553), "mk8.email accepted a sender from another hosted domain.")


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
    test_copy_quota(account, password)


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
    print("The second domain passed delivery, catch-all, login, sender, DKIM, and COPY tests.")


if __name__ == "__main__":
    main()
