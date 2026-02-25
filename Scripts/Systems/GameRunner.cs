using Godot;
using System.Collections.Generic;

public partial class GameRunner : Node
{
    [Export] public PackedScene PlayerUnitScene;
    [Export] public PackedScene DummyUnitScene;
    [Export] public NodePath GridPath = "../HexGridManager"; // adjust to your scene tree
    public GameState State;
    private Entity Me, Opp;
    private List<Card> _compiled = new();
    private DeckManager deckManager;
    private CardDropHandler dropper;
    private HexGridManager grid;
    private Unit playerUnit;
    private Unit dummyUnit;
    

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
        State.OpenPriorityWindow();

        State.Mana[Me] = 1;
        GD.Print("Keys: [T]=cast top of card 1 | [B]=bottom | [Y]=channel top | [SPACE]=pass | [R]=resolve top");
    }
    private void OnCardDroppedOnTile(CardUi cardUi, bool isTop, HexTile tile)
    {
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
            //playerUnit.UpdateManaUI(); // or your label/bar update method
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

    private void SpawnTestUnits()
    {
    grid = GetNodeOrNull<HexGridManager>(GridPath);
    if (grid == null)
    {
        GD.PrintErr($"HexGridManager not found at GridPath: {GridPath}");
        return;
    }

    // Pick two tiles (change coords to fit your grid size)
    var playerTile = grid.GetTile(new Vector2I(1, 1));
    var dummyTile  = grid.GetTile(new Vector2I(4, 2));

    if (playerTile == null || dummyTile == null)
    {
        GD.PrintErr("Could not find spawn tiles. Check axial coords and GridWidth/GridHeight.");
        return;
    }

    if (PlayerUnitScene == null || DummyUnitScene == null)
    {
        GD.PrintErr("Assign PlayerUnitScene and DummyUnitScene in the Inspector.");
        return;
    }

    // Spawn player
    playerUnit = PlayerUnitScene.Instantiate<Unit>();
    AddChild(playerUnit);
    playerUnit.IsPlayerControlled = true;
    playerUnit.TeamId = 0;

    // Optionally set stats here (or via inspector Start* fields)
    playerUnit.StartMaxHealth = 20;
    playerUnit.StartHealth = 20;
    playerUnit.StartBaseSpeed = 3;
    playerUnit.StartMaxMana = 0;
    playerUnit.StartMana = 0;

    // Force stats to apply immediately if you need it before _Ready() runs
    // (usually not required, but safe)
    // playerUnit._Ready();

    playerUnit.PlaceOnTile(playerTile);

    // Spawn dummy
    dummyUnit = DummyUnitScene.Instantiate<Unit>();
    AddChild(dummyUnit);
    dummyUnit.IsPlayerControlled = false;
    dummyUnit.TeamId = 1;

    dummyUnit.StartMaxHealth = 50;
    dummyUnit.StartHealth = 50;
    dummyUnit.StartArmor = 0;
    dummyUnit.StartShield = 0;
    dummyUnit.StartBaseSpeed = 0;
    dummyUnit.StartMaxMana = 0;
    dummyUnit.StartMana = 0;

    dummyUnit.PlaceOnTile(dummyTile);

    GD.Print($"Spawned Player at {playerTile.Axial}, Dummy at {dummyTile.Axial}");

    // Register into rules state (optional but recommended)
    State.Grid = grid;
    State.PlayerUnit = playerUnit;
    State.EnemyUnit = dummyUnit;
    State.UnitsInPlay.Clear();
    State.UnitsInPlay.Add(playerUnit);
    State.UnitsInPlay.Add(dummyUnit);

    playerUnit.Name = "Player";
    dummyUnit.Name = "Dummy";   
    }
}