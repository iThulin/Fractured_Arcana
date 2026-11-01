#!/usr/bin/env python3
"""C#-aware delimiter balance: skips // and /* */ comments, char literals, and
string literals (regular, verbatim @"", interpolated $"" / $@"" / @$"", with
nested holes and strings inside holes). Prints {} () [] balance per file."""
import sys


def scan(src: str):
    bal = {"{": 0, "(": 0, "[": 0}
    close = {"}": "{", ")": "(", "]": "["}
    i, n = 0, len(src)

    def skip_string(i, verbatim, interp):
        # i points just past the opening quote
        while i < n:
            ch = src[i]
            if verbatim and ch == '"':
                if i + 1 < n and src[i + 1] == '"':
                    i += 2
                    continue
                return i + 1
            if not verbatim and ch == "\\":
                i += 2
                continue
            if not verbatim and ch == '"':
                return i + 1
            if interp and ch == "{":
                if i + 1 < n and src[i + 1] == "{":
                    i += 2
                    continue
                i = skip_hole(i + 1)
                continue
            i += 1
        return i

    def skip_hole(i):
        depth = 1
        while i < n and depth > 0:
            ch = src[i]
            if ch == '"' or (ch in "@$" and i + 1 < n and src[i + 1] in '"@$'):
                i = skip_literal_start(i)
                continue
            if ch == "'":
                i = skip_char(i)
                continue
            if ch == "{":
                depth += 1
            elif ch == "}":
                depth -= 1
            i += 1
        return i

    def skip_char(i):
        i += 1
        while i < n:
            if src[i] == "\\":
                i += 2
                continue
            if src[i] == "'":
                return i + 1
            i += 1
        return i

    def skip_literal_start(i):
        verbatim = interp = False
        while i < n and src[i] in "@$":
            if src[i] == "@":
                verbatim = True
            else:
                interp = True
            i += 1
        # src[i] should be '"'
        return skip_string(i + 1, verbatim, interp)

    while i < n:
        ch = src[i]
        if ch == "/" and i + 1 < n and src[i + 1] == "/":
            j = src.find("\n", i)
            i = n if j < 0 else j
            continue
        if ch == "/" and i + 1 < n and src[i + 1] == "*":
            j = src.find("*/", i + 2)
            i = n if j < 0 else j + 2
            continue
        if ch == '"' or (ch in "@$" and i + 1 < n and src[i + 1] in '"@$'):
            i = skip_literal_start(i)
            continue
        if ch == "'":
            i = skip_char(i)
            continue
        if ch in bal:
            bal[ch] += 1
        elif ch in close:
            bal[close[ch]] -= 1
        i += 1
    return bal


if __name__ == "__main__":
    for p in sys.argv[1:]:
        b = scan(open(p, encoding="utf-8").read())
        print(f"{b['{']:+d} {b['(']:+d} {b['[']:+d}  {p}")
