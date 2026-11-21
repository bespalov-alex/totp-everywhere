"""Check that a published WebAssembly app starts at a GitHub Pages project URL."""

import functools
import http.server
import pathlib
import shutil
import sys
import tempfile
import threading

from playwright.sync_api import expect, sync_playwright


class QuietHandler(http.server.SimpleHTTPRequestHandler):
    def log_message(self, *_args):
        pass


publish = pathlib.Path(sys.argv[1]).resolve(strict=True)
with tempfile.TemporaryDirectory() as root:
    # Serve from a project subdirectory, as GitHub Pages does.
    (pathlib.Path(root) / "totp-everywhere").symlink_to(publish, target_is_directory=True)
    handler = functools.partial(QuietHandler, directory=root)
    with http.server.ThreadingHTTPServer(("127.0.0.1", 0), handler) as server:
        threading.Thread(target=server.serve_forever, daemon=True).start()
        try:
            with sync_playwright() as playwright:
                chrome = shutil.which("google-chrome") or shutil.which("chromium")
                browser = playwright.chromium.launch(executable_path=chrome)
                page = browser.new_page()
                errors = []
                page.on("pageerror", lambda error: errors.append(str(error)))

                def on_response(response):
                    if response.status >= 400:
                        errors.append(f"HTTP {response.status}: {response.url}")

                page.on("response", on_response)
                page.goto(f"http://127.0.0.1:{server.server_port}/totp-everywhere/")
                expect(page.locator("canvas").first).to_be_visible(timeout=30000)
                page.wait_for_load_state("networkidle")
                if errors:
                    raise RuntimeError("\n".join(errors))
                print("WebAssembly application rendered at /totp-everywhere/.")
        finally:
            server.shutdown()
