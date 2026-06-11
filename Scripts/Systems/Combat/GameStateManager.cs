using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// GameStateManager.cs
//
// Purpose:        The central GameState class plus its
//                 companions — EventBus, GameStack,
//                 PriorityManager, Resolver. The full combat
//                 state machine and stack-based card resolution
//                 lives here. Every effect, predicate, and
//                 targeter receives a GameState reference.
// Layer:          Runtime
// Collaborators:  RulesManager.cs (the top-level driver),
//                 Unit.cs, HexGridManager.cs, every IEffect /
//                 IPredicate / ITargetSelector implementation
// See:            README §3 — Architecture (combat stack model)
// ============================================================

/// <summary>Top-level mutable combat state. Owns the hex grid, the unit list, the active caster reference, the event bus / stack / priority manager / resolver, and persistent effects. Every card-scripting interface receives this so effects can read and mutate the world.</summary>
public sealed class GameState
{
    public EventBus Bus = new();
    public GameStack Stack = new();
    public PriorityManager Priority = new();
    public Resolver Resolver;

    public List<PersistentEffect> ActiveEffects = new();
    public MemorialManager Memorials;
    public GlyphManager Glyphs;
    public GrowthManager Growth;

    /// <summary>The most recently resolved StackItem. Set by Resolver.ResolveTop; read by replicate/echo/mana-refund effects.</summary>
    public StackItem LastResolvedItem;
    public int SpellsCastThisTurn = 0;
    public int LastDamageDealt = 0;   // set by DealDamageEffect after each hit
    public int LastGriefSpent = 0;    // set by GriefDischargeDamageEffect

    // ── Chronomancer systems ─────────────────────────────────────────────────

    /// <summary>
    /// Scheduled-spell queue. Entries tick in StartPlayerTurn.
    /// </summary>
    public List<AlmanacEntry> Almanac = new();

    /// <summary>
    /// Mana cost reduction applied to the player's NEXT spell. Cleared after use.
    /// </summary>
    public int NextSpellCostReduction = 0;

    /// <summary>
    /// Extra mana cost applied to enemy spells this round. Reset in StartEnemyTurn.
    /// </summary>
    public int EnemySpellCostIncrease = 0;

    /// <summary>
    /// Turns remaining on the redirect-all effect.
    /// When > 0, FindNearestPlayerUnit redirects enemies to attack each other.
    /// </summary>
    public int RedirectAllTurnsRemaining = 0;

    /// <summary>Phase-tile network registered by CreatePhaseTilesEffect.</summary>
    public List<Vector2I> PhaseTiles = new();

    /// <summary>Turns remaining before PhaseTiles clear.</summary>
    public int PhaseTileTurnsRemaining = 0;

    // ── General combat state ─────────────────────────────────────────────────

    public string Step = "Main";
    public HexGridManager Grid;
    public Unit PlayerUnit;
    public Unit EnemyUnit;
    public List<Unit> UnitsInPlay = new();
    public Func<string, TileData, int, Unit> OnSummonRequested;
    public Action<Unit> OnDrawCards;
    public Unit ActiveCasterUnit;

    public Entity PlayerA = new() { Name = "A" };
    public Entity PlayerB = new() { Name = "B" };
    public TargetSet RetargetOrigin;

    public Dictionary<Entity, int> Mana = new();

    public List<Action> OnTurnEndCleanups;

    public GameState()
    {
        Resolver = new Resolver(Bus, Stack);
        Mana[PlayerA] = 5;
        Mana[PlayerB] = 5;
        Priority.ResetForNewStep(PlayerA);
        Memorials = new MemorialManager(Grid);
        Glyphs = new GlyphManager(Grid);
        Glyphs.SetState(this);
    }

    public void OpenPriorityWindow() { Bus.Emit("PriorityOpened"); }

    public void AdvanceStep()
    {
        Step = Step == "Main" ? "End" : "Main";
        Log($"== Step → {Step} ==");
        Priority.ResetForNewStep(PlayerA);
        OpenPriorityWindow();
    }

    public int StackCount() { int n = 0; foreach (var _ in Stack.Items) n++; return n; }

    public void MoveCardToGraveyard(Entity who, Card card)
    {
        // Cards live in UnitDeckData now — discard is handled by DeckManager
        Log($"Card → Graveyard: {card.CardName}");
    }

    public void Log(string msg) { GD.Print(msg); }

    public bool HasActiveEffect<T>(Entity owner) where T : PersistentEffect
    {
        return ActiveEffects?.Exists(e => e is T && e.Owner == owner && !e.IsExpired) ?? false;
    }

    public T GetActiveEffect<T>(Entity owner) where T : PersistentEffect
    {
        return ActiveEffects?.Find(e => e is T && e.Owner == owner && !e.IsExpired) as T;
    }
}