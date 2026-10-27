using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

// ============================================================
// CombatManager.Scripted.cs
//
// Purpose:        The engine hooks the prologue's scripted fights need
//                 (docs/prologue_script_v1.md, section 7, C4 item 6):
//                 the scripted deck (hook a), the deck's school and the
//                 Adept's mana (hook b), and the fight that ends when a
//                 named unit falls (hook d). Also the wizard's health
//                 at the end of a fight, which the duel's clean verdict
//                 reads, and the switch that keeps a scripted fight from
//                 paying out (P-1: no rewards, mastery or honored dead).
//                 A scripted fight also skips the two pre-fight choices:
//                 the opening-hand sculpt (its hand is authored) and
//                 deployment (the party stands where the story puts it).
// Layer:          Combat / runtime
// Collaborators:  PlayerSession (ScriptedDeck, ScriptedInnate,
//                 ScriptedSchool, ScriptedWizardOnly), EncounterRouter
//                 (FallenEnemyUnitIds, EndOnUnitFall, SuppressRewards,
//                 LastWizardHealth), PrologueDirector.cs (sets them),
//                 CombatManager.cs (InitializeUnitDecks, HandleUnitDeath,
//                 DeclareVictory, DeclareDefeat, the opening-hand sculpt,
//                 StartDeploymentPhase),
//                 CombatManager.Objectives.cs (the victory latch)
// ============================================================

public partial class CombatManager
{
    /// <summary>A scripted fight (the prologue): it grants nothing, so casts are not counted
    /// toward mastery and the fallen are not kept as honored dead.</summary>
    private static bool ScriptedFight =>
        EncounterRouter.Instance != null && EncounterRouter.Instance.SuppressRewards;

    /// <summary>Hook a: the wizard's deck for a scripted fight, one card per id in
    /// PlayerSession.ScriptedDeck, with each ScriptedInnate entry flagged on one matching
    /// instance, so the opening hand is exactly those. Companion cards and the staff's bound
    /// card are left out on purpose. Null when no scripted deck is set.</summary>
    private static List<Card> BuildScriptedDeck() =>
        BuildScriptedDeck(PlayerSession.ScriptedDeck, PlayerSession.ScriptedInnate);

    /// <summary>The scripted deck from explicit lists: the fight's builder, and the one the
    /// prologue's round-trip assert checks (PrologueDirector.AssertRoundTrip), so the assert
    /// tests the deck the fight will really deal.</summary>
    internal static List<Card> BuildScriptedDeck(IReadOnlyList<string> ids, IReadOnlyList<string> innate)
    {
        if (ids == null || ids.Count == 0)
            return null;
        var cards = new List<Card>();
        foreach (var id in ids)
        {
            var bp = CardDatabase.Blueprints.Find(b => b.Id == id);
            if (bp == null)
            {
                GD.PushError($"[ScriptedDeck] '{id}' is not in the card database.");
                continue;
            }
            var card = CardDatabase.Instantiate(bp);
            if (card != null)
                cards.Add(card);
        }
        if (innate != null)
            foreach (var id in innate)
            {
                var match = cards.Find(c => c != null && !c.Innate && c.BlueprintId == id);
                if (match != null)
                    match.Innate = true;
                else
                    GD.PushWarning($"[ScriptedDeck] Innate '{id}' has no matching card in the deck.");
            }
        return cards.Count > 0 ? cards : null;
    }

    /// <summary>Hook b (was MatchDebugAttunement): the wizard plays the deck's school for this
    /// fight, attunement and attunement UI included. A scripted deck names its school
    /// (<paramref name="forced"/>). A debug deck plays the school that holds at least half of
    /// its school cards, when that differs from the wizard's: a debug deck is often another
    /// school's cards on a wizard whose launcher school was left at its default, and its
    /// school engine (Time Bank, Grief, Weave, ...) would otherwise never exist.
    ///
    /// <para>The Adept's extra mana was granted at spawn from the save's school, before decks
    /// exist, so it goes when the switch moves the wizard off Adept and comes when it moves the
    /// wizard onto Adept.</para></summary>
    private static void MatchDeckAttunement(Unit unit, List<Card> cards, CardSchool? forced = null)
    {
        if (unit == null)
            return;
        CardSchool target;
        if (forced.HasValue)
        {
            target = forced.Value;
        }
        else
        {
            if (cards == null)
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
            if (top.Value * 2 < total)
                return;
            target = top.Key;
        }
        if (target == unit.School)
            return;

        bool wasAdept = unit.School == CardSchool.Adept;
        bool nowAdept = target == CardSchool.Adept;
        if (wasAdept && !nowAdept)
        {
            unit.Stats.MaxMana = Math.Max(0, unit.Stats.MaxMana - 1);
            unit.Stats.Mana = Math.Min(unit.Stats.Mana, unit.Stats.MaxMana);
        }
        else if (!wasAdept && nowAdept)
        {
            unit.Stats.MaxMana += 1;
            unit.Stats.Mana += 1;
        }
        GD.Print($"[DeckSchool] {unit.Name} plays as {target} instead of {unit.School} for this fight "
                 + $"({(forced.HasValue ? "scripted" : "the debug deck's majority")}); max mana {unit.Stats.MaxMana}.");
        unit.School = target;
        unit.InitializeAttunement();
    }

    /// <summary>Hook d: every enemy that dies is listed on the router by DefinitionId, and a
    /// fight with EndOnUnitFall set is won the moment that unit falls. Latched like every other
    /// objective outcome, so CheckCombatEnd still declares it after the trigger stack settles
    /// (a death trigger resolves first). Called from HandleUnitDeath, after map objects have
    /// taken their own path.</summary>
    private void NoteScriptedUnitFall(Unit unit)
    {
        var router = EncounterRouter.Instance;
        if (router == null || unit == null || unit.TeamId == 0 || unit.IsPlayerControlled)
            return;
        string id = unit.DefinitionId ?? "";
        if (id.Length == 0)
            return;
        router.FallenEnemyUnitIds.Add(id);
        if (id == router.EndOnUnitFall && !_objectiveDefeat && !_objectiveVictory)
        {
            _objectiveVictory = true;
            GD.Print($"[Objective] {unit.Name} has fallen. The scripted fight is won.");
        }
    }

    /// <summary>How long a scripted fight shows its starting positions before deployment
    /// confirms itself. A timer, not a deferred call: StartDeploymentPhase runs inside
    /// SpawnTestUnits, before _Ready queues InitializeUnitDecks, so a deferred confirm would
    /// start round 1 before the decks exist (the skip-deployment path's old "one turn
    /// delay").</summary>
    private const double ScriptedDeployDelay = 0.25;

    /// <summary>Deployment in a scripted fight: the party keeps the tiles it spawned on and the
    /// fight begins, through the same EndDeploymentPhase the confirm button calls (enemies
    /// spawn, the starting tile seeds attunement, round 1 starts). A no-op when the combat is
    /// gone or deployment has already ended.</summary>
    private void AutoConfirmScriptedDeployment()
    {
        if (!IsInstanceValid(this) || !isInDeploymentPhase)
            return;
        GD.Print("[Scripted] Deployment confirmed: the party stands where the story puts it.");
        EndDeploymentPhase();
    }

    /// <summary>Combat start: the fallen list belongs to the fight about to begin. Called from
    /// InitObjectiveState, the one place a fight's objective state is armed.</summary>
    private static void ResetScriptedFightState()
    {
        EncounterRouter.Instance?.FallenEnemyUnitIds.Clear();
    }

    /// <summary>Hand the wizard's health at the end of the fight to the router, for hosts that
    /// read how a fight went (the duel's "won at full health"). -1 when the wizard was not on
    /// the field.</summary>
    private void RecordWizardEndHealth()
    {
        var router = EncounterRouter.Instance;
        if (router == null)
            return;
        var wizard = playerUnits.Find(u => u != null && IsInstanceValid(u) && u.CompanionId == "wizard");
        router.LastWizardHealth = wizard != null ? wizard.Stats.Health : -1;
        router.LastWizardMaxHealth = wizard != null ? wizard.Stats.MaxHealth : -1;
    }
}
