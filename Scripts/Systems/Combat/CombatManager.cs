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
    private struct PendingEnemySpawn
    {
        public EnemyArchetype Archetype;  // ← drives stats, AI, and intel label
        public int MaxHealth;
        public int Health;
        public int BaseSpeed;
        public int Armor;
        public int AttackRange;           // ← needed by ranged AI
        public int AttackDamage;          // ← archetype-specific damage
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

    // ── Selection state ─────────────────────────────────────────────────────
    private Unit selectedUnit = null;
    private Unit inspectedEnemyUnit = null;
    private HashSet<Vector2I> currentMoveTiles = new();
    private Unit _hoveredUnit = null;
    private SchoolAttunementUI schoolAttunementUI;

    // ── Tile highlighting state ─────────────────────────────────────────────
    private HashSet<Vector2I> _targetHighlightTiles = new();
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

            // Unit bar buttons select the corresponding unit
            combatUI.UnitButtonPressed += OnUnitBarButtonPressed;
            // Enemy roster buttons inspect the corresponding enemy
            combatUI.EnemyButtonPressed += OnEnemyRosterButtonPressed;
        }

        // Movement zone renderer — child of HexGridManager
        _zoneRenderer = new MovementZoneRenderer();
        _zoneRenderer.Name = "MovementZoneRenderer";

        CallDeferred(nameof(InitZoneRenderer));

        // Create the attunement UI as a child of CombatUI
        schoolAttunementUI = new SchoolAttunementUI();
        schoolAttunementUI.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
        schoolAttunementUI.Position = new Vector2(0, 162);
        if (combatUI != null)
            combatUI.AddChild(schoolAttunementUI);

        if (playerUnit != null)
            State.Mana[Me] = playerUnit.Stats.Mana;

        State.Bus.OnEvent += OnGameEvent;

        if (!EnableDeploymentPhase)
            State.OpenPriorityWindow();

        CallDeferred(nameof(RefreshPhaseUI));
        CallDeferred(nameof(RefreshSelectedUnitUI));

        RefreshPhaseUI();
        RefreshSelectedUnitUI();

        if (EncounterRouter.Instance != null)
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

        if (currentPhase == CombatPhase.EnemyTurn) return;

        // ── Guard: viewport and world may not be ready on first frames ──
        var viewport = GetViewport();
        if (viewport == null) return;

        var camera = viewport.GetCamera3D();
        if (camera == null) return;

        var world = GetWorld3D();
        if (world?.DirectSpaceState == null) return;
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
                if (current is Unit u) { hitUnit = u; break; }
                current = current.GetParent();
            }
        }

        if (hitUnit != _hoveredUnit)
        {
            _hoveredUnit?.SetHovered(false);
            _hoveredUnit = hitUnit;
            _hoveredUnit?.SetHovered(true);

            // ── Show/hide threat zone for hovered enemy ──
            if (hitUnit != null && !hitUnit.IsPlayerControlled
                && hitUnit.Stats.IsAlive)
            {
                ShowEnemyThreatZone(hitUnit);
            }
            else if (selectedUnit != null)
            {
                // Restore player move zone when no longer hovering enemy
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
        if (grid == null || _zoneRenderer == null) return;
        _zoneRenderer.HexRadius = grid.HexRadius;
        grid.AddChild(_zoneRenderer);
    }

    private void InitializeUnitDecks()
    {
        var companionCards = BuildCompanionCardList();
        bool injectedCompanionCards = false;

        foreach (var unit in playerUnits)
        {
            if (unit == null) continue;
            if (unit.IsMartial) continue;

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
    }

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
        if (combatUI == null) return;

        int mana = selectedUnit?.Stats.Mana ?? 0;
        if (State.Mana.ContainsKey(Me))
            State.Mana[Me] = mana;

        // Priority: deployment selection → player selection → inspected enemy
        Unit unitToShow = isInDeploymentPhase
            ? selectedDeployUnit
            : (selectedUnit ?? inspectedEnemyUnit);

        // Pass mana=-1 for enemies so the mana row is suppressed when appropriate
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
        if (deck == null) return;
        combatUI?.RefreshDeckCounts(deck.DrawPile, deck.DiscardPile);
    }

    private void OnUnitBarButtonPressed(int index)
    {
        if (index < 0 || index >= playerUnits.Count) return;
        if (currentPhase != CombatPhase.PlayerTurn) return;
        SelectUnit(playerUnits[index]);
    }

    private void OnEnemyRosterButtonPressed(int index)
    {
        if (index < 0 || index >= enemyUnits.Count) return;
        var enemy = enemyUnits[index];
        if (enemy == null || !enemy.Stats.IsAlive) return;

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
        GD.Print($"Inspecting enemy: {enemy.Name}");
    }

    private void RefreshMoveHighlight()
    {
        ClearMoveTiles();
        if (selectedUnit == null || !selectedUnit.CanMove()) return;

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

        if (e.IsActionPressed("ui_select")) { Pass(); } // space by default
        if (e.IsActionPressed("ui_accept")) { ResolveTop(); } // enter
        if (e is InputEventKey k && k.Pressed && !k.Echo)
        {
            // Existing bindings
            if (k.Keycode == Key.R) ResolveTop();

            // ── Unit selection ────────────────────────────────────────────
            if (currentPhase == CombatPhase.PlayerTurn)
            {
                // Enemy inspection — check shift first
                if (k.ShiftPressed)
                {
                    if (k.Keycode == Key.Key1) { TryInspectEnemyByIndex(0); return; }
                    if (k.Keycode == Key.Key2) { TryInspectEnemyByIndex(1); return; }
                    if (k.Keycode == Key.Key3) { TryInspectEnemyByIndex(2); return; }
                    if (k.Keycode == Key.Key4) { TryInspectEnemyByIndex(3); return; }
                }

                // Unit selection
                if (k.Keycode == Key.Key1) TrySelectUnitByIndex(0);
                if (k.Keycode == Key.Key2) TrySelectUnitByIndex(1);
                if (k.Keycode == Key.Key3) TrySelectUnitByIndex(2);
                if (k.Keycode == Key.Key4) TrySelectUnitByIndex(3);

                // Tab cycles units
                if (k.Keycode == Key.Tab)
                {
                    if (k.ShiftPressed) CycleSelectedUnit(-1);
                    else CycleSelectedUnit(1);
                }
            }
        }
    }

    private void TryHandleMainPhaseClick()
    {
        GD.Print($"TryHandleMainPhaseClick phase={currentPhase}");

        if (currentPhase != CombatPhase.PlayerTurn)
        {
            // During enemy turn allow clicking to inspect enemies
            if (currentPhase == CombatPhase.EnemyTurn)
                TryInspectClick();
            return;
        }

        var camera = GetViewport().GetCamera3D();
        if (camera == null) { GD.PrintErr("No active camera."); return; }

        Vector2 mousePos = GetViewport().GetMousePosition();
        Vector3 from = camera.ProjectRayOrigin(mousePos);
        Vector3 to = from + camera.ProjectRayNormal(mousePos) * 1000f;

        var spaceState = GetWorld3D().DirectSpaceState;
        var result = spaceState.IntersectRay(PhysicsRayQueryParameters3D.Create(from, to));
        if (result.Count == 0) return;
        if (!result.TryGetValue("collider", out var colliderVar)) return;

        var collider = colliderVar.AsGodotObject() as Node;
        if (collider == null) return;

        Node current = collider;
        while (current != null)
        {
            if (current is Unit unit)
            {
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
        if (isInDeploymentPhase) { TryHandleDeploymentClick(); return; }
        if (currentPhase != CombatPhase.PlayerTurn) return;

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
        if (isInDeploymentPhase) return;
        if (currentPhase != CombatPhase.PlayerTurn) return;

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
        if (camera == null) return null;

        Vector2 mousePos = GetViewport().GetMousePosition();
        Vector3 from = camera.ProjectRayOrigin(mousePos);
        Vector3 to = from + camera.ProjectRayNormal(mousePos) * 1000f;

        var result = GetWorld3D().DirectSpaceState
            .IntersectRay(PhysicsRayQueryParameters3D.Create(from, to));
        if (result.Count == 0) return null;
        if (!result.TryGetValue("collider", out var cv)) return null;

        Node current = cv.AsGodotObject() as Node;
        while (current != null)
        {
            if (current is Unit u) return u;
            current = current.GetParent();
        }
        return null;
    }

    private HexTile GetTileViewUnderMouse()
    {
        var camera = GetViewport().GetCamera3D();
        if (camera == null) return null;

        Vector2 mousePos = GetViewport().GetMousePosition();
        Vector3 from = camera.ProjectRayOrigin(mousePos);
        Vector3 to = from + camera.ProjectRayNormal(mousePos) * 1000f;

        var result = GetWorld3D().DirectSpaceState
            .IntersectRay(PhysicsRayQueryParameters3D.Create(from, to));
        if (result.Count == 0) return null;
        if (!result.TryGetValue("collider", out var cv)) return null;

        Node current = cv.AsGodotObject() as Node;
        while (current != null)
        {
            if (current is HexTile tile) return tile;
            current = current.GetParent();
        }
        return null;
    }

    private void InspectEnemy(Unit enemy)
    {
        if (enemy == null || !enemy.Stats.IsAlive) return;

        // Clear previous inspected enemy ring
        if (inspectedEnemyUnit != null)
            inspectedEnemyUnit.SetSelected(false);

        if (selectedUnit != null)
            selectedUnit.SetSelected(false);
        selectedUnit = null;
        inspectedEnemyUnit = enemy;
        inspectedEnemyUnit.SetSelected(true);   // ← ADD THIS
        ClearMoveTiles();

        RefreshSelectedUnitUI();
        RefreshPlayerUnitBar();
        GD.Print($"Inspecting enemy: {enemy.Name}  HP={enemy.Stats.Health}/{enemy.Stats.MaxHealth}");
    }

    private void ShowEnemyThreatZone(Unit enemy)
    {
        if (_zoneRenderer == null || enemy?.CurrentTile == null) return;

        // Temporarily give enemy enough AP to show their threat range
        int savedAP = enemy.CurrentActionPoints;
        int savedMax = enemy.MaxActionPoints;
        enemy.MaxActionPoints = enemy.Stats.BaseSpeed;
        enemy.CurrentActionPoints = enemy.Stats.BaseSpeed;

        var reachable = grid.GetReachableTiles(enemy);

        enemy.CurrentActionPoints = savedAP;
        enemy.MaxActionPoints = savedMax;

        _zoneRenderer.ShowEnemyZone(reachable, grid);
    }

    /// <summary>Returns the axial coord of the tile currently under the mouse, or null.</summary>
    private Vector2I? GetHoveredTile()
    {
        var camera = GetViewport().GetCamera3D();
        if (camera == null) return null;

        Vector2 mousePos = GetViewport().GetMousePosition();
        Vector3 from = camera.ProjectRayOrigin(mousePos);
        Vector3 to = from + camera.ProjectRayNormal(mousePos) * 1000f;

        var result = GetWorld3D().DirectSpaceState
            .IntersectRay(PhysicsRayQueryParameters3D.Create(from, to));
        if (result.Count == 0) return null;
        if (!result.TryGetValue("collider", out var cv)) return null;

        Node current = cv.AsGodotObject() as Node;
        while (current != null)
        {
            if (current is HexTile tile) return tile.Axial;
            current = current.GetParent();
        }
        return null;
    }

    private void TryInspectClick()
    {
        var camera = GetViewport().GetCamera3D();
        if (camera == null) return;

        Vector2 mousePos = GetViewport().GetMousePosition();
        Vector3 from = camera.ProjectRayOrigin(mousePos);
        Vector3 to = from + camera.ProjectRayNormal(mousePos) * 1000f;

        var result = GetWorld3D().DirectSpaceState
            .IntersectRay(PhysicsRayQueryParameters3D.Create(from, to));
        if (result.Count == 0) return;
        if (!result.TryGetValue("collider", out var cv)) return;

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
        if (unit == null || !unit.IsPlayerControlled) return;

        if (selectedUnit != null) selectedUnit.SetSelected(false);
        if (inspectedEnemyUnit != null)
        {
            inspectedEnemyUnit.SetSelected(false);
            inspectedEnemyUnit = null;
        }

        selectedUnit = unit;
        selectedUnit.SetSelected(true);
        ClearTargetHighlight();

        ClearMoveTiles();
        ShowMoveTilesWithCost(unit);

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
        if (index < 0 || index >= alive.Count) return;
        SelectUnit(alive[index]);
    }

    private void TryInspectEnemyByIndex(int index)
    {
        var alive = enemyUnits
            .Where(u => u != null && u.Stats.IsAlive)
            .ToList();

        if (index < 0 || index >= alive.Count) return;
        InspectEnemy(alive[index]);
    }

    private void CycleSelectedUnit(int direction)
    {
        var alive = playerUnits.Where(u => u != null && u.Stats.IsAlive).ToList();
        if (alive.Count == 0) return;

        int currentIndex = selectedUnit != null ? alive.IndexOf(selectedUnit) : -1;
        int nextIndex = (currentIndex + direction + alive.Count) % alive.Count;
        SelectUnit(alive[nextIndex]);
    }

    private void ShowMoveTilesWithCost(Unit unit)
    {
        if (_zoneRenderer == null) return;
        if (!unit.CanMove()) return;

        var costMap = grid.GetReachableTilesWithCost(unit);

        currentMoveTiles.Clear();
        foreach (var k in costMap.Keys)
            currentMoveTiles.Add(k);

        _zoneRenderer.ShowPlayerZone(costMap, grid);
    }

    private void TryMoveSelectedUnit(HexTile tileView)
    {
        if (selectedUnit == null || tileView == null) return;
        if (!currentMoveTiles.Contains(tileView.Axial))
        {
            GD.Print("Tile not in range.");
            return;
        }

        var tileData = grid.GetTile(tileView.Axial);
        if (tileData == null) return;

        if (!selectedUnit.CanMove())
        {
            combatUI?.AppendActionLog($"{selectedUnit.Name} is immobilized!");
            return;
        }

        var debugTile = grid.GetTile(tileView.Axial);
        GD.Print($"[MoveDebug] Target {tileView.Axial} — Walkable={debugTile?.IsWalkable} Blocked={debugTile?.IsBlocked} Occupied={debugTile?.IsOccupied} Obstacle={debugTile?.ObstacleKind}");

        if (selectedUnit.TryMoveTo(grid, tileData))
        {
            GD.Print($"{selectedUnit.Name} moved to {tileData.Axial}");
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
    }

    private void TryMartialAttack(Unit attacker, Unit target)
    {
        if (attacker == null || target == null) return;
        if (!attacker.IsMartial) return;
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
        if (unit == null || !unit.IsMartial) return false;
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
        if (loadout != null) damage += loadout.BonusAttackDamage;

        // Stance passive damage bonus
        if (stance != null) damage += stance.AttackDamageBonus;

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
            if (target.Stats.IsAlive) target.Stats.Armor = savedArmor;
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
                var nTile = grid.GetTile(neighbor);
                if (nTile?.Occupant == null) continue;
                if (nTile.Occupant == target) continue;        // already hit
                if (nTile.Occupant.TeamId == attacker.TeamId) continue; // skip allies
                nTile.Occupant.ApplyDamage(damage);
                combatUI?.AppendActionLog($"[Reckless] {nTile.Occupant.Name} takes {damage} damage.");
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
                    if (td == null || !td.CanEnter(target)) continue;
                    int d = grid.Distance(casterPos, neighbor);
                    if (d > bestDist) { bestDist = d; bestTile = td; }
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
            attacker.Stats.MovePoints += stance.SpecialTagValue;
            combatUI?.AppendActionLog($"[Skirmish] {attacker.Name} gains " +
                                      $"{stance.SpecialTagValue} free move points.");
            // Recalculate reachable tiles
            ClearMoveTiles();
            var reachable = grid.GetReachableTiles(attacker);
            foreach (var coord in reachable) currentMoveTiles.Add(coord);
            ShowMoveTilesWithCost(selectedUnit);
        }

        // ── Guardian: aura applied at stance start, not on attack ─────────
        // (handled in ApplyMartialStancePassives)

        // ── Mark attack tracking ──────────────────────────────────────────
        attacker.HasAttackedThisCombat = true;

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
        currentPhase = CombatPhase.PlayerTurn;
        enemyPhaseRunning = false;

        foreach (var unit in playerUnits)
        {
            if (unit == null || !IsInstanceValid(unit) || !unit.Stats.IsAlive) continue;
            unit.StartTurn();

            // Apply martial stance passives
            if (unit.IsMartial && unit.ActiveStance != null)
                ApplyMartialStancePassives(unit);

            unit.TickStatuses();
            unit.Attunement?.Decay();
            State.Memorials.Tick();

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
        combatUI?.ClearActionLog();
        schoolAttunementUI?.Refresh();
        RefreshAllUI();
    }

    private void EndPlayerTurn()
    {
        _zoneRenderer?.Clear();

        foreach (var unit in playerUnits)
            DiscardOverflowCards(unit);

        foreach (var unit in playerUnits)
        {
            unit.Stats.Shield = 0;
            unit.RefreshHealthBar();
        }

        if (currentPhase != CombatPhase.PlayerTurn) return;
        selectedUnit = null;
        inspectedEnemyUnit = null;
        ClearMoveTiles();
        GD.Print("=== Player Turn End ===");
        RefreshPhaseUI();
        StartEnemyTurn();
    }

    private void OnEndTurnPressed()
    {
        if (currentPhase != CombatPhase.PlayerTurn) return;
        EndPlayerTurn();
    }

    private void DiscardOverflowCards(Unit unit)
    {
        if (unit?.DeckData == null) return;

        int overflow = unit.DeckData.Hand.Count - unit.DeckData.MaxHandSize;
        if (overflow <= 0) return;

        for (int i = 0; i < overflow; i++)
        {
            // Always discard index 0 — the oldest card
            var dropped = unit.DeckData.Hand[0];
            unit.DeckData.Hand.RemoveAt(0);
            unit.DeckData.DiscardPile.Add(dropped);
            GD.Print($"[Hand] {dropped.TopHalf?.Name ?? dropped.CardName} discarded — hand over limit.");
        }

        // Refresh UI and clear all discard flags
        deckManager?.DrawCards(0);
    }

    private void ApplyMartialStancePassives(Unit unit)
    {
        var stance = unit.ActiveStance;
        if (stance == null) return;

        // Speed bonus
        if (stance.PassiveSpeedBonus != 0)
            unit.Stats.MovePoints += stance.PassiveSpeedBonus;

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
                if (td?.Occupant == null) continue;
                if (td.Occupant.TeamId != unit.TeamId) continue;
                if (td.Occupant == unit) continue;
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
        if (enemyPhaseRunning) return;

        currentPhase = CombatPhase.EnemyTurn;
        enemyPhaseRunning = true;

        _zoneRenderer?.Clear();

        foreach (var unit in enemyUnits)
        {
            if (unit != null && unit.Stats.IsAlive)
            {
                unit.StartTurn();
                unit.TickStatuses();
            }
        }

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

        if (CheckCombatEnd()) return;

        roundNumber++;
        StartPlayerTurn();
    }

    private async System.Threading.Tasks.Task RunEnemyTurn()
    {
        // Snapshot so deaths during iteration don't corrupt the loop
        var snapshot = enemyUnits.ToList();
        foreach (var enemy in snapshot)
        {
            if (enemy == null || !IsInstanceValid(enemy) || !enemy.Stats.IsAlive)
                continue;

            if (!enemy.CanAct())
            {
                GD.Print($"{enemy.Name} is frozen — skipping turn.");
                combatUI?.AppendActionLog($"{enemy.Name} is frozen!");
                continue;
            }

            var target = FindNearestPlayerUnit(enemy);
            if (target == null) continue;

            await ActEnemyUnit(enemy, target);

            if (CheckCombatEnd()) return;
        }

        GD.Print("=== Enemy Turn End ===");
        enemyPhaseRunning = false;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Enemy AI — archetype-driven behaviors
    // ═══════════════════════════════════════════════════════════════════════

    private async System.Threading.Tasks.Task ActEnemyUnit(Unit enemy, Unit target)
    {
        if (!IsValidActor(enemy) || !IsValidActor(target)) return;

        switch (enemy.EnemyArchetype)
        {
            case EnemyArchetype.Soldier:
                await ActSoldier(enemy, target);
                break;
            case EnemyArchetype.Brute:
                await ActBrute(enemy);
                break;
            case EnemyArchetype.Defender:
                await ActDefender(enemy, target);
                break;
            case EnemyArchetype.Ranger:
                await ActRanger(enemy, target);
                break;
            case EnemyArchetype.Wizard:
                await ActWizard(enemy, target);
                break;
            default:
                await ActSoldier(enemy, target);
                break;
        }
    }

    // ── Soldier: move toward nearest, attack adjacent ────────────────────

    private async System.Threading.Tasks.Task ActSoldier(Unit enemy, Unit target)
    {
        if (!IsValidActor(enemy) || !IsValidActor(target)) return;

        int dist = grid.Distance(enemy.CurrentTile, target.CurrentTile);

        if (dist <= 1)
        {
            await PerformAttack(enemy, target);
            return;
        }

        await MoveToward(enemy, target);

        if (!IsValidActor(enemy) || !IsValidActor(target)) return;
        if (grid.Distance(enemy.CurrentTile, target.CurrentTile) <= 1)
            await PerformAttack(enemy, target);
    }

    // ── Brute: target the highest-HP player unit, then close and attack ──

    private async System.Threading.Tasks.Task ActBrute(Unit enemy)
    {
        // Re-pick target: highest current HP among living player units
        Unit target = null;
        int bestHp = -1;
        foreach (var u in playerUnits)
        {
            if (u == null || !IsInstanceValid(u) || !u.Stats.IsAlive) continue;
            if (u.Stats.Health > bestHp) { bestHp = u.Stats.Health; target = u; }
        }

        if (target == null || !IsValidActor(target)) return;
        if (!IsValidActor(enemy)) return;

        int dist = grid.Distance(enemy.CurrentTile, target.CurrentTile);

        if (dist <= 1)
        {
            await PerformAttack(enemy, target);
            return;
        }

        await MoveToward(enemy, target);

        if (!IsValidActor(enemy) || !IsValidActor(target)) return;
        if (grid.Distance(enemy.CurrentTile, target.CurrentTile) <= 1)
            await PerformAttack(enemy, target);
    }

    // ── Defender: holds position near the most number of allies to give them buffs ──

    private async System.Threading.Tasks.Task ActDefender(Unit enemy, Unit target)
    {
        if (!IsValidActor(enemy) || !IsValidActor(target)) return;

        int dist = grid.Distance(enemy.CurrentTile, target.CurrentTile);

        // Attack if adjacent
        if (dist <= 1)
        {
            await PerformAttack(enemy, target);
            return;
        }

        // ── Find nearest living ally ──────────────────────────────────────
        Unit nearestAlly = null;
        int nearestAllyDist = int.MaxValue;
        foreach (var u in enemyUnits)
        {
            if (u == null || u == enemy || !u.Stats.IsAlive || u.CurrentTile == null) continue;
            int d = grid.Distance(enemy.CurrentTile, u.CurrentTile);
            if (d < nearestAllyDist) { nearestAllyDist = d; nearestAlly = u; }
        }

        var moveOptions = grid.GetReachableTiles(enemy);
        Vector2I bestMove = enemy.CurrentTile.Axial;
        int bestAllyCount = CountAdjacentAllies(enemy, enemy.CurrentTile.Axial);
        int bestDistToEnemy = dist;

        foreach (var coord in moveOptions)
        {
            int allyCount = CountAdjacentAllies(enemy, coord);
            int distToEnemy = grid.Distance(coord, target.CurrentTile.Axial);

            if (allyCount > bestAllyCount ||
            (allyCount == bestAllyCount && distToEnemy < bestDistToEnemy))
            {
                bestAllyCount = allyCount;
                bestDistToEnemy = distToEnemy;
                bestMove = coord;
            }
        }

        // ── If no adjacent allies anywhere reachable, chase nearest ally ──
        if (bestAllyCount == 0 && nearestAlly != null)
        {
            var nextStep = grid.GetFirstStepToward(enemy, nearestAlly.CurrentTile.Axial);
            if (nextStep != null && enemy.TryMoveTo(grid, nextStep))
            {
                string msg = $"{enemy.Name} moves to rejoin allies.";
                GD.Print(msg);
                combatUI?.AppendActionLog(msg);
                await ToSignal(GetTree().CreateTimer(0.35f), "timeout");
            }

            // Still attack if adjacent to player after repositioning
            if (!IsValidActor(enemy) || !IsValidActor(target)) return;
            if (grid.Distance(enemy.CurrentTile, target.CurrentTile) <= 1)
                await PerformAttack(enemy, target);
            return;
        }

        // ── Move to best ally-adjacent position ───────────────────────────
        if (bestMove != enemy.CurrentTile.Axial)
        {
            var tile = grid.GetTile(bestMove);
            if (tile != null && enemy.TryMoveTo(grid, tile))
            {
                string msg = $"{enemy.Name} moves to protect allies.";
                GD.Print(msg);
                combatUI?.AppendActionLog(msg);
                await ToSignal(GetTree().CreateTimer(0.35f), "timeout");
            }
        }
        else
        {
            string holdMsg = $"{enemy.Name} holds the line.";
            GD.Print(holdMsg);
            combatUI?.AppendActionLog(holdMsg);
        }

        if (!IsValidActor(enemy) || !IsValidActor(target)) return;
        if (grid.Distance(enemy.CurrentTile, target.CurrentTile) <= 1)
            await PerformAttack(enemy, target);
    }

    private int CountAdjacentAllies(Unit unit, Vector2I fromCoord)
    {
        int count = 0;
        foreach (var neighbor in grid.GetNeighbors(fromCoord))
        {
            var tile = grid.GetTile(neighbor);
            if (tile?.Occupant == null) continue;
            if (tile.Occupant == unit) continue;
            if (tile.Occupant.TeamId == unit.TeamId && tile.Occupant.Stats.IsAlive)
                count++;
        }
        return count;
    }

    // ── Ranger: maintain preferred distance, shoot without closing ───────

    private async System.Threading.Tasks.Task ActRanger(Unit enemy, Unit target)
    {
        if (!IsValidActor(enemy) || !IsValidActor(target)) return;

        int dist = grid.Distance(enemy.CurrentTile, target.CurrentTile);
        int preferred = enemy.AttackRange; // preferred engagement distance = attack range
        int minDist = preferred - 1;     // won't close beyond this

        // If target is within attack range, shoot — no need to move
        if (dist <= enemy.AttackRange && dist >= minDist)
        {
            await PerformRangedAttack(enemy, target);
            return;
        }

        // If target is too close, back away
        if (dist < minDist)
        {
            await MoveAwayFrom(enemy, target, minDist);
            if (!IsValidActor(enemy) || !IsValidActor(target)) return;
            // Shoot if we ended up in range
            if (grid.Distance(enemy.CurrentTile, target.CurrentTile) <= enemy.AttackRange)
                await PerformRangedAttack(enemy, target);
            return;
        }

        // Target is out of range — move closer to preferred distance
        await MoveToDistance(enemy, target, preferred);
        if (!IsValidActor(enemy) || !IsValidActor(target)) return;
        if (grid.Distance(enemy.CurrentTile, target.CurrentTile) <= enemy.AttackRange)
            await PerformRangedAttack(enemy, target);
    }

    // ── Wizard: charge every other turn, then deal high damage + debuff ──

    private async System.Threading.Tasks.Task ActWizard(Unit enemy, Unit target)
    {
        if (!IsValidActor(enemy) || !IsValidActor(target)) return;

        int dist = grid.Distance(enemy.CurrentTile, target.CurrentTile);
        int preferredRange = enemy.AttackRange;
        int maxEngageRange = preferredRange + 2; // will close to within this distance

        // Too close — back away to preferred range
        if (dist < preferredRange)
        {
            await MoveAwayFrom(enemy, target, preferredRange);
        }
        // Too far to ever shoot — advance until within max engage range
        else if (dist > maxEngageRange)
        {
            await MoveToDistance(enemy, target, preferredRange);
        }
        // In preferred zone — hold position
        // (no movement needed)

        if (!IsValidActor(enemy) || !IsValidActor(target)) return;

        // Charge mechanic
        bool wasCharging = enemy.HasStatus("wizard_charging");

        if (wasCharging)
        {
            // Only fire if actually in range now
            int currentDist = grid.Distance(enemy.CurrentTile, target.CurrentTile);
            if (currentDist <= enemy.AttackRange)
            {
                string chargeMsg = $"{enemy.Name} releases a charged blast!";
                GD.Print(chargeMsg);
                combatUI?.AppendActionLog(chargeMsg);
                await PerformRangedAttack(enemy, target);

                if (IsValidActor(target))
                {
                    target.ApplyStatus("slowed", 1);
                    string debuffMsg = $"{target.Name} is slowed by arcane energy!";
                    GD.Print(debuffMsg);
                    combatUI?.AppendActionLog(debuffMsg);
                }
            }
            else
            {
                // Still out of range — skip the shot, reset charge
                string missMsg = $"{enemy.Name} — target out of range, charge wasted.";
                GD.Print(missMsg);
                combatUI?.AppendActionLog(missMsg);
            }
        }
        else
        {
            enemy.ApplyStatus("wizard_charging", 2);
            string chargeMsg = $"{enemy.Name} begins channelling...";
            GD.Print(chargeMsg);
            combatUI?.AppendActionLog(chargeMsg);
        }
    }

    // ── Shared movement helpers ───────────────────────────────────────────

    /// Move one step toward target (existing behaviour, extracted).
    private async System.Threading.Tasks.Task MoveToward(Unit enemy, Unit target)
    {
        if (!IsValidActor(enemy) || !IsValidActor(target)) return;

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
                if (tile == null) continue;
                int d = grid.Distance(tile, target.CurrentTile);
                if (d < bestDist) { bestDist = d; bestMove = coord; }
            }

            if (bestMove == enemy.CurrentTile.Axial) return; // truly stuck

            nextStep = grid.GetTile(bestMove);
        }

        if (nextStep == null) return;

        // Only move there if it's within this turn's movement range
        int pathCost = grid.GetMoveCostTo(enemy, nextStep);
        if (pathCost < 0 || pathCost > enemy.MoveRange)
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
        if (nextStep == null) return;

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
        if (grid.Distance(enemy.CurrentTile, target.CurrentTile) >= minDist) return;

        var nextStep = grid.GetFirstStepAwayFrom(enemy, target.CurrentTile.Axial);
        if (nextStep == null) return;

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
        int dmg = enemy.AttackDamage > 0 ? enemy.AttackDamage : 5;

        string msg = $"{enemy.Name} attacks {target.Name} for {dmg} damage.";
        GD.Print(msg);
        combatUI?.AppendActionLog(msg);

        target.ApplyDamage(dmg);

        string hpMsg = $"  ({target.Stats.Health}/{target.Stats.MaxHealth} HP remaining)";
        GD.Print(hpMsg);

        RefreshSelectedUnitUI();
        RefreshEnemyRoster();
        RefreshPlayerUnitBar();
        RefreshDeckCounts();
        await ToSignal(GetTree().CreateTimer(0.35f), "timeout");
    }

    private async System.Threading.Tasks.Task PerformRangedAttack(Unit enemy, Unit target, int bonusDamage = 0)
    {
        if (!IsValidActor(enemy) || !IsValidActor(target)) return;

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

        int dmg = (enemy.AttackDamage > 0 ? enemy.AttackDamage : 4) + bonusDamage;

        string msg = $"{enemy.Name} shoots {target.Name} for {dmg} damage.";
        GD.Print(msg);
        combatUI?.AppendActionLog(msg);

        target.ApplyDamage(dmg);

        string hpMsg = $"  ({target.Stats.Health}/{target.Stats.MaxHealth} HP remaining)";
        GD.Print(hpMsg);

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

            if (unit.CurrentTile == null) continue;
            if (!unit.CurrentTile.IsHazardous) continue;

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

    private void HandleUnitDeath(Unit unit)
    {
        if (unit == null) return;

        string deathMsg = $"{unit.Name} has died.";
        GD.Print(deathMsg);
        combatUI?.AppendActionLog(deathMsg);

        // ── Memorial creation ─────────────────────────────────────────────
        if (State?.Memorials != null && unit.CurrentTile != null)
        {
            // Find any player-team necromancer to determine owner
            int necroTeam = -1;
            foreach (var u in playerUnits)
            {
                if (u != null && u.School == CardSchool.Necromancer)
                {
                    necroTeam = u.TeamId;
                    break;
                }
            }

            if (necroTeam >= 0)
                State.Memorials.CreateMemorial(unit.CurrentTile, unit, necroTeam);
        }
        // ─────────────────────────────────────────────────────────────────

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
        RefreshPlayerUnitBar();
        RefreshEnemyRoster();
    }

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

    // ═══════════════════════════════════════════════════════════════════════
    // Combat end check
    // ═══════════════════════════════════════════════════════════════════════

    private bool CheckCombatEnd()
    {
        bool allEnemiesDead = true;
        bool allPlayersDead = true;

        foreach (var u in enemyUnits)
            if (u != null && u.Stats.IsAlive) { allEnemiesDead = false; break; }

        foreach (var u in playerUnits)
            if (u != null && u.Stats.IsAlive) { allPlayersDead = false; break; }

        if (allEnemiesDead)
        {
            currentPhase = CombatPhase.Victory;
            RefreshPhaseUI();
            GD.Print("=== VICTORY ===");
            EmitSignal(SignalName.CombatCompleted, true);   // ← ADD THIS
            return true;
        }

        if (allPlayersDead)
        {
            currentPhase = CombatPhase.Defeat;
            RefreshPhaseUI();
            GD.Print("=== DEFEAT ===");
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
        currentPhase = CombatPhase.Deployment;
        RefreshAllUI();

        // Deferred so it fires after the deferred RefreshPhaseUI/RefreshSelectedUnitUI
        // calls in _Ready() have settled — otherwise RefreshAllUI overwrites it.
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

        if (AutoStartAfterDeployment) StartPlayerTurn();
    }

    private void OnConfirmDeploymentPressed()
    {
        if (!isInDeploymentPhase) return;
        EndDeploymentPhase();
    }

    private void HandleDeploymentInput(InputEvent e)
    {
        if (e is InputEventKey key && key.Pressed)
        {
            if (key.Keycode == Key.Enter) { EndDeploymentPhase(); return; }
            if (key.Keycode == Key.Backspace) { ResetDeploymentPositions(); return; }
        }

        if (e is InputEventMouseButton mb && mb.Pressed)
        {
            if (mb.ButtonIndex == MouseButton.Left) { TryHandleDeploymentClick(); return; }
            if (mb.ButtonIndex == MouseButton.Right) { ClearDeploymentSelection(); GD.Print("Deployment selection cleared."); }
        }
    }

    private void TryHandleDeploymentClick()
    {
        var camera = GetViewport().GetCamera3D();
        if (camera == null) return;

        Vector2 mousePos = GetViewport().GetMousePosition();
        Vector3 from = camera.ProjectRayOrigin(mousePos);
        Vector3 to = from + camera.ProjectRayNormal(mousePos) * 1000f;

        var result = GetWorld3D().DirectSpaceState
            .IntersectRay(PhysicsRayQueryParameters3D.Create(from, to));
        if (result.Count == 0) return;
        if (!result.TryGetValue("collider", out var cv)) return;

        Node current = cv.AsGodotObject() as Node;
        while (current != null)
        {
            if (current is Unit unit) { TrySelectDeploymentUnit(unit); return; }
            if (current is HexTile tile) { TryPlaceDeploymentUnit(tile); return; }
            current = current.GetParent();
        }
    }

    private void TrySelectDeploymentUnit(Unit unit)
    {
        if (unit == null || !unit.IsPlayerControlled || !playerUnits.Contains(unit)) return;
        if (selectedDeployUnit != null) selectedDeployUnit.SetSelected(false);
        selectedDeployUnit = unit;
        selectedDeployUnit.SetSelected(true);
        GD.Print($"Selected deploy unit: {unit.Name}");
        RefreshSelectedUnitUI();
    }

    private void TryPlaceDeploymentUnit(HexTile tileView)
    {
        if (selectedDeployUnit == null || tileView == null) return;
        if (!playerDeployCoords.Contains(tileView.Axial)) { GD.Print("Tile outside deployment zone."); return; }

        var tileData = grid.GetTile(tileView.Axial);
        if (tileData == null) return;
        if (!tileData.IsWalkable || tileData.IsBlocked || tileData.IsOccupied) { GD.Print("Deployment tile not available."); return; }

        selectedDeployUnit.PlaceOnTile(tileData);
        GD.Print($"{selectedDeployUnit.Name} deployed to {tileData.Axial}");
        selectedDeployUnit.SetSelected(false);
        selectedDeployUnit = null;
    }

    private void ClearDeploymentSelection()
    {
        if (selectedDeployUnit != null) selectedDeployUnit.SetSelected(false);
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
            if (unit?.Attunement == null) continue;
            if (unit.CurrentTile == null) continue;

            // Only Elementalists have elemental attunement seeding
            if (unit.Attunement is not ElementalAttunement ea) continue;

            ElementTag? element = unit.CurrentTile.TerrainType switch
            {
                TileTerrainType.Lava => ElementTag.Fire,
                TileTerrainType.Stone => ElementTag.Earth,
                TileTerrainType.Ice => ElementTag.Ice,
                TileTerrainType.Arcane => ElementTag.Storm,
                _ => null
            };

            if (element == null) continue;

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
            if (zone.Side != HexGridManager.SpawnSide.Player) continue;
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
                if (alreadySeen.Contains(neighbor)) continue;
                alreadySeen.Add(neighbor);

                var td = grid.GetTile(neighbor);
                if (td == null || !td.IsWalkable || td.IsBlocked) continue;
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
            if (aIsNew && !bIsNew) return -1;
            if (!aIsNew && bIsNew) return 1;
            return 0;
        });

        int added = 0;
        foreach (var td in candidates)
        {
            if (added >= 3) break;
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
            if (td != null) types.Add(td.TerrainType);
            if (types.Count >= 2) return true;
        }
        return false;
    }

    private void SpawnTestUnits()
    {
        GD.Print($"[SpawnTest] PlayerUnitScene={PlayerUnitScene != null}, DummyUnitScene={DummyUnitScene != null}");
        grid = GetNodeOrNull<HexGridManager>(GridPath);
        if (grid == null) { GD.PrintErr($"HexGridManager not found at: {GridPath}"); return; }
        if (PlayerUnitScene == null || DummyUnitScene == null) { GD.PrintErr("Assign PlayerUnitScene and DummyUnitScene in the Inspector."); return; }

        if (PlayerUnitScene == null || DummyUnitScene == null)
        {
            GD.PrintErr($"Assign PlayerUnitScene and DummyUnitScene in the Inspector. Player={PlayerUnitScene != null} Dummy={DummyUnitScene != null}");
            return;
        }

        originalDeployCoords.Clear();
        playerUnits.Clear();
        enemyUnits.Clear();

        // ── Spawn wizard (always first) ───────────────────────────────────
        var wizard = SpawnUnitFromSide(HexGridManager.SpawnSide.Player, PlayerUnitScene,
            teamId: 0, isPlayerControlled: true, namePrefix: "Wizard",
            maxHealth: 20, health: 20, baseSpeed: 3, maxMana: 3, mana: 3, armor: 0, shield: 0);
        if (wizard != null)
        {
            wizard.IsMartial = false;
            wizard.CompanionId = "wizard";
            wizard.MoveRange = 3;
            wizard.MaxActionPoints = wizard.Stats.BaseSpeed;      // ← add this
            wizard.CurrentActionPoints = wizard.MaxActionPoints;  // ← add this
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

            if (unit == null) continue;

            unit.CompanionId = companion.Id;
            unit.IsMartial = isMartial;

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
                    if (stance != null) unit.AvailableStances.Add(stance);
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

        State.Memorials = new MemorialManager(grid);
        State.Memorials.OnMemorialCreated += tile => tile.TileView?.SetMemorial(tile.Memorial);
        State.Memorials.OnMemorialChanged += tile => tile.TileView?.SetMemorial(tile.Memorial);
        State.Memorials.OnMemorialRemoved += tile => tile.TileView?.SetMemorial(null);

        State.PlayerUnit = playerUnit;
        // State.EnemyUnit set after enemy spawn
        State.UnitsInPlay.Clear();
        foreach (var u in playerUnits) State.UnitsInPlay.Add(u);

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
            if (unit.IsMartial) continue;
            unit.InitializeAttunement();
        }

        if (EnableDeploymentPhase)
        {
            if (PlayerSession.DebugMode && PlayerSession.SkipDeployment)
            {
                AutoPlaceUnits();
                StartPlayerTurn();
            }
            else
            {
                StartDeploymentPhase();
            }
        }
    }

    private List<EnemyIntelEntry> BuildEnemyIntel()
    {
        var entries = new List<EnemyIntelEntry>();
        foreach (var p in pendingEnemySpawns)
        {
            entries.Add(new EnemyIntelEntry
            {
                ThreatLabel = EnemyArchetypeData.GetThreatLabel(p.Archetype),
                MaxHealth = p.MaxHealth,
                BaseSpeed = p.BaseSpeed,
                Armor = p.Armor,
                BodyColor = p.BodyColor,
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
        var composition = new EnemyArchetype[]
        {
            EnemyArchetype.Soldier,
            EnemyArchetype.Ranger,
            EnemyArchetype.Wizard,
        };

        // Trim or pad to TestEnemyCount so the inspector export still controls battle size
        // TestEnemyCount caps the fallback only.
        // Real encounters from EncounterContext use their own enemy list length.
        int count = Mathf.Min(TestEnemyCount, composition.Length);

        for (int i = 0; i < count; i++)
        {
            var archetype = composition[i];
            int hp = EnemyArchetypeData.GetMaxHealth(archetype);
            pendingEnemySpawns.Add(new PendingEnemySpawn
            {
                Archetype = archetype,
                MaxHealth = hp,
                Health = hp,
                BaseSpeed = EnemyArchetypeData.GetBaseSpeed(archetype),
                Armor = EnemyArchetypeData.GetArmor(archetype),
                AttackRange = EnemyArchetypeData.GetAttackRange(archetype),
                AttackDamage = EnemyArchetypeData.GetAttackDamage(archetype),
                BodyColor = EnemyArchetypeData.GetBodyColor(archetype),
                NamePrefix = EnemyArchetypeData.GetThreatLabel(archetype),
            });
        }
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
            var archetype = slot.Archetype;
            float mult = slot.DifficultyMult;

            int hp = Mathf.RoundToInt(EnemyArchetypeData.GetMaxHealth(archetype) * mult);

            pendingEnemySpawns.Add(new PendingEnemySpawn
            {
                Archetype = archetype,
                MaxHealth = hp,
                Health = hp,
                BaseSpeed = EnemyArchetypeData.GetBaseSpeed(archetype),
                Armor = EnemyArchetypeData.GetArmor(archetype),
                AttackRange = EnemyArchetypeData.GetAttackRange(archetype),
                AttackDamage = EnemyArchetypeData.GetAttackDamage(archetype),
                BodyColor = EnemyArchetypeData.GetBodyColor(archetype),
                NamePrefix = EnemyArchetypeData.GetThreatLabel(archetype),
            });
        }

        GD.Print($"[Encounter] Loaded '{def.DisplayName}' — " +
                 $"{pendingEnemySpawns.Count} enemies from {def.RegionId}/{def.Tier}");
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
            if (zone.Side != HexGridManager.SpawnSide.Enemy) continue;
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
            unit.EnemyArchetype = p.Archetype;      // ← store so AI can read it
            unit.AttackRange = p.AttackRange;    // ← store attack range
            unit.AttackDamage = p.AttackDamage;   // ← store attack damage
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
        if (playerUnits.Count == 0) return Vector2I.Zero;
        int q = 0, r = 0;
        int count = 0;
        foreach (var u in playerUnits)
        {
            if (u?.CurrentTile == null) continue;
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
        if (slot == null) { GD.PrintErr($"No spawn slot for side: {side}"); return null; }

        var tile = grid.GetTileAtSpawnSlot(slot);
        if (tile == null) { GD.PrintErr($"Spawn slot had no valid tile for side: {side}"); return null; }

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

    private Unit FindNearestPlayerUnit(Unit enemy)
    {
        if (enemy == null || !IsInstanceValid(enemy) || enemy.CurrentTile == null)
            return null;

        Unit best = null;
        int bestDist = int.MaxValue;
        foreach (var player in playerUnits)
        {
            if (player == null || !IsInstanceValid(player)) continue;
            if (!player.Stats.IsAlive || player.CurrentTile == null) continue;

            int dist = grid.Distance(enemy.CurrentTile, player.CurrentTile);
            if (dist < bestDist) { bestDist = dist; best = player; }
        }
        return best;
    }

    private void RegisterSummonHandler()
    {
        State.OnSummonRequested = (unitKind, tile, teamId) =>
        {
            // Look up what to spawn based on unitKind
            PackedScene scene = null;
            int hp = 10;
            int speed = 0;
            int armor = 0;
            bool isPlayerControlled = (teamId == 0);

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
                    // TODO Phase 2: Colossus should absorb imbued tiles as it moves,
                    // gaining +2 DMG per Fire, +2 Armor per Stone, +1 SPD per Storm.
                    // Requires OnTileLeft callback and a ColossusBehavior component.
                    break;

                case "colossus_empowered": // channel version
                    scene = DummyUnitScene;
                    hp = 30;
                    speed = 1;
                    armor = 8; // pre-charged with more armor
                    break;

                case "shield_wall":
                    scene = DummyUnitScene;
                    hp = 20;
                    speed = 0;
                    armor = 8;
                    break;

                case "shield_wall_heavy": // channel version
                    scene = DummyUnitScene;
                    hp = 20;
                    speed = 0;
                    armor = 12; // pre-charged with more armor
                    break;

                default:
                    GD.PrintErr($"[Summon] Unknown unit kind: {unitKind}");
                    return null;
            }

            if (scene == null) return null;

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

            // Name it
            string suffix = unitKind.Replace("_", " ");
            suffix = char.ToUpper(suffix[0]) + suffix.Substring(1);
            unit.Name = suffix;
            unit.RefreshNameLabel();

            // Color: friendly summons are blue-ish, pillars are grey
            if (unitKind.Contains("pillar") || unitKind.Contains("boulder"))
                unit.SetBodyColor(UITheme.SummonColorPillar);
            else if (isPlayerControlled)
                unit.SetBodyColor(UITheme.SummonColorFriendly);
            else
                unit.SetBodyColor(UITheme.SummonColorEnemy);

            // Add to the appropriate unit list
            if (isPlayerControlled)
                playerUnits.Add(unit);
            else
                enemyUnits.Add(unit);

            State.UnitsInPlay.Add(unit);

            GD.Print($"[Summon] Spawned {suffix} at {tile.Axial} (HP:{hp} SPD:{speed} ARM:{armor})");
            return unit;
        };
    }

    /// <summary>
    /// Applies EquipmentLoadout stat bonuses and passive tags to a player unit.
    /// Called immediately after the unit is spawned and initialized.
    /// unitId: "wizard" for the main wizard, companion ID for companions.
    /// </summary>
    private void ApplyEquipmentLoadout(Unit unit, string unitId)
    {
        var loadout = EquipmentLoadout.Get(unitId);
        if (loadout == null) return;

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
            }
        }

        return true;
    }

    private bool TargetHasTileType(TargetSet targets, TileTerrainType terrain, TileElementType element)
    {
        if (targets == null) return false;

        foreach (var obj in targets.Items)
        {
            TileData tile = null;
            if (obj is TileData td) tile = td;
            else if (obj is HexTile tv) tile = grid.GetTile(tv.Axial);
            else if (obj is Unit u && u.CurrentTile != null) tile = u.CurrentTile;
            else if (obj is Entity e)
            {
                // Self-targeting: check the caster's tile
                if (selectedUnit?.CurrentTile != null)
                    tile = selectedUnit.CurrentTile;
            }

            if (tile == null) continue;
            if (tile.TerrainType == terrain || tile.ElementType == element)
                return true;
        }

        return false;
    }

    private bool TargetHasEmptyTile(TargetSet targets)
    {
        if (targets == null) return false;

        foreach (var obj in targets.Items)
        {
            TileData tile = null;
            if (obj is TileData td) tile = td;
            else if (obj is HexTile tv) tile = grid.GetTile(tv.Axial);

            if (tile != null && tile.Occupant == null) return true;
        }

        return false;
    }

    private void OnCardHalfHovered(CardUi cardUi, bool isTop, bool isEntering)
    {
        if (currentPhase != CombatPhase.PlayerTurn) return;

        // Lock during drag — ignore hover changes on other cards entirely
        if (_isCardBeingDragged) return;

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

    private void OnCardDroppedOnTile(CardUi cardUi, bool isTop, HexTile tile)
    {
        _isCardBeingDragged = false;
        _draggedHalf = null;
        ClearTargetHighlight();

        if (isInDeploymentPhase) { GD.Print("Cannot cast during deployment."); return; }

        var half = isTop ? cardUi.TopHalf : cardUi.BottomHalf;
        if (half == null) { State.Log("Dropped half was null."); return; }

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
                    combatUI?.AppendActionLog("Not enough mana to channel.");
                    return;
                }
                resolvedHalf = channelHalf;
                selectedUnit.Stats.Mana -= ChannelResolver.ChannelManaCost;
                combatUI?.AppendActionLog($"[Channel] {half.Name} → {channelHalf.Name}");
            }
        }

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
                    combatUI?.AppendActionLog("No valid unit on that tile.");
                    return;
                }
                if (selectedUnit?.CurrentTile != null)
                {
                    int dist = grid.Distance(selectedUnit.CurrentTile.Axial, unit.CurrentTile.Axial);
                    if (dist > ut.range)
                    {
                        combatUI?.AppendActionLog("Target is out of range!");
                        GD.Print($"[Cast] Out of range: dist={dist} range={ut.range}");
                        return;
                    }
                }
                if (!grid.HasLineOfSight(selectedUnit.CurrentTile.Axial, unit.CurrentTile.Axial))
                {
                    combatUI?.AppendActionLog("No line of sight to target!");
                    return;
                }
                if (ut.enemyOnly && unit.TeamId == selectedUnit?.TeamId)
                {
                    combatUI?.AppendActionLog("Invalid target.");
                    return;
                }
                targets.Items.Add(unit);
                break;

            case SelectTileTarget tt:
                var tileData = grid.GetTile(tile.Axial);
                if (tileData == null) { State.Log("Invalid tile."); return; }
                if (selectedUnit?.CurrentTile != null)
                {
                    int dist = grid.Distance(selectedUnit.CurrentTile.Axial, tile.Axial);
                    if (dist > tt.range)
                    {
                        combatUI?.AppendActionLog("Target is out of range!");
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
                if (etData == null) { State.Log("Invalid tile."); return; }
                targets.Items.Add(etData);
                break;

            case SelectConeTarget:
            case SelectLineTarget:
            case SelectRingTarget:
            case null:
                break;

            case SelectEmptyTileTarget et:
                var emptyTile = grid.GetTile(tile.Axial);
                if (emptyTile == null) { State.Log("Invalid tile."); return; }
                if (selectedUnit?.CurrentTile != null)
                {
                    int dist = grid.Distance(selectedUnit.CurrentTile.Axial, tile.Axial);
                    if (dist > et.Range)
                    {
                        combatUI?.AppendActionLog("Target is out of range!");
                        return;
                    }
                }
                if (emptyTile.Occupant != null)
                {
                    combatUI?.AppendActionLog("Target tile is occupied!");
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
            GD.Print($"Cast blocked: {failMsg}");
            combatUI?.AppendActionLog(failMsg);
            return;
        }

        State.ActiveCasterUnit = selectedUnit;

        var ok = Rules.TryCastWithTargets(resolvedHalf, State, Me, targets, cardUi.CardInstance);
        GD.Print($"Cast result={ok} manaNow={State.Mana[Me]}");

        if (ok)
        {
            // Record mastery cast against the original half's blueprint
            if (!string.IsNullOrEmpty(cardUi.CardInstance?.BlueprintId))
                CastMasteryTracker.RecordCast(cardUi.CardInstance.BlueprintId);

            if (selectedUnit != null)
                selectedUnit.Stats.HasPlayedCardThisTurn = true;

            if (State.ActiveEffects != null && selectedUnit != null)
            {
                foreach (var effect in State.ActiveEffects)
                {
                    if (effect is AvatarAuraEffect aura && effect.Owner == Me && !effect.IsExpired)
                        aura.OnSpellCast(State, selectedUnit, targets);
                }
            }

            // Use resolvedHalf tags for attunement so channeled element tags are read correctly
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

            while (!State.Stack.IsEmpty)
                State.Resolver.ResolveTop(State);

            RefreshEnemyRoster();

            if (deckManager != null && cardUi.CardInstance != null)
            {
                deckManager.DiscardCard(cardUi.CardInstance);
                GD.Print($"Discarded: {cardUi.CardInstance.CardName}");
            }

            State.ActiveCasterUnit = null;
            selectedUnit?.SyncManaToBar();
            RefreshSelectedUnitUI();
            RefreshPlayerUnitBar();
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
        combatUI?.SetHintText("Select a unit, move, cast, then end turn.");
    }

    private void OnGameEvent(GameEvent ge)
    {
        if (ge.Type != "AbilityResolved") return;
        if (ge.Payload is not StackItem item) return;
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
        if (!advanced) GD.Print($"Pass. Priority → {(State.Priority.PriorityHolder == Me ? "Me" : "Opp")}");
    }

    void ResolveTop()
    {
        if (State.Stack.IsEmpty) { GD.Print("Stack empty."); return; }
        GD.Print($"Resolving top… (stack size before: {State.StackCount()})");
        State.Resolver.ResolveTop(State);
        GD.Print($"Resolved. (stack size after: {State.StackCount()})");
    }

    void DumpHand()
    {
        var unit = playerUnits.Count > 0 ? playerUnits[0] : null;
        if (unit?.DeckData == null) { GD.Print("No active deck."); return; }
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
        if (half == null || selectedUnit == null || grid == null) return;

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
        if (half?.Targeting == null || selectedUnit?.CurrentTile == null) return coords;

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
                if (unit == null || !unit.Stats.IsAlive || unit.CurrentTile == null) continue;
                if (ut.enemyOnly && unit.TeamId == 0) continue;
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
                    if (tileData == null) continue;

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
                if (unit == null || !unit.Stats.IsAlive || unit.CurrentTile == null) continue;
                if (ct.EnemiesOnly && unit.TeamId == 0) continue;
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
                    if (tileData == null) continue; // off-grid

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
                if (unit == null || !unit.Stats.IsAlive || unit.CurrentTile == null) continue;
                if (lt.EnemiesOnly && unit.TeamId == 0) continue;
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
                if (unit == null || !unit.Stats.IsAlive || unit.CurrentTile == null) continue;
                if (rt.IncludeTiles) continue; // tile-only targeting, no unit highlights
                int dist = grid.Distance(center, unit.CurrentTile.Axial);
                if (dist == rt.Radius)
                    coords.Add(unit.CurrentTile.Axial);
            }
        }

        return coords;
    }
}