#!/usr/bin/python3

import json
import os
import stat
import sys
import tempfile
from datetime import datetime, timezone
from email.utils import format_datetime
from pathlib import Path


def fail(message: str) -> None:
    raise ValueError(message)


def parse_epoch(value: str) -> int:
    if not value.isascii() or not value.isdecimal():
        fail("The source epoch must contain decimal digits.")

    epoch = int(value)
    try:
        datetime.fromtimestamp(epoch, timezone.utc)
    except (OverflowError, OSError, ValueError) as error:
        raise ValueError("The source epoch is outside the supported range.") from error
    return epoch


def normalize_manifest(path: Path, epoch: int) -> int:
    file_status = path.lstat()
    if stat.S_ISLNK(file_status.st_mode) or not stat.S_ISREG(file_status.st_mode):
        fail("The static web asset manifest must be a regular file.")

    try:
        document = json.loads(path.read_text(encoding="utf-8"))
    except (UnicodeError, json.JSONDecodeError) as error:
        raise ValueError("The static web asset manifest is not valid JSON.") from error

    if not isinstance(document, dict) or not isinstance(document.get("Endpoints"), list):
        fail("The static web asset manifest has an unexpected structure.")

    replacement = format_datetime(
        datetime.fromtimestamp(epoch, timezone.utc),
        usegmt=True,
    )
    changed = 0
    for endpoint in document["Endpoints"]:
        if not isinstance(endpoint, dict):
            fail("The static web asset endpoint has an unexpected structure.")

        headers = endpoint.get("ResponseHeaders", [])
        if not isinstance(headers, list):
            fail("The static web asset response headers have an unexpected structure.")

        for header in headers:
            if not isinstance(header, dict):
                fail("A static web asset response header has an unexpected structure.")
            if header.get("Name") != "Last-Modified":
                continue
            if not isinstance(header.get("Value"), str):
                fail("A Last-Modified response header has an invalid value.")
            header["Value"] = replacement
            changed += 1

    if changed == 0:
        fail("The static web asset manifest has no Last-Modified response headers.")

    content = json.dumps(document, ensure_ascii=False, separators=(",", ":"))
    temporary_name = None
    try:
        with tempfile.NamedTemporaryFile(
            mode="w",
            encoding="utf-8",
            dir=path.parent,
            prefix=f".{path.name}.",
            suffix=".tmp",
            delete=False,
        ) as temporary:
            temporary_name = temporary.name
            os.fchmod(temporary.fileno(), stat.S_IMODE(file_status.st_mode))
            temporary.write(content)
            temporary.flush()
            os.fsync(temporary.fileno())
        os.replace(temporary_name, path)
        temporary_name = None
    finally:
        if temporary_name is not None:
            Path(temporary_name).unlink(missing_ok=True)

    return changed


def main(arguments: list[str]) -> int:
    if len(arguments) != 3:
        print(
            "Usage: normalize-static-web-assets.py MANIFEST SOURCE_EPOCH",
            file=sys.stderr,
        )
        return 2

    try:
        epoch = parse_epoch(arguments[2])
        normalize_manifest(Path(arguments[1]), epoch)
    except (OSError, ValueError) as error:
        print(str(error), file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
