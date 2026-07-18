using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

// ============================================================
// CombatManager.cs
//
// Purpose:        Top-level scene-graph controller for one
//                 combat encounter. Spawns the player and enemy
//                 units from the EncounterDefinition, drives the
//                 deployment → combat → resolution flow, wires
//                 every signal between CombatUI / CardDropHandler
//                 / DeckManager / RulesManager, and reports
//                 results back via EncounterContext.
// Layer:          System
// Collaborators:  RulesManager.cs (stack/priority/resolution),
//                 GameState.cs (state container),
//                 Unit.cs (spawned), CardDropHandler.cs (input),
//                 DeckManager.cs (active deck), CombatUI.cs (HUD),
//                 EncounterContextCarrier.cs (input encounter)
// See:            README §3 — combat orchestration layer
// ============================================================

/// <summary>Top-level controller for a single combat encounter. Builds the GameState, spawns both teams from the encounter definition, runs the deployment phase, then hands off to RulesManager-driven turn cycles. Reports the result via <see cref="EncounterContext"/> on combat end. Massive file — see internal section banners for the deployment/turn-flow/spawning/end-condition split.</summary>
public partial class CombatManager : Node3D
{
    // ── Scene references ────────────────────────────────────────────────────
    [Export] public PackedScene PlayerUnitScene;
    [Export] public PackedScene DummyUnitScene;
    [Export] public NodePath GridPath = "../HexGridManager";
    [Export] public NodePath CombatUIPath = "../CombatUI";
    [Export] public CameraController CombatCamera;

    // ── Core state ──────────────────────────────────────────────────────────
    public GameState State;
    private Entity Me, Opp;
    private List<Card> _compiled = new();
    private DeckManager deckManager;
    private DeckUiManager deckUiManager;
    private CardDropHandler dropper;
    private HexGridManager grid;
    private CombatUI combatUI;

    // ── Deployment phase ────────────────────────────────────────────────────
    [Export] public bool EnableDeploymentPhase = true;
    [Export] public bool AutoStartAfterDeployment = true;
    private bool isInDeploymentPhase = false;
    private Unit selectedDeployUnit = null;
    private HashSet<Vector2I> playerDeployCoords = new();
    private Dictionary<Unit, Vector2I> originalDeployCoords = new();

    // stores the pending enemy spawn parameters so we can defer spawning until after the player commits their deployment.
    // U2: the resolved UnitDefinition is the identity; the flat fields beside it
    // are the ROLLED (difficulty-scaled) stats, which may differ from Def's bases.
    private struct PendingEnemySpawn
    {
        public UnitDefinition Def;        // ← resolved definition: id, behavior key/tags, label, colour
        public int MaxHealth;
        public int Health;
        public int BaseSpeed;
        public int Armor;
        public int AttackRange;           // ← needed by ranged AI
        public int AttackDamage;          // ← rolled damage (difficulty-scaled)
        public Color BodyColor;
        public string NamePrefix;
    }
    private List<PendingEnemySpawn> pendingEnemySpawns = new();

    // ── Unit lists ──────────────────────────────────────────────────────────
    [Export] public int TestPlayerCount = 2;
    [Export] public int TestEnemyCount = 3;

    private Unit playerUnit;   // primary player unit (kept for mana logic)
    private Unit dummyUnit;    // primary enemy unit  (kept for existing refs)
    private List<Unit> playerUnits = new();
    private List<Unit> enemyUnits = new();
    private bool _pruneNeeded;

    /// <summary>Latch so the per-frame CheckCombatEnd deferral prints once per
    /// episode instead of every frame (see CheckCombatEnd).</summary>
    private bool _combatEndDeferLogged;

    // ── Selection state ─────────────────────────────────────────────────────
    private Unit selectedUnit = null;
    private Unit inspectedEnemyUnit = null;
    private HashSet<Vector2I> currentMoveTiles = new();
    private Unit _hoveredUnit = null;
    private SchoolAttunementUI schoolAttunementUI;

    // ── Tile highlighting state ─────────────────────────────────────────────
    private HashSet<Vector2I> _targetHighlightTiles = new();
    // combat_ui §8 aura hover extents: tiles ringed to show a selected player
    // construct's aura radius (Sentinel armor / Lattice / Foundry damage).
    private readonly HashSet<Vector2I> _auraHighlightTiles = new();
    private CardHalf _lastHighlightedHalf = null;
    private bool _isCardBeingDragged = false;
    private CardHalf _draggedHalf = null;

    // ── Movement zone renderer ──────────────────────────────────────────────
    private MovementZoneRenderer _zoneRenderer;

    // ── Drag state ──────────────────────────────────────────────────────────
    private bool _isDraggingUnit = false;
    private Unit _draggedUnit = null;
    private Vector2 _dragStartScreenPos;
    private const float DragThresholdPixels = 8f; // must move this far to count as drag

    // ── Phase ───────────────────────────────────────────────────────────────
    public enum CombatPhase { Deployment, PlayerTurn, EnemyTurn, Victory, Defeat }
    private CombatPhase currentPhase = CombatPhase.Deployment;
    private int roundNumber = 1;
    private bool enemyPhaseRunning = false;
    private bool _isExtraTurn = false; // for Chronomancer's extra turn effect

    // ── Run summary data (for post-run screen) ───────────────────────────────
    [Signal] public delegate void CombatCompletedEventHandler(bool playerWon);

    // ═══════════════════════════════════════════════════════════════════════
    // _Ready
    // ═══════════════════════════════════════════════════════════════════════

    public override void _Ready()
    {
        // Ensure card database is loaded before any gameplay logic that relies on it.
        CardLoaderV2.LoadCardsFromJson("res://Data/Cards");

        State = new GameState();
        ConduitLinkSystem.Clear();
        EtchingSystem.Clear();
        TrapSystem.Clear();
        Me = State.PlayerA;
        Opp = State.PlayerB;

        if (PlayerSession.DebugMode)
        {
            GD.Print("=== DEBUG MODE ENABLED ===");
            State.Mana[Me] = 99;  // unlimited mana in debug
            // EnableDeploymentPhase is controlled by SkipDeployment specifically
        }

        SpawnTestUnits();
        RegisterSummonHandler();

        // Wire up helper nodes
        deckManager = GetNodeOrNull<DeckManager>("../Player/DeckManager");
        if (deckManager == null)
            GD.PrintErr("DeckManager not found. Fix the node path in GameRunner.");

        if (deckManager != null)
            CallDeferred(nameof(InitializeUnitDecks));

        // Assign DeckUiManager separately
        deckUiManager = GetNodeOrNull<DeckUiManager>("../DeckUI/DeckUIManager");
        if (deckUiManager != null)
        {
            deckUiManager.CardHalfHovered += OnCardHalfHovered;

            deckUiManager.SetManaProvider(() => selectedUnit?.Stats.Mana ?? 0);
        }
        else
        {
            GD.PrintErr("DeckUiManager not found. Target highlighting won't work.");
        }

        dropper = GetNodeOrNull<CardDropHandler>("../CardDropHandler");
        if (dropper != null)
        {
            dropper.Connect(CardDropHandler.SignalName.CardDroppedOnTile,
                new Callable(this, nameof(OnCardDroppedOnTile)));

            dropper.CardDragStarted += OnCardDragStarted;
            dropper.CardDragEnded += OnCardDragEnded;
            // R22 damage preview: refresh the flashing HP-bar segment as the
            // dragged card crosses tiles.
            dropper.DragHoverChanged += UpdateDamagePreview;
            dropper.DragHoverCleared += ClearDamagePreview;
        }
        else
        {
            GD.PrintErr("CardDropHandler not found. Fix the node path in GameRunner.");
        }

        combatUI = GetNodeOrNull<CombatUI>(CombatUIPath);
        if (combatUI == null)
            GD.PrintErr("CombatUI not found. Fix CombatUIPath.");

        if (combatUI != null)
        {
            combatUI.ConfirmDeploymentPressed += OnConfirmDeploymentPressed;
            combatUI.EndTurnPressed += OnEndTurnPressed;
            combatUI.PriorityPassPressed += OnPriorityPassPressed;   // U3 trigger window
            combatUI.PriorityRespondPressed += OnPriorityRespondPressed;   // §7c Respond affordance
            combatUI.EnemyRowHovered += OnEnemyRowHovered;           // V2 roster hover → threat overlay

            // Unit bar buttons select the corresponding unit
            combatUI.UnitButtonPressed += OnUnitBarButtonPressed;
            // Enemy roster buttons inspect the corresponding enemy
            combatUI.EnemyButtonPressed += OnEnemyRosterButtonPressed;
        }

        // Movement zone renderer — resolved in InitZoneRenderer: prefers the scene node
        // under HexGridManager (Inspector-tunable), falls back to creating one in code.
        CallDeferred(nameof(InitZoneRenderer));

        // Create the attunement UI as a child of CombatUI
        schoolAttunementUI = new SchoolAttunementUI();
        schoolAttunementUI.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
        schoolAttunementUI.Position = new Vector2(0, 162);
        if (combatUI != null)
            combatUI.AddChild(schoolAttunementUI);


        CallDeferred(nameof(DeferWireAttunement));

        if (playerUnit != null)
            State.Mana[Me] = playerUnit.Stats.Mana;

        State.Bus.OnEvent += OnGameEvent;

        if (!EnableDeploymentPhase)
            State.OpenPriorityWindow();

        CallDeferred(nameof(RefreshPhaseUI));
        CallDeferred(nameof(RefreshSelectedUnitUI));

        RefreshPhaseUI();
        RefreshSelectedUnitUI();

        if (PlayerSession.DebugCombat)
        {
            CombatCompleted += (bool won) => CombatDebugLauncher.ReturnToCampus(this);
            GD.Print("CombatManager: debug fight — CombatCompleted returns to campus.");
        }
        else if (EncounterRouter.Instance != null)
        {
            CombatCompleted += (bool won) => EncounterRouter.Instance.OnCombatFinished(won);
            GD.Print("GameRunner: Wired CombatCompleted to EncounterRouter.");
        }
    }

    public override void _Process(double delta)
    {
        if (_pruneNeeded)
        {
            _pruneNeeded = false;
            PruneDeadUnits();
            if (currentPhase != CombatPhase.Victory && currentPhase != CombatPhase.Defeat)
                CheckCombatEnd();
        }

        if (currentPhase == CombatPhase.EnemyTurn)
            return;

        // ── Guard: viewport and world may not be ready on first frames ──
        var viewport = GetViewport();
        if (viewport == null)
            return;

        var camera = viewport.GetCamera3D();
        if (camera == null)
            return;

        var world = GetWorld3D();
        if (world?.DirectSpaceState == null)
            return;
        // ───────────────────────────────────────────────────────────────

        Vector2 mousePos = viewport.GetMousePosition();
        Vector3 from = camera.ProjectRayOrigin(mousePos);
        Vector3 to = from + camera.ProjectRayNormal(mousePos) * 1000f;

        var result = world.DirectSpaceState
            .IntersectRay(PhysicsRayQueryParameters3D.Create(from, to));

        Unit hitUnit = null;
        if (result.Count > 0 && result.TryGetValue("collider", out var cv))
        {
            Node current = cv.AsGodotObject() as Node;
            while (current != null)
            {
                if (current is Unit u)
                { hitUnit = u; break; }
                current = current.GetParent();
            }
        }

        // Fix (2026-07-09): a unit killed WHILE hovered gets freed by the prune,
        // leaving _hoveredUnit as a disposed wrapper. Touching it threw every
        // frame, and the exception aborted _Process before the reassignment —
        // wedging the error loop permanently. (Surfaced by the drop-on-unit fix:
        // the mouse now legitimately rests on models mid-kill.) Same guard for
        // hitUnit: the corpse's collider can outlive the death by a frame.
        if (_hoveredUnit != null && !IsInstanceValid(_hoveredUnit))
            _hoveredUnit = null;
        if (hitUnit != null && (!IsInstanceValid(hitUnit) || !hitUnit.Stats.IsAlive))
            hitUnit = null;

        if (hitUnit != _hoveredUnit)
        {
            // Collapse old hovered bar only if it isn't pinned by selection/inspection
            if (_hoveredUnit != null
                && _hoveredUnit != selectedUnit
                && _hoveredUnit != inspectedEnemyUnit)
                _hoveredUnit.SetDetailedBar(false);

            _hoveredUnit?.SetHovered(false);
            _hoveredUnit = hitUnit;
            _hoveredUnit?.SetHovered(true);

            // Expand new hovered bar, same pin check
            if (_hoveredUnit != null
                && _hoveredUnit != selectedUnit
                && _hoveredUnit != inspectedEnemyUnit)
                _hoveredUnit.SetDetailedBar(true);

            // ── Show/hide threat zone for hovered enemy ──
            if (hitUnit != null && !hitUnit.IsPlayerControlled && hitUnit.Stats.IsAlive)
            {
                ShowEnemyThreatZone(hitUnit);
            }
            else if (selectedUnit != null)
            {
                ShowMoveTilesWithCost(selectedUnit);
            }
            else
            {
                _zoneRenderer?.Clear();
            }
        }

        // Drag handling
        if (_draggedUnit != null)
        {
            float dragDist = GetViewport().GetMousePosition()
                .DistanceTo(_dragStartScreenPos);

            if (dragDist > DragThresholdPixels && !_isDraggingUnit)
            {
                _isDraggingUnit = true;
                // Could show a ghost unit or change cursor here
            }
        }

        // ── Cost label on hovered tile ──
        if (selectedUnit != null && _zoneRenderer != null
            && currentPhase == CombatPhase.PlayerTurn)
        {
            var tileHit = GetHoveredTile();

            if (tileHit.HasValue && currentMoveTiles.Contains(tileHit.Value))
            {
                _zoneRenderer.ShowCostLabelForTile(
                    tileHit.Value,
                    grid,
                    selectedUnit.Stats.BaseSpeed);
            }
            else
            {
                _zoneRenderer.HideCostLabel();
            }
        }
    }

    private void InitZoneRenderer()
    {
        if (grid == null)
            return;
        // Prefer the in-scene node so its [Export]s stay Inspector-tunable.
        _zoneRenderer = grid.GetNodeOrNull<MovementZoneRenderer>("MovementZoneRenderer");
        if (_zoneRenderer == null)
        {
            _zoneRenderer = new MovementZoneRenderer();
            _zoneRenderer.Name = "MovementZoneRenderer";
            grid.AddChild(_zoneRenderer);
        }
        _zoneRenderer.HexRadius = grid.HexRadius;
    }

    private void InitializeUnitDecks()
    {
        var companionCards = BuildCompanionCardList();
        bool injectedCompanionCards = false;

        foreach (var unit in playerUnits)
        {
            if (unit == null)
                continue;
            if (unit.IsMartial)
                continue;

            unit.DeckData = new UnitDeckData(unit.School, 5);

            if (!injectedCompanionCards)
            {
                // First wizard gets the persistent deck + companion cards on top.
                var cards = PlayerDeckService.HydrateActiveDeck(SaveManager.ActiveSave);

                if (cards.Count == 0)
                {
                    // Fallback: save has no valid persistent deck yet (edge case on
                    // migrated saves that haven't been through NewGame). Seed it now.
                    GD.PrintErr("[InitializeUnitDecks] Persistent deck empty — seeding starter deck.");
                    if (Enum.TryParse<CardSchool>(SaveManager.ActiveSave?.SelectedSchool,
                            ignoreCase: true, out var school))
                        StarterDeckLoader.SeedStarterDeck(SaveManager.ActiveSave, school);
                    SaveManager.Save();
                    cards = PlayerDeckService.HydrateActiveDeck(SaveManager.ActiveSave);
                }

                if (companionCards.Count > 0)
                    cards.AddRange(companionCards);

                unit.DeckData.Initialize(cards);
                injectedCompanionCards = true;

                GD.Print($"Deck built for {unit.Name}: {unit.DeckData.TotalCards} cards " +
                         $"({unit.School}, {companionCards.Count} companion, " +
                         $"{cards.Count - companionCards.Count} from save)");
            }
            else
            {
                // Additional wizards (future multi-wizard party) still get a random deck
                // until per-unit persistent decks are designed.
                unit.DeckData.Initialize(PlayerSession.DeckSize);
                GD.Print($"Deck built for {unit.Name}: {unit.DeckData.TotalCards} cards " +
                         $"({unit.School}) [secondary unit, random]");
            }
        }

        if (playerUnits.Count > 0 && playerUnits[0].DeckData != null)
            deckManager.SetActiveDeck(playerUnits[0].DeckData);

        State.OnDrawCards = (unit) =>
        {
            if (deckManager != null && deckManager.GetActiveDeck() == unit.DeckData)
                deckManager.DrawCards(0);
        };

        // Skip-deploy handoff: decks now exist, so Round 1 can actually draw.
        if (_pendingSkipDeployTurnStart)
        {
            _pendingSkipDeployTurnStart = false;

            // Fix v4 (2026-07-09): the round-1 StartPlayerTurn THROWS in the
            // skip-deploy context (round 2+ runs the same code clean) — the
            // exception aborted this whole deferred call, killing the select
            // and roster sync in every prior version. The banner at the end of
            // StartPlayerTurn never printing was the tell. Caught and printed
            // to the OUTPUT panel (GD.Print, not PrintErr) so it can't hide in
            // the Errors tab again; the eager sync runs regardless.
            try
            {
                StartPlayerTurn();
                deckManager?.DrawCards(0);   // sync the hand UI (same as round transitions)
            }
            catch (Exception e)
            {
                GD.Print($"[SkipDeploy] StartPlayerTurn THREW (round-1 handoff): {e}");
                GD.PrintErr($"[SkipDeploy] StartPlayerTurn THREW (round-1 handoff): {e.Message}");
            }

            // EAGER — select + roster push. CombatUI's pending-replay applies
            // whatever lands before BuildUI at build time.
            try
            {
                if (playerUnits.Count > 0 && playerUnits[0] != null)
                    SelectUnit(playerUnits[0]);
                RefreshEnemyRoster();
                GD.Print("[SkipDeploy] eager sync OK (selected + roster pushed).");
            }
            catch (Exception e)
            {
                GD.Print($"[SkipDeploy] eager sync THREW: {e}");
                GD.PrintErr($"[SkipDeploy] eager sync THREW: {e.Message}");
            }

            // LATE — re-sync after CombatUI reports built, for surfaces with no
            // pending path (the attunement panel wires "Unit: none" otherwise).
            _ = FinishSkipDeployHandoffAsync();
        }
    }

    /// <summary>Skip-deploy handoff, late stage: wait for CombatUI.BuildUI
    /// (deferred on its side), then re-push selection + roster so the first
    /// turn starts fully armed. Fire-and-forget task: exceptions here are
    /// INVISIBLE unless caught — hence the entry print (distinguishes a stale
    /// build from a dead task) and the catch-all.</summary>
    private async System.Threading.Tasks.Task FinishSkipDeployHandoffAsync()
    {
        try
        {
            GD.Print("[SkipDeploy] handoff waiting for CombatUI build...");
            int guard = 0;
            while ((combatUI == null || !combatUI.IsBuilt) && guard++ < 60)
                await ToSignal(GetTree(), "process_frame");
            if (guard >= 60)
                GD.PrintErr("[SkipDeploy] CombatUI never reported built — syncing anyway.");

            // One extra frame: the attunement-section wire is its own deferred
            // chain that resolves just after BuildUI.
            await ToSignal(GetTree(), "process_frame");

            if (playerUnits.Count > 0 && playerUnits[0] != null)
                SelectUnit(playerUnits[0]);
            RefreshEnemyRoster();
            RefreshSelectedUnitUI();
            RefreshPlayerUnitBar();
            RefreshDeckCounts();
            GD.Print("[SkipDeploy] handoff UI sync complete — unit selected, roster loaded.");
        }
        catch (Exception e)
        {
            GD.PrintErr($"[SkipDeploy] handoff sync FAILED: {e}");
        }
    }

    /// <summary>Skip-deployment mode: set by SpawnTestUnits, consumed by
    /// InitializeUnitDecks — the first turn must not start before decks exist.</summary>
    private bool _pendingSkipDeployTurnStart = false;

    private List<Card> BuildCompanionCardList()
    {
        var result = new List<Card>();
        var party = CompanionRoster.GetActiveParty();

        foreach (var companion in party)
        {
            foreach (var cardName in companion.ContributedCardIds)
            {
                var bp = CardDatabase.GetByName(cardName);
                if (bp == null)
                {
                    GD.PrintErr($"Companion '{companion.Name}' references missing card '{cardName}'");
                    continue;
                }
                result.Add(CardDatabase.Instantiate(bp));
            }
        }

        return result;
    }

    private void DeferWireAttunement()
    {
        // One deferred hop lets CombatUI._Ready() queue its BuildUI.
        // A second hop lets BuildUI actually execute before we wire.
        CallDeferred(nameof(WireAttunementSection));
    }

    private void WireAttunementSection()
    {
        if (combatUI?.AttunementSection == null || schoolAttunementUI == null)
        {
            GD.Print("[CombatManager] WireAttunementSection: slot or UI is null");
            return;
        }
        schoolAttunementUI.UseExternalContainer(combatUI.AttunementSection);
        GD.Print("[CombatManager] AttunementSection wired.");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Central UI refresh – call this whenever state changes
    // ═══════════════════════════════════════════════════════════════════════

    private void RefreshAllUI()
    {
        RefreshPhaseUI();
        RefreshSelectedUnitUI();
        RefreshEnemyRoster();
        RefreshPlayerUnitBar();
        RefreshDeckCounts();
    }

    private void RefreshPhaseUI()
    {
        if (combatUI == null)
            return;

        switch (currentPhase)
        {
            case CombatPhase.Deployment:
                combatUI.SetPhaseText("Deployment Phase");
                combatUI.SetHintText("Position your units, then confirm deployment.");
                combatUI.SetDeploymentMode(true);
                break;

            case CombatPhase.PlayerTurn:
                combatUI.SetPhaseText($"Round {roundNumber} - Player Turn");
                combatUI.SetHintText("Select a unit, move, cast, then end turn.");
                combatUI.SetDeploymentMode(false);
                break;

            case CombatPhase.EnemyTurn:
                combatUI.SetPhaseText($"Round {roundNumber} - Enemy Turn");
                combatUI.SetHintText("Enemies are acting...");
                combatUI.SetDeploymentMode(false);
                break;

            case CombatPhase.Victory:
                combatUI.SetPhaseText("Victory");
                combatUI.SetHintText("All enemies defeated.");
                combatUI.SetDeploymentMode(false);
                break;

            case CombatPhase.Defeat:
                combatUI.SetPhaseText("Defeat");
                combatUI.SetHintText("Your party has fallen.");
                combatUI.SetDeploymentMode(false);
                break;
        }
    }

    private void RefreshSelectedUnitUI()
    {
        if (combatUI == null)
            return;

        Unit unitToShow = isInDeploymentPhase
            ? selectedDeployUnit
            : (selectedUnit ?? inspectedEnemyUnit);

        int mana = selectedUnit?.Stats.Mana ?? 0;
        if (State.Mana.ContainsKey(Me))
            State.Mana[Me] = mana;

        int manaToShow = (unitToShow != null && !unitToShow.IsPlayerControlled) ? 0 : mana;

        combatUI.ShowSelectedUnit(unitToShow, manaToShow);
    }

    private void RefreshEnemyRoster()
    {
        // During deployment, enemies don't exist yet — keep the intel panel visible.
        if (isInDeploymentPhase)
        {
            combatUI?.ShowEnemyIntel(BuildEnemyIntel());
            return;
        }
        combatUI?.RefreshEnemyRoster(enemyUnits);
    }

    private void RefreshPlayerUnitBar()
    {
        combatUI?.RefreshPlayerUnitBar(playerUnits, selectedUnit);
        schoolAttunementUI?.ShowForUnit(selectedUnit);
    }

    private void RefreshDeckCounts()
    {
        var deck = deckManager?.GetActiveDeck();
        if (deck == null)
            return;
        combatUI?.RefreshDeckCounts(deck.DrawPile, deck.DiscardPile);
    }

    private void OnUnitBarButtonPressed(int index)
    {
        if (index < 0 || index >= playerUnits.Count)
            return;
        if (currentPhase != CombatPhase.PlayerTurn)
            return;
        SelectUnit(playerUnits[index]);
    }

    private void OnEnemyRosterButtonPressed(int index)
    {
        if (index < 0 || index >= enemyUnits.Count)
            return;
        var enemy = enemyUnits[index];
        if (enemy == null || !enemy.Stats.IsAlive)
            return;

        if (inspectedEnemyUnit != null)
            inspectedEnemyUnit.SetSelected(false);  // ← ADD THIS
        if (selectedUnit != null)
            selectedUnit.SetSelected(false);
        selectedUnit = null;
        inspectedEnemyUnit = enemy;
        inspectedEnemyUnit.SetSelected(true);       // ← ADD THIS
        ClearMoveTiles();
        RefreshSelectedUnitUI();
        RefreshPlayerUnitBar();
    }

    private void RefreshMoveHighlight()
    {
        ClearMoveTiles();
        if (selectedUnit == null || !selectedUnit.CanMove())
            return;

        var reachable = grid.GetReachableTiles(selectedUnit);
        foreach (var coord in reachable)
        {
            currentMoveTiles.Add(coord);
            grid.GetTileView(coord)?.SetMoveHighlight(true);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Input handling
    // ═══════════════════════════════════════════════════════════════════════

    public override void _UnhandledInput(InputEvent e)
    {
        if (isInDeploymentPhase)
        {
            HandleDeploymentInput(e);
            return;
        }

        if (e is InputEventMouseButton mb)
        {
            if (mb.ButtonIndex == MouseButton.Left)
            {
                if (mb.Pressed)
                    OnLeftMousePressed(mb.Position);
                else
                    OnLeftMouseReleased(mb.Position);
                return;
            }
        }

        if (e.IsActionPressed("ui_select"))
        { Pass(); } // space by default
        if (e.IsActionPressed("ui_accept"))
        { ResolveTop(); } // enter
        if (e is InputEventKey k && k.Pressed && !k.Echo)
        {
            // Existing bindings
            if (k.Keycode == Key.R)
                ResolveTop();

            // ── Unit selection ────────────────────────────────────────────
            if (currentPhase == CombatPhase.PlayerTurn)
            {
                // Enemy inspection — check shift first
                if (k.ShiftPressed)
                {
                    if (k.Keycode == Key.Key1)
                    { TryInspectEnemyByIndex(0); return; }
                    if (k.Keycode == Key.Key2)
                    { TryInspectEnemyByIndex(1); return; }
                    if (k.Keycode == Key.Key3)
                    { TryInspectEnemyByIndex(2); return; }
                    if (k.Keycode == Key.Key4)
                    { TryInspectEnemyByIndex(3); return; }
                }

                // Unit selection
                if (k.Keycode == Key.Key1)
                    TrySelectUnitByIndex(0);
                if (k.Keycode == Key.Key2)
                    TrySelectUnitByIndex(1);
                if (k.Keycode == Key.Key3)
                    TrySelectUnitByIndex(2);
                if (k.Keycode == Key.Key4)
                    TrySelectUnitByIndex(3);

                // Tab cycles units
                if (k.Keycode == Key.Tab)
                {
                    if (k.ShiftPressed)
                        CycleSelectedUnit(-1);
                    else
                        CycleSelectedUnit(1);
                }
            }
        }
    }

    private void TryHandleMainPhaseClick()
    {
        //GD.Print($"TryHandleMainPhaseClick phase={currentPhase}");

        if (currentPhase != CombatPhase.PlayerTurn)
        {
            // During enemy turn allow clicking to inspect enemies
            if (currentPhase == CombatPhase.EnemyTurn)
                TryInspectClick();
            return;
        }

        var camera = GetViewport().GetCamera3D();
        if (camera == null)
        { GD.PrintErr("No active camera."); return; }

        Vector2 mousePos = GetViewport().GetMousePosition();
        Vector3 from = camera.ProjectRayOrigin(mousePos);
        Vector3 to = from + camera.ProjectRayNormal(mousePos) * 1000f;

        var spaceState = GetWorld3D().DirectSpaceState;
        var result = spaceState.IntersectRay(PhysicsRayQueryParameters3D.Create(from, to));
        if (result.Count == 0)
            return;
        if (!result.TryGetValue("collider", out var colliderVar))
            return;

        var collider = colliderVar.AsGodotObject() as Node;
        if (collider == null)
            return;

        Node current = collider;
        while (current != null)
        {
            if (current is Unit unit)
            {
                // The Dance (last_rite tier 4): while the caster carries `dancing`,
                // Shift+click swaps the selected eligible mover with this unit
                // (another eligible mover, or any enemy) as a free action.
                if (Input.IsKeyPressed(Key.Shift) && selectedUnit != null
                    && TryDanceSwap(selectedUnit, unit))
                    return;

                if (unit.IsPlayerControlled)
                {
                    inspectedEnemyUnit = null;
                    SelectUnit(unit);
                }
                else
                {
                    // If selected unit is a martial, try to attack
                    if (selectedUnit != null && selectedUnit.IsMartial)
                    {
                        TryMartialAttack(selectedUnit, unit);
                        return;
                    }
                    InspectEnemy(unit);
                }
                return;
            }

            if (current is HexTile tile)
            {
                TryMoveSelectedUnit(tile);
                return;
            }

            current = current.GetParent();
        }
    }

    private void OnLeftMousePressed(Vector2 screenPos)
    {
        if (isInDeploymentPhase)
        { TryHandleDeploymentClick(); return; }
        if (currentPhase != CombatPhase.PlayerTurn)
            return;

        _dragStartScreenPos = screenPos;
        _isDraggingUnit = false;
        _draggedUnit = null;

        // Check if pressing on a player unit — potential drag start
        var hitUnit = GetUnitUnderMouse();
        if (hitUnit != null && hitUnit.IsPlayerControlled && hitUnit.Stats.IsAlive)
        {
            _draggedUnit = hitUnit;
            // Select it immediately so move tiles show
            if (hitUnit != selectedUnit)
                SelectUnit(hitUnit);
        }
    }

    private void OnLeftMouseReleased(Vector2 screenPos)
    {
        if (isInDeploymentPhase)
            return;
        if (currentPhase != CombatPhase.PlayerTurn)
        {
            // U3 window (2026-07-09): while a trigger priority window is open
            // during the enemy phase, a click on a friendly unit switches the
            // responder (selection only — movement/attacks stay phase-gated).
            if (_priorityWindowOpen)
            {
                var clicked = GetUnitUnderMouse();
                if (clicked != null && clicked.IsPlayerControlled && clicked.Stats.IsAlive)
                {
                    GD.Print($"[Priority] responder switched to {clicked.Name}.");
                    SelectUnit(clicked);
                    ClearMoveTiles();   // no move affordance in an enemy-phase window
                }
            }
            return;
        }

        float dragDist = screenPos.DistanceTo(_dragStartScreenPos);
        bool wasDrag = dragDist > DragThresholdPixels;

        if (wasDrag && _draggedUnit != null)
        {
            // Released after drag — try to move to tile under mouse
            var tileView = GetTileViewUnderMouse();
            if (tileView != null)
                TryMoveSelectedUnit(tileView);
        }
        else
        {
            // Short press — treat as normal click
            TryHandleMainPhaseClick();
        }

        _isDraggingUnit = false;
        _draggedUnit = null;
    }

    private Unit GetUnitUnderMouse()
    {
        var camera = GetViewport().GetCamera3D();
        if (camera == null)
            return null;

        Vector2 mousePos = GetViewport().GetMousePosition();
        Vector3 from = camera.ProjectRayOrigin(mousePos);
        Vector3 to = from + camera.ProjectRayNormal(mousePos) * 1000f;

        var result = GetWorld3D().DirectSpaceState
            .IntersectRay(PhysicsRayQueryParameters3D.Create(from, to));
        if (result.Count == 0)
            return null;
        if (!result.TryGetValue("collider", out var cv))
            return null;

        Node current = cv.AsGodotObject() as Node;
        while (current != null)
        {
            if (current is Unit u)
                return u;
            current = current.GetParent();
        }
        return null;
    }

    private HexTile GetTileViewUnderMouse()
    {
        var camera = GetViewport().GetCamera3D();
        if (camera == null)
            return null;

        Vector2 mousePos = GetViewport().GetMousePosition();
        Vector3 from = camera.ProjectRayOrigin(mousePos);
        Vector3 to = from + camera.ProjectRayNormal(mousePos) * 1000f;

        var result = GetWorld3D().DirectSpaceState
            .IntersectRay(PhysicsRayQueryParameters3D.Create(from, to));
        if (result.Count == 0)
            return null;
        if (!result.TryGetValue("collider", out var cv))
            return null;

        Node current = cv.AsGodotObject() as Node;
        while (current != null)
        {
            if (current is HexTile tile)
                return tile;
            current = current.GetParent();
        }
        return null;
    }

    private void OrientCameraForCombat()
    {
        var controller = GetNodeOrNull<CameraController>("../CameraController");
        if (controller == null || grid == null)
            return;

        // Compute player zone centroid
        Vector3 playerCenter = Vector3.Zero;
        int playerCount = 0;
        foreach (var zone in grid.SpawnZones)
        {
            if (zone.Side != HexGridManager.SpawnSide.Player)
                continue;
            foreach (var coord in zone.Tiles)
            {
                var tile = grid.GetTileView(coord);
                if (tile == null)
                    continue;
                playerCenter += tile.GlobalPosition;
                playerCount++;
            }
        }
        if (playerCount == 0)
            return;
        playerCenter /= playerCount;

        // Compute enemy zone centroid
        Vector3 enemyCenter = Vector3.Zero;
        int enemyCount = 0;
        foreach (var zone in grid.SpawnZones)
        {
            if (zone.Side != HexGridManager.SpawnSide.Enemy)
                continue;
            foreach (var coord in zone.Tiles)
            {
                var tile = grid.GetTileView(coord);
                if (tile == null)
                    continue;
                enemyCenter += tile.GlobalPosition;
                enemyCount++;
            }
        }
        if (enemyCount == 0)
            return;
        enemyCenter /= enemyCount;

        controller.FaceToward(playerCenter, enemyCenter);
    }

    private void InspectEnemy(Unit enemy)
    {
        if (enemy == null || !enemy.Stats.IsAlive)
            return;

        if (inspectedEnemyUnit != null)
        {
            inspectedEnemyUnit.SetSelected(false);
            inspectedEnemyUnit.SetDetailedBar(false);   // ← collapse old
        }

        if (selectedUnit != null)
            selectedUnit.SetSelected(false);

        selectedUnit = null;
        inspectedEnemyUnit = enemy;
        inspectedEnemyUnit.SetSelected(true);
        inspectedEnemyUnit.SetDetailedBar(true);        // ← expand new

        ClearMoveTiles();
        RefreshSelectedUnitUI();
        RefreshPlayerUnitBar();
    }

    /// <summary>V2 threat-range overlay (combat_ui_v2 §7a, as superseded): every
    /// tile this enemy could reach-AND-attack next turn — movement envelope
    /// (tag-adjusted: immobile stays put) expanded by AttackRange. Pure
    /// arithmetic over the same reachability the AI uses; zero simulation.
    /// Complements the locked-intent reticles: reticle = THIS turn's committed
    /// strike, this zone = NEXT turn's possibility space.</summary>
    private void ShowEnemyThreatZone(Unit enemy)
    {
        if (_zoneRenderer == null || enemy?.CurrentTile == null)
            return;

        // Tiered threat (2026-07-13). AP economy: 1 AP = one move of up to
        // EffectiveMoveRange tiles; attack costs 1 (melee) / 2 (ranged) AP; multi-move
        // + multi-attack allowed. A tile's level = the most attacks the enemy could land
        // on a unit standing there next turn. Level 0 = reachable but no attack affordable
        // (movement only) -> faint; higher -> blood-red.
        int ap = Mathf.Max(0, enemy.MaxActionPoints);
        int moveRange = Mathf.Max(1, enemy.EffectiveMoveRange);
        int attackCost = Mathf.Max(1, MartialAPCosts.AttackCost(enemy.AttackRange));
        int attackRange = Mathf.Max(1, enemy.AttackRange);
        var start = enemy.CurrentTile.Axial;

        // Stand-tiles -> AP spent reaching them (moveActions = ceil(pathCost / moveRange)).
        var standMoves = new Dictionary<Vector2I, int> { [start] = 0 };
        if (ap > 0 && !enemy.HasBehaviorTag("immobile"))
        {
            foreach (var kv in grid.GetReachableTilesWithBudget(enemy, ap * moveRange))
            {
                int moves = Mathf.CeilToInt(kv.Value / (float)moveRange);
                if (moves >= 1 && moves <= ap
                    && (!standMoves.TryGetValue(kv.Key, out var m) || moves < m))
                    standMoves[kv.Key] = moves;
            }
        }

        // Per-tile threat level = max over stand-tiles S (within attack range of T) of
        // floor((AP - moves(S)) / attackCost). Baseline: every stand-tile is level 0.
        var level = new Dictionary<Vector2I, int>();
        foreach (var st in standMoves.Keys)
            level[st] = 0;
        foreach (var kv in standMoves)
        {
            int attacks = (ap - kv.Value) / attackCost;
            if (attacks <= 0)
                continue;
            AddThreatFootprint(kv.Key, attackRange, attacks, level);
        }
        if (level.Count == 0)
            level[start] = 0;

        int maxLevel = 0;
        foreach (var v in level.Values) if (v > maxLevel) maxLevel = v;
        GD.Print($"[ThreatZone] {enemy.Name} AP={ap} moveRange={moveRange} atkCost={attackCost} atkRange={attackRange} tiles={level.Count} maxHits={maxLevel}");

        _zoneRenderer.ShowEnemyZone(level, grid);
    }

    /// <summary>Rings 1..radius around center (attacks ignore walls — ranged shoots over
    /// gaps). Raises each tile's threat level to <paramref name="attacks"/> if higher. The
    /// center (the enemy's stand-tile) is excluded — a unit can't stand on it.</summary>
    private void AddThreatFootprint(Vector2I center, int radius, int attacks,
        Dictionary<Vector2I, int> level)
    {
        var seen = new HashSet<Vector2I> { center };
        var frontier = new List<Vector2I> { center };
        for (int r = 0; r < radius; r++)
        {
            var next = new List<Vector2I>();
            foreach (var cc in frontier)
            {
                foreach (var n in grid.GetNeighbors(cc))
                {
                    if (!seen.Add(n) || grid.GetTile(n) == null)
                        continue;
                    next.Add(n);
                    if (!level.TryGetValue(n, out var prev) || attacks > prev)
                        level[n] = attacks;
                }
            }
            frontier = next;
        }
    }

    /// <summary>V2: hovering a roster row = hovering the unit in-world (§6).</summary>
    private void OnEnemyRowHovered(int index, bool entering)
    {
        if (index < 0 || index >= enemyUnits.Count)
            return;
        var enemy = enemyUnits[index];
        if (enemy == null || !IsInstanceValid(enemy))
            return;

        if (entering && enemy.Stats.IsAlive)
        {
            enemy.SetHovered(true);
            ShowEnemyThreatZone(enemy);
        }
        else
        {
            enemy.SetHovered(false);
            if (selectedUnit != null)
                ShowMoveTilesWithCost(selectedUnit);
            else
                _zoneRenderer?.Clear();
        }
    }

    /// <summary>Returns the axial coord of the tile currently under the mouse, or null.</summary>
    private Vector2I? GetHoveredTile()
    {
        var camera = GetViewport().GetCamera3D();
        if (camera == null)
            return null;

        Vector2 mousePos = GetViewport().GetMousePosition();
        Vector3 from = camera.ProjectRayOrigin(mousePos);
        Vector3 to = from + camera.ProjectRayNormal(mousePos) * 1000f;

        var result = GetWorld3D().DirectSpaceState
            .IntersectRay(PhysicsRayQueryParameters3D.Create(from, to));
        if (result.Count == 0)
            return null;
        if (!result.TryGetValue("collider", out var cv))
            return null;

        Node current = cv.AsGodotObject() as Node;
        while (current != null)
        {
            if (current is HexTile tile)
                return tile.Axial;
            current = current.GetParent();
        }
        return null;
    }

    private void TryInspectClick()
    {
        var camera = GetViewport().GetCamera3D();
        if (camera == null)
            return;

        Vector2 mousePos = GetViewport().GetMousePosition();
        Vector3 from = camera.ProjectRayOrigin(mousePos);
        Vector3 to = from + camera.ProjectRayNormal(mousePos) * 1000f;

        var result = GetWorld3D().DirectSpaceState
            .IntersectRay(PhysicsRayQueryParameters3D.Create(from, to));
        if (result.Count == 0)
            return;
        if (!result.TryGetValue("collider", out var cv))
            return;

        Node current = cv.AsGodotObject() as Node;
        while (current != null)
        {
            if (current is Unit unit && !unit.IsPlayerControlled)
            {
                InspectEnemy(unit);
                return;
            }
            current = current.GetParent();
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Unit selection / movement
    // ═══════════════════════════════════════════════════════════════════════

    private void SelectUnit(Unit unit)
    {
        if (unit == null || !unit.IsPlayerControlled)
            return;

        // Collapse previous selection's bar
        selectedUnit?.SetDetailedBar(false);
        if (selectedUnit != null)
            selectedUnit.SetSelected(false);

        if (inspectedEnemyUnit != null)
        {
            inspectedEnemyUnit.SetSelected(false);
            inspectedEnemyUnit.SetDetailedBar(false);
            inspectedEnemyUnit = null;
        }

        selectedUnit = unit;
        selectedUnit.SetSelected(true);
        selectedUnit.SetDetailedBar(true);
        ClearTargetHighlight();

        CombatCamera?.FocusOn(unit);

        ClearMoveTiles();
        ShowMoveTilesWithCost(unit);
        ShowConstructAura(unit);   // §8: ring this unit's aura radius if it has one

        // ── Swap deck / hide hand for martial units ──
        if (!unit.IsMartial && unit.DeckData != null && deckManager != null)
        {
            deckManager.SetActiveDeck(unit.DeckData);
            deckManager.PrintDeckState();
            // Show hand UI for arcane units
            if (deckManager.HandContainer != null)
                deckManager.HandContainer.Visible = true;
        }
        else if (unit.IsMartial)
        {
            // Hide hand UI entirely — martial units use StanceUI instead
            if (deckManager?.HandContainer != null)
                deckManager.HandContainer.Visible = false;
        }

        // ── Swap attunement UI ──
        schoolAttunementUI?.ShowForUnit(selectedUnit);

        // ── Sync mana for this unit ──
        if (State.Mana.ContainsKey(Me))
            State.Mana[Me] = unit.Stats.Mana;

        RefreshSelectedUnitUI();
        RefreshPlayerUnitBar();
        RefreshDeckCounts();
        deckUiManager?.RefreshAffordability();

        GD.Print($"Selected: {unit.Name}  AP={unit.CurrentActionPoints}/{unit.MaxActionPoints}");
    }

    private void TrySelectUnitByIndex(int index)
    {
        var alive = playerUnits.Where(u => u != null && u.Stats.IsAlive).ToList();
        if (index < 0 || index >= alive.Count)
            return;
        SelectUnit(alive[index]);
    }

    private void TryInspectEnemyByIndex(int index)
    {
        var alive = enemyUnits
            .Where(u => u != null && u.Stats.IsAlive)
            .ToList();

        if (index < 0 || index >= alive.Count)
            return;
        InspectEnemy(alive[index]);
    }

    private void CycleSelectedUnit(int direction)
    {
        var alive = playerUnits.Where(u => u != null && u.Stats.IsAlive).ToList();
        if (alive.Count == 0)
            return;

        int currentIndex = selectedUnit != null ? alive.IndexOf(selectedUnit) : -1;
        int nextIndex = (currentIndex + direction + alive.Count) % alive.Count;
        SelectUnit(alive[nextIndex]);
    }

    private void ShowMoveTilesWithCost(Unit unit)
    {
        if (_zoneRenderer == null)
            return;
        if (!unit.CanMove())
            return;

        var costMap = grid.GetReachableTilesWithCost(unit);

        currentMoveTiles.Clear();
        foreach (var k in costMap.Keys)
            currentMoveTiles.Add(k);

        _zoneRenderer.ShowPlayerZone(costMap, grid);
    }

    private void TryMoveSelectedUnit(HexTile tileView)
    {
        if (selectedUnit == null || tileView == null)
            return;
        if (!currentMoveTiles.Contains(tileView.Axial))
        {
            GD.Print("Tile not in range.");
            return;
        }

        var tileData = grid.GetTile(tileView.Axial);
        if (tileData == null)
            return;

        if (!selectedUnit.CanMove())
        {
            combatUI?.AppendActionLog($"{selectedUnit.Name} is immobilized!");
            return;
        }

        var debugTile = grid.GetTile(tileView.Axial);

        if (selectedUnit.TryMoveTo(grid, tileData))
        {
            GD.Print($"{selectedUnit.Name} moved to {tileData.Axial}");
            combatUI?.AppendActionLog($"{selectedUnit.Name} moves.");
            ClearMoveTiles();
            ShowMoveTilesWithCost(selectedUnit);
            RefreshSelectedUnitUI();
            RefreshPlayerUnitBar();
        }
    }

    private void ClearMoveTiles()
    {
        _zoneRenderer?.Clear();
        // Also clear any residual tile color highlights
        foreach (var coord in currentMoveTiles)
            grid.GetTileView(coord)?.SetMoveHighlight(false);
        currentMoveTiles.Clear();
        ClearConstructAura();   // §8: aura ring tracks the current selection's world highlights
    }

    // ── §8 aura hover extents ────────────────────────────────────────────────

    /// <summary>Rings the tiles inside a selected PLAYER CONSTRUCT's aura radius
    /// (combat_ui §8 / §11 aura-extent contract), reusing the range-highlight
    /// tile machinery. Range = the widest of its live aura ranges (Sentinel
    /// `AuraArmorRange`, Lattice/Foundry `AuraDamageRange`); Foundry's board-wide
    /// range naturally rings the whole board. Aura sources are all immobile, so
    /// the ring is static for the combat — no move refresh needed. No-op for
    /// non-construct or aura-less units.</summary>
    private void ShowConstructAura(Unit unit)
    {
        ClearConstructAura();

        if (unit == null || !unit.IsPlayerControlled || !unit.IsConstruct
            || unit.CurrentTile == null || grid == null)
            return;

        int range = 0;
        if (unit.AuraArmor > 0)  range = Math.Max(range, unit.AuraArmorRange);
        if (unit.AuraDamage > 0) range = Math.Max(range, unit.AuraDamageRange);
        if (range <= 0)
            return;

        var center = unit.CurrentTile.Axial;
        foreach (var coord in grid.Tiles.Keys)
        {
            if (coord == center)
                continue;
            if (grid.Distance(center, coord) <= range)
            {
                _auraHighlightTiles.Add(coord);
                grid.GetTileView(coord)?.SetRangeHighlight(false, true);   // border ring
            }
        }
    }

    private void ClearConstructAura()
    {
        if (_auraHighlightTiles.Count == 0)
            return;
        foreach (var coord in _auraHighlightTiles)
            grid?.GetTileView(coord)?.SetRangeHighlight(false, false);
        _auraHighlightTiles.Clear();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // The Dance (necromancer_last_rite tier 4)
    // ───────────────────────────────────────────────────────────────────────
    // "This turn: you and all spirits may swap positions with each other or any
    // enemy as free actions." The bottom half applies `dancing` (duration 1) to
    // the caster; TickStatuses expires it at the caster's next turn start, so it
    // lasts exactly the turn it was cast. While it is up, a swap is a free action
    // (no AP / move-point cost). Gesture: select an eligible mover, then Shift+
    // click the swap target.
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>The living player unit currently carrying `dancing`, or null.</summary>
    private Unit DanceCaster()
    {
        foreach (var u in playerUnits)
            if (u != null && u.Stats.IsAlive && u.HasStatus("dancing"))
                return u;
        return null;
    }

    /// <summary>A mover eligible to dance: the dancing caster itself, or one of
    /// its living spirits.</summary>
    private static bool IsDanceEligible(Unit u, Unit caster)
    {
        if (u == null || caster == null || !u.Stats.IsAlive)
            return false;
        if (u == caster)
            return true;
        return u.IsSpirit && u.SummonerTeamId == caster.TeamId;
    }

    /// <summary>Free position swap for The Dance. `mover` must be an eligible
    /// mover; `target` may be another eligible mover or any living enemy.
    /// Returns true iff the swap was performed.</summary>
    private bool TryDanceSwap(Unit mover, Unit target)
    {
        var caster = DanceCaster();
        if (caster == null)
            return false;
        if (mover == null || target == null || mover == target)
            return false;
        if (!IsDanceEligible(mover, caster))
            return false;

        // Enemy defined by team, not IsPlayerControlled, so non-spirit friendly
        // summons (colossi, etc.) are correctly excluded rather than swappable.
        bool targetIsEnemy = target.Stats.IsAlive && target.TeamId != caster.TeamId;
        if (!IsDanceEligible(target, caster) && !targetIsEnemy)
            return false;
        if (mover.CurrentTile == null || target.CurrentTile == null)
            return false;

        var moverTile = mover.CurrentTile;
        var targetTile = target.CurrentTile;
        moverTile.ClearOccupant(mover);
        targetTile.ClearOccupant(target);
        mover.PlaceOnTile(targetTile);
        target.PlaceOnTile(moverTile);

        combatUI?.AppendActionLog($"The Dance: {mover.Name} swaps with {target.Name}.");
        GD.Print($"[Dance] {mover.Name} <-> {target.Name} (free action).");

        ClearMoveTiles();
        ShowMoveTilesWithCost(selectedUnit);
        RefreshSelectedUnitUI();
        RefreshPlayerUnitBar();
        return true;
    }

    private void TryMartialAttack(Unit attacker, Unit target)
    {
        if (attacker == null || target == null)
            return;
        if (!attacker.IsMartial)
            return;
        if (!attacker.CanAct())
        {
            combatUI?.AppendActionLog($"{attacker.Name} is frozen!");
            return;
        }

        int effectiveRange = attacker.AttackRange;
        if (attacker.ActiveStance != null)
            effectiveRange += attacker.ActiveStance.AttackRangeBonus;

        int dist = grid.Distance(attacker.CurrentTile, target.CurrentTile);
        if (dist > effectiveRange)
        {
            combatUI?.AppendActionLog(
                $"{attacker.Name} — target out of range (dist={dist} range={effectiveRange}).");
            return;
        }

        // After the range check, before AP cost:
        if (effectiveRange > 1)  // only ranged attacks need LOS
        {
            if (!grid.HasLineOfSight(attacker.CurrentTile.Axial, target.CurrentTile.Axial))
            {
                combatUI?.AppendActionLog($"{attacker.Name} has no line of sight!");
                return;
            }
        }

        // ── AP cost ───────────────────────────────────────────────────────
        bool isRanged = effectiveRange > 1;
        int apCost = isRanged ? MartialAPCosts.AttackRanged : MartialAPCosts.AttackMelee;

        if (!attacker.TrySpendAP(apCost))
        {
            combatUI?.AppendActionLog(
                $"{attacker.Name} needs {apCost} AP to attack " +
                $"(has {attacker.CurrentActionPoints}).");
            return;
        }

        // Aimed: requires no movement
        if (attacker.ActiveStance?.SpecialTag == StanceSpecialTag.AimedRequiresNoMove
            && attacker.Stats.HasMoved)
        {
            combatUI?.AppendActionLog(
                $"{attacker.Name} — Aimed requires no movement this turn.");
            // Refund AP since we're blocking
            attacker.CurrentActionPoints += apCost;
            return;
        }

        ResolveMartialAttack(attacker, target);

        // Refresh move tiles — AP changed so reachable range may shrink
        ClearMoveTiles();
        ShowMoveTilesWithCost(selectedUnit);
    }

    public bool TrySwitchStance(Unit unit, StanceDefinition newStance)
    {
        if (unit == null || !unit.IsMartial)
            return false;
        if (unit.HasSwitchedStanceThisTurn)
        {
            combatUI?.AppendActionLog($"{unit.Name} has already switched stance this turn.");
            return false;
        }
        if (!unit.AvailableStances.Contains(newStance))
        {
            combatUI?.AppendActionLog($"{unit.Name} doesn't have access to {newStance.DisplayName}.");
            return false;
        }
        if (!unit.TrySpendAP(MartialAPCosts.SwitchStance))
        {
            combatUI?.AppendActionLog(
                $"{unit.Name} needs {MartialAPCosts.SwitchStance} AP to switch stance.");
            return false;
        }

        // Remove previous stance passive armor before switching
        if (unit.ActiveStance != null)
            unit.Stats.Armor = Math.Max(0,
                unit.Stats.Armor - unit.ActiveStance.PassiveArmorBonus);

        unit.ActiveStance = newStance;
        unit.HasSwitchedStanceThisTurn = true;

        // Apply new stance passives immediately
        ApplyMartialStancePassives(unit);

        combatUI?.AppendActionLog(
            $"{unit.Name} switches to {newStance.DisplayName} stance.");

        RefreshSelectedUnitUI();
        RefreshPlayerUnitBar();
        return true;
    }

    private void ResolveMartialAttack(Unit attacker, Unit target)
    {
        var stance = attacker.ActiveStance;

        // ── Compute base damage ───────────────────────────────────────────
        int damage = attacker.AttackDamage;

        // Equipment bonus (from loadout)
        var loadout = EquipmentLoadout.Get(attacker.CompanionId);
        if (loadout != null)
            damage += loadout.BonusAttackDamage;

        // Stance passive damage bonus
        if (stance != null)
            damage += stance.AttackDamageBonus;

        // Berserk scaling
        if (stance?.SpecialTag == StanceSpecialTag.BerserkScaling)
        {
            int missingHP = attacker.Stats.MaxHealth - attacker.Stats.Health;
            int berserkBonus = Math.Min(missingHP / 5, stance.SpecialTagValue);
            damage += berserkBonus;
        }

        // Ambush: double damage on first attack of combat
        if (stance?.SpecialTag == StanceSpecialTag.AmbushFirstStrike
            && !attacker.HasAttackedThisCombat)
        {
            damage *= 2;
        }

        // Aimed: only apply bonus if unit hasn't moved
        if (stance?.SpecialTag == StanceSpecialTag.AimedRequiresNoMove
            && attacker.Stats.HasMoved)
        {
            damage -= stance.AttackDamageBonus; // remove the bonus
        }

        // Armor-piercing
        bool ignoresArmor = stance?.AttackIgnoresArmor ?? false;

        // ── Wildlife behavior tags (2026-07-12) ──────────────────────────
        // Pack: +1 damage per OTHER living pack-tagged ally (wolves hunt together).
        if (attacker.HasBehaviorTag("pack"))
        {
            int packmates = 0;
            foreach (var u in State.UnitsInPlay)
                if (u != null && u != attacker && u.Stats.IsAlive
                    && u.TeamId == attacker.TeamId && u.HasBehaviorTag("pack"))
                    packmates++;
            if (packmates > 0)
            {
                damage += packmates;
                combatUI?.AppendActionLog($"[Pack] {attacker.Name} +{packmates} — the pack hunts together.");
            }
        }

        // Charge: momentum — 2+ tiles covered this turn before the hit = +3.
        if (attacker.HasBehaviorTag("charge") && attacker.TilesMovedThisTurn >= 2)
        {
            damage += 3;
            combatUI?.AppendActionLog($"[Charge] {attacker.Name} slams in with momentum — +3 damage.");
        }

        // Marked target bonus
        int markedBonus = 0;
        if (target.HasStatus("marked"))
        {
            // Find the SpecialTagValue from whichever unit applied the mark
            // Simplified: use a fixed +3 for now
            markedBonus = 3;
            target.RemoveStatus("marked");
            combatUI?.AppendActionLog($"[Mark] {target.Name} was marked — +{markedBonus} damage!");
        }

        damage = Math.Max(1, damage + markedBonus);

        // ── Log and apply damage ──────────────────────────────────────────
        string stanceName = stance != null ? $" [{stance.DisplayName}]" : "";
        string dmgMsg = $"{attacker.Name}{stanceName} attacks {target.Name} for {damage} damage.";
        GD.Print(dmgMsg);
        combatUI?.AppendActionLog(dmgMsg);

        if (ignoresArmor)
        {
            // Bypass armor — apply directly to health
            int savedArmor = target.Stats.Armor;
            target.Stats.Armor = 0;
            target.ApplyDamage(damage);
            if (target.Stats.IsAlive)
                target.Stats.Armor = savedArmor;
            combatUI?.AppendActionLog($"[Aimed] Armor ignored.");
        }
        else
        {
            target.ApplyDamage(damage);
        }

        // ── AoE: Reckless hits all adjacent enemies ────────────────────────
        if (stance?.SpecialTag == StanceSpecialTag.AoeAdjacent
            && attacker.CurrentTile != null)
        {
            foreach (var neighbor in grid.GetNeighbors(attacker.CurrentTile.Axial))
            {
                // Captured once — lethal splash clears Occupant before the log
                // line (2026-07-09 sweep).
                var splashVictim = grid.GetTile(neighbor)?.Occupant;
                if (splashVictim == null)
                    continue;
                if (splashVictim == target)
                    continue;        // already hit
                if (splashVictim.TeamId == attacker.TeamId)
                    continue; // skip allies
                splashVictim.ApplyDamage(damage);
                combatUI?.AppendActionLog($"[Reckless] {splashVictim.Name} takes {damage} damage.");
            }
        }

        // ── On-hit effects ────────────────────────────────────────────────
        if (target.Stats.IsAlive && stance?.OnHitStatusName != null)
        {
            target.ApplyStatus(stance.OnHitStatusName, stance.OnHitStatusDuration);
            combatUI?.AppendActionLog($"[{stance.DisplayName}] {target.Name} is " +
                                      $"{stance.OnHitStatusName}.");
        }

        // Shield gain on hit
        if (stance?.OnHitSelfShieldGain > 0)
        {
            attacker.Stats.Shield += stance.OnHitSelfShieldGain;
            attacker.RefreshHealthBar();
            combatUI?.AppendActionLog($"[{stance.DisplayName}] {attacker.Name} " +
                                      $"gains {stance.OnHitSelfShieldGain} shield.");
        }

        // Self-damage (Reckless)
        if (stance?.OnHitSelfDamage > 0)
        {
            attacker.ApplyDamage(stance.OnHitSelfDamage);
            combatUI?.AppendActionLog($"[{stance.DisplayName}] {attacker.Name} " +
                                      $"takes {stance.OnHitSelfDamage} recoil damage.");
        }

        // Push target
        if (stance?.AttackPushTiles > 0 && target.Stats.IsAlive)
        {
            // Reuse existing push logic via PushEffect direction
            // Simple version: find tile furthest from attacker within 1 step
            if (attacker.CurrentTile != null && target.CurrentTile != null)
            {
                var casterPos = attacker.CurrentTile.Axial;
                TileData bestTile = null;
                int bestDist = -1;
                foreach (var neighbor in grid.GetNeighbors(target.CurrentTile.Axial))
                {
                    var td = grid.GetTile(neighbor);
                    if (td == null || !td.CanEnter(target))
                        continue;
                    int d = grid.Distance(casterPos, neighbor);
                    if (d > bestDist)
                    { bestDist = d; bestTile = td; }
                }
                if (bestTile != null)
                {
                    target.CurrentTile.ClearOccupant(target);
                    target.PlaceOnTile(bestTile);
                    combatUI?.AppendActionLog($"[{stance.DisplayName}] {target.Name} pushed.");
                }
            }
        }

        // ── Skirmish: free move after attack ──────────────────────────────
        if (stance?.SpecialTag == StanceSpecialTag.SkirmishDash)
        {
            attacker.Stats.BonusMoveRange += stance.SpecialTagValue;
            combatUI?.AppendActionLog($"[Skirmish] {attacker.Name} gains " +
                                      $"{stance.SpecialTagValue} free move points.");
            // Recalculate reachable tiles
            ClearMoveTiles();
            var reachable = grid.GetReachableTiles(attacker);
            foreach (var coord in reachable)
                currentMoveTiles.Add(coord);
            ShowMoveTilesWithCost(selectedUnit);
        }

        // ── Guardian: aura applied at stance start, not on attack ─────────
        // (handled in ApplyMartialStancePassives)

        // ── Mark attack tracking ──────────────────────────────────────────
        attacker.HasAttackedThisCombat = true;
        attacker.HasAttackedThisTurn = true;

        // Q2 (§7a): onAttack item procs ride the trigger stack (auto-passing).
        // Queued with the struck target captured; drained now unless a priority
        // window already owns the drain. Fires only when the target survived.
        if (target.Stats.IsAlive)
        {
            QueueItemAttackTriggers(attacker, target);
            if (!_priorityWindowOpen)
                KickTriggerDrain();
        }

        RefreshSelectedUnitUI();
        RefreshEnemyRoster();
        RefreshPlayerUnitBar();

        // Check combat end — attack may have killed the target
        _pruneNeeded = true;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Turn flow
    // ═══════════════════════════════════════════════════════════════════════

    private void StartPlayerTurn()
    {
        State.EnemyPhaseContext = false;   // Time Bank: reaction costs revert to pure mana

        // Reset extra-turn flag and per-round tracking
        if (!_isExtraTurn)
        {
            // New round — reset ExtraTurnFiredThisRound on active effects
            if (State.ActiveEffects != null)
            {
                foreach (var eff in State.ActiveEffects.OfType<ExtraTurnPersistentEffect>())
                    eff.ExtraTurnFiredThisRound = false;
            }
        }
        _isExtraTurn = false;

        currentPhase = CombatPhase.PlayerTurn;
        enemyPhaseRunning = false;

        foreach (var unit in playerUnits)
        {
            if (unit == null || !IsInstanceValid(unit) || !unit.Stats.IsAlive)
                continue;
            unit.StartTurn();

            // Apply martial stance passives
            if (unit.IsMartial && unit.ActiveStance != null)
                ApplyMartialStancePassives(unit);

            unit.Attunement?.Decay();
            State.SpellsCastThisTurn = 0;
            State.EnemiesKilledThisTurn = 0;
            State.ActionsNegatedThisTurn = 0;
            unit.RetaliateDamage = 0;   // Riposte lasts through the enemy turn only

            int wildHeal = State.Growth?.GetUpkeepHeal(unit) ?? 0;
            if (wildHeal > 0)
            {
                unit.Stats.Health = Math.Min(unit.Stats.MaxHealth, unit.Stats.Health + wildHeal);
                unit.RefreshHealthBar();
            }

            if (unit.Attunement is ArcaneAttunement arcane)
                arcane.OnTurnStart();

            State.Memorials.Tick();
            State.Glyphs?.Tick(State);

            if (unit.DeckData != null)
            {
                var drawn = unit.DeckData.DrawToFull();
                foreach (var card in drawn)
                    GD.Print($"[{unit.Name}] Drew: {card.TopHalf?.Name ?? card.CardName}");
            }

            // ── Equipment passive: restore mana on turn start ────────────
            foreach (var (tag, value) in unit.EquipmentPassives)
            {
                if (tag == ItemPassiveTag.RestoreManaOnTurnStart)
                {
                    unit.GainMana(value);
                    GD.Print($"[Equipment] {unit.Name} restores {value} mana (Mana Crystal).");
                }
            }
        }

        // Prune before ticking persistent effects so freed units don't
        // appear in UnitsInPlay when Maelstrom iterates it.
        PruneDeadUnits();

        // Tick persistent effects AFTER units have started their turn
        if (State.ActiveEffects != null)
        {
            foreach (var effect in State.ActiveEffects.ToList())
            {
                effect.Tick(State);
                if (effect.IsExpired)
                    State.ActiveEffects.Remove(effect);
            }
        }

        // ── Tick Almanac entries ──────────────────────────────────────────────────
        if (State.Almanac != null && State.Almanac.Count > 0)
        {
            foreach (var entry in State.Almanac.ToList())
            {
                entry.Tick();
                if (entry.IsReady)
                {
                    GD.Print($"[Almanac] Firing scheduled entry: {entry.Label}.");
                    entry.Child?.Resolve(State, entry.Caster, entry.Targets, entry.Snapshot);
                    State.Almanac.Remove(entry);
                }
            }
        }

        // ── Tick anchor durations ─────────────────────────────────────────────────
        foreach (var unit in playerUnits)
        {
            if (unit == null || !unit.Stats.IsAlive)
                continue;
            if (unit.AnchorTurnsRemaining > 0)
            {
                unit.AnchorTurnsRemaining--;
                if (unit.AnchorTurnsRemaining <= 0)
                {
                    unit.AnchorCoord = null;
                    GD.Print($"[Anchor] {unit.Name}'s anchor expired.");
                }
            }
        }

        // ── Tick phase-tile duration ──────────────────────────────────────────────
        if (State.PhaseTileTurnsRemaining > 0)
        {
            State.PhaseTileTurnsRemaining--;
            if (State.PhaseTileTurnsRemaining <= 0)
            {
                State.PhaseTiles?.Clear();
                GD.Print("[PhaseTiles] Phase network expired.");
            }
        }

        // Q2 (§7a): item AURAS (§5 states, not stack events) recompute here —
        // regen auras heal adjacent allies at each of your turn starts.
        ApplyItemAuras();

        ProcessStatusEffects(playerUnits);
        ApplyHazardDamage(playerUnits);

        // Cleanups (imbue path callbacks, etc.)
        if (State.OnTurnEndCleanups != null)
        {
            foreach (var cleanup in State.OnTurnEndCleanups)
                cleanup();
            State.OnTurnEndCleanups.Clear();
        }

        // Auto-select first living unit
        selectedUnit = null;
        inspectedEnemyUnit = null;
        ClearMoveTiles();

        foreach (var unit in playerUnits)
        {
            if (unit != null && IsInstanceValid(unit) && unit.Stats.IsAlive)
            {
                SelectUnit(unit);
                break;
            }
        }

        GD.Print($"=== Round {roundNumber}: Player Turn ===");
        combatUI?.AppendActionLog($"── Round {roundNumber} ──");
        schoolAttunementUI?.Refresh();
        RefreshAllUI();
    }

    private async void EndPlayerTurn()
    {
        _zoneRenderer?.Clear();

        foreach (var unit in playerUnits)
        {
            if (unit.Attunement is WeaveAttunement w)
                w.OnTurnEnd(State.Glyphs.CountFriendly(unit.TeamId) > 0);

            DiscardOverflowCards(unit);

            // Time Bank (2026-07-10): unspent mana becomes Foresight.
            if (unit.Attunement is FateAttunement fateBank)
                fateBank.BankUnspentMana(unit.Stats.Mana);

            unit.Stats.Shield = 0;

            // Bulwark (2026-07-12): a bear that did NOT attack this turn braces —
            // shield that lives through the enemy turn (granted after the zeroing
            // above, cleared by next turn's zeroing). Hit hard OR be tough.
            if (unit.HasBehaviorTag("bulwark") && !unit.HasAttackedThisTurn && unit.Stats.IsAlive)
            {
                unit.Stats.Shield += 4;
                combatUI?.AppendActionLog($"[Bulwark] {unit.Name} braces — +4 shield until your next turn.");
            }

            unit.RefreshHealthBar();

            // Spirit on-kill riders last a single turn.
            unit.CreateMemorialOnKill = false;
            unit.DrawOnKillCount = 0;
        }

        if (currentPhase != CombatPhase.PlayerTurn)
            return;
        selectedUnit = null;
        inspectedEnemyUnit = null;
        ClearMoveTiles();
        GD.Print("=== Player Turn End ===");
        RefreshPhaseUI();

        // ── Extra turn check ──────────────────────────────────────────────────────
        var extraTurn = State.ActiveEffects?
            .OfType<ExtraTurnPersistentEffect>()
            .FirstOrDefault(e => !e.IsExpired && e.HasExtraTurn);

        if (extraTurn != null)
        {
            extraTurn.ExtraTurnFiredThisRound = true;
            _isExtraTurn = true;

            // Set limited resources for the extra turn
            State.Mana[Me] = extraTurn.ExtraMana;
            foreach (var unit in playerUnits)
            {
                if (unit == null || !unit.Stats.IsAlive)
                    continue;
                unit.DeckData?.Draw(extraTurn.ExtraDraw);
                unit.Stats.MovePoints = unit.Stats.BaseSpeed;
                unit.Stats.BonusMoveRange = 0;
                unit.CurrentActionPoints = unit.MaxActionPoints;
            }

            GD.Print($"[ExtraTurn] Extra turn: {extraTurn.ExtraMana} mana, draw {extraTurn.ExtraDraw}.");
            StartPlayerTurn();
            return; // Don't call StartEnemyTurn — constructs hold until the round actually ends
        }

        await RunConstructPhase();   // ← only on the path that hands off to the enemy

        StartEnemyTurn();
    }

    private void OnEndTurnPressed()
    {
        if (currentPhase != CombatPhase.PlayerTurn)
            return;
        // U3 hardening (2026-07-09): the trigger drain awaits _priorityPassed —
        // ending the turn mid-window would race the drain loop. Pass first.
        if (_priorityWindowOpen)
        {
            combatUI?.AppendActionLog("Resolve the stack first (Pass).");
            GD.Print("[Priority] End Turn blocked — window open.");
            return;
        }
        EndPlayerTurn();
    }

    private void DiscardOverflowCards(Unit unit)
    {
        if (unit?.DeckData == null)
            return;

        int overflow = unit.DeckData.Hand.Count - unit.DeckData.MaxHandSize;
        if (overflow <= 0)
            return;

        for (int i = 0; i < overflow; i++)
        {
            // Always discard index 0 — the oldest card
            var dropped = unit.DeckData.Hand[0];
            unit.DeckData.Hand.RemoveAt(0);
            unit.DeckData.DiscardPile.Add(dropped);
            combatUI?.AppendActionLog($"{dropped.TopHalf?.Name ?? dropped.CardName} discarded (overflow).");
        }

        // Refresh UI and clear all discard flags
        deckManager?.DrawCards(0);
    }

    private void ApplyMartialStancePassives(Unit unit)
    {
        var stance = unit.ActiveStance;
        if (stance == null)
            return;

        // Speed bonus (per-turn stance passive → movespeed currency)
        if (stance.PassiveSpeedBonus != 0)
            unit.Stats.BonusMoveRange += stance.PassiveSpeedBonus;

        // Armor bonus/penalty — temporary for this turn
        // We track the net stance armor separately to avoid double-applying
        // Simple: add directly; it resets next turn via StartTurn → stats rebuilt
        // For now store in a temp variable approach:
        if (stance.PassiveArmorBonus > 0)
        {
            unit.Stats.Armor += stance.PassiveArmorBonus;
            combatUI?.AppendActionLog($"[{stance.DisplayName}] {unit.Name} " +
                                      $"+{stance.PassiveArmorBonus} armor this turn.");
        }
        if (stance.PassiveArmorPenalty > 0)
        {
            unit.Stats.Armor = Math.Max(0, unit.Stats.Armor - stance.PassiveArmorPenalty);
        }

        // Guardian aura: give adjacent allies armor
        if (stance.SpecialTag == StanceSpecialTag.GuardianAura
            && unit.CurrentTile != null)
        {
            int auraArmor = stance.SpecialTagValue;
            foreach (var neighbor in grid.GetNeighbors(unit.CurrentTile.Axial))
            {
                var td = grid.GetTile(neighbor);
                if (td?.Occupant == null)
                    continue;
                if (td.Occupant.TeamId != unit.TeamId)
                    continue;
                if (td.Occupant == unit)
                    continue;
                td.Occupant.Stats.Armor += auraArmor;
                td.Occupant.RefreshHealthBar();
                combatUI?.AppendActionLog($"[Guardian] {td.Occupant.Name} " +
                                          $"gains {auraArmor} armor from {unit.Name}.");
            }
        }

        unit.RefreshHealthBar();
    }

    private async void StartEnemyTurn()
    {
        if (enemyPhaseRunning)
            return;

        currentPhase = CombatPhase.EnemyTurn;

        // ── Reset per-round Chronomancer modifiers ────────────────────────────────
        State.EnemySpellCostIncrease = 0;

        if (State.RedirectAllTurnsRemaining > 0)
        {
            State.RedirectAllTurnsRemaining--;
            GD.Print($"[RedirectAll] {State.RedirectAllTurnsRemaining} turn(s) remaining.");
        }

        enemyPhaseRunning = true;

        _zoneRenderer?.Clear();

        foreach (var unit in enemyUnits)
        {
            if (unit != null && unit.Stats.IsAlive)
            {
                unit.StartTurn();
            }
        }

        ProcessStatusEffects(enemyUnits);
        ApplyHazardDamage(enemyUnits);

        if (State.ActiveEffects != null)
        {
            foreach (var effect in State.ActiveEffects.ToList())
            {
                // Only tick zone effects, not player auras
                if (effect is MaelstromEffect)
                {
                    effect.Tick(State);
                    if (effect.IsExpired)
                        State.ActiveEffects.Remove(effect);
                }
            }
        }

        PruneDeadUnits();

        GD.Print("=== Enemy Turn Start ===");
        RefreshPhaseUI();
        RefreshSelectedUnitUI();

        await RunEnemyTurn();

        PruneDeadUnits();

        State.Growth?.TickEndOfEnemyTurn();

        if (CheckCombatEnd())
            return;

        roundNumber++;
        StartPlayerTurn();
    }

    private int CountAdjacentAllies(Unit unit, Vector2I fromCoord)
    {
        int count = 0;
        foreach (var neighbor in grid.GetNeighbors(fromCoord))
        {
            var tile = grid.GetTile(neighbor);
            if (tile?.Occupant == null)
                continue;
            if (tile.Occupant == unit)
                continue;
            if (tile.Occupant.TeamId == unit.TeamId && tile.Occupant.Stats.IsAlive)
                count++;
        }
        return count;
    }

    // ── Shared movement helpers ───────────────────────────────────────────

    /// Move one step toward target (existing behaviour, extracted).
    private async System.Threading.Tasks.Task MoveToward(Unit enemy, Unit target)
    {
        if (!IsValidActor(enemy) || !IsValidActor(target))
            return;

        // Find the actual next step along a navigable path, not just the
        // closest reachable tile — avoids getting stuck on obstacle walls
        var nextStep = grid.GetFirstStepToward(enemy, target.CurrentTile.Axial);

        if (nextStep == null)
        {
            // No path to target — try to get as close as possible
            // using the old greedy approach as fallback
            var moveOptions = grid.GetReachableTiles(enemy);
            Vector2I bestMove = enemy.CurrentTile.Axial;
            int bestDist = grid.Distance(enemy.CurrentTile, target.CurrentTile);

            foreach (var coord in moveOptions)
            {
                var tile = grid.GetTile(coord);
                if (tile == null)
                    continue;
                int d = grid.Distance(tile, target.CurrentTile);
                if (d < bestDist)
                { bestDist = d; bestMove = coord; }
            }

            if (bestMove == enemy.CurrentTile.Axial)
                return; // truly stuck

            nextStep = grid.GetTile(bestMove);
        }

        if (nextStep == null)
            return;

        // Only move there if it's within this turn's movement range
        int pathCost = grid.GetMoveCostTo(enemy, nextStep);
        if (pathCost < 0 || pathCost > enemy.EffectiveMoveRange)   // unified: honors rooted/slowed/grants
        {
            // First step is too far for one AP spend — shouldn't happen
            // since BFS neighbors are always adjacent, but guard anyway
            return;
        }

        if (enemy.TryMoveTo(grid, nextStep))
        {
            string msg = $"{enemy.Name} moves toward {target.Name}.";
            GD.Print(msg);
            combatUI?.AppendActionLog(msg);
            await ToSignal(GetTree().CreateTimer(0.35f), "timeout");
        }
    }

    /// Move to a specific distance from target (Ranger kiting).
    private async System.Threading.Tasks.Task MoveToDistance(Unit enemy, Unit target, int desiredDist)
    {
        var nextStep = grid.GetFirstStepToDistance(enemy, target.CurrentTile.Axial, desiredDist);
        if (nextStep == null)
            return;

        if (enemy.TryMoveTo(grid, nextStep))
        {
            string msg = $"{enemy.Name} repositions.";
            GD.Print(msg);
            combatUI?.AppendActionLog(msg);
            await ToSignal(GetTree().CreateTimer(0.35f), "timeout");
        }
    }

    /// Move away from target until at least minDist away (Ranger/Wizard retreat).
    private async System.Threading.Tasks.Task MoveAwayFrom(Unit enemy, Unit target, int minDist)
    {
        // Already far enough — no need to move
        if (grid.Distance(enemy.CurrentTile, target.CurrentTile) >= minDist)
            return;

        var nextStep = grid.GetFirstStepAwayFrom(enemy, target.CurrentTile.Axial);
        if (nextStep == null)
            return;

        if (enemy.TryMoveTo(grid, nextStep))
        {
            string msg = $"{enemy.Name} falls back.";
            GD.Print(msg);
            combatUI?.AppendActionLog(msg);
            await ToSignal(GetTree().CreateTimer(0.35f), "timeout");
        }
    }

    // ── Attack execution ─────────────────────────────────────────────────

    private async System.Threading.Tasks.Task PerformAttack(Unit enemy, Unit target)
    {
        int dmg = enemy.ModifyOutgoingAttackDamage(enemy.AttackDamage > 0 ? enemy.AttackDamage : 5);
        if (dmg <= 0)
        {
            combatUI?.AppendActionLog($"{enemy.Name}'s attack misses {target.Name}.");
            return;
        }

        string msg = $"{enemy.Name} attacks {target.Name} for {dmg} damage.";
        GD.Print(msg);
        combatUI?.AppendActionLog(msg);

        target.ApplyDamage(dmg);
        ResolveRetaliation(target, enemy);

        RefreshSelectedUnitUI();
        RefreshEnemyRoster();
        RefreshPlayerUnitBar();
        RefreshDeckCounts();
        await ToSignal(GetTree().CreateTimer(0.35f), "timeout");
    }

    /// <summary>Riposte: a defender with RetaliateDamage strikes back at whoever just hit it.</summary>
    private void ResolveRetaliation(Unit defender, Unit attacker)
    {
        if (defender == null || attacker == null)
            return;
        if (defender.RetaliateDamage <= 0 || !attacker.Stats.IsAlive || attacker.IsDeathQueued)
            return;

        combatUI?.AppendActionLog($"[Riposte] {defender.Name} strikes back at {attacker.Name} " +
                                  $"for {defender.RetaliateDamage}!");
        attacker.ApplyDamage(defender.RetaliateDamage);
    }

    private async System.Threading.Tasks.Task PerformRangedAttack(Unit enemy, Unit target, int bonusDamage = 0)
    {
        if (!IsValidActor(enemy) || !IsValidActor(target))
            return;

        int dist = grid.Distance(enemy.CurrentTile, target.CurrentTile);
        if (dist > enemy.AttackRange)
        {
            GD.Print($"{enemy.Name} — target out of range for ranged attack.");
            return;
        }

        // ── Line of sight check ───────────────────────────────────────────
        if (!grid.HasLineOfSight(enemy.CurrentTile.Axial, target.CurrentTile.Axial))
        {
            GD.Print($"{enemy.Name} — no line of sight to {target.Name}.");
            combatUI?.AppendActionLog($"{enemy.Name} has no line of sight!");
            return;
        }

        int dmg = enemy.ModifyOutgoingAttackDamage(
            (enemy.AttackDamage > 0 ? enemy.AttackDamage : 4) + bonusDamage);
        if (dmg <= 0)
        {
            combatUI?.AppendActionLog($"{enemy.Name}'s shot misses {target.Name}.");
            return;
        }

        string msg = $"{enemy.Name} shoots {target.Name} for {dmg} damage.";
        GD.Print(msg);
        combatUI?.AppendActionLog(msg);

        target.ApplyDamage(dmg);
        ResolveRetaliation(target, enemy);

        RefreshSelectedUnitUI();
        RefreshEnemyRoster();
        RefreshPlayerUnitBar();
        RefreshDeckCounts();
        await ToSignal(GetTree().CreateTimer(0.35f), "timeout");
    }

    private bool IsValidActor(Unit u) =>
        u != null && IsInstanceValid(u) && u.Stats.IsAlive && u.CurrentTile != null;

    private void ApplyHazardDamage(List<Unit> units)
    {
        // Snapshot to avoid issues if a death modifies the list (e.g. summons)
        var snapshot = units.ToList();

        foreach (var unit in snapshot)
        {
            if (unit == null || !IsInstanceValid(unit) || !unit.Stats.IsAlive)
                continue;

            if (unit.CurrentTile == null)
                continue;
            if (!unit.CurrentTile.IsHazardous)
                continue;

            // Capture everything we need from the tile BEFORE damage —
            // ApplyDamage may kill the unit and null out CurrentTile.
            var elementType = unit.CurrentTile.ElementType;
            var elementStrength = unit.CurrentTile.ElementStrength;
            var unitName = unit.Name;

            int hazardDmg = 3;
            if (elementStrength > 0)
                hazardDmg = (int)(hazardDmg * elementStrength);
            hazardDmg = Math.Max(1, hazardDmg);

            unit.ApplyDamage(hazardDmg);

            string msg = $"{unitName} takes {hazardDmg} damage from {elementType} terrain!";
            GD.Print(msg);
            combatUI?.AppendActionLog(msg);
        }
    }

    /// <summary>
    /// Processes per-turn status effect damage and healing for a list of units.
    /// Called at the start of each side's turn, after TickStatuses() has already
    /// decremented durations. Units that die here are handled by the normal
    /// death pipeline via ApplyDamage → OnDied → HandleUnitDeath.
    /// </summary>
    private void ProcessStatusEffects(List<Unit> units)
    {
        var snapshot = units.ToList();
        foreach (var unit in snapshot)
        {
            if (unit == null || !IsInstanceValid(unit) || !unit.Stats.IsAlive)
                continue;

            // ── Burn (3 damage per turn) ─────────────────────────────────────
            if (unit.HasStatus("burn"))
            {
                int burnDmg = 3;
                unit.ApplyDamage(burnDmg);
                string msg = $"{unit.Name} takes {burnDmg} damage from Burn.";
                GD.Print(msg);
                combatUI?.AppendActionLog(msg);
            }
            // ── Bleed (Q2, 2 damage per turn) — applied by onAttack items ────
            if (unit.HasStatus("bleed"))
            {
                int bleedDmg = 2;
                unit.ApplyDamage(bleedDmg);
                string msg = $"{unit.Name} takes {bleedDmg} damage from Bleed.";
                GD.Print(msg);
                combatUI?.AppendActionLog(msg);
            }
            // ── Ball Lightning (Chain Lightning tier 4) ──────────────────────
            // The marked unit crackles: 6 damage to it and to up to 3 of its
            // allies within 3 tiles, every turn while the status holds.
            if (unit.HasStatus("ball_lightning") && unit.CurrentTile != null && grid != null)
            {
                const int ballDmg = 6;
                var struck = new List<Unit> { unit };
                foreach (var ally in State.UnitsInPlay
                    .Where(a => a != null && a != unit && a.Stats.IsAlive
                        && a.TeamId == unit.TeamId && a.CurrentTile != null
                        && grid.Distance(unit.CurrentTile.Axial, a.CurrentTile.Axial) <= 3)
                    .OrderBy(a => grid.Distance(unit.CurrentTile.Axial, a.CurrentTile.Axial))
                    .Take(3))
                    struck.Add(ally);

                foreach (var victim in struck)
                {
                    victim.ApplyDamage(ballDmg);
                    combatUI?.AppendActionLog($"Ball lightning arcs to {victim.Name} for {ballDmg}.");
                }
            }

            // ── Poison (max HP drain per turn) ───────────────────────────────
            if (unit.HasStatus("poisoned") && unit.Stats.PoisonDrainPerTurn > 0)
            {
                int drain = unit.Stats.PoisonDrainPerTurn;

                // Reduce max HP permanently (WitheredMaxHp keeps the original
                // width visible on the bar as a sickly right-end segment)
                int beforeMax = unit.Stats.MaxHealth;
                unit.Stats.MaxHealth = Math.Max(0, unit.Stats.MaxHealth - drain);
                unit.Stats.WitheredMaxHp += beforeMax - unit.Stats.MaxHealth;

                // Clamp current HP to the new max — this IS damage
                if (unit.Stats.Health > unit.Stats.MaxHealth)
                    unit.Stats.Health = unit.Stats.MaxHealth;

                unit.RefreshHealthBar();

                string msg = $"{unit.Name} is poisoned — max HP reduced by {drain} " +
                            $"(now {unit.Stats.Health}/{unit.Stats.MaxHealth}).";
                GD.Print(msg);
                combatUI?.AppendActionLog(msg);

                // Kill if max HP reached zero
                if (unit.Stats.MaxHealth <= 0 && !unit.IsDeathQueued)
                    unit.KillFromEffect();
            }
        }
    }

    private void HandleUnitDeath(Unit unit)
    {
        if (unit == null)
            return;

        string deathMsg = $"{unit.Name} has died.";
        GD.Print(deathMsg);
        combatUI?.AppendActionLog(deathMsg);

        // K2.5: a companion downed on expedition is stabilized at 0 — out for
        // the rest of the expedition, infirmary check at extraction. If this
        // fight is LOST, the §5b wipe rolls decide their fate instead.
        if (unit.IsPlayerControlled && !string.IsNullOrEmpty(unit.CompanionId)
            && unit.CompanionId != "wizard" && PlayerSession.IsOnExpedition)
        {
            var downedComp = SaveManager.ActiveSave?.Companions?.Find(c => c.Id == unit.CompanionId);
            if (downedComp != null)
            {
                downedComp.ExpeditionHP = 0;
                GD.Print($"[ExpeditionHP] {downedComp.Name} downed — stabilized at 0, " +
                         "out for the rest of this expedition.");
            }
        }

        // U3: queue death-driven triggers (onDeath/onAllyDeath) while the corpse
        // still has a tile — "when the unit dies, before removal" (units doc §5).
        // Queuing only; the drain runs at the next safe async point.
        QueueDeathTriggers(unit);

        HonoredDeadService.RecordDeath(unit);
        if (unit.IsConstruct)          // ← feed Schematics on any construct loss
            RegisterConstructLoss(unit);
        ConduitLinkSystem.OnUnitDied(unit);
        if (unit.TeamId != 0 && State != null)   // ← feed mana_per_kill (Aftershock)
            State.EnemiesKilledThisTurn++;

        // ── Spirit death-site tracking (they_chose_to_stay tier 4) ────────
        if (unit.IsSpirit && unit.CurrentTile != null && State != null)
            State.SpiritDeathTiles.Add(unit.CurrentTile.Axial);

        // ── Memorial creation ─────────────────────────────────────────────
        if (State?.Memorials != null && unit.CurrentTile != null)
        {
            int necroTeam = -1;
            foreach (var u in playerUnits)
            {
                if (u != null && u.School == CardSchool.Necromancer)
                {
                    necroTeam = u.TeamId;
                    break;
                }
            }

            if (unit.LeaveMemorialOnDeath.HasValue)
            {
                // Explicit card mark (Last Words) — overrides everything else.
                int team = necroTeam >= 0 ? necroTeam : 0;
                State.Memorials.CreateMemorial(unit.CurrentTile, unit.Name,
                    wasAlly: false, unit.LeaveMemorialOnDeath.Value, team);
                State.Log($"[LastWords] {unit.Name} died while marked — {unit.LeaveMemorialOnDeath.Value} memorial created.");
            }
            else if (unit.HasStatus("haunted"))
            {
                // Haunted overrides normal memorial creation — always creates a
                // Strong memorial regardless of whether a Necromancer is present,
                // and regardless of the unit's HP tier.
                int team = necroTeam >= 0 ? necroTeam : 0;
                State.Memorials.CreateMemorial(unit.CurrentTile, unit.Name,
                    wasAlly: false, MemorialStrength.Strong, team);
                State.Log($"[Haunted] {unit.Name} died while haunted — Strong memorial created.");
            }
            else if (necroTeam >= 0)
            {
                // Normal Necromancer memorial — strength based on unit HP tier
                State.Memorials.CreateMemorial(unit.CurrentTile, unit, necroTeam);
            }
        }
        // ─────────────────────────────────────────────────────────────────

        // Wildlife death enriches the ground — circle of life
        if (State?.Growth != null && unit.CurrentTile != null && unit.HasStatus("wildlife"))
            State.Growth.LeaveCarcass(unit.CurrentTile, unit);

        if (!unit.IsDeathQueued)
            unit.Die();

        // Make sure the unit's logical death cleanup ran
        if (!unit.IsDeathQueued)
            unit.Die();

        // Clear any selection pointing at this unit
        if (selectedUnit == unit)
        {
            selectedUnit.SetSelected(false);
            selectedUnit = null;
            ClearMoveTiles();
            ClearTargetHighlight();
        }
        if (inspectedEnemyUnit == unit)
        {
            inspectedEnemyUnit.SetSelected(false);
            inspectedEnemyUnit = null;
        }
        if (_hoveredUnit == unit)
            _hoveredUnit = null;

        // Refresh UI so dead units disappear from bars/rosters
        RefreshSelectedUnitUI();
        RefreshThreatTiles();
        RefreshPlayerUnitBar();
        RefreshEnemyRoster();
    }

    /// <summary>Public action-log access for ability effects (Triggers partial's
    /// effect classes live outside CombatManager and can't reach combatUI).</summary>
    public void AppendCombatLog(string message) => combatUI?.AppendActionLog(message);

    private void PruneDeadUnits()
    {
        PruneList(playerUnits);
        PruneList(enemyUnits);

        // Also prune State.UnitsInPlay since effects iterate it
        State.UnitsInPlay.RemoveAll(u => u == null || !IsInstanceValid(u) || !u.Stats.IsAlive);
        _pruneNeeded = true;
    }

    private void PruneList(List<Unit> list)
    {
        for (int i = list.Count - 1; i >= 0; i--)
        {
            var u = list[i];
            if (u == null || !IsInstanceValid(u))
            {
                list.RemoveAt(i);
                continue;
            }
            if (!u.Stats.IsAlive)
            {
                list.RemoveAt(i);
                // Now safe to actually free the node — nothing references it
                u.QueueFree();
            }
        }
    }

    private bool TargetHasGrowth(TargetSet targets, int minStage)
    {
        if (targets == null)
            return false;
        foreach (var obj in targets.Items)
        {
            TileData tile = obj switch
            {
                TileData td => td,
                HexTile tv => grid.GetTile(tv.Axial),
                Unit u => u.CurrentTile,
                _ => null
            };
            if (tile != null && tile.GrowthStage >= minStage)
                return true;
        }
        return false;
    }

    private bool AnyGrowthOnBoard()
    {
        if (grid == null)
            return false;
        foreach (var kvp in grid.Tiles)
            if (kvp.Value != null && kvp.Value.GrowthStage > 0)
                return true;
        return false;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Combat end check
    // ═══════════════════════════════════════════════════════════════════════

    private bool CheckCombatEnd()
    {
        // Already ended (2026-07-09): this is re-invoked by the enemy-turn tail
        // and the per-frame prune loop after the phase is decided — without the
        // guard, DEFEAT/VICTORY emitted CombatCompleted repeatedly (observed:
        // 4× on one loss, re-rolling the router's gold each time).
        if (currentPhase == CombatPhase.Victory || currentPhase == CombatPhase.Defeat)
            return true;

        // U3: defer while death triggers are pending or on the stack — killing
        // The Final Service last must not declare victory before Deathburst
        // resolves and the Honored Dead rise. DrainTriggerStackAsync re-checks
        // once the stack settles.
        if (State != null && TriggersOutstanding)
        {
            // Latched (2026-07-09): PruneDeadUnits re-arms _pruneNeeded, so this
            // check runs per-frame from the first kill on — one line per deferral
            // episode is evidence; hundreds are noise.
            if (!_combatEndDeferLogged)
            {
                GD.Print("[CombatEnd] deferred — triggers outstanding on the stack.");
                _combatEndDeferLogged = true;
            }
            return false;
        }
        _combatEndDeferLogged = false;

        bool allEnemiesDead = true;
        bool allPlayersDead = true;

        foreach (var u in enemyUnits)
            if (u != null && u.Stats.IsAlive)
            { allEnemiesDead = false; break; }

        foreach (var u in playerUnits)
            if (u != null && u.Stats.IsAlive)
            { allPlayersDead = false; break; }

        if (allEnemiesDead)
        {
            currentPhase = CombatPhase.Victory;
            RefreshPhaseUI();
            GD.Print("=== VICTORY ===");
            combatUI?.AppendActionLog("Victory!");
            CombatTelemetry.EndFight(true, roundNumber);

            // K2.5 (ruled 2026-07-09): unit HP is the fights — surviving
            // companions carry their remaining HP into the next fight of
            // this expedition. (Downed companions were stabilized at 0 in
            // HandleUnitDeath; the wizard's stand-in is the party pool.)
            if (PlayerSession.IsOnExpedition && SaveManager.ActiveSave != null)
            {
                foreach (var u in playerUnits)
                {
                    if (u == null || !IsInstanceValid(u) || !u.Stats.IsAlive)
                        continue;
                    if (string.IsNullOrEmpty(u.CompanionId) || u.CompanionId == "wizard")
                        continue;
                    var comp = SaveManager.ActiveSave.Companions?.Find(c => c.Id == u.CompanionId);
                    if (comp == null)
                        continue;
                    comp.ExpeditionHP = u.Stats.Health;
                    GD.Print($"[ExpeditionHP] {comp.Name} leaves the fight at " +
                             $"{u.Stats.Health}/{u.Stats.MaxHealth} — carried to the next one.");
                }
            }

            EmitSignal(SignalName.CombatCompleted, true);   // ← ADD THIS
            return true;
        }

        if (allPlayersDead)
        {
            currentPhase = CombatPhase.Defeat;
            RefreshPhaseUI();
            GD.Print("=== DEFEAT ===");
            combatUI?.AppendActionLog("Defeat.");
            CombatTelemetry.EndFight(false, roundNumber);
            EmitSignal(SignalName.CombatCompleted, false);  // ← ADD THIS
            return true;
        }

        return false;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Deployment phase
    // ═══════════════════════════════════════════════════════════════════════

    private void StartDeploymentPhase()
    {
        isInDeploymentPhase = true;
        ClearDeploymentSelection();
        HighlightDeploymentTiles(true);
        GD.Print("Deployment phase started. Select a friendly unit and place it. Press Enter to confirm.");

        PlanAllEnemyIntents();

        currentPhase = CombatPhase.Deployment;
        RefreshAllUI();

        // Auto-select first player unit
        if (playerUnits.Count > 0 && playerUnits[0] != null)
        {
            selectedDeployUnit = playerUnits[0];
            selectedDeployUnit.SetSelected(true);
            RefreshSelectedUnitUI();
        }

        CallDeferred(nameof(OrientCameraForCombat));
        CallDeferred(nameof(ShowDeploymentIntel));
    }

    private void ShowDeploymentIntel()
    {
        if (isInDeploymentPhase)
            combatUI?.ShowEnemyIntel(BuildEnemyIntel());
    }

    private void EndDeploymentPhase()
    {
        isInDeploymentPhase = false;
        ClearDeploymentSelection();
        HighlightDeploymentTiles(false);
        GD.Print("Deployment phase ended. Spawning enemies reactively...");

        // ── Change 1: reactive enemy spawn ───────────────────────────────
        SpawnAndPlaceEnemies();

        // ── Change 3: attunement seed from starting tile ─────────────────
        SeedAttunementFromStartingTile();

        RefreshPhaseUI();
        RefreshSelectedUnitUI();
        RefreshEnemyRoster();

        if (AutoStartAfterDeployment)
            StartPlayerTurn();
    }

    private void OnConfirmDeploymentPressed()
    {
        if (!isInDeploymentPhase)
            return;
        EndDeploymentPhase();
    }

    private void HandleDeploymentInput(InputEvent e)
    {
        if (e is InputEventKey key && key.Pressed)
        {
            if (key.Keycode == Key.Enter)
            { EndDeploymentPhase(); return; }
            if (key.Keycode == Key.Backspace)
            { ResetDeploymentPositions(); return; }
        }

        if (e is InputEventMouseButton mb && mb.Pressed)
        {
            if (mb.ButtonIndex == MouseButton.Left)
            { TryHandleDeploymentClick(); return; }
            if (mb.ButtonIndex == MouseButton.Right)
            { ClearDeploymentSelection(); GD.Print("Deployment selection cleared."); }
        }
    }

    private void TryHandleDeploymentClick()
    {
        var camera = GetViewport().GetCamera3D();
        if (camera == null)
            return;

        Vector2 mousePos = GetViewport().GetMousePosition();
        Vector3 from = camera.ProjectRayOrigin(mousePos);
        Vector3 to = from + camera.ProjectRayNormal(mousePos) * 1000f;

        var result = GetWorld3D().DirectSpaceState
            .IntersectRay(PhysicsRayQueryParameters3D.Create(from, to));
        if (result.Count == 0)
            return;
        if (!result.TryGetValue("collider", out var cv))
            return;

        Node current = cv.AsGodotObject() as Node;
        while (current != null)
        {
            if (current is Unit unit)
            { TrySelectDeploymentUnit(unit); return; }
            if (current is HexTile tile)
            { TryPlaceDeploymentUnit(tile); return; }
            current = current.GetParent();
        }
    }

    private void TrySelectDeploymentUnit(Unit unit)
    {
        if (unit == null || !unit.IsPlayerControlled || !playerUnits.Contains(unit))
            return;
        if (selectedDeployUnit != null)
            selectedDeployUnit.SetSelected(false);
        selectedDeployUnit = unit;
        selectedDeployUnit.SetSelected(true);
        GD.Print($"Selected deploy unit: {unit.Name}");
        RefreshSelectedUnitUI();
    }

    private void TryPlaceDeploymentUnit(HexTile tileView)
    {
        if (selectedDeployUnit == null || tileView == null)
            return;
        if (!playerDeployCoords.Contains(tileView.Axial))
        { GD.Print("Tile outside deployment zone."); return; }

        var tileData = grid.GetTile(tileView.Axial);
        if (tileData == null)
            return;
        if (!tileData.IsWalkable || tileData.IsBlocked || tileData.IsOccupied)
        { GD.Print("Deployment tile not available."); return; }

        selectedDeployUnit.PlaceOnTile(tileData);
        GD.Print($"{selectedDeployUnit.Name} deployed to {tileData.Axial}");
        selectedDeployUnit.SetSelected(false);
        selectedDeployUnit = null;
    }

    private void ClearDeploymentSelection()
    {
        if (selectedDeployUnit != null)
            selectedDeployUnit.SetSelected(false);
        selectedDeployUnit = null;
        RefreshSelectedUnitUI();
    }

    private void ResetDeploymentPositions()
    {
        ClearDeploymentSelection();
        foreach (var kvp in originalDeployCoords)
        {
            var tile = grid.GetTile(kvp.Value);
            if (tile != null && tile.IsWalkable && !tile.IsBlocked)
                kvp.Key.PlaceOnTile(tile);
        }
        RefreshSelectedUnitUI();
        GD.Print("Deployment positions reset.");
    }

    private void HighlightDeploymentTiles(bool enabled)
    {
        foreach (var coord in playerDeployCoords)
            grid.GetTileView(coord)?.SetDeploymentHighlight(enabled);
    }

    /// <summary>
    /// After deployment is committed, give each player unit 1 free attunement
    /// charge based on the terrain type of their starting tile.
    /// Only fires for schools that have an active ISchoolAttunement.
    /// </summary>
    private void SeedAttunementFromStartingTile()
    {
        foreach (var unit in playerUnits)
        {
            if (unit?.Attunement == null)
                continue;
            if (unit.CurrentTile == null)
                continue;

            // Only Elementalists have elemental attunement seeding
            if (unit.Attunement is not ElementalAttunement ea)
                continue;

            ElementTag? element = unit.CurrentTile.TerrainType switch
            {
                TileTerrainType.Lava => ElementTag.Fire,
                TileTerrainType.Stone => ElementTag.Earth,
                TileTerrainType.Ice => ElementTag.Ice,
                TileTerrainType.Arcane => ElementTag.Storm,
                _ => null
            };

            if (element == null)
                continue;

            // Simulate casting a spell with that element tag to go through
            // the proper opposition logic and fire OnChargeChanged events
            ea.OnSpellCast(new[] { element.Value.ToString().ToLowerInvariant() });

            GD.Print($"[Deploy] {unit.Name} seeded 1 {element} charge from {unit.CurrentTile.TerrainType} tile.");
        }

        schoolAttunementUI?.Refresh();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Unit spawning
    // ═══════════════════════════════════════════════════════════════════════

    private void BuildPlayerDeploymentArea()
    {
        playerDeployCoords.Clear();

        // Gather everything inside the defined spawn zone first
        var baseZoneTiles = new List<TileData>();
        foreach (var zone in grid.SpawnZones)
        {
            if (zone.Side != HexGridManager.SpawnSide.Player)
                continue;
            foreach (var coord in zone.Tiles)
            {
                var td = grid.GetTile(coord);
                if (td != null && td.IsWalkable && !td.IsBlocked)
                {
                    baseZoneTiles.Add(td);
                    playerDeployCoords.Add(coord);
                }
            }
        }

        // If the zone already has variety, we're done
        if (HasTerrainVariety(playerDeployCoords))
            return;

        // Otherwise, expand outward one ring looking for different terrain types.
        // We cap expansion at 3 extra tiles so the zone doesn't grow too large.
        var alreadySeen = new HashSet<Vector2I>(playerDeployCoords);
        var candidates = new List<TileData>();

        foreach (var coord in playerDeployCoords.ToList())
        {
            foreach (var neighbor in grid.GetNeighborCoords(coord))
            {
                if (alreadySeen.Contains(neighbor))
                    continue;
                alreadySeen.Add(neighbor);

                var td = grid.GetTile(neighbor);
                if (td == null || !td.IsWalkable || td.IsBlocked)
                    continue;
                candidates.Add(td);
            }
        }

        // Prioritize tiles whose terrain type isn't already represented
        var existingTypes = new HashSet<TileTerrainType>(
            playerDeployCoords.Select(c => grid.GetTile(c)?.TerrainType ?? TileTerrainType.Grass));

        candidates.Sort((a, b) =>
        {
            bool aIsNew = !existingTypes.Contains(a.TerrainType);
            bool bIsNew = !existingTypes.Contains(b.TerrainType);
            if (aIsNew && !bIsNew)
                return -1;
            if (!aIsNew && bIsNew)
                return 1;
            return 0;
        });

        int added = 0;
        foreach (var td in candidates)
        {
            if (added >= 3)
                break;
            playerDeployCoords.Add(td.Axial);
            added++;
        }
    }

    private bool HasTerrainVariety(HashSet<Vector2I> coords)
    {
        var types = new HashSet<TileTerrainType>();
        foreach (var c in coords)
        {
            var td = grid.GetTile(c);
            if (td != null)
                types.Add(td.TerrainType);
            if (types.Count >= 2)
                return true;
        }
        return false;
    }

    private void SpawnTestUnits()
    {
        GD.Print($"[SpawnTest] PlayerUnitScene={PlayerUnitScene != null}, DummyUnitScene={DummyUnitScene != null}");
        grid = GetNodeOrNull<HexGridManager>(GridPath);
        if (grid == null)
        { GD.PrintErr($"HexGridManager not found at: {GridPath}"); return; }
        if (PlayerUnitScene == null || DummyUnitScene == null)
        { GD.PrintErr("Assign PlayerUnitScene and DummyUnitScene in the Inspector."); return; }

        if (PlayerUnitScene == null || DummyUnitScene == null)
        {
            GD.PrintErr($"Assign PlayerUnitScene and DummyUnitScene in the Inspector. Player={PlayerUnitScene != null} Dummy={DummyUnitScene != null}");
            return;
        }

        ConfigureAndGenerateMap();

        originalDeployCoords.Clear();
        playerUnits.Clear();
        enemyUnits.Clear();

        // ── Spawn wizard (always first) ───────────────────────────────────
        var wizard = SpawnUnitFromSide(HexGridManager.SpawnSide.Player, PlayerUnitScene,
            teamId: 0, isPlayerControlled: true, namePrefix: "Wizard",
            maxHealth: 20, health: 20, baseSpeed: 3, maxMana: 3, mana: 3, armor: 0, shield: 0);
        if (wizard != null)
        {
            string wizardName = SaveManager.ActiveSave?.WizardName ?? "Wizard";
            string safeNodeName = wizardName.Replace(" ", "_");
            wizard.Name = safeNodeName;
            wizard.DisplayName = wizardName;
            wizard.IsMartial = false;
            wizard.CompanionId = "wizard";
            wizard.MoveRange = 3;
            wizard.MaxActionPoints = wizard.Stats.BaseSpeed;      // ← add this
            wizard.CurrentActionPoints = wizard.MaxActionPoints;  // ← add this

            // Adept identity (2026-07-10 ruling): raw capability, no engine —
            // the generalist runs on 4 max mana instead of 3. Flat power that
            // never scales; every other school gets an attunement engine instead.
            if (PlayerSession.SelectedSchool == CardSchool.Adept)
            {
                wizard.Stats.MaxMana += 1;
                wizard.Stats.Mana += 1;
                GD.Print("[Adept] The curriculum has no gaps — 4 max mana.");
            }
            playerUnits.Add(wizard);
        }

        // ── Spawn active party companions ─────────────────────────────────
        var party = CompanionRoster.GetActiveParty();
        int trainingTier = SaveManager.ActiveSave?.TrainingGroundsTier ?? 0;

        foreach (var companion in party)
        {
            bool isMartial = companion.UnitClass == "Fighter" ||
                             companion.UnitClass == "Ranger";

            var unit = SpawnUnitFromSide(HexGridManager.SpawnSide.Player, PlayerUnitScene,
                teamId: 0, isPlayerControlled: true, namePrefix: companion.Name,
                maxHealth: companion.BaseHP,
                health: companion.BaseHP,
                baseSpeed: companion.BaseSpeed,
                maxMana: isMartial ? 0 : companion.BaseMana,
                mana: isMartial ? 0 : companion.BaseMana,
                armor: companion.BaseArmor,
                shield: 0);

            if (unit == null)
                continue;

            unit.Name = companion.Name;
            unit.DisplayName = companion.Name;

            unit.CompanionId = companion.Id;

            // K2.5: carry expedition HP into this fight. GetActiveParty already
            // filtered out stabilized (0) companions, so this is always ≥1.
            if (PlayerSession.IsOnExpedition && companion.ExpeditionHP >= 0)
            {
                unit.Stats.Health = Mathf.Clamp(companion.ExpeditionHP, 1, unit.Stats.MaxHealth);
                unit.RefreshHealthBar();
                GD.Print($"[ExpeditionHP] {companion.Name} fields at " +
                         $"{unit.Stats.Health}/{unit.Stats.MaxHealth} (carried from earlier fights).");
            }

            if (isMartial)
            {
                unit.AttackDamage = companion.BaseAttackDamage;
                unit.AttackRange = companion.BaseAttackRange;
                unit.MoveRange = isMartial ? 4 : 3;

                unit.MartialClass = companion.UnitClass switch
                {
                    "Fighter" => MartialClass.Fighter,
                    "Ranger" => MartialClass.Ranger,
                    _ => MartialClass.None,
                };

                // ── Set AP from Training Grounds tier ─────────────────────
                var save = SaveManager.ActiveSave;
                unit.MaxActionPoints = unit.MartialClass == MartialClass.Ranger
                    ? (save?.RangerBaseAP ?? 3)
                    : (save?.FighterBaseAP ?? 3);
                unit.CurrentActionPoints = unit.MaxActionPoints;

                // Training Grounds stat bonuses
                int tgTier = save?.TrainingGroundsTier ?? 0;
                unit.AttackDamage += tgTier >= 2 ? 1 : 0;
                unit.Stats.MaxHealth += tgTier >= 3 ? 4 : 0;
                unit.Stats.Health = unit.Stats.MaxHealth;

                // ── Load trained stances up to slot count ─────────────────
                int stanceSlots = save?.MartialStanceSlots ?? 0;
                unit.AvailableStances.Clear();

                for (int s = 0; s < Math.Min(stanceSlots,
                                             companion.TrainedStanceIds.Count); s++)
                {
                    var stance = StanceRegistry.Get(companion.TrainedStanceIds[s]);
                    if (stance != null)
                        unit.AvailableStances.Add(stance);
                }

                // Default to first trained stance
                if (unit.AvailableStances.Count > 0)
                    unit.ActiveStance = unit.AvailableStances[0];

                GD.Print($"[Spawn] {companion.Name} ({companion.UnitClass}) " +
                         $"AP:{unit.MaxActionPoints} ATK:{unit.AttackDamage} " +
                         $"RNG:{unit.AttackRange} Stances:{unit.AvailableStances.Count}");
            }
            else
            {
                // Arcane companion — school deck gets added in InitializeUnitDecks
                unit.School = System.Enum.TryParse<CardSchool>(companion.School,
                    out var cs) ? cs : CardSchool.Adept;
                unit.MaxActionPoints = unit.Stats.BaseSpeed;      // ← add this
                unit.CurrentActionPoints = unit.MaxActionPoints;  // ← add this
            }

            playerUnits.Add(unit);
            GD.Print($"[Spawn] {companion.Name} IsMartial={unit.IsMartial} UnitClass={companion.UnitClass}");
            GD.Print($"[Spawn] {companion.Name} MaxMana={unit.Stats.MaxMana} Mana={unit.Stats.Mana} School={unit.School}");
        }

        // Fallback: if no save / no party, spawn a second dummy wizard for testing
        if (playerUnits.Count < 2 && SaveManager.ActiveSave == null)
        {
            var dummy = SpawnUnitFromSide(HexGridManager.SpawnSide.Player, PlayerUnitScene,
                teamId: 0, isPlayerControlled: true, namePrefix: "Player",
                maxHealth: 20, health: 20, baseSpeed: 3, maxMana: 3, mana: 3, armor: 0, shield: 0);
            if (dummy != null)
            {
                dummy.MaxActionPoints = dummy.Stats.BaseSpeed;      // ← add this
                dummy.CurrentActionPoints = dummy.MaxActionPoints;  // ← add this
                playerUnits.Add(dummy);
            }
        }

        // Apply equipment loadouts to player units
        // Player_1 is the wizard; companions would use their companion ID.
        // For now all player units map to "wizard" until companion units
        // are spawned as separate entities with their own IDs.
        for (int i = 0; i < playerUnits.Count; i++)
        {
            string unitId = i == 0 ? "wizard" : $"companion_{i}";
            ApplyEquipmentLoadout(playerUnits[i], unitId);
        }

        // Default encounter composition — will be replaced by EncounterDefinition
        // in Step 2 of the architecture plan. For now, a fixed mix that exercises
        // all five archetypes when TestEnemyCount >= 3.
        if (EncounterContextCarrier.HasEncounter)
            QueueEncounterFromContext(EncounterContextCarrier.Current);
        else
            QueueDefaultEncounter();

        if (playerUnits.Count == 0)
        {
            GD.PrintErr("Failed to spawn any player units.");
            return;
        }

        if (pendingEnemySpawns.Count == 0)
        {
            GD.PrintErr("No enemy spawns queued.");
            return;
        }

        playerUnit = playerUnits[0];
        // dummyUnit / enemyUnits are wired after enemy spawn in SpawnAndPlaceEnemies()

        State.Grid = grid;

        // ── Druid living-terrain engine ───────────────────────────────────
        State.Growth = new GrowthManager(
            grid: grid,
            state: State,
            rng: null,   // self-seeds; pass your combat RNG if you have one for determinism
            wildlifeSpawner: (tile, key) =>
            {
                int team = tile.GrowthOwner?.TeamId ?? 0;   // was: (tile.GrowthOwner is Unit ow) ? ow.TeamId : 0
                State.OnSummonRequested?.Invoke(key, tile, team);
            },
            rootHandler: (unit, dur) => unit.ApplyStatus("rooted", dur)
        );

        State.Memorials = new MemorialManager(grid);
        State.Memorials.OnMemorialCreated += tile => tile.TileView?.SetMemorial(tile.Memorial);
        State.Memorials.OnMemorialChanged += tile => tile.TileView?.SetMemorial(tile.Memorial);
        State.Memorials.OnMemorialRemoved += tile => tile.TileView?.SetMemorial(null);

        State.Growth.OnGrowthChanged += tile => tile.TileView?.SetGrowth(tile.GrowthStage);

        Bestiary.EnsureLoaded();


        string regionName = EncounterContextCarrier.HasEncounter
            ? EncounterContextCarrier.Current?.RegionId ?? ""
            : "";
        HonoredDeadService.OnCombatStart(regionName);

        State.PlayerUnit = playerUnit;
        // State.EnemyUnit set after enemy spawn
        State.UnitsInPlay.Clear();
        foreach (var u in playerUnits)
            State.UnitsInPlay.Add(u);

        GD.Print($"Spawned {playerUnits.Count} player unit(s). Enemies pending deployment commit.");

        BuildPlayerDeploymentArea();

        playerUnit.School = PlayerSession.SelectedSchool;
        playerUnit.InitializeAttunement();

        // Wizard gets the selected school + attunement
        if (playerUnits.Count > 0)
        {
            playerUnits[0].School = PlayerSession.SelectedSchool;
            playerUnits[0].InitializeAttunement();
        }

        // Arcane companions initialize their own attunement
        // Martial companions skip this entirely
        foreach (var unit in playerUnits.Skip(1))
        {
            if (unit.IsMartial)
                continue;
            unit.InitializeAttunement();
        }

        // Wilding Riot fires per-unit; the attunement doesn't know its owner, so bind it here.
        foreach (var unit in playerUnits)
        {
            if (unit.Attunement is WildingAttunement w)
            {
                Unit owner = unit;   // capture per-iteration
                w.OnRiotTriggered += () => State.Growth?.ApplyRiot(owner);
            }
        }

        // ── wire school-specific attunement event subscriptions ─────────
        foreach (var unit in playerUnits)
        {
            if (unit?.Attunement is ArcaneAttunement arcane)
            {
                arcane.OnChargeOverflow += overflowAmount =>
                {
                    if (unit.DeckData == null)
                        return;
                    unit.DeckData.Draw(overflowAmount);
                    State.OnDrawCards?.Invoke(unit);
                    GD.Print($"[Arcanist] Overflow — drew {overflowAmount}.");
                    schoolAttunementUI?.Refresh();
                };
            }

            if (unit?.Attunement is GriefAttunement grief)
            {
                grief.OnFloodTriggered += () =>
                {
                    // Refresh all living friendly spirits — reset their turns
                    // so they act again this round (the Flood effect).
                    foreach (var u in State.UnitsInPlay)
                    {
                        if (u == null || !u.IsSpirit || !u.Stats.IsAlive)
                            continue;
                        if (u.SummonerTeamId != unit.TeamId)
                            continue;
                        u.StartTurn();
                    }
                    GD.Print("[Necromancer] Flood — all spirits refreshed.");
                    schoolAttunementUI?.Refresh();
                };
            }

            if (unit?.Attunement is WeaveAttunement weave)
            {
                weave.OnSeventhLayer += () =>
                {
                    // Name the nearest living enemy — apply the "named" status.
                    Unit target = null;
                    float closest = float.MaxValue;
                    foreach (var u in State.UnitsInPlay)
                    {
                        if (u == null || !u.Stats.IsAlive || u.TeamId == unit.TeamId)
                            continue;
                        float dist = unit.CurrentTile != null && u.CurrentTile != null
                            ? State.Grid?.Distance(unit.CurrentTile.Axial, u.CurrentTile.Axial) ?? 99
                            : 99;
                        if (dist < closest)
                        { closest = dist; target = u; }
                    }
                    if (target != null)
                    {
                        target.ApplyStatus("named", 2);
                        GD.Print($"[Enchanter] Seventh Layer — {target.Name} is Named.");
                    }
                    schoolAttunementUI?.Refresh();
                };
            }
        }

        if (EnableDeploymentPhase)
        {
            if (PlayerSession.DebugMode && PlayerSession.SkipDeployment)
            {
                AutoPlaceUnits();
                // Fix (2026-07-09): do NOT start the turn here. This runs
                // synchronously inside _Ready, but InitializeUnitDecks is a
                // CallDeferred that hasn't run yet — Round 1's DrawToFull hit
                // DeckData == null and drew nothing, so the player's first
                // playable turn was Round 2 (the "one turn delay" in skip
                // mode). InitializeUnitDecks starts the turn when decks exist.
                _pendingSkipDeployTurnStart = true;
            }
            else
            {
                StartDeploymentPhase();
            }
        }
    }

    /// <summary>
    /// Resolves the combat arena from the overworld encounter context (terrain that
    /// spawned this fight + tier), applies recipe/density/seed to the grid, then
    /// generates. Standalone/test launches with no encounter fall through to the
    /// grid's inspector defaults (enum theme path).
    /// </summary>
    private void ConfigureAndGenerateMap()
    {
        if (grid == null)
            return;

        string terrain = EncounterContextCarrier.SourceTerrain;
        if (!string.IsNullOrEmpty(terrain))
        {
            grid.MapRecipeId = TerrainRecipeMap.Resolve(terrain);
            grid.DensityControlMode = HexGridManager.DensityMode.Preset;
            grid.DensityPreset = DensityForTier(EncounterContextCarrier.Tier);

            if (EncounterRouter.Instance != null && EncounterRouter.Instance.HasSavedSeed)
                grid.MapSeed = EncounterRouter.Instance.SavedRunSeed;

            GD.Print($"[CombatMap] terrain='{terrain}' → recipe='{grid.MapRecipeId}', " +
                     $"density={grid.DensityPreset}, seed={grid.MapSeed}");
        }
        else
        {
            GD.Print("[CombatMap] No encounter terrain — using grid inspector defaults.");
        }

        ApplyVistaBias(grid);

        grid.GenerateMap();
    }

    /// <summary>
    /// Maps the overworld neighbour terrains captured at launch
    /// (EncounterContextCarrier.NeighborTerrains, one per hex direction) onto the
    /// grid's per-direction vista bias, so the non-playable surround leans toward
    /// what actually borders this fight on the world map — forest vista on the
    /// side that touches forest, water past the rim on a lakeshore, and so on
    /// (combat_environments §5 spatial storytelling). No context = no bias = the
    /// vista purely continues the arena's own field.
    /// </summary>
    private static void ApplyVistaBias(HexGridManager grid)
    {
        grid.VistaTerrainBias.Clear();

        var neighbors = EncounterContextCarrier.NeighborTerrains;
        if (neighbors == null)
            return;

        for (int k = 0; k < 6 && k < neighbors.Length; k++)
        {
            var biased = VistaBiasFor(neighbors[k]);
            if (biased.HasValue)
                grid.VistaTerrainBias[k] = biased.Value;
        }

        if (grid.VistaTerrainBias.Count > 0)
            GD.Print("[CombatMap] Vista bias: " + string.Join(", ",
                System.Linq.Enumerable.Select(grid.VistaTerrainBias, kv => $"dir{kv.Key}={kv.Value}")));
    }

    /// <summary>
    /// Overworld terrain name → the combat terrain the vista should lean toward.
    /// Null = no distinct vista read (the surround just continues the arena):
    /// Grassland/Road/Ruins map to nothing today, Desert waits on SunbakedBarrens.
    /// </summary>
    private static TileTerrainType? VistaBiasFor(string overworldTerrain) => overworldTerrain switch
    {
        "Forest" => TileTerrainType.Forest,
        "Mountain" or "Hills" => TileTerrainType.Stone,
        "Snow" or "Tundra" => TileTerrainType.Ice,
        "Volcanic" => TileTerrainType.Lava,
        "Water" or "Lake" or "Coast" or "Swamp" or "Marsh" => TileTerrainType.Water,
        "ArcaneGround" => TileTerrainType.Arcane,
        _ => null,
    };

    private static HexGridManager.MapDensityPreset DensityForTier(EncounterTier tier) => tier switch
    {
        EncounterTier.Skirmish => HexGridManager.MapDensityPreset.Sparse,
        EncounterTier.Battle => HexGridManager.MapDensityPreset.Standard,
        EncounterTier.Siege => HexGridManager.MapDensityPreset.Dense,
        _ => HexGridManager.MapDensityPreset.Standard,
    };

    private List<EnemyIntelEntry> BuildEnemyIntel()
    {
        var entries = new List<EnemyIntelEntry>();
        foreach (var p in pendingEnemySpawns)
        {
            entries.Add(new EnemyIntelEntry
            {
                ThreatLabel = p.Def.ThreatLabel,
                MaxHealth = p.MaxHealth,
                BaseSpeed = p.BaseSpeed,
                Armor = p.Armor,
                BodyColor = p.BodyColor,
                Role = p.Def.Role,               // V2
                Intel = p.Def.IntelDescription,  // V2
            });
        }
        return entries;
    }

    /// <summary>
    /// Builds a default encounter composition from the archetype roster.
    /// Replace this with EncounterDefinition data when that system is built.
    /// </summary>
    private void QueueDefaultEncounter()
    {
        pendingEnemySpawns.Clear();

        // Default mix: one Soldier, one Ranger, one Wizard.
        // Gives immediate variety and exercises all three AI behaviors.
        // Swap these out per encounter type once EncounterDefinition exists.
        var composition = new string[]
        {
            "generic_soldier",
            "generic_ranger",
            "generic_wizard",
        };

        // Trim or pad to TestEnemyCount so the inspector export still controls battle size
        // TestEnemyCount caps the fallback only.
        // Real encounters from EncounterContext use their own enemy list length.
        int count = Mathf.Min(TestEnemyCount, composition.Length);

        for (int i = 0; i < count; i++)
        {
            var def = UnitRegistry.Get(composition[i]);
            pendingEnemySpawns.Add(new PendingEnemySpawn
            {
                Def = def,
                MaxHealth = def.MaxHealth,
                Health = def.MaxHealth,
                BaseSpeed = def.BaseSpeed,
                Armor = def.Armor,
                AttackRange = def.AttackRange,
                AttackDamage = def.AttackDamage,
                BodyColor = def.BodyColor,
                NamePrefix = def.ThreatLabel,
            });
        }

        CombatTelemetry.BeginFight("debug_default", "", "Battle",
            pendingEnemySpawns.ConvertAll(p => p.NamePrefix));
    }

    /// <summary>
    /// Populates pendingEnemySpawns from an EncounterDefinition provided
    /// by EncounterRouter via EncounterContext. Replaces QueueDefaultEncounter
    /// when a real overworld encounter is in progress.
    /// </summary>
    private void QueueEncounterFromContext(EncounterDefinition def)
    {
        pendingEnemySpawns.Clear();

        foreach (var slot in def.Enemies)
        {
            var unitDef = UnitRegistry.Get(slot.UnitId);
            float mult = slot.DifficultyMult;

            // Option B: HP on a softened (sqrt) curve so high mults don't create
            // slog-sponges; damage closer to linear so deep/corrupted ground is
            // actually lethal, not just tankier. Armor left flat — scaling it
            // compounds the chip-grind against a low-unit-count party.
            float hpMult = Mathf.Sqrt(mult);
            float dmgMult = mult;

            int hp = Mathf.RoundToInt(unitDef.MaxHealth * hpMult);
            int dmg = Mathf.RoundToInt(unitDef.AttackDamage * dmgMult);

            pendingEnemySpawns.Add(new PendingEnemySpawn
            {
                Def = unitDef,
                MaxHealth = hp,
                Health = hp,
                BaseSpeed = unitDef.BaseSpeed,
                Armor = unitDef.Armor,
                AttackRange = unitDef.AttackRange,
                AttackDamage = dmg,
                BodyColor = unitDef.BodyColor,
                NamePrefix = unitDef.ThreatLabel,
            });
        }

        GD.Print($"[Encounter] Loaded '{def.DisplayName}' — " +
                 $"{pendingEnemySpawns.Count} enemies from {def.RegionId}/{def.Tier}");

        CombatTelemetry.BeginFight(def.Id, def.RegionId, def.Tier.ToString(),
            pendingEnemySpawns.ConvertAll(p => p.NamePrefix));
    }

    /// <summary>
    /// Spawns enemy units after the player has committed their deployment.
    /// The enemy AI places units reactively — it reads where the player's
    /// units ended up and tries to counter the formation.
    /// </summary>
    private void SpawnAndPlaceEnemies()
    {
        enemyUnits.Clear();

        var enemyZoneTiles = new List<TileData>();
        var claimed = new HashSet<Vector2I>(); // ← track tiles we've already assigned

        foreach (var zone in grid.SpawnZones)
        {
            if (zone.Side != HexGridManager.SpawnSide.Enemy)
                continue;
            foreach (var coord in zone.Tiles)
            {
                var td = grid.GetTile(coord);
                if (td != null && td.IsWalkable && !td.IsBlocked && !td.IsOccupied)
                    enemyZoneTiles.Add(td);
            }
        }

        Vector2I playerCentroid = ComputePlayerCentroid();

        enemyZoneTiles.Sort((a, b) =>
            grid.Distance(a.Axial, playerCentroid)
                .CompareTo(grid.Distance(b.Axial, playerCentroid)));

        var sorted = pendingEnemySpawns
            .OrderByDescending(p => p.BaseSpeed)
            .ToList();

        // Filter to only unclaimed tiles before assigning
        var availableTiles = enemyZoneTiles
            .Where(td => !claimed.Contains(td.Axial))
            .ToList();

        for (int i = 0; i < sorted.Count; i++)
        {
            if (i >= availableTiles.Count)
            {
                GD.PrintErr($"Not enough enemy zone tiles for all pending spawns (wanted {sorted.Count}, have {availableTiles.Count})");
                break;
            }

            var p = sorted[i];
            var tile = availableTiles[i];
            claimed.Add(tile.Axial); // ← mark claimed locally instead of writing IsOccupied

            var unit = DummyUnitScene.Instantiate<Unit>();
            unit.IsPlayerControlled = false;
            unit.TeamId = 1;
            unit.StartMaxHealth = p.MaxHealth;
            unit.StartHealth = p.Health;
            unit.StartBaseSpeed = p.BaseSpeed;
            unit.StartMaxMana = 0;
            unit.StartMana = 0;
            unit.StartArmor = p.Armor;
            unit.StartShield = 0;

            AddChild(unit);
            unit.OnDied += HandleUnitDeath;
            unit.PlaceOnTile(tile);

            unit.Name = $"{p.NamePrefix}_{i + 1}";
            int sameTypeCount = sorted.Count(s => s.Def.Id == p.Def.Id);
            unit.Name = sameTypeCount > 1
                ? $"{p.NamePrefix}_{sorted.Take(i + 1).Count(s => s.Def.Id == p.Def.Id)}"
                : p.NamePrefix;
            unit.DisplayName = unit.Name;
            unit.DefinitionId = p.Def.Id;
            unit.BehaviorKey = p.Def.BehaviorKey;
            unit.BehaviorTags = new List<string>(p.Def.BehaviorTags);
            unit.Abilities = p.Def.Abilities;   // defs are stateless — share, don't copy
            unit.Role = p.Def.Role;
            unit.FactionId = p.Def.FactionId;
            unit.AttackRange = p.AttackRange;
            unit.AttackDamage = p.AttackDamage;
            unit.MaxActionPoints = p.BaseSpeed;
            unit.CurrentActionPoints = unit.MaxActionPoints;
            unit.SetBodyColor(p.BodyColor);
            unit.RefreshNameLabel();



            enemyUnits.Add(unit);
        }

        if (enemyUnits.Count > 0)
        {
            dummyUnit = enemyUnits[0];
            State.EnemyUnit = dummyUnit;
        }

        foreach (var u in enemyUnits)
            State.UnitsInPlay.Add(u);

        GD.Print($"Reactively spawned {enemyUnits.Count} enemy unit(s) based on player formation.");
        RefreshEnemyRoster();
    }

    /// Returns the axial centroid of all living player units.
    private Vector2I ComputePlayerCentroid()
    {
        if (playerUnits.Count == 0)
            return Vector2I.Zero;
        int q = 0, r = 0;
        int count = 0;
        foreach (var u in playerUnits)
        {
            if (u?.CurrentTile == null)
                continue;
            q += u.CurrentTile.Axial.X;
            r += u.CurrentTile.Axial.Y;
            count++;
        }
        return count == 0 ? Vector2I.Zero : new Vector2I(q / count, r / count);
    }

    private Unit SpawnUnitFromSide(
        HexGridManager.SpawnSide side, PackedScene scene,
        int teamId, bool isPlayerControlled, string namePrefix,
        int maxHealth, int health, int baseSpeed,
        int maxMana, int mana, int armor, int shield)
    {
        var slot = grid.ClaimNextSpawnSlot(side);
        if (slot == null)
        { GD.PrintErr($"No spawn slot for side: {side}"); return null; }

        var tile = grid.GetTileAtSpawnSlot(slot);
        if (tile == null)
        { GD.PrintErr($"Spawn slot had no valid tile for side: {side}"); return null; }

        var unit = scene.Instantiate<Unit>();

        // ── Set ALL exported properties BEFORE AddChild ───────────────────
        // _Ready() fires during AddChild in Godot 4, so exports must be set first
        unit.IsPlayerControlled = isPlayerControlled;
        unit.TeamId = teamId;
        unit.StartMaxHealth = maxHealth;
        unit.StartHealth = health;
        unit.StartBaseSpeed = baseSpeed;
        unit.StartMaxMana = maxMana;
        unit.StartMana = mana;
        unit.StartArmor = armor;
        unit.StartShield = shield;

        // ── Now add to scene — _Ready() fires here ────────────────────────
        AddChild(unit);

        unit.OnDied += HandleUnitDeath;
        unit.PlaceOnTile(tile);

        if (side == HexGridManager.SpawnSide.Player)
            originalDeployCoords[unit] = tile.Axial;

        int countForName = side == HexGridManager.SpawnSide.Player
            ? playerUnits.Count + 1
            : enemyUnits.Count + 1;
        unit.Name = $"{namePrefix}_{countForName}";

        GD.Print($"Spawned {unit.Name} at {tile.Axial}");
        return unit;
    }

    private void AutoPlaceUnits()
    {
        GD.Print("[Debug] Auto-placing units, skipping deployment.");
        foreach (var unit in playerUnits)
        {
            if (unit?.CurrentTile != null)
                GD.Print($"[Debug] {unit.Name} auto-placed at {unit.CurrentTile.Axial}");
        }
        // Enemies still need to be spawned even in debug mode
        SpawnAndPlaceEnemies();
        SeedAttunementFromStartingTile();
    }

    /// <summary>Spell-level target overrides — RedirectAll (attack a random fellow
    /// enemy) and RedirectAura decoys. Returns null when no override applies.
    /// U2: extracted from FindNearestPlayerUnit so behavior keys that ignore
    /// nearest-selection (melee_hunt_wounded) still honor these effects — they
    /// rewrite reality, not target preference.</summary>
    private Unit FindTargetOverride(Unit enemy)
    {
        if (enemy == null || !IsInstanceValid(enemy) || enemy.CurrentTile == null)
            return null;

        // ── RedirectAll: enemy attacks another random enemy ───────────────────
        if (State.RedirectAllTurnsRemaining > 0)
        {
            var otherEnemies = enemyUnits
                .Where(u => u != null && IsInstanceValid(u) && u != enemy && u.Stats.IsAlive)
                .ToList();
            if (otherEnemies.Count > 0)
                return otherEnemies[GD.Randi() % (uint)otherEnemies.Count < (uint)otherEnemies.Count
                    ? (int)(GD.Randi() % (uint)otherEnemies.Count) : 0];
        }

        // ── RedirectAura: decoy in range overrides normal targeting ───────────
        var aura = State.ActiveEffects?
            .OfType<RedirectAuraPersistentEffect>()
            .FirstOrDefault(a => !a.IsExpired);
        if (aura != null)
        {
            var decoy = aura.FindDecoyTarget(State, enemy.CurrentTile.Axial);
            if (decoy != null)
                return decoy;
        }

        return null;
    }

    private Unit FindNearestPlayerUnit(Unit enemy)
    {
        if (enemy == null || !IsInstanceValid(enemy) || enemy.CurrentTile == null)
            return null;

        var overrideTarget = FindTargetOverride(enemy);
        if (overrideTarget != null)
            return overrideTarget;

        // ── Normal: nearest living player unit ───────────────────────────────
        // Untargetable units (Walk Between) are skipped entirely. Taunting units
        // (Iron/Fortress Colossus) win ties and near-ties: a taunter within +1
        // of the true nearest distance is preferred.
        Unit best = null;
        int bestDist = int.MaxValue;
        Unit bestTaunter = null;
        int bestTauntDist = int.MaxValue;
        foreach (var player in playerUnits)
        {
            if (player == null || !IsInstanceValid(player))
                continue;
            if (!player.Stats.IsAlive || player.CurrentTile == null)
                continue;
            if (player.HasStatus("untargetable"))
                continue;

            int dist = grid.Distance(enemy.CurrentTile, player.CurrentTile);
            if (dist < bestDist)
            { bestDist = dist; best = player; }
            if (player.IsTaunting && dist < bestTauntDist)
            { bestTauntDist = dist; bestTaunter = player; }
        }

        if (bestTaunter != null && bestTauntDist <= bestDist + 1)
            return bestTaunter;
        return best;
    }

    private void RegisterSummonHandler()
    {
        State.OnSummonRequested = (unitKind, tile, teamId) =>
        {
            // Canonical lowercase key for matching. The spawn switch and the
            // bestiary/tinker lookups already normalize case; this makes the
            // post-spawn config riders (colossus_absorb, armor, body colors)
            // case-insensitive too, so a capitalized card kind (e.g. Shield_Wall)
            // can never spawn-then-silently-skip its rider. `unitKind` is kept
            // raw for the display-name derivation, which relies on its casing.
            string kindKey = unitKind.ToLowerInvariant();

            // ── U3: registry-resolved units (Deathburst, Fabricate, future keys) ──
            // Checked FIRST: if the kind is a UnitRegistry id, spawn a full
            // definition-driven unit (behavior key, tags, abilities, colours)
            // exactly like SpawnAndPlaceEnemies does — the summon seam and the
            // deployment path produce indistinguishable units (units doc §12).
            if (UnitRegistry.TryResolveId(unitKind, out var registryId))
                return SpawnRegistryUnit(registryId, tile, teamId);

            PackedScene scene = null;
            int hp = 10;
            int speed = 0;
            int armor = 0;
            bool isPlayerControlled = (teamId == 0);
            int schematicBonus = 0;

            // ── Wildlife (Druid bestiary) — data-driven, checked before the switch ──
            bool isWildlife = Bestiary.TryGet(unitKind, out WildlifeDef beast);
            if (isWildlife)
            {
                scene = PlayerUnitScene;
                hp = beast.Hp;
                speed = beast.Speed;
                armor = beast.Armor;
            }

            if (!isWildlife && IsTinkerConstructKind(unitKind))
            {
                if (ConstructRegistry.Count(State, teamId) >= GetConstructCap(teamId))
                {
                    GD.Print($"[Summon] Construct cap reached for team {teamId} — {unitKind} not deployed.");
                    return null;
                }
                schematicBonus = ConsumeDeployBonus(teamId) + EtchingSystem.ConsumeWard(tile.Axial);
            }

            if (!isWildlife)
            {
                switch (unitKind.ToLowerInvariant())
                {
                    case "stone_pillar":
                    case "boulder":
                        scene = DummyUnitScene;
                        hp = 12;
                        speed = 0;
                        armor = 5;
                        break;

                    case "earth_elemental":
                        scene = DummyUnitScene;
                        hp = 16;
                        speed = 1;
                        armor = 0;
                        break;
                    case "earth_elemental_armored":
                        scene = DummyUnitScene;
                        hp = 16;
                        speed = 1;
                        armor = 3;
                        break;

                    case "colossus":
                        scene = DummyUnitScene;
                        hp = 30;
                        speed = 1;
                        armor = 5;
                        break;
                    case "colossus_empowered":
                        scene = DummyUnitScene;
                        hp = 30;
                        speed = 1;
                        armor = kindKey == "colossus_empowered" ? 8 : 5;
                        break;

                    // Terraform tier 3: "iron-clad — taunts adjacent enemies".
                    case "colossus_iron":
                        scene = DummyUnitScene;
                        hp = 30;
                        speed = 1;
                        armor = 8;
                        break;

                    // Terraform tier 4: "living fortress — massive, mobile, devastating".
                    case "colossus_fortress":
                        scene = DummyUnitScene;
                        hp = 40;
                        speed = 2;
                        armor = 8;
                        break;

                    case "decoy":
                        scene = DummyUnitScene;
                        hp = 10;
                        speed = 0;
                        armor = 0;
                        break;

                    case "shield_wall":
                        scene = DummyUnitScene;
                        hp = 20;
                        speed = 0;
                        armor = 8;
                        break;
                    case "shield_wall_heavy":
                        scene = DummyUnitScene;
                        hp = 20;
                        speed = 0;
                        armor = 12;
                        break;

                    case "spirit":
                    case "spirit_wall":
                    case "revenant":
                    case "revenant_champion":
                    case "revenant_elder":
                    case "covenant_elder":
                    case "ossuary":
                    case "ossuary_shrine":
                    case "ossuary_garden":
                    case "soul_well":
                    case "memorial_seat":
                    case "covenant_seat":
                        scene = PlayerUnitScene;
                        hp = 10;
                        speed = 1;
                        armor = 0;
                        break;

                    case "arcaneconstruct":
                    case "arcane_construct":
                        scene = DummyUnitScene;
                        hp = 12;
                        speed = 2;
                        armor = 2;
                        break;

                    case "livingspell":
                    case "living_spell":
                        scene = DummyUnitScene;
                        hp = 8;
                        speed = 3;
                        armor = 0;
                        break;

                    case "illusion":
                        scene = DummyUnitScene;
                        hp = 10;
                        speed = 2;
                        armor = 0;
                        break;

                    case "drone":
                    case "turret":
                    case "cannon":
                    case "grand_turret":
                    case "siege_engine":
                    case "sentinel":
                    case "lattice_node":
                    case "familiar":
                    case "tinker_barrier":
                    case "tinker_colossus":
                    case "foundry":
                        {
                            var st = TinkerConstructStats(unitKind);
                            scene = DummyUnitScene;
                            hp = st.Hp + schematicBonus;
                            speed = st.Speed;
                            armor = st.Armor;
                        }
                        break;

                    default:
                        GD.PrintErr($"[Summon] Unknown unit kind: {unitKind}");
                        return null;
                }
            }

            if (scene == null)
                return null;

            var unit = scene.Instantiate<Unit>();
            unit.IsPlayerControlled = isPlayerControlled;
            unit.TeamId = teamId;
            unit.StartMaxHealth = hp;
            unit.StartHealth = hp;
            unit.StartBaseSpeed = speed;
            unit.StartMaxMana = 0;
            unit.StartMana = 0;
            unit.StartArmor = armor;
            unit.StartShield = 0;

            AddChild(unit);
            unit.PlaceOnTile(tile);
            unit.MaxActionPoints = unit.Stats.BaseSpeed;
            unit.CurrentActionPoints = unit.MaxActionPoints;

            string suffix = unitKind.Replace("_", " ");
            suffix = char.ToUpper(suffix[0]) + suffix.Substring(1);
            unit.Name = suffix;
            unit.RefreshNameLabel();

            if (!isWildlife && IsTinkerConstructKind(unitKind))
                ConfigureTinkerConstruct(unit, unitKind, teamId, schematicBonus);

            if (kindKey is "colossus" or "colossus_empowered")
                unit.ApplyStatus("colossus_absorb", 999);

            // Iron/Fortress Colossus: taunts — enemies prefer it (FindNearestPlayerUnit).
            if (kindKey is "colossus_iron" or "colossus_fortress")
            {
                unit.ApplyStatus("colossus_absorb", 999);
                unit.IsTaunting = true;
            }

            // Persistent marker so the death hook can leave a carcass (same pattern as colossus_absorb)
            if (isWildlife)
            {
                unit.ApplyStatus("wildlife", 999);

                // Wildlife fights like a martial companion: select it, click an
                // enemy in range to attack (PT7 — summoned Boar had no attack
                // input; click fell through to InspectEnemy). Damage comes from
                // the bestiary; default 5 if the def omits it.
                unit.IsMartial = true;
                unit.AttackRange = 1;
                if (beast.Damage > 0)
                    unit.AttackDamage = beast.Damage;

                // Identity pass (2026-07-12): behavior tags drive pack/charge/
                // bulwark riders; ap/moveRange decouple action count from reach
                // (Boar: 2 AP but 5-tile moves — a fast line-breaker).
                foreach (var t in beast.Tags)
                    if (!unit.BehaviorTags.Contains(t))
                        unit.BehaviorTags.Add(t);
                if (beast.Ap > 0)
                {
                    unit.MaxActionPoints = beast.Ap;
                    unit.CurrentActionPoints = beast.Ap;
                }
                if (beast.MoveRange > 0)
                    unit.MoveRange = beast.MoveRange;
            }

            if (kindKey.Contains("pillar") || kindKey.Contains("boulder"))
                unit.SetBodyColor(UITheme.SummonColorPillar);
            else if (kindKey is "spirit" or "spirit_wall" or "revenant"
                or "revenant_champion" or "revenant_elder" or "covenant_elder"
                or "ossuary" or "ossuary_shrine" or "ossuary_garden"
                or "soul_well" or "memorial_seat" or "covenant_seat")
            {
                // spirit visuals handled by ApplySpiritAppearance
            }
            else if (isWildlife)
                unit.SetBodyColor(UITheme.SummonColorFriendly); // TODO: add UITheme.SummonColorWildlife (a green)
            else if (kindKey is "arcaneconstruct" or "arcane_construct"
                or "livingspell" or "living_spell" or "illusion")
                unit.SetBodyColor(UITheme.SummonColorFriendly);
            else if (isPlayerControlled)
                unit.SetBodyColor(UITheme.SummonColorFriendly);
            else
                unit.SetBodyColor(UITheme.SummonColorEnemy);

            if (isPlayerControlled)
                playerUnits.Add(unit);
            else
                enemyUnits.Add(unit);

            State.UnitsInPlay.Add(unit);

            GD.Print($"[Summon] Spawned {suffix} at {tile.Axial} (HP:{hp} SPD:{speed} ARM:{armor})");
            return unit;
        };
    }

    /// <summary>U3: spawns a fully definition-driven unit through the summon seam
    /// (Deathburst, Fabricate, future ability keys). Mirrors SpawnAndPlaceEnemies'
    /// config exactly — behavior key, tags, abilities, colour, death wiring — so
    /// risen units fight identically to deployed ones. Base stats only: the
    /// difficulty mult applies at encounter spawn, not to mid-fight summons
    /// (they're an ability's output, not an encounter slot — ruling logged).</summary>
    private Unit SpawnRegistryUnit(string unitId, TileData tile, int teamId)
    {
        var def = UnitRegistry.Get(unitId);
        var unit = DummyUnitScene.Instantiate<Unit>();
        unit.IsPlayerControlled = (teamId == 0);
        unit.TeamId = teamId;
        unit.StartMaxHealth = def.MaxHealth;
        unit.StartHealth = def.MaxHealth;
        unit.StartBaseSpeed = def.BaseSpeed;
        unit.StartMaxMana = 0;
        unit.StartMana = 0;
        unit.StartArmor = def.Armor;
        unit.StartShield = 0;

        AddChild(unit);
        unit.OnDied += HandleUnitDeath;
        unit.PlaceOnTile(tile);
        unit.MaxActionPoints = def.BaseSpeed;
        unit.CurrentActionPoints = unit.MaxActionPoints;

        int sameKind = 1;
        foreach (var u in enemyUnits)
            if (u != null && IsInstanceValid(u) && u.DefinitionId == def.Id)
                sameKind++;
        foreach (var u in playerUnits)
            if (u != null && IsInstanceValid(u) && u.DefinitionId == def.Id)
                sameKind++;

        unit.Name = sameKind > 1 ? $"{def.ThreatLabel}_{sameKind}" : def.ThreatLabel;
        unit.DisplayName = unit.Name;
        unit.DefinitionId = def.Id;
        unit.BehaviorKey = def.BehaviorKey;
        unit.BehaviorTags = new List<string>(def.BehaviorTags);
        unit.Abilities = def.Abilities;
        unit.Role = def.Role;
        unit.FactionId = def.FactionId;
        unit.AttackRange = def.AttackRange;
        unit.AttackDamage = def.AttackDamage;
        unit.SetBodyColor(def.BodyColor);
        unit.RefreshNameLabel();

        if (teamId == 0)
            playerUnits.Add(unit);
        else
            enemyUnits.Add(unit);
        State.UnitsInPlay.Add(unit);

        GD.Print($"[Summon] Registry unit {def.Id} rises at {tile.Axial} " +
                 $"(HP:{def.MaxHealth} SPD:{def.BaseSpeed} ARM:{def.Armor}).");
        RefreshEnemyRoster();
        return unit;
    }

    /// <summary>
    /// Applies EquipmentLoadout stat bonuses and passive tags to a player unit.
    /// Called immediately after the unit is spawned and initialized.
    /// unitId: "wizard" for the main wizard, companion ID for companions.
    /// </summary>
    private void ApplyEquipmentLoadout(Unit unit, string unitId)
    {
        var loadout = EquipmentLoadout.Get(unitId);
        if (loadout == null)
            return;

        // Q1 completion (Phase B): capture the pre-apply baseline so the
        // parity assertion below can verify every bonus actually landed —
        // the item system's "mostly broken" era gets a floor Q2 can stand on.
        int baseMaxHP = unit.Stats.MaxHealth, baseMaxMana = unit.Stats.MaxMana;
        int baseArmor = unit.Stats.Armor, baseSpeed = unit.Stats.BaseSpeed;
        int baseAtkDmg = unit.AttackDamage, baseAtkRng = unit.AttackRange;
        int baseShield = unit.Stats.Shield;

        // ── Stat modifiers ────────────────────────────────────────────────
        if (loadout.BonusMaxHP > 0)
        {
            unit.Stats.MaxHealth += loadout.BonusMaxHP;
            unit.Stats.Health += loadout.BonusMaxHP;
        }

        if (loadout.BonusMaxMana > 0)
        {
            unit.Stats.MaxMana += loadout.BonusMaxMana;
            unit.Stats.Mana += loadout.BonusMaxMana;
        }

        if (loadout.BonusArmor > 0)
            unit.Stats.Armor += loadout.BonusArmor;

        if (loadout.BonusBaseSpeed != 0)
            unit.Stats.BaseSpeed += loadout.BonusBaseSpeed;

        if (loadout.BonusAttackDamage != 0)
            unit.AttackDamage += loadout.BonusAttackDamage;

        if (loadout.BonusAttackRange != 0)
            unit.AttackRange += loadout.BonusAttackRange;

        if (loadout.BonusSpellDamage != 0)
            unit.BonusSpellDamage = loadout.BonusSpellDamage;

        // ── Passive tags ──────────────────────────────────────────────────
        unit.EquipmentPassives = new List<(ItemPassiveTag, int)>(loadout.Passives);

        // Q2 (§7a): trigger-bus item abilities — dispatched on the shared map,
        // separate from the enum passives (so the Q1 parity assert below, which
        // counts EquipmentPassives, stays valid). onSpawn fires at method end.
        unit.ItemAbilities = new List<ItemAbility>(loadout.Abilities);

        // Apply immediate passives that take effect at combat start
        foreach (var (tag, value) in loadout.Passives)
        {
            switch (tag)
            {
                case ItemPassiveTag.StartCombatWithShield:
                    unit.Stats.Shield += value;
                    break;
                    // Other passives are applied at their relevant moment
                    // (turn start, on attack, etc.) — see passive hooks below
            }
        }

        unit.RefreshHealthBar();

        if (loadout.Passives.Count > 0 || HasAnyBonus(loadout))
            GD.Print($"[Equipment] Applied loadout to {unit.Name}: " +
                     $"+HP:{loadout.BonusMaxHP} +Mana:{loadout.BonusMaxMana} " +
                     $"+Armor:{loadout.BonusArmor} +Spd:{loadout.BonusBaseSpeed} " +
                     $"Passives:{loadout.Passives.Count}");

        // ── Q1 parity assertion: expected = baseline + loadout, verified stat
        // by stat at spawn. Fails LOUDLY (PushError) — a silently-dropped item
        // bonus is exactly the defect class this exists to catch.
        int expShieldBonus = 0;
        foreach (var (tag, value) in loadout.Passives)
            if (tag == ItemPassiveTag.StartCombatWithShield)
                expShieldBonus += value;

        var mismatches = new List<string>();
        void Check(string stat, int expected, int actual)
        { if (expected != actual) mismatches.Add($"{stat}: expected {expected}, got {actual}"); }

        Check("MaxHP", baseMaxHP + Math.Max(0, loadout.BonusMaxHP), unit.Stats.MaxHealth);
        Check("MaxMana", baseMaxMana + Math.Max(0, loadout.BonusMaxMana), unit.Stats.MaxMana);
        Check("Armor", baseArmor + Math.Max(0, loadout.BonusArmor), unit.Stats.Armor);
        Check("Speed", baseSpeed + loadout.BonusBaseSpeed, unit.Stats.BaseSpeed);
        Check("AtkDmg", baseAtkDmg + loadout.BonusAttackDamage, unit.AttackDamage);
        Check("AtkRng", baseAtkRng + loadout.BonusAttackRange, unit.AttackRange);
        Check("Shield", baseShield + expShieldBonus, unit.Stats.Shield);
        Check("SpellDmg", loadout.BonusSpellDamage, unit.BonusSpellDamage);
        Check("PassiveCount", loadout.Passives.Count, unit.EquipmentPassives.Count);

        if (mismatches.Count > 0)
            GD.PushError($"[Q1 Parity] {unit.Name} loadout '{unitId}' MISMATCH — " +
                         string.Join("; ", mismatches));
        else if (loadout.Passives.Count > 0 || HasAnyBonus(loadout))
            GD.Print($"[Q1 Parity] {unit.Name}: loadout '{unitId}' verified item-for-item.");

        // Q2 (§7a): onSpawn item triggers — fired AFTER the parity assert so the
        // ward's shield doesn't read as a stat mismatch. Shared dispatcher + log.
        FireItemSpawnTriggers(unit);
    }

    private static bool HasAnyBonus(ResolvedLoadout l) =>
        l.BonusMaxHP != 0 || l.BonusMaxMana != 0 || l.BonusArmor != 0 ||
        l.BonusBaseSpeed != 0 || l.BonusAttackDamage != 0 ||
        l.BonusAttackRange != 0 || l.BonusSpellDamage != 0;

    // ═══════════════════════════════════════════════════════════════════════
    // Casting / card logic
    // ═══════════════════════════════════════════════════════════════════════

    private bool CheckCastRequirements(CardHalf half, TargetSet targets, out string failReason)
    {
        failReason = null;
        if (half.Requirements == null || half.Requirements.Length == 0)
            return true;

        foreach (var req in half.Requirements)
        {
            switch (req.ToLowerInvariant())
            {
                case "stone_tile":
                    if (!TargetHasTileType(targets, TileTerrainType.Stone, TileElementType.Earth))
                    {
                        failReason = "Requires a stone tile!";
                        return false;
                    }
                    break;

                case "ice_tile":
                    if (!TargetHasTileType(targets, TileTerrainType.Ice, TileElementType.Frost))
                    {
                        failReason = "Requires an ice tile!";
                        return false;
                    }
                    break;

                case "fire_tile":
                    if (!TargetHasTileType(targets, TileTerrainType.Lava, TileElementType.Fire))
                    {
                        failReason = "Requires a fire tile!";
                        return false;
                    }
                    break;

                case "storm_tile":
                    if (!TargetHasTileType(targets, TileTerrainType.Grass, TileElementType.Lightning))
                    {
                        failReason = "Requires a storm tile!";
                        return false;
                    }
                    break;

                case "empty_tile":
                    if (!TargetHasEmptyTile(targets))
                    {
                        failReason = "Requires an empty tile!";
                        return false;
                    }
                    break;

                case "memorial_tile":
                    // Target-tile check: enforced by the targeter (only memorial tiles
                    // are selectable), so nothing to validate here at cast time.
                    break;

                case "memorial_or_spirit_tile":
                    // Also enforced by the targeter — pass through.
                    break;

                case "any_memorial":
                    // Board-state check: at least one memorial must exist anywhere.
                    if (State.Memorials == null || State.Memorials.CountMemorials() == 0)
                    {
                        failReason = "Requires at least one memorial on the board!";
                        return false;
                    }
                    break;

                case "any_spirit":
                    // Board-state check: at least one friendly spirit must be in play.
                    bool hasSpirit = false;
                    foreach (var u in State.UnitsInPlay)
                    {
                        if (u != null && u.IsSpirit && u.SummonerTeamId == selectedUnit?.TeamId
                            && u.Stats.IsAlive)
                        {
                            hasSpirit = true;
                            break;
                        }
                    }
                    if (!hasSpirit)
                    {
                        failReason = "Requires at least one spirit in play!";
                        return false;
                    }
                    break;

                case "spirit_or_memorial":
                    // Board-state check: either a spirit or a memorial must exist.
                    bool hasSpiritOrMem = State.Memorials?.CountMemorials() > 0;
                    if (!hasSpiritOrMem)
                    {
                        foreach (var u in State.UnitsInPlay)
                        {
                            if (u != null && u.IsSpirit && u.SummonerTeamId == selectedUnit?.TeamId
                                && u.Stats.IsAlive)
                            {
                                hasSpiritOrMem = true;
                                break;
                            }
                        }
                    }
                    if (!hasSpiritOrMem)
                    {
                        failReason = "Requires a spirit or memorial on the board!";
                        return false;
                    }
                    break;

                case "growth_tile":
                    if (!TargetHasGrowth(targets, 1))
                        return false;
                    break;
                case "old_growth_tile":
                    if (!TargetHasGrowth(targets, 3))
                        return false;
                    break;
                case "any_growth":
                    if (!AnyGrowthOnBoard())
                        return false;
                    break;
            }
        }

        return true;
    }

    private bool TargetHasTileType(TargetSet targets, TileTerrainType terrain, TileElementType element)
    {
        if (targets == null)
            return false;

        foreach (var obj in targets.Items)
        {
            TileData tile = null;
            if (obj is TileData td)
                tile = td;
            else if (obj is HexTile tv)
                tile = grid.GetTile(tv.Axial);
            else if (obj is Unit u && u.CurrentTile != null)
                tile = u.CurrentTile;
            else if (obj is Entity e)
            {
                // Self-targeting: check the caster's tile
                if (selectedUnit?.CurrentTile != null)
                    tile = selectedUnit.CurrentTile;
            }

            if (tile == null)
                continue;
            if (tile.TerrainType == terrain || tile.ElementType == element)
                return true;
        }

        return false;
    }

    private bool TargetHasEmptyTile(TargetSet targets)
    {
        if (targets == null)
            return false;

        foreach (var obj in targets.Items)
        {
            TileData tile = null;
            if (obj is TileData td)
                tile = td;
            else if (obj is HexTile tv)
                tile = grid.GetTile(tv.Axial);

            if (tile != null && tile.Occupant == null)
                return true;
        }

        return false;
    }

    private void OnCardHalfHovered(CardUi cardUi, bool isTop, bool isEntering)
    {
        if (currentPhase != CombatPhase.PlayerTurn)
            return;

        // Lock during drag — ignore hover changes on other cards entirely
        if (_isCardBeingDragged)
            return;

        if (isEntering)
        {
            var half = isTop ? cardUi.TopHalf : cardUi.BottomHalf;
            ShowTargetHighlight(half);
        }
        else
        {
            ClearTargetHighlight();
        }
    }

    /// <summary>Every failed card drop reports WHY — to the action log (player)
    /// AND the console (playtest transcripts). PT8: silent failed drops.</summary>
    private void CastFail(string msg)
    {
        GD.Print($"[CastFail] {msg}");
        combatUI?.AppendActionLog($"✕ {msg}");
    }

    private void OnCardDroppedOnTile(CardUi cardUi, bool isTop, HexTile tile)
    {
        _isCardBeingDragged = false;
        _draggedHalf = null;
        ClearTargetHighlight();

        if (isInDeploymentPhase)
        { GD.Print("Cannot cast during deployment."); return; }

        var half = isTop ? cardUi.TopHalf : cardUi.BottomHalf;
        if (half == null)
        { State.Log("Dropped half was null."); return; }

        // ── U3: priority-window speed gate ────────────────────────────────
        // Two-speed ruling (2026-07-10): while a trigger window is open, any
        // NON-SORCERY half may be cast as a response (it lands ON TOP of the
        // trigger and resolves first). Other schools opt in by reserving mana
        // — which evaporates at turn start; the Chronomancer's bank persists.
        // Outside a window, casting during the enemy phase stays blocked.
        if (_priorityWindowOpen && half.Speed == PlaySpeed.Studied)
        {
            combatUI?.AppendActionLog("Studied spells cannot respond — only Reflexes.");
            GD.Print($"[Priority] rejected {half.Name} — Studied speed.");
            return;
        }
        if (!_priorityWindowOpen && currentPhase == CombatPhase.EnemyTurn)
        {
            CastFail($"{half.Name}: cannot cast — enemy turn, no reaction window open.");
            return;
        }

        if (selectedUnit != null && !selectedUnit.CanAct())
        {
            GD.Print($"{selectedUnit.Name} is frozen and cannot act!");
            combatUI?.AppendActionLog($"{selectedUnit.Name} is frozen!");
            return;
        }

        // ── Channel resolution ────────────────────────────────────────────
        bool isChanneling = Input.IsKeyPressed(Key.Shift);
        CardHalf resolvedHalf = half;

        if (isChanneling && half.CanChannel)
        {
            var channelHalf = ChannelResolver.ResolveChannel(half, cardUi.CardInstance);
            if (channelHalf != null)
            {
                int totalCost = half.ManaCost + ChannelResolver.ChannelManaCost;
                if ((selectedUnit?.Stats.Mana ?? 0) < totalCost)
                {
                    CastFail($"{half.Name}: not enough mana to channel ({selectedUnit?.Stats.Mana ?? 0}/{totalCost}).");
                    return;
                }
                resolvedHalf = channelHalf;
                selectedUnit.Stats.Mana -= ChannelResolver.ChannelManaCost;
                combatUI?.AppendActionLog($"[Channel] {half.Name} → {channelHalf.Name}");
            }
        }

        // Flag for the is_channeled predicate — true only while this cast resolves.
        State.LastCastWasChannel = resolvedHalf != half;

        GD.Print($"Attempt cast {resolvedHalf.Name} cost? " +
                 $"{(resolvedHalf.Costs.Length > 0 ? resolvedHalf.Costs[0].GetType().Name : "none")} " +
                 $"mana={State.Mana[Me]}");

        // ── Target building ───────────────────────────────────────────────
        // Use resolvedHalf.Targeting so channeled half's targeter is respected
        var targets = new TargetSet();
        switch (resolvedHalf.Targeting)
        {
            case SelectUnitTarget ut:
                var unit = State.UnitsInPlay
                    .FirstOrDefault(u => u?.CurrentTile?.Axial == tile.Axial && u.Stats.IsAlive);
                if (unit == null)
                {
                    CastFail($"{resolvedHalf.Name}: no unit on tile {tile.Axial}.");
                    return;
                }
                if (selectedUnit?.CurrentTile != null)
                {
                    int dist = grid.Distance(selectedUnit.CurrentTile.Axial, unit.CurrentTile.Axial);
                    if (dist > ut.range)
                    {
                        CastFail($"{resolvedHalf.Name}: {unit.Name} is out of range ({dist} > {ut.range}).");
                        return;
                    }
                }
                if (!grid.HasLineOfSight(selectedUnit.CurrentTile.Axial, unit.CurrentTile.Axial))
                {
                    var blocker = grid.FirstLosBlocker(selectedUnit.CurrentTile.Axial, unit.CurrentTile.Axial);
                    string what = blocker == null ? "terrain"
                        : blocker.GrowthStage >= 2 ? $"thicket growth at {blocker.Axial}"
                        : !string.IsNullOrEmpty(blocker.ObstacleKind) ? $"{blocker.ObstacleKind} at {blocker.Axial}"
                        : $"terrain at {blocker.Axial}";
                    CastFail($"{resolvedHalf.Name}: no line of sight to {unit.Name} — blocked by {what}.");
                    return;
                }
                if (ut.enemyOnly && unit.TeamId == selectedUnit?.TeamId)
                {
                    CastFail($"{resolvedHalf.Name}: must target an enemy.");
                    return;
                }
                targets.Items.Add(unit);
                break;

            case SelectTileTarget tt:
                var tileData = grid.GetTile(tile.Axial);
                if (tileData == null)
                { CastFail($"{resolvedHalf.Name}: invalid tile."); return; }
                if (selectedUnit?.CurrentTile != null)
                {
                    int dist = grid.Distance(selectedUnit.CurrentTile.Axial, tile.Axial);
                    if (dist > tt.range)
                    {
                        CastFail($"{resolvedHalf.Name}: tile is out of range ({dist} > {tt.range}).");
                        return;
                    }
                }
                targets.Items.Add(tileData);
                break;

            case SelectSelfTarget:
                targets.Items.Add(Me);
                break;

            case SelectAreaTarget:
            case SelectGlobalTarget:
                targets.Items.Add(Me);
                break;

            case SelectElementTileTarget:
                var etData = grid.GetTile(tile.Axial);
                if (etData == null)
                { State.Log("Invalid tile."); return; }
                targets.Items.Add(etData);
                break;

            case SelectConeTarget:
            case SelectLineTarget:
            case SelectRingTarget:
            case null:
                break;

            case SelectEmptyTileTarget et:
                var emptyTile = grid.GetTile(tile.Axial);
                if (emptyTile == null)
                { CastFail($"{resolvedHalf.Name}: invalid tile."); return; }
                if (selectedUnit?.CurrentTile != null)
                {
                    int dist = grid.Distance(selectedUnit.CurrentTile.Axial, tile.Axial);
                    if (dist > et.Range)
                    {
                        CastFail($"{resolvedHalf.Name}: tile is out of range ({dist} > {et.Range}).");
                        return;
                    }
                }
                if (emptyTile.Occupant != null)
                {
                    CastFail($"{resolvedHalf.Name}: that tile is occupied.");
                    return;
                }
                targets.Items.Add(emptyTile);
                break;

            default:
                GD.PrintErr($"[GameRunner] Unhandled targeter type: {resolvedHalf.Targeting.GetType().Name}");
                targets.Items.Add(Me);
                break;
        }

        if (!CheckCastRequirements(resolvedHalf, targets, out var failMsg))
        {
            CastFail($"{resolvedHalf.Name}: {failMsg}");
            return;
        }

        // R22 self-check (debug): predict this exact cast's damage via the SAME
        // ComputePreviewDamage the flashing preview uses, and snapshot pre-cast
        // HP, so we can verify prediction == reality once it resolves.
        Dictionary<Unit, int> selfCheckPredicted = null;
        Dictionary<Unit, int> selfCheckPreHp = null;
        if (PlayerSession.DebugPreviewSelfCheck)
        {
            var scTile = grid.GetTile(tile.Axial);
            if (scTile?.Occupant != null && IsInstanceValid(scTile.Occupant)
                && scTile.Occupant.TeamId != selectedUnit.TeamId)
            {
                selfCheckPredicted = ComputePreviewDamage(resolvedHalf, scTile);
                if (selfCheckPredicted != null)
                {
                    selfCheckPreHp = new Dictionary<Unit, int>();
                    foreach (var v in selfCheckPredicted.Keys)
                        selfCheckPreHp[v] = v.Stats.Health;
                }
            }
        }

        State.ActiveCasterUnit = selectedUnit;

        var ok = Rules.TryCastWithTargets(resolvedHalf, State, Me, targets, cardUi.CardInstance);
        GD.Print($"Cast result={ok} manaNow={State.Mana[Me]}");

        // R22 self-check: once the cast settles, compare actual HP delta to the
        // prediction. 0.5s covers direct damage + imbue tick + attunement bonus.
        if (ok && selfCheckPredicted != null && selfCheckPreHp != null)
        {
            string scCardName = resolvedHalf.Name;
            var scPredicted = selfCheckPredicted;
            var scPreHp = selfCheckPreHp;
            var scTimer = GetTree().CreateTimer(0.5f);
            scTimer.Timeout += () => VerifyPreviewSelfCheck(scCardName, scPredicted, scPreHp);
        }

        if (!ok)
        {
            if (State.Mana[Me] < resolvedHalf.ManaCost)
                CastFail($"{resolvedHalf.Name}: not enough mana ({State.Mana[Me]}/{resolvedHalf.ManaCost}).");
            else
                CastFail($"{resolvedHalf.Name}: cast failed (cost or timing not payable).");
        }

        if (ok)
        {
            combatUI?.AppendActionLog($"Cast: {resolvedHalf.Name}.");
            // Record mastery cast against the original half's blueprint
            if (!string.IsNullOrEmpty(cardUi.CardInstance?.BlueprintId))
                CastMasteryTracker.RecordCast(cardUi.CardInstance.BlueprintId);

            CombatTelemetry.RecordCardCast(
                cardUi.CardInstance?.BlueprintId ?? resolvedHalf.Name,
                ReferenceEquals(resolvedHalf, cardUi.CardInstance?.TopHalf) ? "top"
                    : ReferenceEquals(resolvedHalf, cardUi.CardInstance?.BottomHalf) ? "bottom"
                    : "resolved",
                resolvedHalf.School.ToString(),
                resolvedHalf.ManaCost,
                roundNumber);

            if (selectedUnit != null)
                selectedUnit.Stats.HasPlayedCardThisTurn = true;

            State.SpellsCastThisTurn++;

            if (State.ActiveEffects != null && selectedUnit != null)
                foreach (var effect in State.ActiveEffects.ToList())
                    if (effect.Owner == Me && !effect.IsExpired)
                        effect.OnSpellCast(State, selectedUnit, targets);

            // Fate attunement
            if (selectedUnit?.Attunement is FateAttunement fate)
            {
                fate.OnSpellCast(resolvedHalf.Speed, State.SpellsCastThisTurn);
                schoolAttunementUI?.Refresh();
            }

            // Arcane attunement
            if (selectedUnit?.Attunement is ArcaneAttunement arcane)
            {
                string cardId = cardUi.CardInstance?.BlueprintId ?? "";
                string cardName = resolvedHalf.Name ?? "";
                arcane.OnSpellCast(cardId, cardName);
                schoolAttunementUI?.Refresh();
            }

            // Elementalist attunement
            if (selectedUnit != null &&
                selectedUnit.School == CardSchool.Elementalist &&
                selectedUnit.Attunement is ElementalAttunement elemAtt &&
                resolvedHalf.Tags != null && resolvedHalf.Tags.Length > 0)
            {
                var burstEffects = elemAtt.OnSpellCast(resolvedHalf.Tags);

                var bonusLog = AttunementResolver.ApplyThresholdEffects(
                    elemAtt, resolvedHalf.Tags, State, selectedUnit, targets);
                foreach (var msg in bonusLog)
                {
                    GD.Print(msg);
                    combatUI?.AppendActionLog(msg);
                }

                foreach (var burst in burstEffects)
                {
                    var burstLog = AttunementResolver.ResolveBurst(burst.Element, State, selectedUnit);
                    foreach (var msg in burstLog)
                    {
                        GD.Print(msg);
                        combatUI?.AppendActionLog(msg);
                    }
                }

                schoolAttunementUI?.Refresh();
                RefreshAllUI();
            }

            if (resolvedHalf.Tags != null)
            {
                foreach (var tag in resolvedHalf.Tags)
                {
                    if (ElementalAttunement.TryParseTag(tag, out var elem))
                    {
                        selectedUnit.LastCastElement = elem;
                        break;
                    }
                }
            }

            // U3: while a priority window is open, the trigger drain loop owns
            // resolution — the response stays ON the stack (above the trigger)
            // and resolves when the player passes. Otherwise drain as before.
            if (!_priorityWindowOpen)
            {
                while (!State.Stack.IsEmpty)
                    State.Resolver.ResolveTop(State);
            }

            if (State.ActiveEffects != null && selectedUnit != null)
                foreach (var effect in State.ActiveEffects.ToList())
                    if (effect.Owner == Me && !effect.IsExpired)
                        effect.OnSpellResolved(State, selectedUnit, targets);

            RefreshEnemyRoster();

            // U3: kills during this cast queued death triggers — resolve their
            // stack (with priority windows) now. No-op when nothing is queued;
            // guarded against re-entry while a window-owned drain runs.
            if (!_priorityWindowOpen)
                KickTriggerDrain();

            if (deckManager != null && cardUi.CardInstance != null)
            {
                deckManager.DiscardCard(cardUi.CardInstance);
                GD.Print($"Discarded: {cardUi.CardInstance.CardName}");
            }

            State.ActiveCasterUnit = null;
            selectedUnit?.SyncManaToBar();
            RefreshSelectedUnitUI();
            RefreshPlayerUnitBar();
            // A cast may have changed movement (Dash/Imbue grant BonusMoveRange) or
            // position — recompute the move zone so the new range shows immediately.
            if (selectedUnit != null)
            {
                ClearMoveTiles();
                ShowMoveTilesWithCost(selectedUnit);
            }
            deckUiManager?.RefreshAffordability();
            RefreshDeckCounts();
        }
    }

    private void OnCardDragStarted(CardUi cardUi, bool isTop)
    {
        _isCardBeingDragged = true;
        var half = isTop ? cardUi.TopHalf : cardUi.BottomHalf;
        _draggedHalf = half;
        ShowTargetHighlight(half);

        // Show channel hint if available
        if (half?.CanChannel ?? false)
            combatUI?.SetHintText("Drop to cast · Hold Shift to channel (+1 mana)");
    }

    private void OnCardDragEnded()
    {
        _isCardBeingDragged = false;
        _draggedHalf = null;
        ClearTargetHighlight();
        ClearDamagePreview();   // R22
        combatUI?.SetHintText("Select a unit, move, cast, then end turn.");
    }

    // ── R22 damage preview ───────────────────────────────────────────────────

    /// <summary>Every unit currently showing a flashing HP-bar preview segment
    /// (primary + chain / AoE / retarget victims).</summary>
    private readonly List<Unit> _previewedVictims = new();

    private void ClearDamagePreview()
    {
        foreach (var v in _previewedVictims)
            if (v != null && IsInstanceValid(v))
                v.ClearHpDamagePreview();
        _previewedVictims.Clear();
    }

    /// <summary>R22 self-check (DebugPreviewSelfCheck): after a real cast settles,
    /// compare each predicted per-enemy HP loss to the actual delta. A DESYNC line
    /// means the CombatSim preview and the live resolver diverged — the guard that
    /// makes the interception approach maintainable without a parallel test suite.
    /// A unit freed by a lethal hit is read as having lost all its pre-cast HP.</summary>
    private void VerifyPreviewSelfCheck(string cardName,
        Dictionary<Unit, int> predicted, Dictionary<Unit, int> preHp)
    {
        foreach (var kv in predicted)
        {
            var v = kv.Key;
            int actual = (v != null && IsInstanceValid(v) && v.Stats.IsAlive)
                ? preHp[v] - v.Stats.Health   // survived → live delta
                : preHp[v];                   // died/freed → lost all pre-cast HP
            if (actual != kv.Value)
                GD.PrintErr($"[PreviewSelfCheck] DESYNC on {cardName} → " +
                    $"{(v != null ? v.Name : "?")}: predicted −{kv.Value} HP, actual −{actual}.");
            else
                GD.Print($"[PreviewSelfCheck] OK {cardName} → " +
                    $"{(v != null ? v.Name : "?")}: −{kv.Value} HP.");
        }
    }

    /// <summary>Predicted damage while dragging a card half over an enemy,
    /// rendered as a flashing span of the victim's HP bar equal to the HP it
    /// would lose. R22: this RUNS THE REAL EFFECT RESOLUTION in CombatSim mode —
    /// the mutation chokepoints (Unit.ApplyDamage/ApplyStatus/RemoveStatus,
    /// ImbueTile's tile write, GameState.Log) divert to a per-hit ledger / no-op
    /// instead of touching live state, so the resolver's own code produces every
    /// number: base damage, the imbue-tile TICK, the taken conditional branch,
    /// arcane-mark consumption. It therefore cannot drift from an actual cast.
    /// Only preview-SAFE effect types run (RunPreviewEffect); a card carrying any
    /// unrecognized effect shows no preview rather than risk an ungated mutation
    /// on hover (fail-safe). Amber flash = ⚠: an open stack or pending redirect
    /// could still change the number.</summary>
    private void UpdateDamagePreview(HexTile tile)
    {
        ClearDamagePreview();

        if (!_isCardBeingDragged || _draggedHalf == null || tile == null || selectedUnit == null)
            return;

        var tileData = grid?.GetTile(tile.Axial);
        var victim = tileData?.Occupant;
        if (victim == null || !IsInstanceValid(victim) || !victim.Stats.IsAlive
            || victim.TeamId == selectedUnit.TeamId)
            return;   // the flashing preview starts only when hovering a living enemy

        var map = ComputePreviewDamage(_draggedHalf, tileData);
        if (map == null || map.Count == 0)
            return;

        bool globalWarn = State.StackCount() > 0 || _priorityWindowOpen;
        foreach (var kv in map)
        {
            bool warn = globalWarn || kv.Key.RedirectNextDamageTo != null;
            kv.Key.ShowHpDamagePreview(kv.Value, warn);
            _previewedVictims.Add(kv.Key);
        }
    }

    /// <summary>The shared preview math: per-enemy predicted HP loss for casting
    /// <paramref name="half"/> at <paramref name="tileData"/>, produced by a real
    /// CombatSim no-mutation run. Returns null when a non-preview-safe effect
    /// aborted the run (caller shows nothing); otherwise a possibly-empty map.
    /// Used BOTH by the flashing preview and by the DebugPreviewSelfCheck, so the
    /// two can never diverge from each other — the self-check compares THIS
    /// function's output against the live resolver's actual HP delta.</summary>
    private Dictionary<Unit, int> ComputePreviewDamage(CardHalf half, TileData tileData)
    {
        if (half == null || tileData == null || selectedUnit == null)
            return null;

        var primary = tileData.Occupant;
        var targets = new TargetSet();
        targets.Items.Add(tileData);
        var ctx = new PredicateContext
        {
            Game = State,
            Caster = Me,
            Targets = targets,
            Snapshot = new EffectSnapshot(),   // echo/rewind scaling unknown pre-cast
        };

        List<(Unit victim, int amount)> ledger = null;
        bool safe = true;
        var savedCaster = State.ActiveCasterUnit;
        CombatSim.Begin(State);
        State.ActiveCasterUnit = selectedUnit;   // caster-side bonuses read the RIGHT unit
        try
        {
            // Elementalist attunement bonus (a separate cast-time step applied to
            // the PRIMARY enemy before the card resolves). PreviewBonusDamageAfterCast
            // accounts for this cast's own charge increment. Gated ApplyDamage → ledger.
            if (primary != null && IsInstanceValid(primary) && primary.Stats.IsAlive
                && primary.TeamId != selectedUnit.TeamId
                && selectedUnit.School == CardSchool.Elementalist
                && selectedUnit.Attunement is ElementalAttunement elemAtt
                && half.Tags != null)
            {
                foreach (var tagStr in half.Tags)
                    if (ElementalAttunement.TryParseTag(tagStr, out var element))
                    {
                        int b = Math.Max(0, elemAtt.PreviewBonusDamageAfterCast(element));
                        if (b > 0) primary.ApplyDamage(b);
                    }
            }

            if (half.Effects != null)
                foreach (var eff in half.Effects)
                    if (!RunPreviewEffect(eff, ctx)) { safe = false; break; }

            if (safe)
                ledger = CombatSim.SnapshotHits();   // take BEFORE End() clears it
        }
        catch { safe = false; }
        finally
        {
            State.ActiveCasterUnit = savedCaster;
            CombatSim.End();
        }

        if (!safe || ledger == null)
            return null;

        // Group hits by enemy victim, preserving order for per-hit mitigation.
        var perVictim = new Dictionary<Unit, List<int>>();
        var orderList = new List<Unit>();
        foreach (var (v, amount) in ledger)
        {
            if (v == null || !IsInstanceValid(v) || !v.Stats.IsAlive)
                continue;
            if (v.TeamId == selectedUnit.TeamId)   // enemies only
                continue;
            if (!perVictim.TryGetValue(v, out var list))
            {
                list = new List<int>();
                perVictim[v] = list;
                orderList.Add(v);
            }
            list.Add(amount);
        }

        var result = new Dictionary<Unit, int>();
        foreach (var v in orderList)
        {
            int shield = Math.Max(0, v.Stats.Shield);
            int armor  = Math.Max(0, v.Stats.Armor);
            int hp     = v.Stats.Health;
            bool shrouded = v.HasStatus("shrouded");
            bool immortal = v.HasStatus("immortal");
            int hpLoss = 0;
            foreach (int hit in perVictim[v])
            {
                var (sL, aL, hL, _) = Unit.MitigateCore(hit, shield, armor, hp, shrouded, immortal);
                shield -= sL; armor -= aL; hp -= hL; hpLoss += hL;
            }
            if (hpLoss > 0)
                result[v] = hpLoss;
        }
        return result;
    }

    /// <summary>Runs one effect in CombatSim preview mode, returning false when
    /// the effect type is not preview-safe (the caller then aborts the preview,
    /// showing nothing). Leaf damage / imbue / status effects run their REAL
    /// Resolve — gated to the ledger, so imbue TICK damage and arcane-mark
    /// consumption are captured exactly. Sequence / Conditional / Retarget are
    /// navigated with the REAL targeting and branch choices, and their children
    /// recurse back through this whitelist — so a Chain Lightning bounce
    /// (retarget → damage) is captured, but a retarget → move would still fail
    /// safe. Any unrecognized effect (move, heal, summon, …) fails safe so a
    /// hover can never trigger it.</summary>
    private bool RunPreviewEffect(IEffect effect, PredicateContext ctx)
    {
        switch (effect)
        {
            case null:
                return true;
            case DealDamageEffect:
            case ImbueTileEffect:
            case ApplyStatusEffect:
                effect.Resolve(ctx.Game, ctx.Caster, ctx.Targets, ctx.Snapshot);
                return true;
            case SequenceEffect seq:
                if (seq.Steps != null)
                    foreach (var step in seq.Steps)
                        if (!RunPreviewEffect(step, ctx))
                            return false;
                return true;
            case ConditionalEffect cond:
                bool branch;
                try { branch = cond.If.Evaluate(ctx); }
                catch { branch = true; }
                var chosen = branch ? cond.Then : cond.Else;
                return chosen == null || RunPreviewEffect(chosen, ctx);
            case RetargetEffect rt:
            {
                // Mirror RetargetEffect: set the origin, run the REAL targeter
                // (nearest-enemy selection is a pure grid read), recurse the
                // child through this whitelist, then restore. Captures chain /
                // AoE bounce damage into the ledger.
                var savedOrigin = ctx.Game.RetargetOrigin;
                var savedTargets = ctx.Targets;
                bool ok = true;
                ctx.Game.RetargetOrigin = ctx.Targets;
                if (rt.Targeter != null
                    && rt.Targeter.Select(ctx.Game, ctx.Caster, out var newTargets))
                {
                    ctx.Targets = newTargets;
                    ok = RunPreviewEffect(rt.Child, ctx);
                }
                ctx.Targets = savedTargets;
                ctx.Game.RetargetOrigin = savedOrigin;
                return ok;
            }
            default:
                return false;   // unknown effect → fail safe, no preview
        }
    }

    private void OnGameEvent(GameEvent ge)
    {
        if (ge.Type != "AbilityResolved")
            return;
        if (ge.Payload is not StackItem item)
            return;
        if (item.SourceCard != null && deckManager != null
            && item.Ability is CardHalf half && half.ConsumesCardOnResolve)
        {
            deckManager.DiscardCard(item.SourceCard);
            RefreshDeckCounts();
        }
    }

    void Pass()
    {
        var advanced = State.Priority.PassPriority(State);
        if (!advanced)
            GD.Print($"Pass. Priority → {(State.Priority.PriorityHolder == Me ? "Me" : "Opp")}");
    }

    void ResolveTop()
    {
        if (State.Stack.IsEmpty)
        { GD.Print("Stack empty."); return; }
        GD.Print($"Resolving top… (stack size before: {State.StackCount()})");
        State.Resolver.ResolveTop(State);
        GD.Print($"Resolved. (stack size after: {State.StackCount()})");
    }

    void DumpHand()
    {
        var unit = playerUnits.Count > 0 ? playerUnits[0] : null;
        if (unit?.DeckData == null)
        { GD.Print("No active deck."); return; }
        GD.Print("Hand:");
        for (int i = 0; i < unit.DeckData.Hand.Count; i++)
        {
            var c = unit.DeckData.Hand[i];
            GD.Print($"[{i}] {c.CardName} (Top:{c.TopHalf?.Name ?? "-"} | Bottom:{c.BottomHalf?.Name ?? "-"})");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Target highlighting logic
    // ═══════════════════════════════════════════════════════════════════════

    private void ShowTargetHighlight(CardHalf half)
    {
        ClearTargetHighlight();
        ClearConstructAura();   // §8: targeting range takes over the tile highlights during a drag
        if (half == null || selectedUnit == null || grid == null)
            return;

        _lastHighlightedHalf = half;
        var enemyCoords = GetValidTargetCoords(half); // now also sets range highlights internally

        // Target highlights go on top of range highlights for enemy tiles
        foreach (var coord in enemyCoords)
        {
            _targetHighlightTiles.Add(coord);
            grid.GetTileView(coord)?.SetTargetHighlight(true);
        }
    }

    private void ClearTargetHighlight()
    {
        foreach (var coord in _targetHighlightTiles)
        {
            var tileView = grid.GetTileView(coord);
            tileView?.SetTargetHighlight(false);
            tileView?.SetRangeHighlight(false, false); // clear both interior and border
        }
        _targetHighlightTiles.Clear();
        _lastHighlightedHalf = null;
    }

    private HashSet<Vector2I> GetValidTargetCoords(CardHalf half)
    {
        var coords = new HashSet<Vector2I>();
        if (half?.Targeting == null || selectedUnit?.CurrentTile == null)
            return coords;

        var center = selectedUnit.CurrentTile.Axial;
        var targeter = half.Targeting;

        // Determine range from targeter type and highlight accordingly
        if (targeter is SelectUnitTarget ut)
        {
            int spellRange = ut.range;

            // Highlight interior tiles (within range)
            foreach (var kvp in grid.Tiles)
            {
                int dist = grid.Distance(center, kvp.Key);
                if (dist <= spellRange)
                {
                    _targetHighlightTiles.Add(kvp.Key);
                    grid.GetTileView(kvp.Key)?.SetRangeHighlight(
                        interior: dist < spellRange,   // subtle tint inside
                        border: dist == spellRange      // strong ring at edge
                    );
                }
            }

            // Highlight valid enemy targets on top of range
            foreach (var unit in State.UnitsInPlay)
            {
                if (unit == null || !unit.Stats.IsAlive || unit.CurrentTile == null)
                    continue;
                if (ut.enemyOnly && unit.TeamId == 0)
                    continue;
                coords.Add(unit.CurrentTile.Axial);
            }

            return coords; // return early — we handled tile highlighting directly
        }
        else if (targeter is SelectTileTarget tt)
        {
            // Show all tiles in range
            foreach (var kvp in grid.Tiles)
            {
                int dist = grid.Distance(center, kvp.Key);
                if (dist <= tt.range)
                {
                    _targetHighlightTiles.Add(kvp.Key);
                    grid.GetTileView(kvp.Key)?.SetRangeHighlight(
                        interior: dist < tt.range,
                        border: dist == tt.range
                    );
                }
            }
        }
        else if (targeter is SelectAreaTarget at)
        {
            // Show AoE radius centered on caster
            foreach (var kvp in grid.Tiles)
            {
                int dist = grid.Distance(center, kvp.Key);
                if (dist <= at.Radius)
                {
                    _targetHighlightTiles.Add(kvp.Key);
                    grid.GetTileView(kvp.Key)?.SetTargetHighlight(true); // full fill for AoE
                }
            }
        }
        else if (targeter is SelectSelfTarget || targeter is SelectGlobalTarget)
        {
            // Just highlight the caster's tile
            coords.Add(center);
        }
        else if (targeter is SelectElementTileTarget et)
        {
            // Highlight matching element tiles
            TileElementType needed = et.Element.ToLowerInvariant() switch
            {
                "fire" => TileElementType.Fire,
                "ice" => TileElementType.Frost,
                "storm" => TileElementType.Lightning,
                "stone" => TileElementType.Earth,
                _ => TileElementType.None
            };
            foreach (var kvp in grid.Tiles)
                if (kvp.Value?.ElementType == needed)
                    coords.Add(kvp.Key);
        }
        else if (targeter is SelectConeTarget ct)
        {
            var hexDirs = new Vector2I[]
            {
                new(1, 0), new(1, -1), new(0, -1),
                new(-1, 0), new(-1, 1), new(0, 1)
            };

            foreach (var dir in hexDirs)
            {
                // Only highlight the spine (center column) of each cone direction
                for (int step = 1; step <= ct.Range; step++)
                {
                    var coord = center + dir * step;
                    var tileData = grid.GetTile(coord);
                    if (tileData == null)
                        continue;

                    bool isTip = step == ct.Range;
                    _targetHighlightTiles.Add(coord);
                    grid.GetTileView(coord)?.SetRangeHighlight(
                        interior: !isTip,
                        border: isTip
                    );
                }
            }

            // Highlight valid targets on top
            foreach (var unit in State.UnitsInPlay)
            {
                if (unit == null || !unit.Stats.IsAlive || unit.CurrentTile == null)
                    continue;
                if (ct.EnemiesOnly && unit.TeamId == 0)
                    continue;
                coords.Add(unit.CurrentTile.Axial);
            }
        }
        else if (targeter is SelectLineTarget lt)
        {
            // Show all 6 possible line directions at this length
            var hexDirs = new Vector2I[]
            {
                new(1, 0), new(1, -1), new(0, -1),
                new(-1, 0), new(-1, 1), new(0, 1)
            };

            foreach (var dir in hexDirs)
            {
                for (int step = 1; step <= lt.Length; step++)
                {
                    var coord = center + dir * step;
                    var tileData = grid.GetTile(coord);
                    if (tileData == null)
                        continue; // off-grid

                    bool isTip = step == lt.Length;
                    _targetHighlightTiles.Add(coord);
                    grid.GetTileView(coord)?.SetRangeHighlight(
                        interior: !isTip,
                        border: isTip
                    );
                }
            }

            // Highlight valid targets on top
            foreach (var unit in State.UnitsInPlay)
            {
                if (unit == null || !unit.Stats.IsAlive || unit.CurrentTile == null)
                    continue;
                if (lt.EnemiesOnly && unit.TeamId == 0)
                    continue;
                coords.Add(unit.CurrentTile.Axial);
            }
        }
        else if (targeter is SelectRingTarget rt)
        {
            // Show the ring at the exact radius — this is what the spell targets
            foreach (var kvp in grid.Tiles)
            {
                int dist = grid.Distance(center, kvp.Key);

                if (dist == rt.Radius)
                {
                    _targetHighlightTiles.Add(kvp.Key);
                    grid.GetTileView(kvp.Key)?.SetRangeHighlight(
                        interior: false,
                        border: true  // all ring tiles are the border
                    );
                }
                else if (dist < rt.Radius)
                {
                    // Subtle interior tint so the player can see the ring's context
                    _targetHighlightTiles.Add(kvp.Key);
                    grid.GetTileView(kvp.Key)?.SetRangeHighlight(
                        interior: true,
                        border: false
                    );
                }
            }

            // Highlight any valid targets on the ring
            foreach (var unit in State.UnitsInPlay)
            {
                if (unit == null || !unit.Stats.IsAlive || unit.CurrentTile == null)
                    continue;
                if (rt.IncludeTiles)
                    continue; // tile-only targeting, no unit highlights
                int dist = grid.Distance(center, unit.CurrentTile.Axial);
                if (dist == rt.Radius)
                    coords.Add(unit.CurrentTile.Axial);
            }
        }

        return coords;
    }
}