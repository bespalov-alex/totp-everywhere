"""Record the decoded lengths of published WebAssembly resources for startup progress."""

import json
import os
import pathlib
import sys

root = pathlib.Path(sys.argv[1])
sizes = {path.relative_to(root).as_posix(): path.stat().st_size
         for path in sorted((root / "_framework").rglob("*.wasm"))}
if not sizes:
    raise SystemExit("No published WebAssembly resources found.")
(root / "boot-sizes.json").write_text(json.dumps(sizes, separators=(",", ":")), encoding="utf-8")

client_id = os.environ.get("GOOGLE_DRIVE_CLIENT_ID", "").strip()
if client_id:
    (root / "drive-config.json").write_text(json.dumps({"clientId": client_id}), encoding="utf-8")
