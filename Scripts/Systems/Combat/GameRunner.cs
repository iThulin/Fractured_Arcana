using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

public partial class GameRunner : Node3D
{
    // ── Scene references ────────────────────────────────────────────────────
    [Export] public PackedScene PlayerUnitScene;
    [Export] public PackedScene DummyUnitScene;
    [Export] public NodePath GridPath      = "../HexGridManager";
    [Export] public NodePath CombatUIPath  = "../CombatUI";

    // ── Core state ──────────────────────────────────────────────────────────
    public GameState State;
    private Entity Me, Opp;
    private List<Card>       _compiled = new();
    private DeckManager      deckManager;
    private DeckUiManager  deckUiManager;
    private CardDropHandler  dropper;
    private HexGridManager   grid;
    private CombatUI         combatUI;

    // ── Deployment phase ────────────────────────────────────────────────────
    [Export] public bool EnableDeploymentPhase    = true;
    [Export] public bool AutoStartAfterDeployment = true;
    private bool                         isInDeploymentPhase = false;
    private Unit                         selectedDeployUnit  = null;
    private HashSet<Vector2I>            playerDeployCoords  = new();
    private Dictionary<Unit, Vector2I>   originalDeployCoords = new();

    // ── Unit lists ──────────────────────────────────────────────────────────
    [Export] public int TestPlayerCount = 2;
    [Export] public int TestEnemyCount  = 3;

    private Unit       playerUnit;   // primary player unit (kept for mana logic)
    private Unit       dummyUnit;    // primary enemy unit  (kept for existing refs)
    private List<Unit> playerUnits = new();
    private List<Unit> enemyUnits  = new();
    private bool _pruneNeeded;

    // ── Selection state ─────────────────────────────────────────────────────
    private Unit               selectedUnit       = null;
    private Unit               inspectedEnemyUnit = null;   
    private HashSet<Vector2I>  currentMoveTiles   = new();
    private Unit _hoveredUnit = null;
    private SchoolAttunementUI schoolAttunementUI;

    // ── Tile highlighting state ─────────────────────────────────────────────
    private HashSet<Vector2I> _targetHighlightTiles = new();
    private CardHalf _lastHighlightedHalf = null;
    private bool _isCardBeingDragged = false;
    private CardHalf _draggedHalf = null;

    // ── Phase ───────────────────────────────────────────────────────────────
    public enum CombatPhase { Deployment, PlayerTurn, EnemyTurn, Victory, Defeat }
    private CombatPhase currentPhase    = CombatPhase.Deployment;
    private int         roundNumber     = 1;
    private bool        enemyPhaseRunning = false;

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
            dropper.CardDragEnded   += OnCardDragEnded;
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
            combatUI.EndTurnPressed           += OnEndTurnPressed;

            // NEW – unit bar buttons select the corresponding unit
            combatUI.UnitButtonPressed  += OnUnitBarButtonPressed;
            // NEW – enemy roster buttons inspect the corresponding enemy
            combatUI.EnemyButtonPressed += OnEnemyRosterButtonPressed;
        }

        // Create the attunement UI as a child of CombatUI
        schoolAttunementUI = new SchoolAttunementUI();
        schoolAttunementUI.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
        schoolAttunementUI.Position = new Vector2(0, 162);
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

        var camera = GetViewport().GetCamera3D();
        if (camera == null) return;

        Vector2 mousePos = GetViewport().GetMousePosition();
        Vector3 from = camera.ProjectRayOrigin(mousePos);
        Vector3 to   = from + camera.ProjectRayNormal(mousePos) * 1000f;

        var result = GetWorld3D().DirectSpaceState
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
        }
    }

    private void InitializeUnitDecks()
    {
        // Get cards from active party companions (added to the wizard's deck)
        var companionCards = BuildCompanionCardList();

        bool injectedCompanionCards = false;

        foreach (var unit in playerUnits)
        {
            if (unit == null) continue;

            unit.DeckData = new UnitDeckData(unit.School, 5);

            if (!injectedCompanionCards && companionCards.Count > 0)
            {
                // First unit gets a school deck PLUS companion cards
                var schoolDeck = CardDatabase.BuildRandomDeck(unit.School, PlayerSession.DeckSize);
                schoolDeck.AddRange(companionCards);
                unit.DeckData.Initialize(schoolDeck);
                injectedCompanionCards = true;
                GD.Print($"Deck built for {unit.Name}: {unit.DeckData.TotalCards} cards " +
                         $"({unit.School}, {companionCards.Count} from companions)");
            }
            else
            {
                unit.DeckData.Initialize(PlayerSession.DeckSize);
                GD.Print($"Deck built for {unit.Name}: {unit.DeckData.TotalCards} cards ({unit.School})");
            }
        }

        if (playerUnits.Count > 0 && playerUnits[0].DeckData != null)
            deckManager.SetActiveDeck(playerUnits[0].DeckData);
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
        combatUI?.RefreshEnemyRoster(enemyUnits);
    }

    private void RefreshPlayerUnitBar()
    {
        combatUI?.RefreshPlayerUnitBar(playerUnits, selectedUnit);
        schoolAttunementUI?.ShowForUnit(selectedUnit);
    }

    private void RefreshDeckCounts()
    {
        combatUI?.RefreshDeckCounts(State.LibraryA, State.Graveyard);
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
        selectedUnit        = null;
        inspectedEnemyUnit  = enemy;
        inspectedEnemyUnit.SetSelected(true);       // ← ADD THIS
        ClearMoveTiles();
        RefreshSelectedUnitUI();
        RefreshPlayerUnitBar();
        GD.Print($"Inspecting enemy: {enemy.Name}");
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

        if (e is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
        {
            TryHandleMainPhaseClick();
            return;
        }

        if (e.IsActionPressed("ui_select")) { Pass(); } // space by default
        if (e.IsActionPressed("ui_accept")) { ResolveTop(); } // enter
        if (e is InputEventKey k && k.Pressed)
        {
            if (k.Keycode == Key.R) ResolveTop();
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
        Vector3 to   = from + camera.ProjectRayNormal(mousePos) * 1000f;

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
                    // Selecting a player unit clears any enemy inspection
                    inspectedEnemyUnit = null;
                    SelectUnit(unit);
                }
                else
                {
                    // Clicking an enemy inspects it
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

    private void InspectEnemy(Unit enemy)
    {
        if (enemy == null || !enemy.Stats.IsAlive) return;

        // Clear previous inspected enemy ring
        if (inspectedEnemyUnit != null)
            inspectedEnemyUnit.SetSelected(false);

        if (selectedUnit != null)
            selectedUnit.SetSelected(false);
        selectedUnit       = null;
        inspectedEnemyUnit = enemy;
        inspectedEnemyUnit.SetSelected(true);   // ← ADD THIS
        ClearMoveTiles();

        RefreshSelectedUnitUI();
        RefreshPlayerUnitBar();
        GD.Print($"Inspecting enemy: {enemy.Name}  HP={enemy.Stats.Health}/{enemy.Stats.MaxHealth}");
    }

    private void TryInspectClick()
    {
        var camera = GetViewport().GetCamera3D();
        if (camera == null) return;

        Vector2 mousePos = GetViewport().GetMousePosition();
        Vector3 from = camera.ProjectRayOrigin(mousePos);
        Vector3 to   = from + camera.ProjectRayNormal(mousePos) * 1000f;

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
        var reachable = grid.GetReachableTiles(unit);
        foreach (var coord in reachable)
            currentMoveTiles.Add(coord);
        ShowMoveTiles(currentMoveTiles);

        // ── Swap deck to this unit's deck ──
        if (unit.DeckData != null && deckManager != null)
        {
            deckManager.SetActiveDeck(unit.DeckData);
            deckManager.PrintDeckState();
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

        GD.Print($"Selected: {unit.Name}  move={unit.Stats.MovePoints}/{unit.Stats.BaseSpeed}  reachable={reachable.Count}");
    }

    private void TryMoveSelectedUnit(HexTile tileView)
    {
        if (selectedUnit == null || tileView == null) return;
        if (!currentMoveTiles.Contains(tileView.Axial)) { GD.Print("Tile not in range."); return; }

        var tileData = grid.GetTile(tileView.Axial);
        if (tileData == null) return;

        if (selectedUnit != null && !selectedUnit.CanMove())
        {
            GD.Print($"{selectedUnit.Name} cannot move!");
            combatUI?.AppendActionLog($"{selectedUnit.Name} is immobilized!");
            return;
        }

        if (selectedUnit.TryMoveTo(grid, tileData))
        {
            GD.Print($"{selectedUnit.Name} moved to {tileData.Axial}");
            ClearMoveTiles();
            var reachable = grid.GetReachableTiles(selectedUnit);
            foreach (var coord in reachable)
                currentMoveTiles.Add(coord);
            ShowMoveTiles(currentMoveTiles);

            RefreshSelectedUnitUI();
            RefreshPlayerUnitBar();
        }
    }

    private void ShowMoveTiles(HashSet<Vector2I> coords)
    {
        // Don't call ClearMoveTiles here — just apply highlights
        foreach (var coord in coords)
            grid.GetTileView(coord)?.SetMoveHighlight(true);
    }

    private void ClearMoveTiles()
    {
        foreach (var coord in currentMoveTiles)
            grid.GetTileView(coord)?.SetMoveHighlight(false);
        currentMoveTiles.Clear();
    }
    
    // ═══════════════════════════════════════════════════════════════════════
    // Turn flow
    // ═══════════════════════════════════════════════════════════════════════

    private void StartPlayerTurn()
    {
        currentPhase      = CombatPhase.PlayerTurn;
        enemyPhaseRunning = false;

        foreach (var unit in playerUnits)
        {
            if (unit == null || !IsInstanceValid(unit) || !unit.Stats.IsAlive) continue;
            unit.StartTurn();
            unit.TickStatuses();
            unit.Attunement?.Decay();

            if (unit.DeckData != null)
            {
                var drawn = unit.DeckData.DrawToFull();
                foreach (var card in drawn)
                    GD.Print($"[{unit.Name}] Drew: {card.TopHalf?.Name ?? card.CardName}");
            }
        }

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

    private async void StartEnemyTurn()
    {
        if (enemyPhaseRunning) return;

        currentPhase      = CombatPhase.EnemyTurn;
        enemyPhaseRunning = true;

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
        
    private async System.Threading.Tasks.Task ActEnemyUnit(Unit enemy, Unit target)
    {
        if (!IsValidActor(enemy) || !IsValidActor(target)) return;

        int dist = grid.Distance(enemy.CurrentTile, target.CurrentTile);

        // ── Attack if already adjacent ──
        if (dist <= 1)
        {
            await PerformAttack(enemy, target);
            return;
        }

        // ── Move toward target ──
        var moveOptions = grid.GetReachableTiles(enemy);
        Vector2I bestMove = enemy.CurrentTile.Axial;
        int bestDist = dist;

        foreach (var coord in moveOptions)
        {
            var tile = grid.GetTile(coord);
            if (tile == null) continue;
            int newDist = grid.Distance(tile, target.CurrentTile);
            if (newDist < bestDist) { bestDist = newDist; bestMove = coord; }
        }

        if (bestMove != enemy.CurrentTile.Axial)
        {
            var tile = grid.GetTile(bestMove);
            if (tile != null && enemy.TryMoveTo(grid, tile))
            {
                string moveMsg = $"{enemy.Name} moves toward {target.Name}.";
                GD.Print(moveMsg);
                combatUI?.AppendActionLog(moveMsg);
                await ToSignal(GetTree().CreateTimer(0.35f), "timeout");
            }
        }

        // ── Re-validate after movement; target or enemy may have died via hazards ──
        if (!IsValidActor(enemy) || !IsValidActor(target)) return;

        // ── Attack if now adjacent ──
        if (grid.Distance(enemy.CurrentTile, target.CurrentTile) <= 1)
            await PerformAttack(enemy, target);
    }

    private async System.Threading.Tasks.Task PerformAttack(Unit enemy, Unit target)
    {
        const int dmg = 5;

        // Log attack first, then apply damage
        string msg = $"{enemy.Name} attacks {target.Name} for {dmg} damage.";
        GD.Print(msg);
        combatUI?.AppendActionLog(msg);

        target.ApplyDamage(dmg);

        // HP-remaining suffix as a separate line, so death messages can interleave naturally
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
        bool allEnemiesDead  = true;
        bool allPlayersDead  = true;

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
    }

    private void EndDeploymentPhase()
    {
        isInDeploymentPhase = false;
        ClearDeploymentSelection();
        HighlightDeploymentTiles(false);
        GD.Print("Deployment phase ended.");
        RefreshPhaseUI();
        RefreshSelectedUnitUI();
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
            if (key.Keycode == Key.Enter)    { EndDeploymentPhase();       return; }
            if (key.Keycode == Key.Backspace) { ResetDeploymentPositions(); return; }
        }

        if (e is InputEventMouseButton mb && mb.Pressed)
        {
            if (mb.ButtonIndex == MouseButton.Left)  { TryHandleDeploymentClick(); return; }
            if (mb.ButtonIndex == MouseButton.Right) { ClearDeploymentSelection(); GD.Print("Deployment selection cleared."); }
        }
    }

    private void TryHandleDeploymentClick()
    {
        var camera = GetViewport().GetCamera3D();
        if (camera == null) return;

        Vector2 mousePos = GetViewport().GetMousePosition();
        Vector3 from = camera.ProjectRayOrigin(mousePos);
        Vector3 to   = from + camera.ProjectRayNormal(mousePos) * 1000f;

        var result = GetWorld3D().DirectSpaceState
            .IntersectRay(PhysicsRayQueryParameters3D.Create(from, to));
        if (result.Count == 0) return;
        if (!result.TryGetValue("collider", out var cv)) return;

        Node current = cv.AsGodotObject() as Node;
        while (current != null)
        {
            if (current is Unit unit)  { TrySelectDeploymentUnit(unit); return; }
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

    // ═══════════════════════════════════════════════════════════════════════
    // Unit spawning
    // ═══════════════════════════════════════════════════════════════════════

    private void BuildPlayerDeploymentArea()
    {
        playerDeployCoords.Clear();
        foreach (var zone in grid.SpawnZones)
            if (zone.Side == HexGridManager.SpawnSide.Player)
                foreach (var coord in zone.Tiles)
                    playerDeployCoords.Add(coord);
    }

    private void SpawnTestUnits()
    {
        grid = GetNodeOrNull<HexGridManager>(GridPath);
        if (grid == null) { GD.PrintErr($"HexGridManager not found at: {GridPath}"); return; }
        if (PlayerUnitScene == null || DummyUnitScene == null) { GD.PrintErr("Assign PlayerUnitScene and DummyUnitScene in the Inspector."); return; }

        originalDeployCoords.Clear();
        playerUnits.Clear();
        enemyUnits.Clear();

        for (int i = 0; i < TestPlayerCount; i++)
        {
            var unit = SpawnUnitFromSide(HexGridManager.SpawnSide.Player, PlayerUnitScene,
                teamId: 0, isPlayerControlled: true, namePrefix: "Player",
                maxHealth: 20, health: 20, baseSpeed: 3, maxMana: 3, mana: 3, armor: 0, shield: 0);
            if (unit != null) playerUnits.Add(unit);
        }

        for (int i = 0; i < TestEnemyCount; i++)
        {
            var unit = SpawnUnitFromSide(HexGridManager.SpawnSide.Enemy, DummyUnitScene,
                teamId: 1, isPlayerControlled: false, namePrefix: "Enemy",
                maxHealth: 30, health: 30, baseSpeed: 2, maxMana: 0, mana: 0, armor: 0, shield: 0);
            if (unit != null) enemyUnits.Add(unit);
        }

        if (playerUnits.Count == 0 || enemyUnits.Count == 0)
        {
            GD.PrintErr("Failed to spawn at least one player and one enemy.");
            return;
        }

        // Distinct colors for each enemy so they're visually differentiable
        var enemyColors = new Color[]
        {
            new Color(1.0f, 0.25f, 0.25f), // red
            new Color(1.0f, 0.55f, 0.1f),  // orange
            new Color(0.8f, 0.2f, 0.9f),   // purple
            new Color(0.2f, 0.8f, 0.9f),   // cyan
            new Color(0.9f, 0.9f, 0.1f),   // yellow
        };
        for (int i = 0; i < enemyUnits.Count; i++)
        {
            enemyUnits[i].SetBodyColor(enemyColors[i % enemyColors.Length]);
            enemyUnits[i].RefreshNameLabel();
        }

        playerUnit = playerUnits[0];
        dummyUnit  = enemyUnits[0];

        State.Grid       = grid;
        State.PlayerUnit = playerUnit;
        State.EnemyUnit  = dummyUnit;
        State.UnitsInPlay.Clear();
        foreach (var u in playerUnits) State.UnitsInPlay.Add(u);
        foreach (var u in enemyUnits)  State.UnitsInPlay.Add(u);

        GD.Print($"Spawned {playerUnits.Count} player unit(s) and {enemyUnits.Count} enemy unit(s).");

        BuildPlayerDeploymentArea();

        playerUnit.School = PlayerSession.SelectedSchool;
        playerUnit.InitializeAttunement();

        // Do the same for Player_2 if they should also be an Elementalist:
        foreach (var unit in playerUnits)
        {
            unit.School = PlayerSession.SelectedSchool;
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

        unit.IsPlayerControlled = isPlayerControlled;
        unit.TeamId             = teamId;
        unit.StartMaxHealth     = maxHealth;
        unit.StartHealth        = health;
        unit.StartBaseSpeed     = baseSpeed;
        unit.StartMaxMana       = maxMana;
        unit.StartMana          = mana;
        unit.StartArmor         = armor;
        unit.StartShield        = shield;

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
        // Player units stay on their spawn tiles — deployment is already done by SpawnUnits
        // Just confirm placement
        foreach (var unit in playerUnits)
        {
            if (unit?.CurrentTile != null)
                GD.Print($"[Debug] {unit.Name} auto-placed at {unit.CurrentTile.Axial}");
        }
    }

    private Unit FindNearestPlayerUnit(Unit enemy)
    {
        if (enemy == null || !IsInstanceValid(enemy) || enemy.CurrentTile == null)
            return null;

        Unit best     = null;
        int  bestDist = int.MaxValue;
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

            // Name it
            string suffix = unitKind.Replace("_", " ");
            suffix = char.ToUpper(suffix[0]) + suffix.Substring(1);
            unit.Name = suffix;
            unit.RefreshNameLabel();

            // Color: friendly summons are blue-ish, pillars are grey
            if (unitKind.Contains("pillar") || unitKind.Contains("boulder"))
                unit.SetBodyColor(new Color(0.5f, 0.45f, 0.35f)); // stone grey-brown
            else if (isPlayerControlled)
                unit.SetBodyColor(new Color(0.3f, 0.6f, 0.9f)); // friendly blue
            else
                unit.SetBodyColor(new Color(0.9f, 0.3f, 0.3f)); // enemy red

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

        GD.Print($"Attempt cast {half.Name} cost? {(half.Costs.Length > 0 ? half.Costs[0].GetType().Name : "none")} mana={State.Mana[Me]}");

        if (selectedUnit != null && !selectedUnit.CanAct())
        {
            GD.Print($"{selectedUnit.Name} is frozen and cannot act!");
            combatUI?.AppendActionLog($"{selectedUnit.Name} is frozen!");
            return;
        }

        var targets = new TargetSet();
        switch (half.Targeting)
        {
            case SelectUnitTarget:
                var unit = State.UnitsInPlay
                    .FirstOrDefault(u => u?.CurrentTile?.Axial == tile.Axial && u.Stats.IsAlive);
                if (unit == null)
                {
                    combatUI?.AppendActionLog("No valid unit on that tile.");
                    return;
                }
                targets.Items.Add(unit);
                break;

            case SelectTileTarget:
                var tileData = grid.GetTile(tile.Axial);
                if (tileData == null) { State.Log("Invalid tile."); return; }
                targets.Items.Add(tileData);
                break;

            case SelectSelfTarget:
                // Self-targeting: target is the caster's own unit/entity
                targets.Items.Add(Me);
                break;

            case SelectAreaTarget:
            case SelectGlobalTarget:
                // AoE and global effects don't need a player-selected target;
                // pass the caster as a stand-in so the TargetSet is non-empty.
                targets.Items.Add(Me);
                break;

            case SelectElementTileTarget et:
                // Player dropped on a specific tile — use it if it matches the element
                var etData = grid.GetTile(tile.Axial);
                if (etData == null) { State.Log("Invalid tile."); return; }
                targets.Items.Add(etData);
                break;

            case SelectConeTarget:
            case SelectLineTarget:
            case SelectRingTarget:
            case null:
                // No targeting required — targets stays empty, TryCastWithTargets handles null targeting
                break;

            default:
                GD.PrintErr($"[GameRunner] Unhandled targeter type: {half.Targeting.GetType().Name}");
                targets.Items.Add(Me); // fallback
                break;
        }

        if (!CheckCastRequirements(half, targets, out var failMsg))
        {
            GD.Print($"Cast blocked: {failMsg}");
            combatUI?.AppendActionLog(failMsg);
            return;
        }

        // Set the active caster unit for this cast (used to direct which unit's mana to consume)
        State.ActiveCasterUnit = selectedUnit;

        // Try to cast the card half on the target tile
        var ok = Rules.TryCastWithTargets(half, State, Me, targets, cardUi.CardInstance);
        GD.Print($"Cast result={ok} manaNow={State.Mana[Me]}");

        if (ok)
        {

            // --- Avatar aura: notify on cast, apply bonus damage ---
            if (State.ActiveEffects != null && selectedUnit != null)
            {
                foreach (var effect in State.ActiveEffects)
                {
                    if (effect is AvatarAuraEffect aura && effect.Owner == Me && !effect.IsExpired)
                    {
                        aura.OnSpellCast(State, selectedUnit, targets);
                    }
                }
            }
            // --- Attunement integration ---
            if (selectedUnit != null &&
                selectedUnit.School == CardSchool.Elementalist &&
                selectedUnit.Attunement is ElementalAttunement elemAtt &&
                half.Tags != null && half.Tags.Length > 0)
            {
                // 1. Feed tags to tracker
                var burstEffects = elemAtt.OnSpellCast(half.Tags);

                // 2. Apply threshold bonuses
                var bonusLog = AttunementResolver.ApplyThresholdEffects(
                    elemAtt, half.Tags, State, selectedUnit, targets);

                foreach (var msg in bonusLog)
                {
                    GD.Print(msg);
                    combatUI?.AppendActionLog(msg);
                }

                // 3. Resolve bursts
                foreach (var burst in burstEffects)
                {
                    var burstLog = AttunementResolver.ResolveBurst(
                        burst.Element, State, selectedUnit);

                    foreach (var msg in burstLog)
                    {
                        GD.Print(msg);
                        combatUI?.AppendActionLog(msg);
                    }
                }

                // 4. Refresh
                schoolAttunementUI?.Refresh();
                RefreshAllUI();
            }

            // Set last cast element for potential synergies
            if (half.Tags != null)
            {
                foreach (var tag in half.Tags)
                {
                    if (ElementalAttunement.TryParseTag(tag, out var elem))
                    {
                        selectedUnit.LastCastElement = elem;
                        break; // take the first element tag
                    }
                }
            }

            // Resolve the stack immediatly
            while (!State.Stack.IsEmpty)
                State.Resolver.ResolveTop(State);
            
            RefreshEnemyRoster();

            // Discard the card immediately on successful cast
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
    }

    private void OnCardDragEnded()
    {
        _isCardBeingDragged = false;
        _draggedHalf = null;
        ClearTargetHighlight();
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
        if (!advanced) GD.Print($"Pass. Priority → {(State.Priority.PriorityHolder==Me?"Me":"Opp")}");
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
        GD.Print("Hand:");
        for (int i = 0; i < State.HandA.Count; i++)
        {
            var c = State.HandA[i];
            GD.Print($"[{i}] {c.CardName}  (Top:{c.TopHalf?.Name ?? "-"} | Bottom:{c.BottomHalf?.Name ?? "-"})");
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
                "fire"  => TileElementType.Fire,
                "ice"   => TileElementType.Frost,
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