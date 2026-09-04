#!/usr/bin/python3

import hashlib
import json
import os
import subprocess
import sys
import tempfile
from pathlib import Path


def require(condition: bool, message: str) -> None:
    if not condition:
        raise AssertionError(message)


def run_normalizer(
    script: Path,
    manifest: Path,
    epoch: str,
) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        [sys.executable, str(script), str(manifest), epoch],
        check=False,
        capture_output=True,
        text=True,
        timeout=10,
    )


def main() -> int:
    repository = Path(__file__).resolve().parents[2]
    script = repository / "deploy" / "scripts" / "normalize-static-web-assets.py"
    require(script.is_file(), "The static web asset normalizer is missing.")

    temporary_root = os.environ.get("TMPDIR")
    with tempfile.TemporaryDirectory(prefix="mk8-static-assets-", dir=temporary_root) as directory:
        manifest = Path(directory) / "manifest.json"
        document = {
            "Version": 1,
            "ManifestType": "Build",
            "Endpoints": [
                {
                    "Route": "site.css",
                    "ResponseHeaders": [
                        {"Name": "Cache-Control", "Value": "no-cache"},
                        {"Name": "Last-Modified", "Value": "now"},
                    ],
                },
                {
                    "Route": "site.css.gz",
                    "ResponseHeaders": [
                        {"Name": "Last-Modified", "Value": "later"},
                    ],
                },
            ],
        }
        manifest.write_text(
            json.dumps(document, separators=(",", ":")),
            encoding="utf-8",
        )

        first = run_normalizer(script, manifest, "1788517728")
        require(first.returncode == 0, first.stderr)
        first_bytes = manifest.read_bytes()
        normalized = json.loads(first_bytes)
        values = [
            header["Value"]
            for endpoint in normalized["Endpoints"]
            for header in endpoint["ResponseHeaders"]
            if header["Name"] == "Last-Modified"
        ]
        require(
            values == ["Fri, 04 Sep 2026 10:28:48 GMT"] * 2,
            "The static web asset timestamps are not deterministic.",
        )

        second = run_normalizer(script, manifest, "1788517728")
        require(second.returncode == 0, second.stderr)
        require(
            hashlib.sha256(first_bytes).digest()
            == hashlib.sha256(manifest.read_bytes()).digest(),
            "A second normalization changed the manifest.",
        )

        manifest.write_text('{"Version":1,"Endpoints":[]}', encoding="utf-8")
        empty = run_normalizer(script, manifest, "1788517728")
        require(empty.returncode == 1, "The normalizer accepted a manifest without timestamps.")

        invalid_epoch = run_normalizer(script, manifest, "-1")
        require(invalid_epoch.returncode == 1, "The normalizer accepted a negative epoch.")

    print("Static web asset normalization smoke test passed.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
