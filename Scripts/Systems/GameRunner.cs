using Godot;
using System.Collections.Generic;

public partial class GameRunner : Node3D
{
    // Scene references

    [Export] public PackedScene PlayerUnitScene;
    [Export] public PackedScene DummyUnitScene;
    [Export] public NodePath GridPath = "../HexGridManager";

    // Core game state and references

    public GameState State;
    private Entity Me, Opp;
    private List<Card> _compiled = new();
    private DeckManager deckManager;
    private CardDropHandler dropper;
    private HexGridManager grid;

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

        // Get DeckManager and CardDropHandler references
        deckManager = GetNodeOrNull<DeckManager>("../Player/DeckManager");
        if (deckManager == null)
            GD.PrintErr("DeckManager not found. Fix the node path in GameRunner.");

        dropper = GetNodeOrNull<CardDropHandler>("../CardDropHandler");
        if (dropper == null)
            GD.PrintErr("CardDropHandler not found. Fix the node path in GameRunner.");
        else
            dropper.Connect(CardDropHandler.SignalName.CardDroppedOnTile,
                new Callable(this, nameof(OnCardDroppedOnTile)));

        // Listen to events
        State.Bus.OnEvent += OnGameEvent;

        if (!EnableDeploymentPhase)
            State.OpenPriorityWindow();

        State.Mana[Me] = 3;
        GD.Print("Keys: [T]=cast top of card 1 | [B]=bottom | [Y]=channel top | [SPACE]=pass | [R]=resolve top");
    }

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

    private void StartDeploymentPhase()
    {
        isInDeploymentPhase = true;
        ClearDeploymentSelection();

        HighlightDeploymentTiles(true);
        GD.Print("Deployment phase started. Select a friendly unit and place it within the highlighted zone. Press Enter to confirm.");
    }

    private void EndDeploymentPhase()
    {
        isInDeploymentPhase = false;
        ClearDeploymentSelection();

        HighlightDeploymentTiles(false);
        GD.Print("Deployment phase ended.");

        if (AutoStartAfterDeployment)
            State.OpenPriorityWindow();
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
            selectedDeployUnit.SetDeploymentSelected(false);

        selectedDeployUnit = unit;
        selectedDeployUnit.SetDeploymentSelected(true);

        GD.Print($"Selected deploy unit: {unit.Name}");
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

        selectedDeployUnit.SetDeploymentSelected(false);
        selectedDeployUnit = null;
    }

    private void ClearDeploymentSelection()
    {
        if (selectedDeployUnit != null)
            selectedDeployUnit.SetDeploymentSelected(false);

        selectedDeployUnit = null;
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

        GD.Print("Deployment positions reset.");
    }

}