# Claude instructions - Fractured Arcana

## Writing style (mandatory)

- Never use the em dash (U+2014) in ANY authored text: code comments, C# string
  literals, JSON data, scene-file comments, docs, help/UI copy, commit messages.
  This includes the escaped form (backslash-u-2014) inside JSON and C# strings.
- Do NOT substitute another dash for it. A hyphen, a spaced hyphen ( - ), or an
  en dash is not an acceptable replacement: the sentence gets REWRITTEN instead.
  Pick the connective the sentence actually wants:
  - elaboration or afterthought: end the sentence, start a new one
  - explanation or a list that follows: a colon
  - an aside or appositive: commas, or parentheses when commas would be ambiguous
  - a cross-reference ("X - see Y"): "X (see Y)" or "X. See Y."
  - interrupted or trailing dialogue: a period, a comma, or an ellipsis
- The one exception is a BARE placeholder glyph in UI code, e.g. `Text = "-"` or
  `hasThing ? "yes" : "-"`, where the character is a "no value" marker and not
  prose. A single plain hyphen is correct there.
- The entire codebase (Scripts, Data, Scenes, README) was purged of em dashes on
  2026-08-19, every occurrence rewritten as prose. Do not reintroduce them. Both
  of these must stay empty:
  `grep -rP "\x{2014}" Scripts Data Scenes README.md`
  `grep -r '\\u2014' Scripts Data`
- This rule does NOT apply to box-drawing banner characters, section rules, or
  arrows. Those are different codepoints and remain in use. When a rewrite
  shortens a banner line, re-pad its rule characters to keep the column width.
- Prefer plain, direct phrasing generally. If a sentence only parses because of
  a dash or a pile of clauses, split it.
