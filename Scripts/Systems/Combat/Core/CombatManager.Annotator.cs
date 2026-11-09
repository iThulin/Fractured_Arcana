using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

// ============================================================
// CombatManager.Annotator.cs  (partial of CombatManager)
//
// Purpose:        The enemy side of the Arcanist's Annotator cards
//                 (class_identity_arcanist_v1 §3C, slice 14b):
//                 • Strike the Clause: a struck enemy's next attack or spell
//                   lands without its rider (no status, no push, no imbue)
//                 • Red Ink and Blot: an enemy spell's damage is cut as it
//                   lands; Red Ink pays the cut back as Charge
//                 • Purloin: an enemy caster's telegraphed spell is taken
//                   whole and handed to the player's effect
//                 "An enemy spell" is a Channel, Release or Imbue intent (§5).
// Layer:          System (combat glue)
// Collaborators:  ArcanistAnnotatorEffects.cs, CombatManager.EnemyIntents.cs
// ============================================================
public partial class CombatManager
{
    private void InstallAnnotatorHooks()
    {
        IntentTime.PurloinHook = StealSpell;
    }

    /// <summary>True when the enemy's next attack or spell loses its rider.</summary>
    private static bool ClauseStruck(Unit enemy) =>
        enemy != null && IsInstanceValid(enemy) && enemy.HasStatus("clause_struck");

    /// <summary>Called after an intent executes: an attack or spell spends the struck clause
    /// (a Channel start does not; its rider comes at the Release).</summary>
    private void SpendClause(Unit enemy, IntentKind kind)
    {
        if (!ClauseStruck(enemy))
            return;
        if (kind is IntentKind.Attack or IntentKind.RangedAttack or IntentKind.Release
                 or IntentKind.Imbue or IntentKind.Shove)
        {
            enemy.RemoveStatus("clause_struck");
            enemy.RefreshHealthBar();
        }
    }

    private void LogClause(Unit enemy, string what)
    {
        string msg = $"The clause is struck: {enemy.Name}'s {what} never happens.";
        GD.Print(msg);
        combatUI?.AppendActionLog(msg);
    }

    /// <summary>An enemy spell is about to land on <paramref name="tile"/> for
    /// <paramref name="value"/>: a Blot on the caster, then a ready Red Ink within 3 of
    /// the tile, cut it. Returns the damage that lands.</summary>
    private int AnnotateEnemySpell(Unit enemy, Vector2I tile, int value)
    {
        if (enemy != null && IsInstanceValid(enemy) && enemy.NextSpellPenalty > 0)
        {
            int cut = Math.Min(value, enemy.NextSpellPenalty);
            value -= cut;
            enemy.NextSpellPenalty = 0;
            enemy.RemoveStatus("ink_blot");
            enemy.RefreshHealthBar();
            combatUI?.AppendActionLog($"The blot runs: {enemy.Name}'s spell deals {cut} less.");
        }

        foreach (var p in playerUnits)
        {
            if (value <= 0)
                break;
            if (p == null || !IsInstanceValid(p) || !p.Stats.IsAlive || p.CurrentTile == null
                || !p.HasStatus("red_ink") || p.RedInkAmount <= 0)
                continue;
            if (grid.Distance(p.CurrentTile.Axial, tile) > 3)
                continue;
            int cut = Math.Min(value, p.RedInkAmount);
            value -= cut;
            p.RemoveStatus("red_ink");
            p.RedInkAmount = 0;
            int got = p.Attunement is ArcaneAttunement arc ? arc.Add(cut) : 0;
            p.RefreshHealthBar();
            schoolAttunementUI?.Refresh();
            combatUI?.AppendActionLog($"{p.Name} corrects {enemy?.Name ?? "the spell"} in red ink: {cut} less damage, +{got} Charge.");
        }
        return value;
    }

    /// <summary>Purloin's seam: takes the enemy's telegraphed spell. The enemy loses it (a
    /// charging channel is broken off) and does nothing this activation.</summary>
    private StolenSpell StealSpell(Unit enemy, out string error)
    {
        error = null;
        if (!IsValidActor(enemy))
        { error = "it cannot act"; return null; }
        if (enemy == _actingEnemy)
        { error = "its spell is already being cast"; return null; }
        if (IsRitualist(enemy) || IsWarpChanneler(enemy))
        { error = "that working is too large to carry off"; return null; }
        var intent = enemy.CurrentIntent;
        if (intent == null || intent.Kind is not (IntentKind.Channel or IntentKind.Release or IntentKind.Imbue))
        { error = "it has no spell telegraphed"; return null; }

        var aim = enemy.ChannelTile ?? intent.TargetTile ?? enemy.CurrentTile?.Axial ?? Vector2I.Zero;
        var spell = new StolenSpell
        {
            Name = intent.Kind == IntentKind.Imbue ? $"{intent.ImbueElement} imbue" : "charged blast",
            Damage = intent.Kind == IntentKind.Imbue ? 0 : Math.Max(0, intent.Value),
            Element = intent.Kind == IntentKind.Imbue ? intent.ImbueElement : TileElementType.None,
            Offsets = (intent.ThreatTiles != null && intent.ThreatTiles.Count > 0
                ? intent.ThreatTiles.Select(t => t - aim) : new[] { Vector2I.Zero }).Distinct().ToList(),
        };
        if (intent.Kind != IntentKind.Imbue)
            (spell.Status, spell.StatusTurns) = StolenRider(enemy.CasterSpell);

        // The enemy loses it whole: no channel, no release, nothing this activation.
        enemy.ChannelTile = null;
        enemy.RemoveStatus("wizard_charging");
        enemy.ChannelDelayRemaining = 0;
        enemy.CurrentIntent = new EnemyIntent { Kind = IntentKind.Unknown, Revealed = true };
        UpdateIntentDisplay(enemy);
        RefreshThreatTiles();
        combatUI?.AppendActionLog($"{enemy.Name}'s {spell.Name} is purloined. It has nothing left to cast.");
        return spell;
    }

    /// <summary>The player-side form of a caster school's release rider (ApplyCasterRider).</summary>
    private static (string status, int turns) StolenRider(string casterSpell) => (casterSpell ?? "") switch
    {
        "ember" => ("burn", 2),
        "chrono" => ("slowed", 2),
        "grave" => ("poisoned", 1),
        "thorn" => ("rooted", 1),
        "mind" => ("stunned", 1),
        "geas" => ("bleed", 2),
        "arclance" or "forge" => (null, 0),
        _ => ("slowed", 1),
    };
}
