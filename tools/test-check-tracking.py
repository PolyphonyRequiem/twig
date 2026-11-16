#!/usr/bin/env python3
"""Exercise the real tracking script with an offline CLI read boundary."""
import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[1]
CHECKER = ROOT / "tools" / "check-tracking.sh"
BASH = r"C:\Program Files\Git\bin\bash.exe" if os.name == "nt" else shutil.which("bash")


class TrackingTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="twig-tracking-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.tools = self.root / "tools"
        self.tools.mkdir()
        self.script = self.tools / "check-tracking.sh"
        shutil.copyfile(CHECKER, self.script)
        self.tickets = self.root / "wayfinder" / "tickets"
        self.tickets.mkdir(parents=True)
        (self.root / "wayfinder-1.0" / "tickets").mkdir(parents=True)
        self.bin = self.root / "bin"
        self.bin.mkdir()
        self.calls = self.root / "calls"
        stub = self.bin / "twig"
        stub.write_text(
            """#!/usr/bin/env bash
printf '%s\\n' "$*" >>"$TRACKING_FIXTURE/calls"
if [[ ! ( $# -eq 6 && "$1" == show && "$3" == --fields &&
          "$4" == System.Description && "$5" == --output && "$6" == json ) &&
      ! ( $# -eq 4 && "$1" == show && "$3" == --output && "$4" == json ) ]]; then
    echo 'Only the exact current cached description read is allowed.' >&2
    exit 97
fi
if [[ ! "$2" =~ ^[1-9][0-9]*$ || ! -f "$TRACKING_FIXTURE/$2.json" ]]; then
    echo 'Fixture item unavailable.' >&2
    exit 3
fi
cat "$TRACKING_FIXTURE/$2.json"
if [[ -f "$TRACKING_FIXTURE/$2.exit" ]]; then
    exit "$(cat "$TRACKING_FIXTURE/$2.exit")"
fi
""",
            encoding="utf-8",
            newline="\n",
        )
        stub.chmod(0o755)
        python3 = self.bin / "python3"
        python3.write_text('#!/usr/bin/env bash\nexec "$PYTHON" "$@"\n',
                           encoding="utf-8", newline="\n")
        python3.chmod(0o755)
        self.env = dict(os.environ, PATH=str(self.bin) + os.pathsep + os.environ["PATH"],
                        PYTHON=sys.executable, TRACKING_FIXTURE=str(self.root))

    def ticket(self, links="[139]", newline="\n"):
        (self.tickets / "1007-fixture.md").write_bytes(
            newline.join(("---", "id: 1007", f"tracked_in: {links}", "---",
                          "Body mentions ruling 9999, not a declared link.", "")).encode()
        )

    def item(self, **changes):
        # CoreCommandAdapter.ShowAsync's single Record is projected directly by JsonRenderer.
        item = {"id": 139, "localId": None, "isSeed": False, "type": "Fixture.Type",
                "title": "Unrelated title", "state": "Fixture.State", "revision": "7",
                "isDirty": False, "cached": True,
                "fields": {"System.Description": "Implements wayfinder ticket 1007."}}
        item.update(changes)
        self.payload(item)

    def payload(self, payload):
        (self.root / "139.json").write_text(json.dumps(payload), encoding="utf-8")

    def run_checker(self, *arguments):
        result = subprocess.run([BASH, str(self.script), *arguments], cwd=self.root,
                                env=self.env, text=True, capture_output=True, timeout=5)
        return result.returncode, result.stdout + result.stderr

    def assert_verdict(self, code, token, *arguments):
        actual, output = self.run_checker(*arguments)
        self.assertEqual(code, actual, output)
        self.assertIn(f"TWIG-TRACKING: {token}", output)
        return output

    def test_current_cached_projection_and_crlf_frontmatter(self):
        self.ticket(newline="\r\n")
        self.item(fields={"System.Description": "\U0001f331 Implements wayfinder ticket 1007."})
        self.assert_verdict(0, "OK (1 links, both directions)")
        self.assertEqual(["show 139 --fields System.Description --output json"],
                         self.calls.read_text().splitlines())

    def test_only_the_description_can_supply_the_backlink(self):
        self.ticket()
        self.item(title="Implements wayfinder ticket 1007.",
                  fields={"System.Description": "Unrelated description; 1007 is just a number."})
        self.assert_verdict(1, "BROKEN (1 of 1 links)")

    def test_rejects_noncurrent_or_misattributed_projections(self):
        self.ticket()
        cases = [
            {"fields": {"System.Description": "Implements ticket 1007."}},
            {"id": 140},
            {"isSeed": True},
            {"cached": False},
            {"revision": None},
            {"fields": {"System.Description": ["ticket 1007"]}},
        ]
        for changes in cases:
            with self.subTest(changes=changes):
                if set(changes) == {"fields"} and isinstance(changes["fields"]["System.Description"], str):
                    self.payload(changes)
                else:
                    self.item(**changes)
                self.assert_verdict(1, "BROKEN (1 of 1 links)")

    def test_native_failure_cannot_be_masked_by_valid_json(self):
        self.ticket()
        self.item()
        (self.root / "139.exit").write_text("9")
        output = self.assert_verdict(1, "BROKEN (1 of 1 links)")
        self.assertIn("exit 9", output)

    def test_malformed_missing_and_partial_reads_remain_failures(self):
        self.ticket("[139, 140]")
        self.item()
        self.assert_verdict(1, "BROKEN (1 of 2 links)")
        self.ticket()
        (self.root / "139.json").write_text("{invalid")
        self.assert_verdict(1, "BROKEN (1 of 1 links)")
        self.item(fields={})
        self.assert_verdict(1, "BROKEN (1 of 1 links)")

    def test_usage_and_empty_selection_have_distinct_verdicts(self):
        self.ticket()
        self.item()
        self.assertIn("unknown option", self.assert_verdict(2, "BROKEN", "--no-such-option"))
        self.assert_verdict(2, "BROKEN", "99999999")
        self.assert_verdict(2, "BROKEN", "1007", "ignored")
        self.assertFalse(self.calls.exists(), "Usage failures must not invoke Twig.")
        self.assert_verdict(0, "OK", "1007")

    def test_body_only_links_do_not_become_declared_links(self):
        (self.tickets / "unlinked.md").write_text(
            "---\nid: 1007\n---\ntracked_in: [139]\n", encoding="utf-8")
        self.assert_verdict(0, "no declared links to check.")
        self.assert_verdict(2, "BROKEN", "1007")
        self.assertFalse(self.calls.exists(), "Undeclared links must not invoke Twig.")

    def test_selftest_delegates_offline_without_invoking_twig(self):
        interpreter = self.bin / "offline-python"
        interpreter.write_text(
            """#!/usr/bin/env bash
[[ $# -eq 1 && "$1" == */tools/test-check-tracking.py ]] || exit 98
echo 'Offline regression entrypoint.'
""",
            encoding="utf-8", newline="\n",
        )
        interpreter.chmod(0o755)
        self.env["PYTHON"] = str(interpreter)
        code, output = self.run_checker("--selftest")
        self.assertEqual(0, code, output)
        self.assertIn("Offline regression entrypoint.", output)
        self.assertFalse(self.calls.exists(), "Selftest must never invoke live Twig.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--checker", type=Path, default=CHECKER)
    options, remaining = parser.parse_known_args()
    CHECKER = options.checker.resolve(strict=True)
    unittest.main(argv=[sys.argv[0], *remaining])
