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

## Player-facing text (mandatory, see docs/voice_and_terms_v1.md)

- Every player-facing string is one of three kinds. Instruction (buttons, labels,
  tooltips, dialogs, toasts, fight and action results, errors, tutorial steps,
  quest objectives) is plain: say what happened, what it changes, and what to do,
  with numbers, addressed to "you", with no metaphor and nothing acting that
  cannot act. Rules text (cards, items, buildings, spells) follows
  docs/card_text_style_guide_v1.md. Story (the Register's remarks, encounters,
  the chronicle, lore) keeps its voice.
- Story never carries the only copy of a rule, and a choice states its cost or
  effect in plain words after its label.
- One name per thing in instruction text: timeline (not reading or cycle), moon
  (not lunation), unlock and exam (Declaration and Practical survive only as the
  proper names of the ceremony and the fight, with the plain word beside them),
  doctrine (not charter), expedition, assignment.
- A theme word may stay as a proper noun with a plain gloss beside it. It is
  never a bare verb or status the player has to decode.
- A disabled button shows its reason in visible text, not only in a tooltip.
