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

edit('Scripts/Systems/Combat/Core/Unit.cs', [(
'''    public EnemyIntent CurrentIntent;
''',
'''    public EnemyIntent CurrentIntent;

    /// <summary>Whether <see cref="CurrentIntent"/>'s attack was moved in time this round
    /// (class_identity_chronomancer_v1 §2a: Defer, Advance). Reset when intents are planned.</summary>
    public IntentTiming AttackTiming = IntentTiming.Normal;
''')])

edit('Scripts/Systems/Combat/Core/CombatManager.cs', [
(
'''        InstallReactions();   // Edge M4: walk-step hook and routing cost
''',
'''        InstallReactions();   // Edge M4: walk-step hook and routing cost
        InstallIntentTime();  // Chronomancer Defer / Advance hooks
'''),
(
'''        UninstallZoneOfControl();
        UninstallReactions();
    }
''',
'''        UninstallZoneOfControl();
        UninstallReactions();
        UninstallIntentTime();
    }
'''),
])

edit('Scripts/Systems/Combat/Core/CombatManager.EnemyIntents.cs', [
# Fresh plans start untimed.
(
'''            enemy.CurrentIntent = PlanIntent(enemy);

            if (enemy.CurrentIntent != null)
                enemy.CurrentIntent.Revealed = enemy.IntentPermanentlyRevealed;
''',
'''            enemy.AttackTiming = IntentTiming.Normal;   // a new plan is a new attack (Defer/Advance)
            enemy.CurrentIntent = PlanIntent(enemy);

            if (enemy.CurrentIntent != null)
                enemy.CurrentIntent.Revealed = enemy.IntentPermanentlyRevealed;
'''),
# Activation: a timed attack moves without striking.
(
'''            await ExecuteIntent(enemy);

            // Edge M4: a beat that never strikes (Guard, Shove, a channel start)
''',
'''            // Chronomancer Defer / Advance (CombatManager.IntentTime.cs): an attack
            // moved in time is not struck here; the enemy still moves.
            _actingEnemy = enemy;
            if (!await RunTimedActivation(enemy))
                await ExecuteIntent(enemy);

            // Edge M4: a beat that never strikes (Guard, Shove, a channel start)
'''),
# Overdraw's second plan is a new, untimed attack.
(
'''                    enemy.CurrentIntent = PlanIntent(enemy);
                    if (enemy.CurrentIntent != null)
                        enemy.CurrentIntent.Revealed = true;   // never a hidden bonus turn
''',
'''                    enemy.AttackTiming = IntentTiming.Normal;   // the second beat is a fresh attack
                    enemy.CurrentIntent = PlanIntent(enemy);
                    if (enemy.CurrentIntent != null)
                        enemy.CurrentIntent.Revealed = true;   // never a hidden bonus turn
'''),
# End of round: deferred attacks land.
(
'''        GD.Print("=== Enemy Turn End ===");
        enemyPhaseRunning = false;
''',
'''        // Chronomancer Defer: every deferred attack lands now, after all activations,
        // before the round's end ticks and the next plans.
        _actingEnemy = null;
        if (await ResolveDeferredAttacksAsync())
            return;

        GD.Print("=== Enemy Turn End ===");
        enemyPhaseRunning = false;
'''),
# Nameplate says DEFERRED / SPENT.
(
'''        var reaction = PredictIntentReaction(intent);
        if (reaction != ElementReaction.None)
            body += $"\\nFORMS {ElementReactions.DisplayName(reaction).ToUpperInvariant()}";

        // The marker line is reference text, not a glyph, so shrink it and two lines
        // don't swallow the board.
        int size = !string.IsNullOrEmpty(markers) ? 24 : reaction != ElementReaction.None ? 30 : 40;
''',
'''        var reaction = enemy.AttackTiming == IntentTiming.Spent ? ElementReaction.None : PredictIntentReaction(intent);
        if (reaction != ElementReaction.None)
            body += $"\\nFORMS {ElementReactions.DisplayName(reaction).ToUpperInvariant()}";

        // class_identity_chronomancer_v1 §2a: an attack moved in time says so.
        bool timed = enemy.AttackTiming != IntentTiming.Normal;
        if (enemy.AttackTiming == IntentTiming.Deferred)
            body += "\\nDEFERRED";
        else if (enemy.AttackTiming == IntentTiming.Spent)
            body += "\\nSPENT";

        // The marker line is reference text, not a glyph, so shrink it and two lines
        // don't swallow the board.
        int size = !string.IsNullOrEmpty(markers) ? 24 : (reaction != ElementReaction.None || timed) ? 30 : 40;
'''),
# A spent attack no longer threatens its tile; a deferred one still does.
(
'''            bool revealed = enemy.CurrentIntent.Revealed;
''',
'''            if (enemy.AttackTiming == IntentTiming.Spent)
                continue;   // Advance: that blow has already landed
            bool revealed = enemy.CurrentIntent.Revealed;
'''),
])

edit('Scripts/Cards/Loader/CardScriptRegistry.Chronomancer.cs', [(
'''        // Skip the next N turns of the enemy, causing them to lose their next N actions (can be used on self for a "stasis" effect)
''',
'''        // Defer target enemy's attack: it lands at the end of the round on its locked tile.
        // radius widens it to every enemy within n of the target (99 = every enemy).
        // { "type": "defer_intent", "radius": n }
        RegisterEffect("defer_intent", n =>
        {
            int radius = n.TryGetProperty("radius", out var r) ? r.GetInt32() : 0;
            return new DeferIntentEffect(radius).WithTag("Control");
        });

        // Advance target enemy's attack: it lands now on its locked tile.
        // { "type": "advance_intent", "radius": n }
        RegisterEffect("advance_intent", n =>
        {
            int radius = n.TryGetProperty("radius", out var r) ? r.GetInt32() : 0;
            return new AdvanceIntentEffect(radius).WithTag("Control");
        });

        // Skip the next N turns of the enemy, causing them to lose their next N actions (can be used on self for a "stasis" effect)
''')])

edit('Schemas/card.schema.json', [(
'''						"retarget",
''',
'''						"retarget",
						"defer_intent",
						"advance_intent",
''')])

# Register: first Defer, first Advance.
p = os.path.join(root, 'Data/Register/barks.json')
data = json.load(open(p, encoding='utf-8'))
keys = [b['key'] for b in data['barks']]
assert 'chrono.defer' not in keys
at = keys.index('strategic.first_council')
data['barks'][at:at] = [
    {
        "key": "chrono.defer",
        "category": "explain",
        "once": True,
        "title": "Deferred",
        "lines": ["An attack deferred is not an attack excused. It will arrive. Stand elsewhere."],
        "note": "A deferred attack is lifted out of the enemy's turn. The enemy still moves, but the blow lands at the end of the round, after every enemy has acted, on the tile it was aimed at. Whoever stands there then is hit: you, nobody, or another enemy. If the attacker dies first, the attack is lost."
    },
    {
        "key": "chrono.advance",
        "category": "explain",
        "once": True,
        "title": "Advanced",
        "lines": ["Moved up on the docket. The court will hear it... now."],
        "note": "An advanced attack lands immediately, on the tile it was aimed at, and the enemy takes its turn without it. Clear the tile first and the blow falls on empty ground."
    },
]
with open(p, 'w', encoding='utf-8') as f:
    f.write(json.dumps(data, indent=2, ensure_ascii=False) + '\n')
print("ok Data/Register/barks.json")
