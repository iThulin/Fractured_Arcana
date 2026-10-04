#!/usr/bin/env python3
# Slice 10b: the Once keyword (chip on the card face, "Used" once spent) + a Register tip.
import sys, os, json
root = sys.argv[1]
def edit(rel, pairs):
    p = os.path.join(root, rel)
    s = open(p, encoding='utf-8').read()
    for old, new in pairs:
        n = s.count(old)
        assert n == 1, f"{rel}: expected 1 match, got {n} for:\n{old}"
        s = s.replace(old, new)
    open(p, 'w', encoding='utf-8').write(s)
    print("ok", rel)

edit('Scripts/Cards/CardUi.cs', [
('''        // Speed badge at the far right of the row. The split-view row is a plain
        // HBox, so an expanding spacer pushes it over; the full view's row is
        // already right-aligned and needs none.''',
'''        // Once (2026-10-04): a fight-long half can be cast once per fight. Rendered
        // from the same flag the rules check (CardHalf.OncePerFight), so the card
        // face and the rule cannot disagree. Reads "Used" after it has been cast.
        if (half != null && half.OncePerFight)
        {
            bool used = HalfSpentProvider != null && HalfSpentProvider(half);
            var once = MakeChip(used ? "Used" : "Once", used ? UITheme.TextDim : UITheme.Gold, OnceTooltip, onDark);
            once.Name = "OnceChip";
            container.AddChild(once);
        }

        // Speed badge at the far right of the row. The split-view row is a plain
        // HBox, so an expanding spacer pushes it over; the full view's row is
        // already right-aligned and needs none.'''),
('''    private static void CompactRowIfOverflowing(HBoxContainer row)
    {''',
'''    private const string OnceTooltip =
        "Once: this half can be cast once per fight. Its effect lasts the rest of the fight, "
        + "and every copy of this card shares the limit.";

    /// <summary>Flips an existing Once chip to "Used" (or back) without rebuilding the row.</summary>
    private void RefreshOnceChip(HBoxContainer row, CardHalf half)
    {
        if (row == null || half == null || !half.OncePerFight)
            return;
        if (row.GetNodeOrNull<Label>("OnceChip") is not Label chip)
            return;
        bool used = HalfSpentProvider != null && HalfSpentProvider(half);
        string text = used ? "Used" : "Once";
        if (chip.Text == text)
            return;
        chip.Text = text;
        var tint = used ? UITheme.TextDim : UITheme.Gold;
        if (chip.GetThemeStylebox("normal") is StyleBoxFlat old)
        {
            var st = (StyleBoxFlat)old.Duplicate();
            var fill = tint;
            fill.A = UITheme.CardChipFillAlpha;
            st.BgColor = fill;
            st.BorderColor = tint;
            chip.AddThemeStyleboxOverride("normal", st);
        }
    }

    private static void CompactRowIfOverflowing(HBoxContainer row)
    {'''),
('''        _lastKnownMana = currentMana;
''',
'''        _lastKnownMana = currentMana;
        RefreshOnceChip(_topElementTags, TopHalf);
        RefreshOnceChip(_botElementTags, BottomHalf);
'''),
])

edit('Scripts/Systems/Combat/Core/RulesManager.cs', [
('''        if (a is CardHalf spent && spent.OncePerFight)
            s.OncePerFightSpent.Add(GameState.OncePerFightKey(spent));''',
'''        if (a is CardHalf spent && spent.OncePerFight)
        {
            s.OncePerFightSpent.Add(GameState.OncePerFightKey(spent));
            RegisterManager.Fire("card.once");
        }'''),
])

p = os.path.join(root, 'Data/Register/barks.json')
d = json.load(open(p, encoding='utf-8'))
assert not any(b.get('key') == 'card.once' for b in d['barks'])
d['barks'].append({
    'key': 'card.once',
    'category': 'explain',
    'once': True,
    'title': 'Once',
    'lines': ['Some workings are written into the hour itself. You do not write them twice.'],
    'note': ('A half marked Once can be cast one time per fight. Its effect lasts the rest of the fight, '
             'so every copy of that card now shows the half as Used.'),
})
# barks.json is authored with 2-space indent (slice 7 wrote tabs; restore the house style)
open(p, 'w', encoding='utf-8').write(json.dumps(d, indent=2, ensure_ascii=False) + '\n')
print('ok', p)
print('slice 10b applied')
