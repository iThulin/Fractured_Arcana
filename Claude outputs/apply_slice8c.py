#!/usr/bin/env python3
# Slice 8c: choose-one modal layout, debug-deck attunement, visible deferred strikes,
# honest Reflex texts (Blink Between, Ward of Hours ladder, Stolen Moment bottom).
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

# ── 1. Choose-one modal: the text block had no width, so every word wrapped ──
edit('Scripts/Systems/Combat/Core/CombatManager.CardChoice.cs', [
('''            var box = new VBoxContainer { Position = new Vector2(Pad, Pad) };
            box.AddThemeConstantOverride("separation", 4);
            holder.AddChild(box);
            var name = new Label { Text = card.CardName ?? "(card)", Modulate = UITheme.TextPrimary };
            name.AddThemeFontSizeOverride("font_size", UITheme.FontSizeNormal);
            name.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            box.AddChild(name);
            AddHalfBlock(box, card.TopHalf);
            AddHalfBlock(box, card.BottomHalf);''',
'''            // 2026-10-03: the box sits in a plain Panel (not a container), so it got no
            // width and every autowrapped label collapsed to one word per line. Give it
            // the holder's inner width explicitly.
            var box = new VBoxContainer
            {
                Position = new Vector2(Pad, Pad),
                CustomMinimumSize = new Vector2(w, 0),
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            box.Size = new Vector2(w, h);
            box.AddThemeConstantOverride("separation", 6);
            holder.AddChild(box);
            var name = new Label { Text = card.CardName ?? "(card)", Modulate = UITheme.Gold };
            name.AddThemeFontSizeOverride("font_size", UITheme.FontSizeNormal + 2);
            name.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            name.CustomMinimumSize = new Vector2(w, 0);
            box.AddChild(name);
            if (_activeChoice?.SyntheticOptions == true)
            {
                // A mode, not a card: the label is the name, so only the description follows.
                string desc = card.TopHalf?.RulesText ?? "";
                if (!string.IsNullOrEmpty(desc))
                {
                    var rules = new Label { Text = desc, Modulate = UITheme.TextPrimary };
                    rules.AddThemeFontSizeOverride("font_size", UITheme.FontSizeNormal);
                    rules.AutowrapMode = TextServer.AutowrapMode.WordSmart;
                    rules.CustomMinimumSize = new Vector2(w, 0);
                    box.AddChild(rules);
                }
            }
            else
            {
                AddHalfBlock(box, card.TopHalf);
                AddHalfBlock(box, card.BottomHalf);
            }'''),
])

# ── 2. Deferred attacks: point the camera at the blow and give it time to land ──
edit('Scripts/Systems/Combat/Core/CombatManager.IntentTime.cs', [
('''            CombatCamera?.FocusOn(enemy);

            ResolveTimedAttack(enemy, intent);
            await DrainTriggerStackAsync();
            RefreshSelectedUnitUI();
            RefreshEnemyRoster();
            RefreshPlayerUnitBar();
            if (CheckCombatEnd())
                return true;
            await ToSignal(GetTree().CreateTimer(0.35f), "timeout");''',
'''            // 2026-10-03: the camera used to start gliding to the attacker and the blow
            // resolved the same frame, so the player never saw it land. Frame the attacker
            // AND its locked tile, let the glide arrive, strike, then hold on the result.
            var lockedAt = (intent.Kind == IntentKind.Release ? enemy.ChannelTile : null) ?? intent.TargetTile;
            var lockedView = lockedAt.HasValue ? grid.GetTileView(lockedAt.Value) : null;
            if (CombatCamera != null)
            {
                if (lockedView != null)
                    CombatCamera.FocusOn((enemy.GlobalPosition + lockedView.GlobalPosition) * 0.5f);
                else
                    CombatCamera.FocusOn(enemy);
            }
            await ToSignal(GetTree().CreateTimer(0.6f), "timeout");
            if (!IsValidActor(enemy))
                continue;

            ResolveTimedAttack(enemy, intent);
            await DrainTriggerStackAsync();
            RefreshSelectedUnitUI();
            RefreshEnemyRoster();
            RefreshPlayerUnitBar();
            if (CheckCombatEnd())
                return true;
            await ToSignal(GetTree().CreateTimer(0.8f), "timeout");'''),
])

# ── 3. Debug deck: the wizard's attunement follows the deck it actually carries ──
edit('Scripts/Systems/Combat/Core/CombatManager.cs', [
('''                unit.DeckData.Initialize(cards);
                injectedCompanionCards = true;
''',
'''                unit.DeckData.Initialize(cards);
                injectedCompanionCards = true;
                if (PlayerDeckSave.UseDebugDeck)
                    MatchDebugAttunement(unit, cards);
'''),
('''    private void SelectNextLivingAfterDeath()
    {''',
'''    /// <summary>Debug fights (2026-10-03): a debug deck is often another school's cards
    /// on a wizard whose launcher school was left at its default. Its school engine
    /// (Time Bank, Grief, Weave, ...) then never existed, so every Foresight card
    /// silently did nothing and no bank was drawn. When one school holds at least half
    /// of the deck's school cards and differs from the wizard's, the wizard plays as
    /// that school for this fight: attunement and attunement UI included.</summary>
    private static void MatchDebugAttunement(Unit unit, List<Card> cards)
    {
        if (unit == null || cards == null)
            return;
        var counts = new Dictionary<CardSchool, int>();
        foreach (var c in cards)
        {
            var school = c?.TopHalf?.School ?? CardSchool.Adept;
            if (school == CardSchool.Adept)
                continue;
            counts[school] = counts.GetValueOrDefault(school) + 1;
        }
        if (counts.Count == 0)
            return;
        var top = counts.OrderByDescending(kv => kv.Value).First();
        int total = counts.Values.Sum();
        if (top.Key == unit.School || top.Value * 2 < total)
            return;
        GD.Print($"[DebugDeck] {unit.Name}'s debug deck is mostly {top.Key} ({top.Value}/{total} school cards); "
                 + $"playing as {top.Key} instead of {unit.School} for this fight.");
        unit.School = top.Key;
        unit.InitializeAttunement();
    }

    private void SelectNextLivingAfterDeath()
    {'''),
])

# ── 4. Reflex texts that promised a trigger the card does not have ──
def fix(name, base_text=None, rung_texts=None, rung_effects=None, base_effect=None, rung_targeting=None):
    p = os.path.join(root, 'Data/Cards', name + '.json')
    d = json.load(open(p, encoding='utf-8'))
    if base_text: d['bottom']['rules_text'] = base_text
    if base_effect: d['bottom']['effect'] = base_effect
    for u in d['upgrades']:
        t = u['tier']
        if u['half'] not in ('both', 'bottom'):
            continue
        for c in u['changes']:
            if c['half'] != 'bottom':
                continue
            if c['field'] == 'rules_text' and rung_texts and t in rung_texts:
                c['value'] = rung_texts[t]
            if c['field'] == 'effect' and rung_effects and t in rung_effects:
                c['value'] = rung_effects[t]
            if c['field'] == 'targeting' and rung_targeting and t in rung_targeting:
                c['value'] = rung_targeting[t]
    open(p, 'w', encoding='utf-8').write(json.dumps(d, indent='\t', ensure_ascii=False) + '\n')
    print('ok', p)

fix('chronomancer_misdirection',
    base_text='Teleport to an adjacent empty tile and gain 2 shield.',
    rung_texts={1: 'Teleport to an empty tile within 1 and gain 4 shield.',
                2: 'Teleport to an empty tile within 2 and gain 4 shield.'})
fix('chronomancer_stolen_moment',
    base_text='Teleport to an empty tile within 2.',
    rung_texts={2: 'Teleport to an empty tile within 3 and gain 2 shield.',
                4: 'Free. Teleport to an empty tile within 3 and gain 2 shield.'})
# Ward of Hours: shields only ever go on the caster, and "the attacker takes 3" dealt 3 to YOU.
seq = lambda *s: {'type': 'sequence', 'steps': list(s)}
sh = lambda n: {'type': 'shield', 'amount': n}
gf = lambda n: {'type': 'gain_foresight', 'amount': n}
fix('chronomancer_glimpse',
    base_text='Gain 6 shield until your next turn.',
    rung_texts={1: 'Gain 8 shield until your next turn.',
                2: 'Gain 8 shield until your next turn. Gain 1 Foresight.',
                3: 'Gain 10 shield until your next turn. Gain 1 Foresight.',
                4: 'Gain 12 shield until your next turn. Gain 2 Foresight.'},
    rung_effects={2: seq(sh(8), gf(1)), 3: seq(sh(10), gf(1)), 4: seq(sh(12), gf(2))},
    rung_targeting={4: {'type': 'self'}})
# Tier 3 and 4 Ward rungs changed only effect.amount / targeting; give them full effects.
p = os.path.join(root, 'Data/Cards/chronomancer_glimpse.json')
d = json.load(open(p, encoding='utf-8'))
for u in d['upgrades']:
    if u['half'] == 'bottom' and u['tier'] in (3, 4):
        u['changes'] = [c for c in u['changes'] if c['field'] not in ('effect', 'effect.amount')]
        u['changes'].insert(0, {'half': 'bottom', 'field': 'effect',
                                'value': seq(sh(10), gf(1)) if u['tier'] == 3 else seq(sh(12), gf(2))})
        if u['tier'] == 3: u['description'] = 'A thicker shell, and a little time banked.'
        if u['tier'] == 4: u['description'] = 'The thickest shell.'
    if u['half'] == 'bottom' and u['tier'] == 2:
        u['description'] = 'Sturdier, and a little time banked.'
open(p, 'w', encoding='utf-8').write(json.dumps(d, indent='\t', ensure_ascii=False) + '\n')
print('ok glimpse ward ladder')
print('slice 8c applied')
