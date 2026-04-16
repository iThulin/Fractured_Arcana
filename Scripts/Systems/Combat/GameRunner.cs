using Godot;
using System.Collections.Generic;

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

    // ── Selection state ─────────────────────────────────────────────────────
    private Unit               selectedUnit       = null;
    private Unit               inspectedEnemyUnit = null;   
    private HashSet<Vector2I>  currentMoveTiles   = new();
    private Unit _hoveredUnit = null;

    // ── Phase ───────────────────────────────────────────────────────────────
    public enum CombatPhase { Deployment, PlayerTurn, EnemyTurn, Victory, Defeat }
    private CombatPhase currentPhase    = CombatPhase.Deployment;
    private int         roundNumber     = 1;
    private bool        enemyPhaseRunning = false;

    // ═══════════════════════════════════════════════════════════════════════
    // _Ready
    // ═══════════════════════════════════════════════════════════════════════

    public override void _Ready()
    {
        if (CardDatabase.Blueprints.Count == 0)
            CardDatabase.LoadFromCsv("res://Data/cards.csv");

        State = new GameState();
        Me = State.PlayerA;
        Opp = State.PlayerB;

        if (PlayerSession.DebugMode)
        {
            GD.Print("=== DEBUG MODE ENABLED ===");
            EnableDeploymentPhase = false;  // skip straight to player turn
            State.Mana[Me] = 99;            // unlimited mana
        }

        SpawnTestUnits();

        // Wire up helper nodes
        deckManager = GetNodeOrNull<DeckManager>("../Player/DeckManager");
        if (deckManager == null)
            GD.PrintErr("DeckManager not found. Fix the node path in GameRunner.");

        if (deckManager != null)
            CallDeferred(nameof(SyncDeckManagerToState));

        dropper = GetNodeOrNull<CardDropHandler>("../CardDropHandler");
        if (dropper == null)
            GD.PrintErr("CardDropHandler not found. Fix the node path in GameRunner.");
        else
            dropper.Connect(CardDropHandler.SignalName.CardDroppedOnTile,
                new Callable(this, nameof(OnCardDroppedOnTile)));

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

        if (playerUnit != null)
            State.Mana[Me] = playerUnit.Stats.Mana;

        State.Bus.OnEvent += OnGameEvent;

        if (!EnableDeploymentPhase)
            State.OpenPriorityWindow();

        CallDeferred(nameof(RefreshPhaseUI));
        CallDeferred(nameof(RefreshSelectedUnitUI));

        RefreshPhaseUI();
        RefreshSelectedUnitUI();
    }

    public override void _Process(double delta)
    {
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

    private void SyncDeckManagerToState()
    {
        if (deckManager == null) return;

        // Build the deck using the school + size chosen on the class select screen
        var startingDeck = deckManager.GenerateStartingDeck(
            PlayerSession.SelectedSchool,
            PlayerSession.DeckSize
        );
        deckManager.InitializeDeck(startingDeck);  // fills DrawPile and calls SafeRefreshUI

        // Now sync the populated deck into GameState
        State.LibraryA.Clear();
        State.HandA.Clear();

        foreach (var card in deckManager.DrawPile)
            State.LibraryA.Add(card);

        GD.Print($"Deck built: {deckManager.DrawPile.Count} cards ({PlayerSession.SelectedSchool})");
        RefreshDeckCounts();
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

        int mana = State.Mana.ContainsKey(Me) ? State.Mana[Me] : 0;

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

        selectedUnit       = unit;
        inspectedEnemyUnit = null;    // clear any enemy inspection
        selectedUnit.SetSelected(true);

        ClearMoveTiles();
        var reachable = grid.GetReachableTiles(unit);
        foreach (var coord in reachable)
            currentMoveTiles.Add(coord);
        ShowMoveTiles(currentMoveTiles);

        RefreshSelectedUnitUI();
        RefreshPlayerUnitBar();
        GD.Print($"Selected: {unit.Name}  move={unit.Stats.MovePoints}/{unit.Stats.BaseSpeed}  reachable={reachable.Count}");
    }

    private void TryMoveSelectedUnit(HexTile tileView)
    {
        if (selectedUnit == null || tileView == null) return;
        if (!currentMoveTiles.Contains(tileView.Axial)) { GD.Print("Tile not in range."); return; }

        var tileData = grid.GetTile(tileView.Axial);
        if (tileData == null) return;

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
        ClearMoveTiles();
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
            if (unit == null || !IsInstanceValid(unit) || !unit.Stats.IsAlive)
                continue;
            unit.StartTurn();
        }

        selectedUnit       = null;
        inspectedEnemyUnit = null;
        ClearMoveTiles();

            // Draw up to hand size at the start of each player turn
        if (deckManager != null)
        {
            int cardsToDraw = deckManager.MaxHandSize - deckManager.Hand.Count;
            if (cardsToDraw > 0)
                deckManager.DrawCards(cardsToDraw);
        }

        GD.Print($"DeckManager state — Draw: {deckManager?.DrawPile.Count}, Hand: {deckManager?.Hand.Count}");

        GD.Print($"=== Round {roundNumber}: Player Turn ===");
        combatUI?.ClearActionLog();
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
            unit.StartTurn();

        GD.Print("=== Enemy Turn Start ===");
        RefreshPhaseUI();
        RefreshSelectedUnitUI();

        await RunEnemyTurn();

        PruneDeadUnits();   // ← ADD THIS before CheckCombatEnd

        if (CheckCombatEnd()) return;

        roundNumber++;
        StartPlayerTurn();
    }

    private async System.Threading.Tasks.Task RunEnemyTurn()
    {
        foreach (var enemy in enemyUnits)
        {
            if (enemy == null || !enemy.Stats.IsAlive) continue;

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
        if (enemy.CurrentTile == null || target.CurrentTile == null) return;

        int dist = grid.Distance(enemy.CurrentTile, target.CurrentTile);

        if (dist <= 1)
        {
            // ── Attack ──────────────────────────────────────────────────────
            int dmg = 5;
            target.ApplyDamage(dmg);

            string msg = $"{enemy.Name} attacks {target.Name} for {dmg} damage. ({target.Stats.Health}/{target.Stats.MaxHealth} HP remaining)";
            GD.Print(msg);
            combatUI?.AppendActionLog(msg);

            RefreshSelectedUnitUI();
            RefreshEnemyRoster();
            RefreshPlayerUnitBar();
            RefreshDeckCounts();
            await ToSignal(GetTree().CreateTimer(0.35f), "timeout");
            return;
        }

        // ── Move toward target ───────────────────────────────────────────────
        var moveOptions  = grid.GetReachableTiles(enemy);
        Vector2I bestMove  = enemy.CurrentTile.Axial;
        int      bestDist  = dist;

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

        // ── Attack again if now adjacent after moving ────────────────────────
        if (enemy.CurrentTile != null && target.CurrentTile != null
            && grid.Distance(enemy.CurrentTile, target.CurrentTile) <= 1)
        {
            int dmg = 5;
            target.ApplyDamage(dmg);

            string atkMsg = $"{enemy.Name} attacks {target.Name} for {dmg} damage. ({target.Stats.Health}/{target.Stats.MaxHealth} HP remaining)";
            GD.Print(atkMsg);
            combatUI?.AppendActionLog(atkMsg);

            RefreshSelectedUnitUI();
            RefreshEnemyRoster();
            RefreshPlayerUnitBar();
            await ToSignal(GetTree().CreateTimer(0.35f), "timeout");
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
            return true;
        }

        if (allPlayersDead)
        {
            currentPhase = CombatPhase.Defeat;
            RefreshPhaseUI();
            GD.Print("=== DEFEAT ===");
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

        if (EnableDeploymentPhase)
            StartDeploymentPhase();
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

    private Unit FindNearestPlayerUnit(Unit enemy)
    {
        Unit best     = null;
        int  bestDist = int.MaxValue;
        foreach (var player in playerUnits)
        {
            if (player == null || !player.Stats.IsAlive || player.CurrentTile == null) continue;
            int dist = grid.Distance(enemy.CurrentTile, player.CurrentTile);
            if (dist < bestDist) { bestDist = dist; best = player; }
        }
        return best;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Casting / card logic
    // ═══════════════════════════════════════════════════════════════════════

    private void OnCardDroppedOnTile(CardUi cardUi, bool isTop, HexTile tile)
    {
        if (isInDeploymentPhase) { GD.Print("Cannot cast during deployment."); return; }

        var half = isTop ? cardUi.TopHalf : cardUi.BottomHalf;
        if (half == null) { State.Log("Dropped half was null."); return; }

        GD.Print($"Attempt cast {half.Name} cost? {(half.Costs.Length > 0 ? half.Costs[0].GetType().Name : "none")} mana={State.Mana[Me]}");

        var targets = new TargetSet();
        targets.Items.Add(tile);

        var ok = Rules.TryCastWithTargets(half, State, Me, targets, cardUi.CardInstance);
        GD.Print($"Cast result={ok} manaNow={State.Mana[Me]}");
        if (ok && playerUnit != null)
        {
            playerUnit.Stats.Mana = State.Mana[Me];
            playerUnit.SyncManaToBar();
            RefreshSelectedUnitUI();
            RefreshDeckCounts();
        }
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
    // Unchanged/old code below
    // ═══════════════════════════════════════════════════════════════════════

    private void PruneDeadUnits()
    {
        playerUnits.RemoveAll(u => u == null || !u.Stats.IsAlive);
        enemyUnits.RemoveAll(u => u == null || !u.Stats.IsAlive);
    }

}