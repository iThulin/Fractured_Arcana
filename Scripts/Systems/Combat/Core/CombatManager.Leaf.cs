using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

// ============================================================
// CombatManager.Leaf.cs  (partial of CombatManager)
//
// Purpose:        The Register's leaf in combat (class_identity_arcanist_v1
//                 §2, slice 14a):
//                 • casting from a page: mana plus 1 Charge (unless free),
//                   checked before the cast and paid after it lands
//                 • the babble: at the end of the turn a full leaf recites
//                   its oldest page's top half for free at the nearest legal
//                   target, and that card is exiled for the fight
//                 • the memorized page recites its top half each turn end
//                   for 1 Charge (Infinity Loop)
//                 • Rapid Notation's on-demand recital
// Layer:          System (combat glue)
// Collaborators:  UnitDeckData (Pages, Memorized), ArcanistLeafEffects.cs,
//                 ArcaneAttunement (Charge), DeckUiManager (page row)
// ============================================================
public partial class CombatManager
{
    private void InstallLeafHooks()
    {
        if (State != null)
            State.OnReciteOldest = u => ReciteOldestPage(u, "recites on demand");
    }

    // ── Casting from a page ─────────────────────────────────────────────

    /// <summary>Gate before a cast: a page needs 1 Charge unless it is free. Returns
    /// false (and says why) when the page cannot be cast.</summary>
    private bool LeafCastAllowed(Card card, bool isTop, out string why)
    {
        why = null;
        var deck = selectedUnit?.DeckData;
        if (deck == null || card == null || !deck.IsLeafCard(card))
            return true;
        if (card == deck.Memorized)
        {
            why = "the memorized page recites itself at the end of your turn";
            return false;
        }
        if (LeafCastIsFree(deck, card, isTop))
            return true;
        if (selectedUnit.Attunement is ArcaneAttunement arc && arc.Charge >= 1)
            return true;
        why = "casting from your leaf costs 1 Charge";
        return false;
    }

    private static bool LeafCastIsFree(UnitDeckData deck, Card card, bool isTop) =>
        deck.FreeLeafCasts > 0 || (isTop && deck.FreeTopPages.Contains(card));

    /// <summary>After a page cast lands: pay its Charge (or use up the free cast).</summary>
    private void PayLeafCast(Card card, bool isTop)
    {
        var deck = selectedUnit?.DeckData;
        if (deck == null || card == null || !deck.Pages.Contains(card))
            return;
        if (isTop && deck.FreeTopPages.Contains(card))
        {
            combatUI?.AppendActionLog($"{card.CardName} is cast from your leaf at no Charge.");
            return;
        }
        if (deck.FreeLeafCasts > 0)
        {
            deck.FreeLeafCasts--;
            combatUI?.AppendActionLog($"{card.CardName} is cast from your leaf at no Charge.");
            return;
        }
        if (selectedUnit.Attunement is ArcaneAttunement arc)
        {
            arc.Spend(1);
            combatUI?.AppendActionLog($"{card.CardName} is cast from your leaf (-1 Charge).");
            schoolAttunementUI?.Refresh();
        }
    }

    // ── End of turn: babble and the memorized page ──────────────────────

    /// <summary>Runs for each player unit at the end of the player's turn.</summary>
    private void LeafEndOfTurn(Unit unit)
    {
        var deck = unit?.DeckData;
        if (deck == null || !unit.Stats.IsAlive)
            return;

        if (deck.Pages.Count >= UnitDeckData.MaxPages)
        {
            ReciteOldestPage(unit, "can't finish a sentence, so it starts another");
            RegisterManager.Fire("arc.babble");
        }

        if (deck.Memorized != null && unit.Attunement is ArcaneAttunement arc)
        {
            if (arc.Charge < 1)
                LogLeaf($"{unit.Name}'s memorized page stays silent: no Charge.");
            else
            {
                var half = deck.Memorized.TopHalf;
                if (half != null && ReciteHalf(unit, half, preferEnemy: true))
                {
                    arc.Spend(1);
                    LogLeaf($"The fourth page recites {half.Name} (-1 Charge).");
                }
            }
        }
    }

    /// <summary>The oldest page's top half is cast for free, and the card is exiled.</summary>
    private void ReciteOldestPage(Unit unit, string why)
    {
        var deck = unit?.DeckData;
        if (deck == null || deck.Pages.Count == 0)
        {
            LogLeaf($"{unit?.Name ?? "The leaf"} has no page to recite.");
            return;
        }
        var card = deck.Pages[0];
        deck.Pages.RemoveAt(0);
        deck.FreeTopPages.Remove(card);
        deck.ExilePile.Add(card);
        var half = card.TopHalf;
        bool cast = half != null && ReciteHalf(unit, half, preferEnemy: false);
        LogLeaf(cast
            ? $"The leaf {why}: {half.Name} is cast for free. {card.CardName} is lost for the fight."
            : $"The leaf {why}, but {card.CardName}'s words find no target. It is lost for the fight.");
        if (deckManager != null && deckManager.GetActiveDeck() == deck)
            deckManager.DrawCards(0);   // refresh the hand row and the page row
    }

    /// <summary>Resolves a half for free at an automatic target: the nearest enemy for a
    /// hostile half, the lowest-HP ally for a helpful one (unless preferEnemy), the caster
    /// for a self half. Two-step halves can't be aimed automatically and fizzle.</summary>
    private bool ReciteHalf(Unit unit, CardHalf half, bool preferEnemy)
    {
        if (unit?.CurrentTile == null || half?.Effects == null || half.Targeting is SelectTwoStepTarget)
            return false;

        Unit aimUnit = NearestEnemyOf(unit);
        if (!preferEnemy && HalfHelpsAllies(half))
            aimUnit = LowestHpAlly(unit) ?? unit;
        var aimTile = aimUnit?.CurrentTile ?? unit.CurrentTile;

        var prevCaster = State.ActiveCasterUnit;
        var prevOrigin = State.RetargetOrigin;
        var prevName = State.ResolvingAbilityName;
        var prevHalf = GlyphManager.ResolvingHalf;
        try
        {
            State.ActiveCasterUnit = unit;
            State.ResolvingAbilityName = half.Name;
            GlyphManager.ResolvingHalf = half;
            var origin = new TargetSet();
            origin.Items.Add(aimTile);
            State.RetargetOrigin = origin;

            TargetSet targets;
            if (half.Targeting is SelectSelfTarget || half.Targeting is SelectGlobalTarget || half.Targeting == null)
            {
                targets = new TargetSet();
                targets.Items.Add(Me);
            }
            else if (half.Targeting is SelectUnitTarget)
            {
                if (aimUnit == null)
                    return false;
                targets = new TargetSet();
                targets.Items.Add(aimUnit);
            }
            else if (!half.Targeting.Select(State, Me, out targets) || targets == null)
            {
                targets = new TargetSet();
                targets.Items.Add(aimTile);
            }

            var snap = new EffectSnapshot();
            foreach (var e in half.Effects)
                e?.Resolve(State, Me, targets, snap);
            while (!State.Stack.IsEmpty)
                State.Resolver.ResolveTop(State);
            RefreshEnemyRoster();
            KickTriggerDrain();
            return true;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[Leaf] recital of {half.Name} threw: {ex.Message}");
            return false;
        }
        finally
        {
            State.ActiveCasterUnit = prevCaster;
            State.RetargetOrigin = prevOrigin;
            State.ResolvingAbilityName = prevName;
            GlyphManager.ResolvingHalf = prevHalf;
        }
    }

    private Unit NearestEnemyOf(Unit unit)
    {
        Unit best = null;
        int bestD = int.MaxValue;
        foreach (var e in enemyUnits)
        {
            if (!IsValidActor(e) || e.CurrentTile == null)
                continue;
            int d = grid.Distance(unit.CurrentTile.Axial, e.CurrentTile.Axial);
            if (d < bestD) { bestD = d; best = e; }
        }
        return best;
    }

    private Unit LowestHpAlly(Unit unit)
    {
        return playerUnits
            .Where(p => p != null && IsInstanceValid(p) && p.Stats.IsAlive && p.CurrentTile != null && !p.IsStructure)
            .OrderBy(p => p.Stats.Health * 1000 / Math.Max(1, p.Stats.MaxHealth))
            .FirstOrDefault();
    }

    /// <summary>A half that only helps (heal, shield, buffs) is aimed at an ally.</summary>
    private static bool HalfHelpsAllies(CardHalf half)
    {
        if (half.Targeting is SelectUnitTarget ut && ut.enemyOnly)
            return false;
        bool helps = false, hurts = false;
        void Walk(IEffect e, int depth)
        {
            if (e == null || depth > 8) return;
            foreach (var t in e.Tags ?? Array.Empty<string>())
            {
                if (t is "Heal" or "Defense" or "Buff") helps = true;
                if (t is "Damage" or "Control" or "Debuff" or "Status") hurts = true;
            }
            if (e.Children != null)
                foreach (var c in e.Children) Walk(c, depth + 1);
        }
        foreach (var e in half.Effects ?? Array.Empty<IEffect>())
            Walk(e, 0);
        return helps && !hurts;
    }

    private void LogLeaf(string msg)
    {
        GD.Print($"[Leaf] {msg}");
        combatUI?.AppendActionLog(msg);
    }
}
