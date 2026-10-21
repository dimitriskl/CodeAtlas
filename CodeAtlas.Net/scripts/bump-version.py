#!/usr/bin/env python3
"""Increment the completed-change version and keep its documentation synchronized."""

import argparse
from pathlib import Path
import re


VERSION_PATTERN = r"[0-9]\.[0-9]{2}\.[0-9]{2}"


def next_version(current, *, major_release=False):
    if re.fullmatch(VERSION_PATTERN, current) is None:
        raise ValueError("Version must use x.xx.xx with two-digit middle and final components.")
    major, minor, patch = map(int, current.split("."))
    if major_release:
        if major == 9:
            raise ValueError("Major version range exhausted for x.xx.xx.")
        return f"{major + 1}.00.00"
    if minor == 99 and patch == 99:
        raise ValueError("Version range exhausted; an explicit major-release decision is required.")
    minor, patch = minor + (patch == 99), (patch + 1) % 100
    return f"{major}.{minor:02d}.{patch:02d}"


def bump(root, summary, *, major_release=False):
    summary = summary.strip()
    if not summary or "\n" in summary or "\r" in summary:
        raise ValueError("Provide a nonempty, single-line description of the completed change.")
    version_path = root / "Version.props"
    version_text = version_path.read_text(encoding="utf-8")
    matches = list(re.finditer(r"<CodeAtlasVersion>([^<]+)</CodeAtlasVersion>", version_text))
    if len(matches) != 1:
        raise ValueError("Version.props must contain exactly one CodeAtlasVersion value.")
    current = matches[0].group(1)
    updated = next_version(current, major_release=major_release)
    changes = {version_path: version_text.replace(f"<CodeAtlasVersion>{current}</CodeAtlasVersion>", f"<CodeAtlasVersion>{updated}</CodeAtlasVersion>")}
    marker = f"Version: **{current}**"
    for relative in ["README.md", "docs/CodeAtlas-Project-Recap.md", "docs/VERSION.md"]:
        path = root / relative
        text = path.read_text(encoding="utf-8")
        if len(re.findall(r"^" + re.escape(marker) + r"$", text, re.MULTILINE)) != 1:
            raise ValueError(f"{relative} must have exactly one current-version line matching {current}.")
        text = re.sub(r"^" + re.escape(marker) + r"$", f"Version: **{updated}**", text, flags=re.MULTILINE)
        if relative == "docs/VERSION.md":
            if text.count("## History\n") != 1:
                raise ValueError("docs/VERSION.md must have exactly one History section.")
            text = text.replace("## History\n", f"## History\n\n### {updated}\n\n- {summary}\n", 1)
        changes[path] = text
    # Validate every input before writing any file. The agent reviews and validates the change afterward.
    for path, text in changes.items():
        path.write_text(text, encoding="utf-8")
    return updated


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--summary", required=True, help="One-line description to add to the version history")
    parser.add_argument("--major-release", action="store_true",
                        help="Promote to the next x.00.00 only after explicit user publication authorization and release qualification")
    args = parser.parse_args()
    try:
        updated = bump(Path(__file__).resolve().parents[1], args.summary, major_release=args.major_release)
    except (ValueError, OSError) as error:
        parser.exit(1, f"Version update failed: {error}\n")
    print(updated)


if __name__ == "__main__":
    main()
