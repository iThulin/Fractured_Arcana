using Godot;
using System.Collections.Generic;

public partial class GameRunner : Node3D
{
    // Scene references

    [Export] public PackedScene PlayerUnitScene;
    [Export] public PackedScene DummyUnitScene;
    [Export] public NodePath GridPath = "../HexGridManager";
    [Export] public NodePath CombatUIPath = "../CombatUI";
    //res://Scripts/UI/CombatUI.cs


    // Core game state and references

    public GameState State;
    private Entity Me, Opp;
    private List<Card> _compiled = new();
    private DeckManager deckManager;
    private CardDropHandler dropper;
    private HexGridManager grid;
    private CombatUI combatUI;

    // Deployment phase settings

    [Export] public bool EnableDeploymentPhase = true;
    [Export] public bool AutoStartAfterDeployment = true;
    private bool isInDeploymentPhase = false;
    private Unit selectedDeployUnit = null;
    private HashSet<Vector2I> playerDeployCoords = new();
    private readonly Dictionary<Unit, Vector2I> originalDeployCoords = new();

    // Test unit spawn settings

    [Export] public int TestPlayerCount = 2;
    [Export] public int TestEnemyCount = 3;

    private Unit playerUnit; // keep as primary unit for existing mana/UI logic
    private Unit dummyUnit;  // keep as primary enemy for existing references
    private readonly List<Unit> playerUnits = new();
    private readonly List<Unit> enemyUnits = new();
    private Unit selectedUnit = null;
    private readonly HashSet<Vector2I> currentMoveTiles = new();

    public enum CombatPhase
    {
        Deployment,
        PlayerTurn,
        EnemyTurn,
        Victory,
        Defeat
    }

    private CombatPhase currentPhase = CombatPhase.Deployment;
    private int roundNumber = 1;
    private bool enemyPhaseRunning = false;

    public override void _Ready()
    {
        // Ensure global card pool is loaded once (acts like autoload)
        if (CardDatabase.Blueprints.Count == 0)
        CardDatabase.LoadFromCsv("res://Data/cards.csv");

        State = new GameState();
        Me = State.PlayerA; 
        Opp = State.PlayerB;

        SpawnTestUnits();

        // Existing demo library setup: compile first 10 blueprints and use them as the library
        _compiled.Clear();
        for (int i = 0; i < 10 && i < CardDatabase.Blueprints.Count; i++)
            _compiled.Add(CardDatabase.Instantiate(CardDatabase.Blueprints[i]));
        for (int i=0; i<10 && i<_compiled.Count; i++) State.LibraryA.Add(_compiled[i]);

        // Draw opening hand
        State.Draw(Me, 3);
        DumpHand();

        // Get references to other nodes
        deckManager = GetNodeOrNull<DeckManager>("../Player/DeckManager");
        if (deckManager == null)
            GD.PrintErr("DeckManager not found. Fix the node path in GameRunner.");

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
                combatUI.EndTurnPressed += OnEndTurnPressed;
            }
        
        CallDeferred(nameof(RefreshPhaseUI));
        CallDeferred(nameof(RefreshSelectedUnitUI));

        // Listen to events
        State.Bus.OnEvent += OnGameEvent;

        if (!EnableDeploymentPhase)
            State.OpenPriorityWindow();

        State.Mana[Me] = 3;
        GD.Print("Keys: [T]=cast top of card 1 | [B]=bottom | [Y]=channel top | [SPACE]=pass | [R]=resolve top");

        RefreshPhaseUI();
        RefreshSelectedUnitUI();
    }

    // --- Deck/Hand/Casting Logic ---

    private void OnCardDroppedOnTile(CardUi cardUi, bool isTop, HexTile tile)
    {
        if (isInDeploymentPhase)
        {
            GD.Print("Cannot cast during deployment.");
            return;
        }

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
        }
    }

    private void OnGameEvent(GameEvent ge)
    {
        if (ge.Type != "AbilityResolved") return;
        if (ge.Payload is not StackItem item) return;

        // Discard the actual card from DeckManager when the stack resolves
        if (item.SourceCard != null && deckManager != null && item.Ability is CardHalf half && half.ConsumesCardOnResolve)
        {
            deckManager.DiscardCard(item.SourceCard);
        }
    }

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
            if (k.Keycode == Key.T) CastTop(0);
            if (k.Keycode == Key.B) CastBottom(0);
            if (k.Keycode == Key.Y) CastTopChannel(0);
        }
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

        int mana = State.Mana.ContainsKey(Me) ? State.Mana[Me] : 0;

        Unit unitToShow = isInDeploymentPhase ? selectedDeployUnit : selectedUnit;
        combatUI.ShowSelectedUnit(unitToShow, mana);
    }
    
    void CastTop(int handIndex)
    {
        if (State.HandA.Count <= handIndex) { GD.Print("No card."); return; }
        var c = State.HandA[handIndex];
        var a = c.TopHalf;
        if (a == null) { GD.Print("No top half."); return; }
        if (Rules.TryCast(a, State, Me)) GD.Print($"→ Cast top: {a.Name}");
    }

    void CastTopChannel(int handIndex)
    {
        if (State.HandA.Count <= handIndex) return;
        var a = State.HandA[handIndex].TopHalf?.ChannelVariant;
        if (a==null){ GD.Print("No channel."); return; }
        if (Rules.TryCast(a, State, Me)) GD.Print($"→ Cast channel: {a.Name}");
    }

    void CastBottom(int handIndex)
    {
        if (State.HandA.Count <= handIndex) return;
        var a = State.HandA[handIndex].BottomHalf;
        if (a == null) { GD.Print("No bottom half."); return; }
        if (Rules.TryCast(a, State, Me)) GD.Print($"→ Cast bottom: {a.Name}");
    }

    void Pass()
    {
        var advanced = State.Priority.PassPriority(State);
        if (!advanced) GD.Print($"Pass. Priority → {(State.Priority.PriorityHolder==Me?"Me":"Opp")}");
    }

    void ResolveTop()
    {
        if (State.Stack.IsEmpty) { GD.Print("Stack empty."); return; }

        GD.Print($"Resolving top... (stack size before: {State.StackCount()})");
        State.Resolver.ResolveTop(State);
        GD.Print($"Resolved. (stack size after: {State.StackCount()})");
    }

    void DumpHand()
    {
        GD.Print("Hand:");
        for (int i=0; i<State.HandA.Count; i++)
        {
            var c = State.HandA[i];
            GD.Print($"[{i}] {c.CardName}  (Top:{c.TopHalf?.Name ?? "-"} | Bottom:{c.BottomHalf?.Name ?? "-"})");
        }
    }

    // --- Unit Spawning ---

    private void BuildPlayerDeploymentArea()
    {
        playerDeployCoords.Clear();

        foreach (var zone in grid.SpawnZones)
        {
            if (zone.Side != HexGridManager.SpawnSide.Player)
                continue;

            foreach (var coord in zone.Tiles)
                playerDeployCoords.Add(coord);
        }
    }

    private void SpawnTestUnits()
    {
        grid = GetNodeOrNull<HexGridManager>(GridPath);
        if (grid == null)
        {
            GD.PrintErr($"HexGridManager not found at GridPath: {GridPath}");
            return;
        }

        if (PlayerUnitScene == null || DummyUnitScene == null)
        {
            GD.PrintErr("Assign PlayerUnitScene and DummyUnitScene in the Inspector.");
            return;
        }

        // Clear old Data
        originalDeployCoords.Clear();
        playerUnits.Clear();
        enemyUnits.Clear();

        // Spawn players
        for (int i = 0; i < TestPlayerCount; i++)
        {
            var unit = SpawnUnitFromSide(
                HexGridManager.SpawnSide.Player,
                PlayerUnitScene,
                teamId: 0,
                isPlayerControlled: true,
                namePrefix: "Player",
                maxHealth: 20,
                health: 20,
                baseSpeed: 3,
                maxMana: 0,
                mana: 0,
                armor: 0,
                shield: 0);

            if (unit != null)
                playerUnits.Add(unit);
        }

        // Spawn enemies
        for (int i = 0; i < TestEnemyCount; i++)
        {
            var unit = SpawnUnitFromSide(
                HexGridManager.SpawnSide.Enemy,
                DummyUnitScene,
                teamId: 1,
                isPlayerControlled: false,
                namePrefix: "Dummy",
                maxHealth: 50,
                health: 50,
                baseSpeed: 0,
                maxMana: 0,
                mana: 0,
                armor: 0,
                shield: 0);

            if (unit != null)
                enemyUnits.Add(unit);
        }

        if (playerUnits.Count == 0 || enemyUnits.Count == 0)
        {
            GD.PrintErr("Failed to spawn at least one player and one enemy.");
            return;
        }

        // Keep these for compatibility with the current code
        playerUnit = playerUnits[0];
        dummyUnit = enemyUnits[0];

        State.Grid = grid;
        State.PlayerUnit = playerUnit;
        State.EnemyUnit = dummyUnit;
        State.UnitsInPlay.Clear();

        foreach (var u in playerUnits)
            State.UnitsInPlay.Add(u);

        foreach (var u in enemyUnits)
            State.UnitsInPlay.Add(u);

        GD.Print($"Spawned {playerUnits.Count} player unit(s) and {enemyUnits.Count} enemy unit(s).");

        BuildPlayerDeploymentArea();

        if (EnableDeploymentPhase)
        {
            StartDeploymentPhase();
        }
    }

    private Unit SpawnUnitFromSide(
        HexGridManager.SpawnSide side,
        PackedScene scene,
        int teamId,
        bool isPlayerControlled,
        string namePrefix,
        int maxHealth,
        int health,
        int baseSpeed,
        int maxMana,
        int mana,
        int armor,
        int shield)
    {
        var slot = grid.ClaimNextSpawnSlot(side);
        if (slot == null)
        {
            GD.PrintErr($"No available spawn slot for side: {side}");
            return null;
        }

        var tile = grid.GetTileAtSpawnSlot(slot);
        if (tile == null)
        {
            GD.PrintErr($"Spawn slot had no valid tile for side: {side}");
            return null;
        }

        var unit = scene.Instantiate<Unit>();
        AddChild(unit);

        unit.IsPlayerControlled = isPlayerControlled;
        unit.TeamId = teamId;

        unit.StartMaxHealth = maxHealth;
        unit.StartHealth = health;
        unit.StartBaseSpeed = baseSpeed;
        unit.StartMaxMana = maxMana;
        unit.StartMana = mana;
        unit.StartArmor = armor;
        unit.StartShield = shield;

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

    // --- Deployment Phase Logic ---

    private void StartDeploymentPhase()
    {
        isInDeploymentPhase = true;
        ClearDeploymentSelection();

        HighlightDeploymentTiles(true);
        GD.Print("Deployment phase started. Select a friendly unit and place it within the highlighted zone. Press Enter to confirm.");
    
        currentPhase = CombatPhase.Deployment;
        RefreshPhaseUI();
        RefreshSelectedUnitUI();

    }

    private void EndDeploymentPhase()
    {
        isInDeploymentPhase = false;
        ClearDeploymentSelection();

        HighlightDeploymentTiles(false);
        GD.Print("Deployment phase ended.");

        RefreshPhaseUI();
        RefreshSelectedUnitUI();

        if (AutoStartAfterDeployment)
            StartPlayerTurn();
    }

    private void OnConfirmDeploymentPressed()
    {
        if (!isInDeploymentPhase)
            return;

        EndDeploymentPhase();
    }

    private void HighlightDeploymentTiles(bool enabled)
    {
        foreach (var coord in playerDeployCoords)
        {
            var tileView = grid.GetTileView(coord);
            if (tileView == null)
                continue;

            tileView.SetDeploymentHighlight(enabled);
        }
    }

    private void HandleDeploymentInput(InputEvent e)
    {
        if (e is InputEventKey key && key.Pressed)
        {
            if (key.Keycode == Key.Enter)
            {
                EndDeploymentPhase();
                return;
            }

            if (key.Keycode == Key.Backspace)
            {
                ResetDeploymentPositions();
                return;
            }
        }

        if (e is InputEventMouseButton mb && mb.Pressed)
        {
            if (mb.ButtonIndex == MouseButton.Left)
            {
                TryHandleDeploymentClick();
                return;
            }

            if (mb.ButtonIndex == MouseButton.Right)
            {
                ClearDeploymentSelection();
                GD.Print("Deployment selection cleared.");
                return;
            }
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

        var spaceState = GetWorld3D().DirectSpaceState;
        var query = PhysicsRayQueryParameters3D.Create(from, to);
        var result = spaceState.IntersectRay(query);

        if (result.Count == 0)
            return;

        if (!result.TryGetValue("collider", out var colliderVar))
            return;

        var collider = colliderVar.AsGodotObject() as Node;
        if (collider == null)
            return;

        // Try unit first
        Node current = collider;
        while (current != null)
        {
            if (current is Unit unit)
            {
                TrySelectDeploymentUnit(unit);
                return;
            }

            if (current is HexTile tile)
            {
                TryPlaceDeploymentUnit(tile);
                return;
            }

            current = current.GetParent();
        }
    }

    private void TrySelectDeploymentUnit(Unit unit)
    {
        if (unit == null)
            return;

        if (!unit.IsPlayerControlled)
            return;

        if (!playerUnits.Contains(unit))
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
        {
            GD.Print("That tile is outside the deployment zone.");
            return;
        }

        var tileData = grid.GetTile(tileView.Axial);
        if (tileData == null)
            return;

        if (!tileData.IsWalkable || tileData.IsBlocked || tileData.IsOccupied)
        {
            GD.Print("That deployment tile is not available.");
            return;
        }

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
            var unit = kvp.Key;
            var coord = kvp.Value;
            var tile = grid.GetTile(coord);

            if (tile == null)
                continue;

            if (!tile.IsWalkable || tile.IsBlocked)
                continue;

            unit.PlaceOnTile(tile);
        }

        RefreshSelectedUnitUI();
        GD.Print("Deployment positions reset.");
    }

    // --- Main Phase Logic ---

    private void StartPlayerTurn()
    {
        currentPhase = CombatPhase.PlayerTurn;
        enemyPhaseRunning = false;

        foreach (var unit in playerUnits)
            unit.StartTurn();

        selectedUnit = null;
        ClearMoveTiles();

        GD.Print($"=== Round {roundNumber}: Player Turn ===");

        RefreshPhaseUI();
        RefreshSelectedUnitUI();
    }

    private void EndPlayerTurn()
    {
        if (currentPhase != CombatPhase.PlayerTurn)
            return;

        selectedUnit = null;
        ClearMoveTiles();

        GD.Print("=== Player Turn End ===");

        RefreshPhaseUI();
        StartEnemyTurn();
    }

    private async void StartEnemyTurn()
    {
        if (enemyPhaseRunning)
            return;

        currentPhase = CombatPhase.EnemyTurn;
        enemyPhaseRunning = true;

        foreach (var unit in enemyUnits)
            unit.StartTurn();

        GD.Print("=== Enemy Turn Start ===");
        RefreshPhaseUI();
        RefreshSelectedUnitUI();

        await RunEnemyTurn();

        if (CheckCombatEnd())
            return;

        roundNumber++;
        StartPlayerTurn();
    }

    private async System.Threading.Tasks.Task RunEnemyTurn()
    {
        foreach (var enemy in enemyUnits)
        {
            if (enemy == null || !enemy.Stats.IsAlive)
                continue;

            var target = FindNearestPlayerUnit(enemy);
            if (target == null)
                continue;

            await ActEnemyUnit(enemy, target);

            if (CheckCombatEnd())
                return;
        }

        GD.Print("=== Enemy Turn End ===");
        enemyPhaseRunning = false;
    }

    private async System.Threading.Tasks.Task ActEnemyUnit(Unit enemy, Unit target)
    {
        if (enemy.CurrentTile == null || target.CurrentTile == null)
            return;

        int dist = grid.Distance(enemy.CurrentTile, target.CurrentTile);

        // If adjacent, attack
        if (dist <= 1)
        {
            GD.Print($"{enemy.Name} attacks {target.Name}");
            target.ApplyDamage(5);

            RefreshSelectedUnitUI();
            await ToSignal(GetTree().CreateTimer(0.35f), "timeout");
            return;
        }

        // Otherwise move
        var moveOptions = grid.GetReachableTiles(enemy);
        Vector2I bestMove = enemy.CurrentTile.Axial;
        int bestMoveDist = dist;

        foreach (var coord in moveOptions)
        {
            var tile = grid.GetTile(coord);
            if (tile == null)
                continue;

            int newDist = grid.Distance(tile, target.CurrentTile);
            if (newDist < bestMoveDist)
            {
                bestMoveDist = newDist;
                bestMove = coord;
            }
        }

        if (bestMove != enemy.CurrentTile.Axial)
        {
            var tile = grid.GetTile(bestMove);
            if (tile != null && enemy.TryMoveTo(grid, tile))
            {
                GD.Print($"{enemy.Name} moves to {bestMove}");
                await ToSignal(GetTree().CreateTimer(0.35f), "timeout");
            }
        }

        // Attack after moving if now adjacent
        if (enemy.CurrentTile != null && target.CurrentTile != null)
        {
            dist = grid.Distance(enemy.CurrentTile, target.CurrentTile);
            if (dist <= 1)
            {
                GD.Print($"{enemy.Name} attacks {target.Name}");
                target.ApplyDamage(5);
                RefreshSelectedUnitUI();
                await ToSignal(GetTree().CreateTimer(0.35f), "timeout");
            }
        }
    }

    private bool CheckCombatEnd()
    {
        bool anyPlayersAlive = false;
        foreach (var unit in playerUnits)
        {
            if (unit != null && unit.Stats.IsAlive)
            {
                anyPlayersAlive = true;
                break;
            }
        }

        bool anyEnemiesAlive = false;
        foreach (var unit in enemyUnits)
        {
            if (unit != null && unit.Stats.IsAlive)
            {
                anyEnemiesAlive = true;
                break;
            }
        }

        if (!anyPlayersAlive)
        {
            currentPhase = CombatPhase.Defeat;
            RefreshPhaseUI();
            GD.Print("=== Defeat ===");
            return true;
        }

        if (!anyEnemiesAlive)
        {
            currentPhase = CombatPhase.Victory;
            RefreshPhaseUI();
            GD.Print("=== Victory ===");
            return true;
        }

        return false;
    }

    // Main Phase Helpers

    private Unit FindNearestPlayerUnit(Unit enemy)
    {
        Unit best = null;
        int bestDist = int.MaxValue;

        foreach (var player in playerUnits)
        {
            if (player == null || !player.Stats.IsAlive || player.CurrentTile == null)
                continue;

            int dist = grid.Distance(enemy.CurrentTile, player.CurrentTile);
            if (dist < bestDist)
            {
                bestDist = dist;
                best = player;
            }
        }

        return best;
    }

    private void ShowMoveTiles(HashSet<Vector2I> coords)
    {
        ClearMoveTiles();

        foreach (var coord in coords)
        {
            var tile = grid.GetTileView(coord);
            tile?.SetMoveHighlight(true);
        }
    }

    private void ClearMoveTiles()
    {
        foreach (var coord in currentMoveTiles)
        {
            var tile = grid.GetTileView(coord);
            tile?.SetMoveHighlight(false);
        }

        currentMoveTiles.Clear();
    }

    private void SelectUnit(Unit unit)
    {
        if (unit == null)
            return;

        if (!unit.IsPlayerControlled)
            return;

        if (selectedUnit != null)
            selectedUnit.SetSelected(false);

        selectedUnit = unit;
        selectedUnit.SetSelected(true);

        ClearMoveTiles();

        var reachable = grid.GetReachableTiles(unit);
        foreach (var coord in reachable)
            currentMoveTiles.Add(coord);

        ShowMoveTiles(currentMoveTiles);

        RefreshSelectedUnitUI();
        GD.Print($"Selected unit: {unit.Name}, move points={unit.Stats.MovePoints}");
        GD.Print($"Reachable tiles count: {reachable.Count}");
    }

    private void TryMoveSelectedUnit(HexTile tileView)
    {
        if (selectedUnit == null || tileView == null)
            return;

        if (!currentMoveTiles.Contains(tileView.Axial))
        {
            GD.Print("Tile is not in movement range.");
            return;
        }

        var tileData = grid.GetTile(tileView.Axial);
        if (tileData == null)
            return;

        if (selectedUnit.TryMoveTo(grid, tileData))
        {
            GD.Print($"{selectedUnit.Name} moved to {tileData.Axial}");
            RefreshSelectedUnitUI();

            ClearMoveTiles();
            currentMoveTiles.Clear();

            // Recompute remaining movement if multi-step movement in one turn is allowed
            var reachable = grid.GetReachableTiles(selectedUnit);
            foreach (var coord in reachable)
                currentMoveTiles.Add(coord);

            ShowMoveTiles(currentMoveTiles);
        }
    }

    private void TryHandleMainPhaseClick()
    {
        GD.Print($"TryHandleMainPhaseClick phase={currentPhase}");

        if (currentPhase != CombatPhase.PlayerTurn)
        {
            GD.Print("Not player turn, ignoring click.");
            return;
        }

        var camera = GetViewport().GetCamera3D();
        if (camera == null)
        {
            GD.PrintErr("No active camera.");
            return;
        }

        Vector2 mousePos = GetViewport().GetMousePosition();
        Vector3 from = camera.ProjectRayOrigin(mousePos);
        Vector3 to = from + camera.ProjectRayNormal(mousePos) * 1000f;

        var spaceState = GetWorld3D().DirectSpaceState;
        var query = PhysicsRayQueryParameters3D.Create(from, to);
        var result = spaceState.IntersectRay(query);

        if (result.Count == 0)
        {
            GD.Print("Main phase click hit nothing.");
            return;
        }

        if (!result.TryGetValue("collider", out var colliderVar))
        {
            GD.Print("Main phase click had no collider entry.");
            return;
        }

        var collider = colliderVar.AsGodotObject() as Node;
        GD.Print($"Main phase click hit: {collider?.Name}");

        if (collider == null)
            return;

        Node current = collider;
        while (current != null)
        {
            GD.Print($"Walking node: {current.Name} ({current.GetType().Name})");

            if (current is Unit unit)
            {
                GD.Print($"Selecting unit: {unit.Name}");
                SelectUnit(unit);
                return;
            }

            if (current is HexTile tile)
            {
                GD.Print($"Trying move to tile: {tile.Axial}");
                TryMoveSelectedUnit(tile);
                return;
            }

            current = current.GetParent();
        }
    }

    private void OnEndTurnPressed()
    {
        if (currentPhase != CombatPhase.PlayerTurn)
            return;

        EndPlayerTurn();
    }


}