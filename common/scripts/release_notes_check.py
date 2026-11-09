"""Fail a publication if its What's New body contains no specific changes."""

import re
import sys
from pathlib import Path


def detailed_changes(text: str) -> list[str]:
    return [line[2:].strip() for line in text.splitlines()
            if line.startswith("- ") and len(re.sub(r"[*`_]", "", line)) >= 55
            and not re.search(r"(?i)no changes|test build only|release notes unavailable", line)]


if __name__ == "__main__":
    if len(sys.argv) != 2:
        raise SystemExit("Usage: release_notes_check.py FILE")
    details = detailed_changes(Path(sys.argv[1]).read_text(encoding="utf-8-sig"))
    if not details:
        raise SystemExit("Release blocked: provide specific feature/fix details in the version's CHANGELOG section")
    print(f"Validated {len(details)} published implementation details")
