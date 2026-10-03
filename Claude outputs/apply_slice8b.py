#!/usr/bin/env python3
# Slice 8b: deferred enemies keep their full planned movement; Grand Design pays Foresight per Defer.
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

edit('Scripts/Systems/Combat/Core/CombatManager.IntentTime.cs', [
('''    private readonly List<(Unit enemy, EnemyIntent intent)> _deferredAttacks = new();
''',
'''    private readonly List<(Unit enemy, EnemyIntent intent)> _deferredAttacks = new();

    /// <summary>The enemy whose activation is running without its blow (Deferred or
    /// Spent): StrikeTile and the melee strike block skip it, everything else in its
    /// normal intent (approach, charge sprint, kiting, flanking, skirmish retreat) runs.</summary>
    private Unit _holdStrikeFor;
'''),
('''        if (intent?.Kind == IntentKind.Attack && intent.TargetTile.HasValue && IsValidActor(enemy)
            && grid.Distance(enemy.CurrentTile.Axial, intent.TargetTile.Value) > 1
            && MayMove(enemy, out _))
        {
            await MoveTowardTile(enemy, intent.TargetTile.Value, quiet: true);
        }
        return true;''',
'''        // 2026-10-03 (slice 8b): the enemy runs its own intent with the blow held, so
        // it moves exactly as it planned to: melee approach and charge, ranged kiting,
        // flanking for a clear shot, a skirmisher's retreat. Before this only a melee
        // attacker out of reach moved, and everyone else stood still, which made a
        // Deferred round look like nothing happened. Channels, imbues and shoves keep
        // their old behaviour: they have no movement step to replay.
        if (intent != null && IsValidActor(enemy)
            && (intent.Kind == IntentKind.Attack || intent.Kind == IntentKind.RangedAttack))
        {
            _holdStrikeFor = enemy;
            try
            {
                if (intent.Kind == IntentKind.Attack)
                    await ExecuteMeleeIntent(enemy, intent);
                else
                    await ExecuteRangedIntent(enemy, intent);
            }
            finally
            {
                _holdStrikeFor = null;
            }
        }
        return true;'''),
])

edit('Scripts/Systems/Combat/Core/CombatManager.EnemyIntents.cs', [
('''            // U2: recompute tag bonuses against the board as the player left it.''',
'''            // Deferred or Spent (Chronomancer): it has closed in, but the blow is held.
            if (enemy == _holdStrikeFor)
                return;

            // U2: recompute tag bonuses against the board as the player left it.'''),
('''    private async Task StrikeTile(Unit attacker, Vector2I tile, int damage, bool ranged, string label = null)
    {
''',
'''    private async Task StrikeTile(Unit attacker, Vector2I tile, int damage, bool ranged, string label = null)
    {
        // Deferred or Spent (Chronomancer): the activation runs, the blow does not.
        if (attacker != null && attacker == _holdStrikeFor)
            return;
'''),
])

edit('Scripts/Cards/Effects/IntentTimeEffects.cs', [
('''    public bool NearDecoy;
    public DeferIntentEffect(int radius = 0) { Radius = Math.Max(0, radius); }''',
'''    public bool NearDecoy;
    /// <summary>Foresight gained per attack actually Deferred (Grand Design).</summary>
    public int ForesightPer;
    public DeferIntentEffect(int radius = 0) { Radius = Math.Max(0, radius); }'''),
('''        foreach (var enemy in victims)
        {
            string why = IntentTime.Defer(enemy);
            if (why != null)
                s?.Log($"[Defer] {enemy.Name}: {why}");
        }
    }
}

/// <summary>
/// Advance target enemy's attack''',
'''        int deferred = 0;
        foreach (var enemy in victims)
        {
            string why = IntentTime.Defer(enemy);
            if (why != null)
                s?.Log($"[Defer] {enemy.Name}: {why}");
            else
                deferred++;
        }

        if (ForesightPer > 0 && deferred > 0 && casterUnit?.Attunement is FateAttunement fate)
        {
            fate.GainCharges(ForesightPer * deferred);
            s.Log($"[Defer] {deferred} attack(s) deferred: +{ForesightPer * deferred} Foresight.");
        }
    }
}

/// <summary>
/// Advance target enemy's attack'''),
])

edit('Scripts/Cards/Loader/CardScriptRegistry.Chronomancer.cs', [
('''            eff.NearDecoy = n.TryGetProperty("near_decoy", out var nd) && nd.GetBoolean();''',
'''            eff.NearDecoy = n.TryGetProperty("near_decoy", out var nd) && nd.GetBoolean();
            if (n.TryGetProperty("foresight_per", out var fp))
                eff.ForesightPer = fp.GetInt32();'''),
])

# ── Grand Design bottom ──
p = os.path.join(root, 'Data/Cards/chronomancer_grand_design.json')
d = json.load(open(p, encoding='utf-8'))
def ch(field, value): return {'half': 'bottom', 'field': field, 'value': value}
gd = {'type': 'defer_intent', 'radius': 99, 'foresight_per': 1}
def seq(*s): return {'type': 'sequence', 'steps': list(s)}
def draw(n): return {'type': 'draw', 'count': n}
BASE = ('Defer every enemy attack: they all move first, then every attack lands on its locked tile '
        'at the end of the round. Gain 1 Foresight for each attack Deferred.')
d['bottom']['effect'] = gd
d['bottom']['rules_text'] = BASE
rungs = {
    1: (None, [ch('effect', seq(gd, draw(1))), ch('rules_text', BASE + ' Draw 1 card.')]),
    2: ('Cheaper.', [ch('mana', 3), ch('effect', seq(gd, draw(1))), ch('rules_text', BASE + ' Draw 1 card.')]),
    3: ('A deeper read.', [ch('mana', 3), ch('effect', seq(gd, draw(2))), ch('rules_text', BASE + ' Draw 2 cards.')]),
    4: ('The design closes.', [ch('mana', 2), ch('effect', seq(gd, draw(2))), ch('rules_text', BASE + ' Draw 2 cards.')]),
}
for u in d['upgrades']:
    if u['tier'] in rungs and u['half'] in (('both',) if u['tier'] == 1 else ('bottom',)):
        desc, changes = rungs[u['tier']]
        u['changes'] = [c for c in u['changes'] if c['half'] != 'bottom'] + changes
        if desc: u['description'] = desc
open(p, 'w', encoding='utf-8').write(json.dumps(d, indent='\t', ensure_ascii=False) + '\n')
print("ok Data/Cards/chronomancer_grand_design.json")
print("slice 8b applied")
