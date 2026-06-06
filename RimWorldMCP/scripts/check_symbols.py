"""
check_symbols.py - Validate Symbols.json

Checks:
  1. Each entry has char (single character) and group fields
  2. All chars are unique (one-to-one mapping)
  3. No char conflicts with fixed grids (fertility/temperature/pollution)
  4. No control/unprintable chars

Usage:
  python3 check_symbols.py RimWorldMCP/resource/Symbols.json

Exit: 0=pass, 1=fail
"""

import json
import sys
import unicodedata
from collections import Counter

RESERVED = set("▓▒░·○◎●█P.?")


def main():
    path = sys.argv[1]

    with open(path, "r", encoding="utf-8") as f:
        data = json.load(f)

    errors = []

    symbols = data.get("symbols")
    if not isinstance(symbols, dict):
        errors.append("missing or invalid 'symbols' field")
    else:
        char_to_def = {}
        chars = []

        for def_name, entry in symbols.items():
            if not isinstance(entry, dict):
                errors.append(f"{def_name}: not an object")
                continue

            ch = entry.get("char")
            grp = entry.get("group")

            if not ch or not isinstance(ch, str) or len(ch) != 1:
                errors.append(f"{def_name}: invalid char ({ch!r})")
                continue

            chars.append(ch)

            if ch in char_to_def:
                errors.append(f"dup: '{ch}' used by {char_to_def[ch]} and {def_name}")
            else:
                char_to_def[ch] = def_name

            if ch in RESERVED:
                errors.append(f"reserved: {def_name} char '{ch}' used by fixed grid")

            cat = unicodedata.category(ch)
            if cat.startswith("C"):
                errors.append(f"control: {def_name} '{ch}' (U+{ord(ch):04X})")

            if not grp:
                errors.append(f"no group: {def_name}")

        dupes = {c: n for c, n in Counter(chars).items() if n > 1}
        if dupes:
            for c, n in dupes.items():
                names = [k for k, v in symbols.items() if v.get("char") == c]
                errors.append(f"dup char '{c}' x{n}: {names}")

    if errors:
        print(f"FAIL - {len(errors)} errors:")
        for e in errors:
            print(f"  {e}")
        sys.exit(1)
    else:
        print(f"OK - {len(symbols)} entries, all chars unique")


if __name__ == "__main__":
    main()
