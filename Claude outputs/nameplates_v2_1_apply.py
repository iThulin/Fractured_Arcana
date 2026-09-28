#!/usr/bin/env python3
# Nameplates v2.1 (2026-09-28): intent colour tokens for UITheme.cs (LIVE file).
# The find must match exactly once or nothing is written.
# Run from the repo root:  python3 "Claude outputs/nameplates_v2_1_apply.py"
import sys

PATH = "Scripts/UI/UITheme.cs"
FIND = """    public static readonly Color PlateNameEnemy = new Color(1.00f, 0.76f, 0.72f, 1f);
"""
REPL = """    public static readonly Color PlateNameEnemy = new Color(1.00f, 0.76f, 0.72f, 1f);
    // Intent pill icons (UnitNameplate.Intent.cs): one hue per IntentKind.
    public static readonly Color IntentAttack  = new Color(1.00f, 0.40f, 0.36f, 1f); // sword
    public static readonly Color IntentRanged  = new Color(1.00f, 0.66f, 0.30f, 1f); // arrow
    public static readonly Color IntentChannel = new Color(0.70f, 0.52f, 1.00f, 1f); // swirl
    public static readonly Color IntentRelease = new Color(1.00f, 0.45f, 0.88f, 1f); // burst
    public static readonly Color IntentGuard   = PlateArmor;                          // armor plate
    public static readonly Color IntentImbue   = new Color(0.30f, 0.85f, 0.80f, 1f); // droplet
    public static readonly Color IntentShove   = new Color(0.98f, 0.84f, 0.38f, 1f); // push arrow
"""

def main():
    with open(PATH, encoding="utf-8", newline="") as f:
        src = f.read()
    crlf = "\r\n" in src
    text = src.replace("\r\n", "\n")
    if "IntentAttack" in text:
        print("already applied")
        return
    n = text.count(FIND)
    if n != 1:
        print(f"ABORT: {PATH} matched {n} times (expected 1). Nothing written.")
        sys.exit(1)
    text = text.replace(FIND, REPL)
    with open(PATH, "w", encoding="utf-8", newline="") as f:
        f.write(text.replace("\n", "\r\n") if crlf else text)
    print(f"patched {PATH}")

if __name__ == "__main__":
    main()
