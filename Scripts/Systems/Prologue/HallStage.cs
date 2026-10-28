using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

// ============================================================
// HallStage.cs
//
// Purpose:        The Ceremony Hall, seen again (S3,
//                 docs/sundering_long_second_founding_v1.md section 5):
//                 the fight the Long Second stopped, exactly as it
//                 stopped, every time the player goes into the hall,
//                 until the Fracture moves it again.
//
//                 It is the fight's own scene (Battlefield.tscn) with the
//                 fight taken out before it enters the tree: its grid
//                 builds the Sundering's map and the hall round it, its
//                 camera frames them, and this stands everything where it
//                 stopped. From the save's tableau: every Keeper and
//                 summon on its tile, turned the way it was going, and the
//                 floor as the fight left it, its ground (heights, rubble,
//                 obstacles, scars) and its marks (elements, reactions,
//                 growth, memorials, glyphs). From canon and the fight's
//                 fixed beats: every break in the hall, the archmagi at
//                 their chairs (the spent ones down on one knee), the
//                 traitor at the sphere,
//                 the Provost at the lectern with her last words in the
//                 air, the Examiner, the class down behind the benches,
//                 the sphere in its six shards (the fragments brought home
//                 warm), the folio at knee height, the Register gone, the
//                 spell an arm's length from the empty mark, and a few
//                 stray spells caught in the air. Then everything stops.
//
//                 The hall's own panel (the chronicle's on_hall) shows
//                 over it, as it showed over the city, and its choice is
//                 applied as the city's are (NarrativeOutcome). The camera
//                 is the fight's, bounded to the hall, free to turn.
//                 Leave goes back to the city.
// Layer:          Prologue / presentation
// Collaborators:  LongSecondTableau (the save), HallSet (.Sundering: the
//                 cast, the breaks, the settled sphere and folio, the
//                 spell), StageField (the battlefield without its
//                 fight), HexGridManager, Unit, SpeechBubble,
//                 CampusLandmarkRegistry (the hall's panel),
//                 NarrativeOutcome, NarrativeEncounterPanel,
//                 SceneTransition, PrologueDirector, StrategicView and
//                 CampusScreen (the way in)
// ============================================================

/// <summary>The frozen Ceremony Hall (see the file header). The root of HallStage.tscn.</summary>
public partial class HallStage : Node3D
{
    public const string ScenePath = "res://Scenes/Prologue/HallStage.tscn";
    private const string DefaultRecipe = "ceremony_hall_sundering";
    private const string ClassId = "class";
    private const string ExaminerId = "examiner";

    /// <summary>Frames to wait for the grid to build the hall before giving up.</summary>
    private const int BuildPatienceFrames = 300;
    /// <summary>Frames between standing everything and stopping it, so the dust settles into its
    /// shafts first.</summary>
    private const int SettleFrames = 3;
    /// <summary>Seconds of the still hall before its panel shows.</summary>
    private const float PanelDelay = 1.2f;
    private const float FrameZoom = 11f;
    private const int StrayCount = 7;
    private const float StrayTrail = 1.4f;
    private const float SpeakerHead = 2.1f;
    private const int UiLayer = 10;
    private const int SpeechLayer = 91;
    private const int PanelLayer = 95;
    /// <summary>How far above the bottom of the screen the toasts stack: clear of the way out.</summary>
    private const int ToastClearance = 56;

    // A spirit's look in the fight (Unit.ApplySpiritAppearance) when the record it claims is not an
    // ally's, or there is none: the cool one. In the Sundering the dead are Keepers, so it wore this.
    // The stage claims no record, so it never takes one from the honored dead.
    private static readonly Color GhostAlbedo = new(0.72f, 0.88f, 1.0f, 0.45f);
    private static readonly Color GhostGlow = new(0.55f, 0.78f, 1.0f);

    private StageField _field;
    private HexGridManager _grid;
    private CameraController _camera;
    private PackedScene _unitScene;
    private PackedScene _wildScene;
    private HallSet _hall;
    private HallSet.SunderingCast _cast;
    private LongSecondTableau _tableau;
    private SunderingVolleySet _words;
    private readonly List<Unit> _figures = new();
    /// <summary>The tableau's floor: each row with its tile (ReadTheFloor).</summary>
    private readonly List<(TileData tile, TableauTileRow row)> _floor = new();
    private readonly HashSet<string> _spent = new();
    private int _waited;
    private bool _built;
    private int _settle = -1;
    private bool _leaving;
    private CanvasLayer _panelLayer;
    private NarrativeEncounterPanel _panel;
    private Button _leave;
    private Label _caption;
    private ToastManager _toasts;
    private SpeechBubble _bubble;

    // ════════════════════════════════════════════════════════════════════════
    //  The way in
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>The hall shows the frozen fight: always, until the Fracture moves it again (the
    /// finale's second phase, in this reading).</summary>
    public static bool Holds(GuildSaveData save)
    {
        if (save?.Ledger == null)
            return false;
        var finale = save.Cycle?.Convergence;
        return finale == null || finale.Phase < 2;
    }

    /// <summary>Into the hall, behind the curtain.</summary>
    public static void Enter(SceneTree tree)
    {
        var words = Words();
        SceneTransition.Go(tree, ScenePath, CombatManager.SunderingFill(words?.StageTitle),
                           CombatManager.SunderingFill(words?.StageSubtitle));
    }

    /// <summary>The Sundering's words (its volley set), where the stage's lines live too.</summary>
    private static SunderingVolleySet Words() =>
        SunderingScript.Get(PrologueDirector.ScriptFor(PrologueDirector.SunderingFightId)?.Volleys);

    // ════════════════════════════════════════════════════════════════════════
    //  The scene
    // ════════════════════════════════════════════════════════════════════════

    public override void _Ready()
    {
        var save = SaveManager.ActiveSave;
        _words = Words();
        if (save?.Ledger == null)
        {
            GD.PushWarning("[HallStage] No save is loaded: back to the city.");
            CallDeferred(nameof(Leave));
            return;
        }
        LongSecondTableau.EnsureDefault(save);   // a save from before S2, or a full skip, gets the authored floor
        _tableau = save.Ledger.Tableau;
        if (_tableau?.SpentFaculty != null)
            foreach (var id in _tableau.SpentFaculty)
                _spent.Add(id ?? "");
        if (!BuildField())
        {
            CallDeferred(nameof(Leave));
            return;
        }
        BuildUi();
    }

    /// <summary>The fight's scene without the fight (StageField): the grid (set to the tableau's map),
    /// the hall it builds, the camera, the light and the environment. The two unit scenes the fight
    /// spawns from are kept for the frozen ones: the dummy's (Keepers and most summons) and the
    /// player's (wildlife).</summary>
    private bool BuildField()
    {
        // The recipe is authored throughout: any seed builds the same hall.
        string recipe = !string.IsNullOrEmpty(_tableau?.MapRecipe) ? _tableau.MapRecipe : DefaultRecipe;
        _field = StageField.Build(this, recipe, 1, "HallStage");
        if (_field == null)
            return false;
        _grid = _field.Grid;
        _camera = _field.Camera;
        _unitScene = _field.DummyUnitScene;
        _wildScene = _field.PlayerUnitScene;
        return true;
    }

    public override void _Process(double delta)
    {
        if (_leaving)
            return;
        if (!_built)
        {
            var hall = FindHall();
            if (hall != null)
            {
                Build(hall);
                return;
            }
            if (++_waited > BuildPatienceFrames)
            {
                GD.PushError($"[HallStage] The grid built no Ceremony Hall in {BuildPatienceFrames} frames: back to the city.");
                Leave();
            }
            return;
        }
        if (_settle > 0 && --_settle == 0)
            Settle();
    }

    /// <summary>The Ceremony Hall round the board, once the grid has built it.</summary>
    private HallSet FindHall() => _field?.FindHall(hall => hall.IsCeremonyHall);

    // ════════════════════════════════════════════════════════════════════════
    //  The frozen fight
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>Stands everything where the Long Second stopped it (see the file header).</summary>
    private void Build(HallSet hall)
    {
        _built = true;
        _hall = hall;
        var save = SaveManager.ActiveSave;
        Vector3 board = (_grid.GridBoundsMin + _grid.GridBoundsMax) * 0.5f;

        foreach (var piece in HallSet.Pieces)
            hall.BreakPiece(piece, instant: true);

        _cast = hall.StageSundering(_tableau?.TraitorId ?? "", board);
        foreach (var id in _spent)
        {
            if (id == ClassId)
            {
                foreach (var member in _cast.Class)
                    HallSet.KneelNow(member, HallSet.ClassDrop);
            }
            else if (_cast.Archmagi.TryGetValue(id, out var archmage))
            {
                HallSet.KneelNow(archmage);
            }
        }

        hall.CatchSphere(0f, settled: true);
        for (int i = 0; i < ShardZones.FragmentKeys.Length; i++)
            if (save != null && save.HasFlag($"fragment_{ShardZones.FragmentKeys[i]}_placed"))
                hall.WarmShard(i);
        hall.DropFolio(0f, settled: true);
        hall.TakeRegister();
        if (_cast.Traitor != null)
            hall.HangSpell();   // the spell is the traitor's: with no traitor there is none

        // The floor in three passes. Its ground first, so every figure stands at its tile's height
        // and none where an obstacle is. Then the figures, on tiles with no marks yet: a tile's entry
        // reactions (Unit.PlaceOnTile) would sear a Keeper standing in fire or set off a glyph under
        // one. Then the marks, as plain writes that never touch whoever stands there. The last fight's
        // conduit links stay listed until the next fight clears them as it starts; their units are
        // gone, but one would still zap a figure set down on its old line, so they go first. The hall
        // is no fight. An earlier scene's stamps in the ground's element field would show on this
        // floor, so the field is cleared before the marks: only the tableau's belong here.
        ReadTheFloor();
        ConduitLinkSystem.Clear();
        LayTheGround();
        StandTheFigures();
        ImbuementField.Clear();
        LayTheMarks();
        CatchTheStrays();
        FrameTheHall();
        SayTheLastWords();
        _settle = SettleFrames;
        GD.Print($"[HallStage] The hall, as it stopped: {_figures.Count} figure(s) on the floor, "
                 + $"{_floor.Count} tile(s) of floor ({_floor.Count(f => f.row.Full)} with their ground), "
                 + $"spent [{string.Join(", ", _spent)}].");
    }

    /// <summary>Everything stops: the hall's set, and every figure on the floor. Then its panel.</summary>
    private void Settle()
    {
        _hall?.Still();
        foreach (var unit in _figures)
            if (unit != null && IsInstanceValid(unit))
                unit.ProcessMode = ProcessModeEnum.Disabled;
        GetTree().CreateTimer(PanelDelay, processAlways: false).Timeout += ShowHallPanel;   // waits out a pause
    }

    /// <summary>The tableau's floor rows, each with its tile, read once (LongSecondTableau.TryParseTile).
    /// A row whose tile is not on this board is passed over.</summary>
    private void ReadTheFloor()
    {
        _floor.Clear();
        if (_tableau?.Tiles == null)
            return;
        foreach (var text in _tableau.Tiles)
        {
            if (!LongSecondTableau.TryParseTile(text, out var row))
                continue;
            var tile = _grid.GetTile(row.At);
            if (tile != null)
                _floor.Add((tile, row));
        }
    }

    /// <summary>The ground as the fight left it, from each row that carries it (an older row's ground
    /// is the map's): the height, raised as a card raises a tile; the terrain, its rubble and its cost;
    /// the hazard; an obstacle with its health (a Glacier's ice); the scar; and what growth and the dead
    /// left in the soil.</summary>
    private void LayTheGround()
    {
        bool obstacles = false;
        foreach (var (tile, row) in _floor)
        {
            if (!row.Full)
                continue;
            if (tile.Height != row.Height)
            {
                tile.Height = row.Height;
                tile.TileView?.SetHeight(row.Height);
            }
            if (Enum.TryParse<TileTerrainType>(row.Terrain, true, out var terrain))
                tile.TerrainType = terrain;
            tile.TerrainModifier = row.Modifier;
            tile.MoveCost = row.MoveCost;
            tile.BaseMoveCost = row.BaseMoveCost;
            tile.IsHazardous = row.Hazardous;
            if (tile.IsBlocked || (tile.ObstacleKind ?? "").Length > 0 || row.Blocked || row.Obstacle.Length > 0)
                obstacles = true;
            tile.IsWalkable = row.Walkable;
            tile.IsBlocked = row.Blocked;
            tile.BlocksLineOfSight = row.BlocksSight;
            tile.AuthoredCover = Enum.TryParse<CoverKind>(row.Cover, true, out var cover) ? cover : CoverKind.None;
            tile.ObstacleKind = row.Obstacle;
            tile.ObstacleHp = row.ObstacleHp;
            tile.CarcassTicks = row.Carcass;
            tile.SummonDiscount = row.Discount;
            tile.SummonDiscountTurns = row.DiscountTurns;
            tile.TileView?.SetTerrainScar(row.Scar);
            _grid.ApplyVisualToTile(tile);
        }
        if (obstacles)
            _grid.RefreshObstacleVisuals();
    }

    /// <summary>The marks as the fight left them: elements at their strength, reactions with their
    /// rounds and side, growth with its age, memorials in their state (where something fell), and
    /// glyphs in their own sigils. An older row's details are the ones the fight starts a mark with.</summary>
    private void LayTheMarks()
    {
        foreach (var (tile, row) in _floor)
        {
            if (Enum.TryParse<TileElementType>(row.Element, true, out var kind) && kind != TileElementType.None)
                ElementReactions.WriteRaw(tile, kind, row.Full ? row.Strength : 1f);
            if (Enum.TryParse<ElementReaction>(row.Reaction, true, out var marked) && marked != ElementReaction.None)
                MarkReaction(tile, marked, row);
            if (row.Growth > 0)
            {
                tile.GrowthStage = row.Growth;
                tile.GrowthAge = row.GrowthAge;
                tile.TileView?.SetGrowth(row.Growth);
            }
            if (row.Memorial.Length > 0)
            {
                tile.Memorial = MemorialOf(row);
                tile.TileView?.SetMemorial(tile.Memorial);
            }
            if (row.Glyph.Length > 0)
            {
                tile.Glyph = GlyphOf(row);
                tile.TileView?.ShowGlyph(tile.Glyph);
            }
            if (row.Full)
                tile.IsHazardous = row.Hazardous;   // an element's write works the hazard out again; the fight's stands
        }
    }

    /// <summary>A reaction's mark on a tile, as ElementReactions leaves it, without its effects. A full
    /// row brings its rounds, its side and its scar (laid with the ground); an older row has the ones
    /// the reaction starts with.</summary>
    private static void MarkReaction(TileData tile, ElementReaction reaction, TableauTileRow row)
    {
        tile.Reaction = reaction;
        tile.ReactionRounds = row.Full ? row.ReactionRounds : reaction == ElementReaction.Fulgurite ? -1 : 1;
        tile.ReactionTeam = row.Full ? row.ReactionTeam : -1;
        if (reaction == ElementReaction.Magma)
            tile.TileView?.SetElement(TileElementType.Fire);   // reads as molten
        if (reaction == ElementReaction.Fulgurite && !row.Full)
            tile.TileView?.SetTerrainScar("rubble");
        tile.TileView?.SetReaction(reaction, tile.ReactionRounds);
    }

    /// <summary>A memorial as the fight left it: its name, side and strength, and from a full row its
    /// state (Fresh, Established, Hallowed), its source and its age.</summary>
    private static MemorialData MemorialOf(TableauTileRow row)
    {
        var strength = Enum.TryParse<MemorialStrength>(row.MemorialStrength, true, out var s) ? s : MemorialStrength.Faint;
        var memorial = new MemorialData(row.Memorial, row.MemorialAlly, strength, row.MemorialTeam);
        if (row.Full)
        {
            if (Enum.TryParse<MemorialState>(row.MemorialState, true, out var state))
                memorial.State = state;
            memorial.SourceTeamId = row.MemorialSourceTeam;
            memorial.TurnsAlive = row.MemorialTurns;
        }
        return memorial;
    }

    /// <summary>A glyph as it shows: its name, and from a full row its card (so its own sigil is
    /// drawn, not the plain marker), its side and its trigger (which set its colors).</summary>
    private static GlyphData GlyphOf(TableauTileRow row)
    {
        var glyph = new GlyphData
        {
            SourceName = row.Glyph,
            SourceCardId = row.GlyphCard,
            SourceHalf = row.GlyphHalf,
            OwnerTeam = row.GlyphTeam,
            ReflectCharges = row.GlyphReflect,
        };
        if (Enum.TryParse<GlyphTrigger>(row.GlyphTrigger, true, out var trigger))
            glyph.Trigger = trigger;
        return glyph;
    }

    /// <summary>Every unit the tableau keeps, on its tile, turned the way it was going, in the colors the
    /// fight gave it (BodyOf; a spirit as the fight's ghost), with no plate: the hall is a picture, not a
    /// fight.</summary>
    private void StandTheFigures()
    {
        if (_tableau?.Figures == null || _unitScene == null)
            return;
        foreach (var f in _tableau.Figures)
        {
            if (f == null)
                continue;
            var tile = _grid.GetTile(new Vector2I(f.Q, f.R));
            if (tile?.TileView == null || tile.IsOccupied)
                continue;
            var def = UnitRegistry.TryResolveId(f.UnitId ?? "", out var id) ? UnitRegistry.Get(id) : null;
            // As the fight builds them: a registry unit from the dummy's scene, wildlife (the bestiary's,
            // by its kind) from the player's, any other summon from the dummy's.
            bool wild = def == null && _wildScene != null && Bestiary.TryGet(f.UnitId ?? "", out _);
            var unit = (wild ? _wildScene : _unitScene).Instantiate<Unit>();
            int max = Math.Max(1, f.MaxHealth);
            unit.IsPlayerControlled = f.Team == 0;
            unit.TeamId = f.Team;
            unit.StartMaxHealth = max;
            unit.StartHealth = Math.Clamp(f.Health > 0 ? f.Health : max, 1, max);
            unit.Name = def != null && !string.IsNullOrEmpty(def.ThreatLabel) ? def.ThreatLabel : SummonName(f.UnitId);
            unit.DisplayName = unit.Name;
            unit.DefinitionId = def?.Id ?? (f.UnitId ?? "");
            AddChild(unit);
            unit.PlaceOnTile(tile);
            unit.Rotation = new Vector3(0f, f.Facing, 0f);
            if (f.Kind == LongSecondTableau.KindSpirit)
                Ghost(unit);
            else
                unit.SetBodyColor(BodyOf(def, f));
            // Its plate never shows: the bar is stopped while its plate is still hidden (the unit keeps
            // its reference to it, so it is not freed).
            var bar = unit.GetNodeOrNull<Node>("HealthBarRoot");
            if (bar != null)
                bar.ProcessMode = ProcessModeEnum.Disabled;
            _figures.Add(unit);
        }
    }

    /// <summary>A summon's name as the summon handler gives it, which a unit with a name label shows:
    /// its kind with spaces, capitalized ("Boar", "Dire wolf").</summary>
    private static string SummonName(string id)
    {
        string name = (id ?? "").Replace('_', ' ').Trim();
        return name.Length > 0 ? char.ToUpperInvariant(name[0]) + name.Substring(1) : "Figure";
    }

    /// <summary>A figure's color as the fight painted it: a unit from the registry (every Keeper, and a
    /// summon the registry resolves) in its definition's; any other summon as the summon handler paints
    /// it (CombatManager: a pillar or a boulder in stone, one of yours in your summons' blue, anything
    /// else in the enemy's).</summary>
    private static Color BodyOf(UnitDefinition def, TableauFigure f)
    {
        if (def != null)
            return def.BodyColor;
        string kind = (f.UnitId ?? "").ToLowerInvariant();
        if (kind.Contains("pillar") || kind.Contains("boulder"))
            return UITheme.SummonColorPillar;
        return f.Team == 0 ? UITheme.SummonColorFriendly : UITheme.SummonColorEnemy;
    }

    /// <summary>A spirit of yours as the fight showed one in the Sundering: a ghost of cool light.</summary>
    private static void Ghost(Unit unit)
    {
        var mesh = unit.GetNodeOrNull<MeshInstance3D>("MeshInstance3D");
        if (mesh == null)
            return;
        mesh.SetSurfaceOverrideMaterial(0, new StandardMaterial3D
        {
            AlbedoColor = GhostAlbedo,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            EmissionEnabled = true,
            Emission = GhostGlow,
            EmissionEnergyMultiplier = 0.8f,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            RimEnabled = true,
            Rim = 0.6f,
            RimTint = 0.3f,
        });
        mesh.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
        mesh.Scale = new Vector3(1.05f, 1.12f, 1.05f);
    }

    /// <summary>A few of the stray spells that were in the air when it stopped: the Keepers' from the
    /// doors at the dais end, the faculty's still casting at the doors' end, each held partway along
    /// its flight with its trail behind it. The same few on every visit.</summary>
    private void CatchTheStrays()
    {
        if (_hall == null)
            return;
        var rng = new Random(HallSet.StableSeed("strays:" + (_tableau?.TraitorId ?? "") + ":" + _spent.Count));
        var faculty = new List<(Vector3 origin, string style)>();
        foreach (var kv in _cast.Archmagi)
            if (!_spent.Contains(kv.Key))
                faculty.Add((kv.Value.GlobalPosition + new Vector3(0f, HallSet.FigureChest, 0f), StyleOf(kv.Key)));
        if (_cast.Examiner != null)
            faculty.Add((_cast.Examiner.GlobalPosition + new Vector3(0f, HallSet.FigureChest, 0f), StyleOf(ExaminerId)));
        string theirs = !string.IsNullOrEmpty(_words?.OpeningStrike?.Style) ? _words.OpeningStrike.Style : "temporal";

        var strays = new Node3D { Name = "Strays" };
        AddChild(strays);
        for (int i = 0; i < StrayCount; i++)
        {
            bool keepers = faculty.Count == 0 || i % 3 != 2;
            Vector3 from;
            Color tint;
            if (keepers)
            {
                from = _hall.DoorwayPoint + new Vector3(0.4f, Lerp(rng, -1.0f, 1.4f), Lerp(rng, -2.0f, 2.0f));
                tint = Tint(theirs);
            }
            else
            {
                var (origin, style) = faculty[rng.Next(faculty.Count)];
                from = origin;
                tint = Tint(style);
            }
            Vector3 to = _hall.StrayTarget(rng, west: keepers);
            float along = Lerp(rng, 0.3f, 0.8f);
            float lift = rng.NextDouble() < 0.35 ? Mathf.Clamp(from.DistanceTo(to) * 0.25f, 0.35f, 1.6f) : 0f;
            Vector3 at = from.Lerp(to, along) + Vector3.Up * (lift * 4f * along * (1f - along));
            Vector3 heading = to - from + Vector3.Up * (lift * 4f * (1f - 2f * along));
            Streak(strays, at, heading, tint);
        }
    }

    /// <summary>One spell held in the air: a bright core, as a flying spell's, and a fading trail behind
    /// it along <paramref name="heading"/>.</summary>
    private static void Streak(Node3D parent, Vector3 at, Vector3 heading, Color tint)
    {
        var bolt = new Node3D { Name = "Stray" };
        parent.AddChild(bolt);
        bolt.GlobalPosition = at;
        bolt.AddChild(new MeshInstance3D
        {
            Name = "Core",
            Mesh = new SphereMesh { Radius = 0.12f, Height = 0.24f, RadialSegments = 12, Rings = 6 },
            MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                AlbedoColor = tint.Lightened(0.35f),
                EmissionEnabled = true,
                Emission = tint,
                EmissionEnergyMultiplier = 3.0f,
            },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });
        Vector3 back = heading.LengthSquared() > 1e-6f ? -heading.Normalized() : Vector3.Back;
        bolt.AddChild(new MeshInstance3D
        {
            Name = "Trail",
            Mesh = TrailMesh(back * StrayTrail, tint),
            MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                BlendMode = BaseMaterial3D.BlendModeEnum.Add,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                VertexColorUseAsAlbedo = true,
            },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });
    }

    /// <summary>A trail from the core (the origin) to <paramref name="tail"/>: two crossed bands that
    /// narrow and fade toward the tail.</summary>
    private static ArrayMesh TrailMesh(Vector3 tail, Color tint)
    {
        Vector3 dir = tail.Normalized();
        Vector3 first = dir.Cross(Vector3.Up);
        if (first.LengthSquared() < 1e-6f)
            first = dir.Cross(Vector3.Right);
        first = first.Normalized();
        Vector3 second = dir.Cross(first).Normalized();
        var head = new Color(tint, 0.8f);
        var end = new Color(tint, 0f);
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        foreach (var side in new[] { first, second })
        {
            Vector3 a = side * 0.09f;
            Vector3 b = side * 0.01f;
            Corner(st, -a, head);
            Corner(st, a, head);
            Corner(st, tail + b, end);
            Corner(st, -a, head);
            Corner(st, tail + b, end);
            Corner(st, tail - b, end);
        }
        return st.Commit();
    }

    private static void Corner(SurfaceTool st, Vector3 at, Color color)
    {
        st.SetColor(color);
        st.AddVertex(at);
    }

    /// <summary>The camera as the climax framed it: at the dais end, looking south, so the empty mark,
    /// the spell, the lectern and the traitor's line sit across the screen. Free to turn from there,
    /// bounded to the hall as in the fight.</summary>
    private void FrameTheHall()
    {
        if (_camera == null || !IsInstanceValid(_camera))
            return;
        Vector3 min = _grid.GridBoundsMin;
        Vector3 max = _grid.GridBoundsMax;
        float x = min.X + Mathf.Min(_camera.PanEdgeInset, (max.X - min.X) * 0.5f);
        var center = new Vector3(x, max.Y, (min.Z + max.Z) * 0.5f);
        var toward = center + new Vector3(0f, 0f, -1f);
        _camera.AcceptInput = true;
        _camera.FaceToward(center, toward);
        _camera.SwingToward(center, toward, FrameZoom);
    }

    /// <summary>The Provost's last words, in the air over her where the Long Second stopped them, an
    /// instant before the discipline.</summary>
    private void SayTheLastWords()
    {
        if (_cast?.Provost == null)
            return;
        var clauses = PrologueDirector.ScriptFor(PrologueDirector.SunderingFightId)?.Clauses;
        if (clauses == null || clauses.Count == 0)
            return;
        int last = Math.Clamp(_tableau?.Clause ?? clauses.Count, 1, clauses.Count);
        var layer = new CanvasLayer { Name = "SpeechLayer", Layer = SpeechLayer };
        AddChild(layer);
        _bubble = new SpeechBubble { Name = "ProvostBubble" };
        layer.AddChild(_bubble);
        _bubble.Speaker = _cast.Provost;
        _bubble.HeadHeight = SpeakerHead;
        if (_caption != null)
            _bubble.KeepClearOf.Add(_caption);
        _bubble.ShowStill(CombatManager.SunderingFill(clauses[last - 1]));
    }

    // ════════════════════════════════════════════════════════════════════════
    //  The hall's panel, and the way out
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>The way out, the line at the top, and the city's toasts above the way out; the hall's
    /// panel goes on a layer of its own.</summary>
    private void BuildUi()
    {
        var ui = new CanvasLayer { Name = "HallStageUi", Layer = UiLayer };
        AddChild(ui);

        string caption = CombatManager.SunderingFill(_words?.StageCaption);
        if (caption.Length > 0)
        {
            _caption = new Label
            {
                Name = "Caption",
                Text = caption,
                HorizontalAlignment = HorizontalAlignment.Center,
                MouseFilter = Control.MouseFilterEnum.Ignore,
                AnchorLeft = 0f,
                AnchorRight = 1f,
                AnchorTop = 0f,
                AnchorBottom = 0f,
                OffsetTop = UITheme.PaddingLarge,
                OffsetBottom = UITheme.PaddingLarge + 32,
            };
            _caption.AddThemeColorOverride("font_color", UITheme.Gold);
            _caption.AddThemeColorOverride("font_outline_color", UITheme.CardInk);
            _caption.AddThemeConstantOverride("outline_size", 6);
            _caption.AddThemeFontSizeOverride("font_size", UITheme.FontSizeMedium);
            ui.AddChild(_caption);
        }

        string leave = CombatManager.SunderingFill(_words?.StageLeave);
        _leave = new Button
        {
            Name = "Leave",
            Text = leave.Length > 0 ? leave : "Leave",
            AnchorLeft = 1f,
            AnchorRight = 1f,
            AnchorTop = 1f,
            AnchorBottom = 1f,
            OffsetLeft = -200,
            OffsetRight = -UITheme.PaddingLarge,
            OffsetTop = -56,
            OffsetBottom = -UITheme.PaddingLarge,
            GrowHorizontal = Control.GrowDirection.Begin,
            GrowVertical = Control.GrowDirection.Begin,
        };
        UITheme.ApplyButtonStyle(_leave);
        _leave.Pressed += Leave;
        ui.AddChild(_leave);

        // A companion arc's step from the hall's panel toasts as it does in the city, stacked above
        // the way out (the toasts fill their parent and stack at its bottom right).
        var toastArea = new Control { Name = "ToastArea", MouseFilter = Control.MouseFilterEnum.Ignore };
        toastArea.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        toastArea.OffsetBottom = -ToastClearance;
        ui.AddChild(toastArea);
        _toasts = new ToastManager { Name = "HallStageToasts" };
        toastArea.AddChild(_toasts);

        _panelLayer = new CanvasLayer { Name = "HallPanelLayer", Layer = PanelLayer };
        AddChild(_panelLayer);
        _panel = new NarrativeEncounterPanel { Name = "HallPanel", Visible = false };
        _panelLayer.AddChild(_panel);
    }

    /// <summary>The hall's own panel (the chronicle's on_hall: an angle, a report, or the hall at rest),
    /// raised now as the city raised it on the click, over the still hall. Its choice is applied as the
    /// city's are. The way out waits until it is answered.</summary>
    private void ShowHallPanel()
    {
        var save = SaveManager.ActiveSave;
        if (_leaving || save == null || _panel == null || !IsInstanceValid(_panel))
            return;
        var hall = CampusLandmarkRegistry.Get(CampusLandmarkRegistry.CeremonyHallId);
        var enc = hall?.GetEncounter(save.HasFlag);
        if (enc == null)
            return;
        if (_camera != null && IsInstanceValid(_camera))
            _camera.AcceptInput = false;
        if (_leave != null)
            _leave.Disabled = true;
        _panel.Visible = true;
        _panel.ShowEncounter(enc, save.HasFlag, save.Cycle?.SelectedSchool, save.Gold, save.Cycle?.Campaign,
            hasItem: id => save.Armory.OwnedItems.Exists(i => i.DefinitionId == id),
            hasCompanion: id => CompanionRoster.GetActiveParty().Exists(c => c.Id == id));
        _panel.OnCompleted = choice =>
        {
            if (_panel != null && IsInstanceValid(_panel))
                _panel.Visible = false;
            if (_camera != null && IsInstanceValid(_camera))
                _camera.AcceptInput = true;
            if (_leave != null && IsInstanceValid(_leave))
                _leave.Disabled = false;
            var arc = NarrativeOutcome.Apply(enc, choice);
            if (arc != null && _toasts != null && IsInstanceValid(_toasts))
                _toasts.Push(NarrativeOutcome.ArcLine(arc), QuestToastKind.Progress);
        };
        GD.Print($"[HallStage] Over the hall: {enc.Id}.");
    }

    /// <summary>Back to the city, behind the curtain.</summary>
    private void Leave()
    {
        if (_leaving)
            return;
        _leaving = true;
        PlayerSession.StartInCityOnOpen = true;
        string title = CombatManager.SunderingFill(_words?.StageReturn);
        if (title.Length > 0 && char.IsLower(title[0]))
            title = char.ToUpperInvariant(title[0]) + title.Substring(1);
        SceneTransition.Go(GetTree(), PrologueDirector.StrategicScenePath, title);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Helpers
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>The palette a figure on the dais casts in (its volley's style).</summary>
    private string StyleOf(string id)
    {
        if (_words?.Volleys == null)
            return "";
        foreach (var v in _words.Volleys)
            if (v != null && v.Id == id)
                return v.Style ?? "";
        return "";
    }

    private static Color Tint(string style) => VfxLibrary.TintFor(new VisualEvent { VfxStyle = style ?? "" });

    private static float Lerp(Random rng, float a, float b) => a + (b - a) * (float)rng.NextDouble();
}
