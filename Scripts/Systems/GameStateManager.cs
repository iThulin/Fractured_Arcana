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

    /// <summary>True while the enemy phase is executing. Reaction costs may be
    /// paid from banked Foresight (Time Bank, 2026-07-10) only in this context.
    /// Set in RunEnemyTurn; cleared in StartPlayerTurn.</summary>
    public bool EnemyPhaseContext = false;

    /// <summary>True while the current cast is a channeled (upgraded) variant. Set at cast in CombatManager's channel resolution; read by the IsChanneled predicate.</summary>
    public bool LastCastWasChannel = false;

    /// <summary>Enemies that died since the start of the player's turn. Incremented in HandleUnitDeath; reset in StartPlayerTurn. Read by mana_per_kill (Aftershock).</summary>
    public int EnemiesKilledThisTurn = 0;

    /// <summary>Enemy actions negated since the start of the player's turn (Counterspell). Incremented at cast; reset in StartPlayerTurn. Read by actions_negated_this_turn (Spell Drain).</summary>
    public int ActionsNegatedThisTurn = 0;
    public int LastDamageDealt = 0;   // set by DealDamageEffect after each hit
    public int LastGriefSpent = 0;    // set by GriefDischargeDamageEffect

    /// <summary>Combined StrengthValue of memorials consumed by the most recent consume_memorials_for_champion step. Read by summon_spirit_scaled.</summary>
    public int LastMemorialStrengthConsumed = 0;

    /// <summary>Tiles where friendly spirits have fallen this combat. Appended by HandleUnitDeath; read by summon_spirit_from_all_memorials_and_death_sites.</summary>
    public List<Vector2I> SpiritDeathTiles = new();

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
    /// Extra cost applied to enemy spells this round (Ritardando). Enemies pay no
    /// mana — the only "spell" they cast is the ranged_charge Channel→Release — so
    /// this is charged in the currency they DO spend: the channel is held for N extra
    /// activations before the blast lands. Read at ExecuteChannelStart into
    /// Unit.ChannelDelayRemaining.
    ///
    /// (2026-07-28, U3e) Was a DEAD FIELD: written by CostModifyEffect, reset in
    /// StartEnemyTurn, and read by nothing at all — so Ritardando's "+1 cost" clause
    /// did nothing for its entire life. Worse, the reset sat at the HEAD of
    /// StartEnemyTurn, which wiped the value the player had just paid 3 mana to set,
    /// before a single enemy acted. The reset now lives in StartPlayerTurn so the
    /// tax spans exactly the enemy phase it was bought for.
    /// </summary>
    public int EnemySpellCostIncrease = 0;

    /// <summary>
    /// U3e (tithe_aura): extra mana every PLAYER spell costs while a tithe carrier
    /// lives. The sibling of EnemySpellCostIncrease in name only — this one is
    /// actually consumed, in ManaCost.CanPay and ManaCost.Pay, so affordability is
    /// tested at the TAXED price rather than at the printed one.
    ///
    /// A STATE, not an event: recomputed wholesale by CombatManager.ApplyEnemyAuras
    /// at every player-turn start, at the head of the enemy phase, and on any death,
    /// so killing the warden drops the tax on the same frame. Never written by hand.
    ///
    /// Ruling (spec §9 open decision 1, 2026-07-28): the tax is CLAMPED so a taxed
    /// cost can never exceed the caster's MaxMana. tithe_aura prices your curve; it
    /// does not delete cards from your hand. See ManaCost.EffectiveAmount.
    /// </summary>
    public int PlayerSpellCostIncrease = 0;

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

    // R22 sim gate: preview runs are silent — the resolver's log lines would
    // otherwise spam the console on every hover.
    public void Log(string msg) { if (!CombatSim.Active) GD.Print(msg); }

    public bool HasActiveEffect<T>(Entity owner) where T : PersistentEffect
    {
        return ActiveEffects?.Exists(e => e is T && e.Owner == owner && !e.IsExpired) ?? false;
    }

    public T GetActiveEffect<T>(Entity owner) where T : PersistentEffect
    {
        return ActiveEffects?.Find(e => e is T && e.Owner == owner && !e.IsExpired) as T;
    }
}