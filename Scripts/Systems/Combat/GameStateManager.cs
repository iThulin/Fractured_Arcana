using Godot;
using System;
using System.Collections.Generic;

public sealed class GameState {
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
    public Unit ActiveCasterUnit;

    public Entity PlayerA = new(){ Name="A"};
    public Entity PlayerB = new(){ Name="B"};
    public TargetSet RetargetOrigin;

    public Dictionary<Entity,int> Mana = new();
    public List<Card> LibraryA = new(), LibraryB = new();
    public List<Card> HandA = new(), HandB = new();
    public List<Card> Graveyard = new();


    /// <summary>
    /// Cleanup actions to run at end of turn (remove movement callbacks, etc.)
    /// </summary>
    public List<Action> OnTurnEndCleanups;

    public GameState(){
        Resolver = new Resolver(Bus, Stack);
        Mana[PlayerA] = 5; Mana[PlayerB] = 5;
        Priority.ResetForNewStep(PlayerA);
    }

    public void OpenPriorityWindow(){ Bus.Emit("PriorityOpened"); }

    public void AdvanceStep(){
        Step = Step=="Main" ? "End" : "Main";
        Log($"== Step → {Step} ==");
        Priority.ResetForNewStep(PlayerA);
        OpenPriorityWindow();
    }

    public int StackCount(){ int n=0; foreach(var _ in Stack.Items) n++; return n; }

    public void MoveCardToGraveyard(Entity who, Card card){
        // remove from hands
        HandA.Remove(card); HandB.Remove(card);
        Graveyard.Add(card);
        Log($"Card → Graveyard: {card.CardName}");
    }

    public void Draw(Entity who, int count){
        var lib = (who == PlayerA) ? LibraryA : LibraryB;
        var hand= (who == PlayerA) ? HandA : HandB;
        for (int i=0;i<count && lib.Count>0;i++){ var c=lib[0]; lib.RemoveAt(0); hand.Add(c); }
    }

    public void Log(string msg){ GD.Print(msg); } // replace with your UI log if you want

    public bool HasActiveEffect<T>(Entity owner) where T : PersistentEffect
    {
        return ActiveEffects?.Exists(e => e is T && e.Owner == owner && !e.IsExpired) ?? false;
    }

    public T GetActiveEffect<T>(Entity owner) where T : PersistentEffect
    {
        return ActiveEffects?.Find(e => e is T && e.Owner == owner && !e.IsExpired) as T;
    }
}