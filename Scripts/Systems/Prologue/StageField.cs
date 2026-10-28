using Godot;
using System;

// ============================================================
// StageField.cs
//
// Purpose:        The fight's own battlefield scene without the fight,
//                 for a scene that shows a hall exactly as a fight builds
//                 it: the Ceremony Hall seen again (HallStage, S3) and the
//                 founding in the Grand Hall (FoundingStage, S4).
//
//                 The fight's nodes (the fight itself, its HUD, the hand
//                 and the cards) are freed before the scene enters the
//                 tree, so none of them ever runs. The grid builds the
//                 recipe it is given, and the hall round it, on its own a
//                 frame after it enters (HexGridManager._Ready, then
//                 AutoGenerateIfEmpty); the camera, the light and the
//                 environment stay. The two unit scenes the fight spawns
//                 from are kept for a stage's figures. Verified for S3:
//                 the camera looks for the card handler without assuming
//                 it, and the movement-zone and canopy renderers do
//                 nothing without a fight.
// Layer:          Prologue / presentation
// Collaborators:  HallStage, FoundingStage, Battlefield.tscn,
//                 HexGridManager, CameraController, CombatManager (its
//                 unit scenes), HallSet
// ============================================================

/// <summary>A battlefield without its fight, for a stage (see the file header).</summary>
public sealed class StageField
{
    public const string ScenePath = "res://Scenes/Combat/Battlefield.tscn";

    /// <summary>The fight's nodes, taken out of its scene before it enters the tree: the fight itself,
    /// its HUD, the hand and the cards.</summary>
    private static readonly string[] FightNodes = { "GameRunner", "CombatUI", "DeckUI", "Player", "CardData", "CardDropHandler" };

    public HexGridManager Grid { get; private set; }
    public CameraController Camera { get; private set; }
    /// <summary>The scene the fight spawns Keepers and most summons from.</summary>
    public PackedScene DummyUnitScene { get; private set; }
    /// <summary>The scene the fight spawns the candidate and wildlife from.</summary>
    public PackedScene PlayerUnitScene { get; private set; }

    /// <summary>Builds the field under <paramref name="parent"/>, its grid set to build
    /// <paramref name="recipe"/> with <paramref name="seed"/>. Null, with an error in the log under
    /// <paramref name="who"/>, when the scene does not load or has no grid.</summary>
    public static StageField Build(Node parent, string recipe, int seed, string who)
    {
        var packed = GD.Load<PackedScene>(ScenePath);
        var root = packed?.Instantiate<Node3D>();
        if (root == null)
        {
            GD.PushError($"[{who}] {ScenePath} does not load: back to the city.");
            return null;
        }
        var field = new StageField();
        if (root.GetNodeOrNull("GameRunner") is CombatManager fight)
        {
            field.DummyUnitScene = fight.DummyUnitScene;
            field.PlayerUnitScene = fight.PlayerUnitScene;
        }
        foreach (var name in FightNodes)
        {
            var node = root.GetNodeOrNull(name);
            if (node == null)
                continue;
            root.RemoveChild(node);
            node.Free();
        }
        field.Grid = root.GetNodeOrNull<HexGridManager>("HexGridManager");
        field.Camera = root.GetNodeOrNull<CameraController>("CameraController");
        if (field.Grid == null)
        {
            GD.PushError($"[{who}] The battlefield has no HexGridManager: back to the city.");
            root.Free();
            return null;
        }
        field.Grid.MapRecipeId = recipe ?? "";
        field.Grid.MapSeed = seed;
        parent.AddChild(root);   // the grid builds itself once the frame settles (HexGridManager._Ready)
        return field;
    }

    /// <summary>The hall round the board once the grid has built it and <paramref name="wanted"/> says
    /// it is the one the stage is waiting for; null until then.</summary>
    public HallSet FindHall(Func<HallSet, bool> wanted)
    {
        if (Grid == null || !GodotObject.IsInstanceValid(Grid) || Grid.Tiles.Count == 0 || wanted == null)
            return null;
        foreach (Node child in Grid.GetChildren())
            if (child is HallSet hall && !hall.IsQueuedForDeletion() && wanted(hall))
                return hall;
        return null;
    }
}
