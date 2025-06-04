using Godot;
using System;
using System.Collections.Generic;

public enum CardType { Attack, Skill, Environment, Summon, Reaction }
public enum TargetType {None, SingleEnemy, AllEnemies, Tile, Self, Global}

public partial class CardData : Node2D
{

    public string CardName { get; set; }
    public string Description { get; set; }
    public int ManaCost { get; set; }
    public CardType Type { get; set; }
    public TargetType Target { get; set; }
    public string School { get; set; }

    // Effect data
    public Dictionary<string, float> Effects = new();
}
