#!/usr/bin/env python3
# Slice 7d: round 1 plans enemy intents (Glimpse / reveals had nothing to show on turn 1).
import sys, os
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

edit('Scripts/Systems/Combat/Core/CombatManager.cs', [
('''    private void StartPlayerTurn()
    {''',
'''    /// <summary>True once round 1's intents have been locked (see StartPlayerTurn).</summary>
    private bool _openingIntentsPlanned;

    private void StartPlayerTurn()
    {'''),
('''        RegisterManager.Fire("combat.basics.move");
''',
'''        RegisterManager.Fire("combat.basics.move");

        // Round 1 intents (2026-10-03). Plans are locked at the end of each enemy
        // phase, plus once in StartDeploymentPhase. But enemies now spawn AFTER
        // deployment (reactive spawn, SpawnAndPlaceEnemies on confirm), and the
        // skip-deploy path never planned at all, so turn 1 had no intents: nothing
        // to read, nothing for Glimpse or Survey to reveal, and the enemy phase
        // improvised. Plan once here for any round-1 enemy still without a plan.
        if (!_openingIntentsPlanned)
        {
            _openingIntentsPlanned = true;
            bool anyUnplanned = enemyUnits.Exists(e => IsValidActor(e) && e.CurrentIntent == null);
            if (anyUnplanned)
            {
                try
                {
                    PlanAllEnemyIntents();
                    GD.Print("[Intents] Round 1: planned opening intents.");
                }
                catch (Exception e)
                {
                    GD.Print($"[Intents] Round 1 planning THREW: {e}");
                }
            }
        }
'''),
])
print("slice 7d applied")
