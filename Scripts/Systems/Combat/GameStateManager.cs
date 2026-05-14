using Godot;
using System;
using System.Collections.Generic;

public sealed class GameState
{
    public EventBus Bus = new();
    public GameStack Stack = new();
    public PriorityManager Priority = new();
    public Resolver Resolver;

    public List<PersistentEffect> ActiveEffects = new();

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
        Mana[PlayerA] = 5; Mana[PlayerB] = 5;
        Priority.ResetForNewStep(PlayerA);
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