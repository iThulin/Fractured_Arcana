using Godot;
using System.Collections.Generic;

public enum CardType { Attack, Skill, Environment, Summon, Reaction }
public enum TargetType { None, SingleEnemy, AllEnemies, Tile, Self, Global }
public enum CardSchool { Generic, Engineer, Necromancer, Enchanter, Elementalist, Arcanist }
public enum Controller { Player, Computer }
public enum Zone { Library, Hand, Grave, Stack, Exile }

public partial class CardData : Node2D
{
    public string CardName { get; set; }
    public string Description { get; set; }
    public string ChannelDescription { get; set; }
    public int ManaCost { get; set; }
    public CardType Type { get; set; }
    public TargetType Target { get; set; }
    public CardSchool School { get; set; }

    // Effect data (if you still want it for some cards)
    public Dictionary<string, float> Effects = new();
}