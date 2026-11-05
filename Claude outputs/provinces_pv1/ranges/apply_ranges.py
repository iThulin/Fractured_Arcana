#!/usr/bin/env python3
# Provinces PV1, ranges (ruling 2026-11-03: big mountain ranges become provinces of
# their own). Replaces the two Claude-authored PV1 files with their range versions.
# Refuses unless each target is still the version PV1 wrote, verifies what it writes,
# and refuses a second run. Usage: python3 apply_ranges.py <repo_root> [--dry-run]
import hashlib
import shutil
import sys
from pathlib import Path

# The dashes the house style bans, built from code points so this file carries none.
EM_DASH = chr(0x2014)
EN_DASH = chr(0x2013)
ESCAPED_EM_DASH = "\\" + "u2014"

HERE = Path(__file__).resolve().parent
TARGETS = [
    ("Scripts/Systems/Overworld/WorldGen/ProvinceBuilder.cs", "04d3cb512d801a8f9c580088f8efc255939ee56b"),
    ("Scripts/Systems/Verification/ProvinceSaveAssert.cs", "52e730849f84d8c159b02fb136a15936bd981d93"),
]


def blob(data: bytes) -> str:
    return hashlib.sha1(b"blob %d\0" % len(data) + data).hexdigest()


def main() -> None:
    if len(sys.argv) < 2:
        print("usage: apply_ranges.py <repo_root> [--dry-run]")
        sys.exit(1)
    root = Path(sys.argv[1])
    dry = "--dry-run" in sys.argv[2:]
    plan = []
    for rel, expected in TARGETS:
        target = root / rel
        got = blob(target.read_bytes())
        if got != expected:
            print(f"REFUSED: {rel} is at blob {got}, expected {expected}")
            sys.exit(1)
        source = HERE / Path(rel).name
        data = source.read_bytes()
        text = data.decode("utf-8")
        if "\r" in text or EM_DASH in text or EN_DASH in text or ESCAPED_EM_DASH in text:
            print(f"REFUSED: {source.name} carries CR or a banned dash")
            sys.exit(1)
        if any(ord(c) > 127 and not (0x2500 <= ord(c) <= 0x257F) for c in text):
            print(f"REFUSED: {source.name} carries characters outside ASCII and box drawing")
            sys.exit(1)
        plan.append((rel, target, source, got, blob(data)))
    for rel, target, source, before, after in plan:
        print(f"{rel}: {before[:7]} -> {after[:7]}")
    if dry:
        print("DRY RUN: nothing written.")
        return
    for rel, target, source, before, after in plan:
        shutil.copyfile(source, target)
    for rel, target, source, before, after in plan:
        if blob(target.read_bytes()) != after:
            print(f"FAILED: {rel} did not verify")
            sys.exit(1)
    print("WRITTEN: both files verified.")


if __name__ == "__main__":
    main()
