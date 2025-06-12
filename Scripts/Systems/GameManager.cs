using Godot;
using System;
using System.Collections.Generic;

public partial class GameManager : Node3D
{

    private DeckManager deckManager;

    public override void _Ready()
    {
        deckManager = GetNode<DeckManager>("Player/DeckManager");

        List<CardData> startingDeck = new()
        {
            new CardData
            {
                CardName = "Fireball",
                Description = "Deal 10 fire damage.",
                ManaCost = 3,
                School = CardSchool.Elemental,
                Type = CardType.Attack,
                Target = TargetType.AllEnemies,
                Effects = new Dictionary<string, float>
                {
                    {"damage", 10},
                    {"burnDuration", 2}
                }

            },
            new CardData
            {
                CardName = "Shield",
                Description = "Gain 8 Block.",
                ManaCost = 1,
                School = CardSchool.Abjuration,
                Type = CardType.Skill,
                Target = TargetType.Self,
                Effects = new Dictionary<string, float>
                {
                    {"block", 8},
                }
            },
            new CardData
            {
                CardName = "Reveal Weakness",
                Description = "Reduce enemy defense by 2.",
                ManaCost = 1,
                School = CardSchool.Illusion,
                Type = CardType.Environment,
                Target = TargetType.SingleEnemy,
                Effects = new Dictionary<string, float>
                {
                    {"defenseDown", 2},
                }
            }
        };

        deckManager.InitializeDeck(deckManager.GenerateTestDeck(15));
        deckManager.DrawCards(10);
    }
}
