using Godot;

public partial class GameBootstrap : Node
{
    public override void _Ready()
    {
        if (CardDatabase.Blueprints.Count == 0)
            CardDatabase.LoadFromCsv("res://Data/cards.csv");
    }
}