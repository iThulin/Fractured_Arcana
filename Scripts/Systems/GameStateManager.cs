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

    /// <summary>Post-cast player choice (2026-07-28). An effect that cannot finish
    /// without asking the player something leaves its request HERE and returns;
    /// Resolver.ResolveTop publishes it to <see cref="OnCardChoiceRequested"/> once the
    /// resolution has unwound, and SequenceEffect chains its remaining steps onto it
    /// first so ordering survives. Exactly one request is in flight at a time — a second
    /// effect finding this slot occupied means two choices in one resolution, which the
    /// seam queues rather than dropping.</summary>
    public CardChoiceRequest PendingChoice;

    /// <summary>The UI end of the choice seam — the third of its kind, alongside
    /// OnSummonRequested and OnDrawCards. Null in headless contexts, which is why every
    /// request carries a usable default: an unanswered question must not wedge a fight.</summary>
    public Action<CardChoiceRequest> OnCardChoiceRequested;

    /// <summary>THE publish path for a choice request. Resolver.ResolveTop calls this
    /// for whatever an effect left in <see cref="PendingChoice"/>; an effect chaining a
    /// SECOND request behind a first calls it directly from the continuation, because
    /// by then the resolver has long since cleared the slot and stopped looking.
    /// One function, so the "nobody is listening" fallback cannot exist in two versions
    /// that disagree.</summary>
    public void DispatchCardChoice(CardChoiceRequest req)
    {
        if (req == null)
            return;
        if (OnCardChoiceRequested != null)
        {
            OnCardChoiceRequested(req);
            return;
        }
        Log($"[{req.Source}] no choice UI is listening — taking the default.");
        req.Complete(req.DefaultPick());
    }

    /// <summary>Publish-or-queue for effects that want a choice (2026-07-29). The slot
    /// holds ONE request; a second effect in the same resolution folds its request
    /// behind whatever is already there and dispatches DIRECTLY from the continuation —
    /// by then Resolver.ResolveTop has cleared the slot and stopped looking at it, so a
    /// second assignment would sit unread until some unrelated later resolution
    /// happened to publish it. Extracted from ScryEffect so the fourth and fifth
    /// choice effects cannot re-implement the queueing subtly differently.</summary>
    public void RequestCardChoice(CardChoiceRequest req)
    {
        if (req == null)
            return;
        if (PendingChoice != null)
            PendingChoice.Then(_ => DispatchCardChoice(req));
        else if (ResolutionDepth > 0)
            PendingChoice = req;          // Resolver.ResolveTop publishes it after unwind
        else
            // No resolver pass is coming — the effect is running inside a
            // continuation (Spell Storm's ordered casts), an Almanac tick, or some
            // other out-of-band resolve. Parking the request in the slot here would
            // strand it (and the cards it holds) until an unrelated cast happened to
            // publish it. Dispatch directly instead.
            DispatchCardChoice(req);
    }

    /// <summary>Depth counter for Resolver.ResolveTop's effect loop. Non-zero means a
    /// resolver pass is running and will publish whatever lands in
    /// <see cref="PendingChoice"/>; zero means effects are resolving out-of-band and
    /// <see cref="RequestCardChoice"/> must dispatch directly. See that method.</summary>
    public int ResolutionDepth = 0;

    // ── Per-card cost modifiers (2026-07-29) ─────────────────────────────────

    /// <summary>Per-card-INSTANCE mana discounts, keyed by Card.InstanceId. This is
    /// the field the scry doc-comment said "does not exist yet": NextSpellCostReduction
    /// is single-use and global, so it cannot express "THIS specific card costs 1
    /// less" (Precognition, Borrowed Future, Eternal Recall). Keyed by Guid rather
    /// than by Card because halves are SHARED between instances (CardDatabase's clone
    /// is shallow) — a discount stored anywhere on the card object would discount
    /// every copy in every deck. Combat-scoped: GameState dies with the fight, so a
    /// discount can never leak into the next one. Consumed on cast in
    /// Rules.TryCastWithTargets, EXCEPT for Perfected cards, whose zero-cost is
    /// permanent for the fight.</summary>
    public Dictionary<Guid, int> CardCostDeltas = new();

    /// <summary>Magnum Opus (2026-07-29): cards Perfected this combat, mapping
    /// InstanceId → flat bonus damage. A Perfected card keeps its cost-0 delta when
    /// cast (see CardCostDeltas), gets its bonus pinned onto the caster for exactly
    /// its own resolution (Resolver.ResolveTop), and returns to hand instead of
    /// discarding (CombatManager's discard step).</summary>
    public Dictionary<Guid, int> PerfectedCards = new();

    /// <summary>Adds a mana discount to one specific card instance. Stacks.</summary>
    public void AddCardDiscount(Card card, int amount)
    {
        if (card == null || amount <= 0)
            return;
        CardCostDeltas.TryGetValue(card.InstanceId, out int cur);
        CardCostDeltas[card.InstanceId] = cur + amount;
    }

    /// <summary>The discount currently attached to a card instance (0 when none).</summary>
    public int GetCardDiscount(Card card)
        => card != null && CardCostDeltas.TryGetValue(card.InstanceId, out int v) ? v : 0;

    /// <summary>The card whose costs are currently being evaluated — the per-card
    /// analogue of ActiveCasterUnit, read by ManaCost.EffectiveAmount. Pinned by
    /// Rules.TryCastWithTargets around affordability + payment, and by the UI's
    /// effective-cost provider around its read. Like the tithe, the discount lives in
    /// EffectiveAmount so affordability and payment cannot disagree: a card discounted
    /// to 1 is CASTABLE at 1 mana, which the pay-full-then-refund shape used for the
    /// global discounts structurally cannot express.</summary>
    public Card CostContextCard;

    // ── Foretell (2026-07-29) ────────────────────────────────────────────────

    /// <summary>Cards set aside by ForetellEffect, waiting to enter their owner's
    /// hand. Ticked in CombatManager.StartPlayerTurn, AFTER the normal draw — a
    /// Foretold card arriving over MaxHandSize is kept, not burned; the player paid a
    /// card and a turn for it. The cards live in NO deck pile while here (same
    /// held-out convention as a scry's revealed cards), so deck counts exclude them
    /// by construction.</summary>
    public List<ForetoldEntry> Foretold = new();

    // ── Choose-one (2026-07-29) ──────────────────────────────────────────────

    /// <summary>Mode index for the next cast whose effect tree contains a
    /// ChooseOneEffect. Set by CombatManager after the cast-time mode picker;
    /// transferred onto EffectSnapshot.ChosenOption inside Rules.TryCastWithTargets /
    /// TryCast and reset to -1 there. It rides the SNAPSHOT and not this field so a
    /// Reaction cast between this spell going on the stack and resolving cannot
    /// clobber the choice. -1 = no choice made (AI, headless) → option 0 resolves.</summary>
    public int PendingChooseOneIndex = -1;
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