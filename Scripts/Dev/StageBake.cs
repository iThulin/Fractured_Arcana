using Godot;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

// ============================================================
// StageBake.cs   (dev tooling)
//
// Purpose:        Bake Stage Previews (Debug Mode, PROLOGUE; stage
//                 direction SD1, docs/stage_direction_v1.md section 3).
//                 For each prologue hall it builds the battlefield
//                 without its fight (StageField), so the hall is built
//                 exactly as the game builds it; dresses it to its
//                 preview's state; and saves it as a preview in six parts
//                 under Scenes/Prologue/Sets/PREVIEW/ (SetSnapshot), for
//                 the direction scenes to show in the editor. Then it
//                 writes any direction scene that is missing, from its
//                 built-in direction (DefaultDirections), with the
//                 preview's parts instanced as placeholders: the
//                 founding's from the Grand Hall, and the rig shots
//                 (SD3) of the Sundering from the Ceremony Hall whole and
//                 of the hall seen again from the hall stopped, read off
//                 the battlefield's own camera. It never overwrites a
//                 direction scene.
//
//                 Only while the game runs from the editor (Available):
//                 an exported game cannot write into the project. If the
//                 project folder cannot be written all the same, each file
//                 goes under user://stage_bake/ at the same path, and the
//                 list says so.
//
//                 Reads the save (the Grand Hall dresses itself from it;
//                 the Sundering's traitor); writes nothing to it. Its
//                 scene sits under Scenes/Prologue so the top bar keeps
//                 out of it, as it does for the other prologue scenes.
// Layer:          Dev tooling
// Collaborators:  StageField, HallSet, SetSnapshot, DefaultDirections,
//                 StageDirection, SceneTransition, CampusGuildPanel (the
//                 button), PrologueDirector (the way back)
// ============================================================

/// <summary>Bake Stage Previews (see the file header). The root of StageBake.tscn.</summary>
public partial class StageBake : Node3D
{
    public const string ScenePath = "res://Scenes/Prologue/StageBake.tscn";

    public const string CeremonyHallPreview = "ceremony_hall";
    public const string CeremonyHallStoppedPreview = "ceremony_hall_stopped";
    public const string GrandHallPreview = "grand_hall";

    /// <summary>Frames to wait for a grid to build its hall before giving it up.</summary>
    private const int BuildPatienceFrames = 300;
    /// <summary>Frames between dressing a hall and copying it.</summary>
    private const int SettleFrames = 6;
    private const int UiLayer = 10;
    private const string FallbackTraitor = "aurel";

    private enum Dress
    {
        Whole,     // the Ceremony Hall as the Sundering opens: the cast at their places
        Stopped,   // the Ceremony Hall as the Long Second left it
        Founded,   // the Grand Hall as the save has it
    }

    private sealed class Spec
    {
        public string Preview = "";
        public string Recipe = "";
        public bool GrandHall;
        public Dress Dress;
        /// <summary>The direction written from this hall when its scene is missing ("" for none yet).</summary>
        public string Direction = "";
    }

    private static readonly Spec[] Specs =
    {
        new() { Preview = CeremonyHallPreview, Recipe = "ceremony_hall_sundering", Dress = Dress.Whole,
                Direction = CombatManager.SunderingDirectionId },
        new() { Preview = CeremonyHallStoppedPreview, Recipe = "ceremony_hall_sundering", Dress = Dress.Stopped,
                Direction = HallStage.DirectionId },
        new() { Preview = GrandHallPreview, Recipe = "grand_hall", GrandHall = true, Dress = Dress.Founded,
                Direction = StageDirection.FoundingId },
    };

    private Label _list;
    private Button _back;
    private readonly List<string> _lines = new();
    private bool _leaving;

    /// <summary>True when the game runs from the editor, the only place a bake can write into the project.</summary>
    public static bool Available => OS.HasFeature("editor");

    /// <summary>Into the bake, behind the curtain. Needs a loaded guild: the Grand Hall dresses itself from it.</summary>
    public static void Begin(SceneTree tree)
    {
        if (tree == null || !Available || SceneTransition.IsTransitioning)
            return;
        if (SaveManager.ActiveSave?.Ledger == null)
        {
            GD.PrintErr("[StageBake] Load a guild first: the Grand Hall dresses itself from the save.");
            return;
        }
        SceneTransition.Go(tree, ScenePath, "Stage previews", "The halls, built as the game builds them.");
    }

    public override void _Ready()
    {
        CallDeferred(nameof(BuildUi));
        CallDeferred(nameof(StartBake));   // after the panel, so every line has somewhere to go
    }

    // ════════════════════════════════════════════════════════════════════════
    //  The bake
    // ════════════════════════════════════════════════════════════════════════

    private void StartBake() => Run();

    private async void Run()
    {
        var save = SaveManager.ActiveSave;
        if (save?.Ledger == null)
        {
            Line("No guild is loaded, so there is nothing to bake.");
            Done();
            return;
        }
        int files = 0, failures = 0;
        foreach (var spec in Specs)
        {
            Line($"{spec.Preview}: building {spec.Recipe}.");
            var field = StageField.Build(this, spec.Recipe, 1, "StageBake");
            if (field == null)
            {
                Line("  The battlefield did not build.");
                failures++;
                continue;
            }
            if (field.Camera != null && IsInstanceValid(field.Camera))
                field.Camera.AcceptInput = false;
            HallSet hall = null;
            for (int frame = 0; frame < BuildPatienceFrames && hall == null; frame++)
            {
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                if (_leaving)
                    return;
                hall = field.FindHall(h => spec.GrandHall ? h.IsGrandHall : h.IsCeremonyHall);
            }
            if (hall == null)
            {
                Line($"  The grid built no hall in {BuildPatienceFrames} frames.");
                failures++;
                Drop(field);
                await Frames(2);
                continue;
            }
            DressHall(spec, hall, field, save);
            await Frames(SettleFrames);
            if (_leaving)
                return;
            hall.ShowWholeSet();
            var written = SetSnapshot.SavePreview(field.Root, hall, spec.Preview, out var errors);
            files += written.Count;
            failures += errors.Count;
            foreach (var path in written)
                Line("  " + Where(path));
            foreach (var error in errors)
                Line("  Not written: " + error);
            if (spec.Direction.Length > 0)
            {
                if (SeedDirection(spec, hall, field))
                    files++;
            }
            Drop(field);
            await Frames(2);
            if (_leaving)
                return;
        }
        Line(failures == 0
            ? $"Done: {files} file(s). Open the direction scenes in Scenes/Prologue/Direction in the editor."
            : $"Done, with {failures} problem(s) above: {files} file(s) written.");
        Done();
    }

    /// <summary>The hall as its preview shows it. The Grand Hall already stands as the save has it.</summary>
    private static void DressHall(Spec spec, HallSet hall, StageField field, GuildSaveData save)
    {
        if (spec.Dress == Dress.Founded)
            return;
        Vector3 board = (field.Grid.GridBoundsMin + field.Grid.GridBoundsMax) * 0.5f;
        if (spec.Dress == Dress.Stopped)
            foreach (var piece in HallSet.Pieces)
                hall.BreakPiece(piece, instant: true);
        var cast = hall.StageSundering(Traitor(save), board);
        if (spec.Dress != Dress.Stopped)
            return;
        // As the hall seen again sets it down (HallStage.Build), with no one spent.
        hall.CatchSphere(0f, settled: true);
        hall.DropFolio(0f, settled: true);
        hall.TakeRegister();
        if (cast.Traitor != null)
            hall.HangSpell();
    }

    /// <summary>The save's traitor: the tableau's, else the pinned co-conspirator, else the campaign's; else
    /// FallbackTraitor, so the dais always shows one.</summary>
    private static string Traitor(GuildSaveData save)
    {
        foreach (var id in new[] { save?.Ledger?.Tableau?.TraitorId, save?.Ledger?.CoConspiratorId, save?.Cycle?.Campaign?.CoConspirator })
            if (!string.IsNullOrEmpty(id) && Array.IndexOf(CampaignGenerator.TraitorPool, id) >= 0)
                return id;
        return FallbackTraitor;
    }

    /// <summary>Writes the direction scene <paramref name="spec"/> names from its built-in direction, when it is
    /// missing. True when a file was written.</summary>
    private bool SeedDirection(Spec spec, HallSet hall, StageField field)
    {
        string path = StageDirection.ScenePath(spec.Direction);
        if (FileAccess.FileExists(path))
        {
            Line($"  {path} is there already, so it is left as it is.");
            return false;
        }
        var camera = field.Camera != null && IsInstanceValid(field.Camera) ? field.Camera : null;
        Node3D root = spec.Direction switch
        {
            StageDirection.FoundingId => DefaultDirections.Founding(hall, spec.Preview),
            CombatManager.SunderingDirectionId when camera != null =>
                DefaultDirections.SunderingShots(hall, field.Grid, camera, CandidateStart(field.Grid), spec.Preview),
            HallStage.DirectionId when camera != null =>
                DefaultDirections.HallStageShots(hall, field.Grid, camera, spec.Preview),
            _ => null,
        };
        if (root == null)
        {
            Line($"  Not written: {path} (the battlefield has no camera to read its shots from).");
            return false;
        }
        string where = SetSnapshot.SaveScene(root, path, false, out string error);
        root.Free();
        if (where.Length == 0)
        {
            Line("  Not written: " + error);
            return false;
        }
        Line($"  {Where(where)} (the direction, written from the built-in one)");
        return true;
    }

    /// <summary>Where the Sundering's candidate stands as the fight opens: the player's spawn anchor, the first
    /// slot, which the wizard takes. The board's west quarter when the grid planned no player zone.</summary>
    private static Vector3 CandidateStart(HexGridManager grid)
    {
        foreach (var zone in grid.SpawnZones)
            if (zone.Side == HexGridManager.SpawnSide.Player && grid.GetTileView(zone.Anchor) is Node3D tile)
                return tile.GlobalPosition;
        Vector3 min = grid.GridBoundsMin;
        Vector3 max = grid.GridBoundsMax;
        return new Vector3(Mathf.Lerp(min.X, max.X, 0.25f), max.Y, (min.Z + max.Z) * 0.5f);
    }

    private static void Drop(StageField field)
    {
        if (field?.Root != null && IsInstanceValid(field.Root))
            field.Root.QueueFree();
    }

    private async Task Frames(int count)
    {
        for (int i = 0; i < count; i++)
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    /// <summary>A written path, with a note when it went to the fallback outside the project.</summary>
    private static string Where(string path) =>
        path.StartsWith("user://", StringComparison.Ordinal)
            ? $"{path} (outside the project: copy it to the same path under res://)"
            : path;

    // ════════════════════════════════════════════════════════════════════════
    //  The panel, and the way back
    // ════════════════════════════════════════════════════════════════════════

    private void BuildUi()
    {
        var layer = new CanvasLayer { Name = "StageBakeUi", Layer = UiLayer };
        AddChild(layer);
        var panel = new PanelContainer { Name = "Panel", MouseFilter = Control.MouseFilterEnum.Stop };
        panel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.Center);
        panel.CustomMinimumSize = new Vector2(980, 560);
        panel.GrowHorizontal = Control.GrowDirection.Both;
        panel.GrowVertical = Control.GrowDirection.Both;
        var style = UITheme.MakePanelStyle(UITheme.BgBase, UITheme.Neutral);
        style.SetContentMarginAll(UITheme.PaddingLarge);
        panel.AddThemeStyleboxOverride("panel", style);
        layer.AddChild(panel);

        var column = new VBoxContainer { Name = "Column" };
        column.AddThemeConstantOverride("separation", UITheme.PaddingNormal);
        panel.AddChild(column);

        var title = new Label { Name = "Title", Text = "Stage previews" };
        title.AddThemeColorOverride("font_color", UITheme.Gold);
        title.AddThemeFontSizeOverride("font_size", UITheme.FontSizeLarge);
        column.AddChild(title);

        var scroll = new ScrollContainer { Name = "Scroll", SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        column.AddChild(scroll);
        _list = new Label
        {
            Name = "List",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        _list.AddThemeColorOverride("font_color", UITheme.TextPrimary);
        _list.AddThemeFontSizeOverride("font_size", UITheme.FontSizeSmall);
        scroll.AddChild(_list);

        _back = new Button { Name = "Back", Text = "Back to the city", Disabled = true };
        UITheme.ApplyButtonStyle(_back);
        _back.Pressed += Back;
        column.AddChild(_back);
        Refresh();
    }

    private void Line(string text)
    {
        _lines.Add(text);
        GD.Print("[StageBake] " + text);
        Refresh();
    }

    private void Refresh()
    {
        if (_list != null && IsInstanceValid(_list))
            _list.Text = string.Join("\n", _lines);
    }

    private void Done()
    {
        if (_back != null && IsInstanceValid(_back))
            _back.Disabled = false;
    }

    private void Back()
    {
        if (_leaving)
            return;
        _leaving = true;
        PlayerSession.StartInCityOnOpen = true;
        SceneTransition.Go(GetTree(), PrologueDirector.StrategicScenePath, "Back to the city");
    }
}
