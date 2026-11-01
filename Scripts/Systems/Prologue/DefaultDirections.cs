using Godot;
using System;

// ============================================================
// DefaultDirections.cs
//
// Purpose:        The built-in directions (stage direction,
//                 docs/stage_direction_v1.md section 5.4): each stage's
//                 direction as it was shot before it had a scene, built
//                 from the live hall. Two uses, one encoding:
//                   - the bake (StageBake) saves one as its direction
//                     scene when that scene is missing, with the hall's
//                     preview instanced under SetPreview for the editor;
//                   - a stage runs one in memory while its scene is
//                     missing, or lacks what the stage needs.
//                 Once a direction scene exists, these numbers are only
//                 its first version: the scene is the one to change.
//
//                 Poses are in the direction's own frame: the set's origin
//                 (HallSet.SetOrigin) is its zero.
//
//                 Rig shots (SD3): the Sundering's four framings and the
//                 hall seen again's opening frame, each the pose of a
//                 camera with the rig's own lens, read off the rig as its
//                 framings set it (CameraController.PoseFor), at the
//                 tilt the rig opens at (its default pitch).
// Layer:          Prologue / presentation
// Collaborators:  StageDirection (the names), DirectionCues, HallSet (the
//                 hall's points), FoundingStage (the sequences and cues),
//                 CombatManager.Sundering and HallStage (their framings),
//                 CameraController (the poses), StageBake
// ============================================================

/// <summary>The built-in directions (see the file header).</summary>
public static class DefaultDirections
{
    // ── The founding's beats, in seconds (S4's) ──────────────────────────
    private const float OpenHold = 1.0f;          // the doors, before the first letter
    /// <summary>The whole name, cut in; also the cut's length when a cut_name cue gives none.</summary>
    public const float NameSeconds = 2.6f;
    private const float NameHold = 1.1f;          // the name, cut
    private const float FadeSeconds = 0.4f;       // each half of the cut to the west end
    private const float WestHold = 0.6f;
    private const float RegisterSeconds = 0.5f;   // the Register, set down
    private const float RollDelay = 0.4f;
    private const float RollSeconds = 1.4f;       // the Roll's first line, written
    private const float PanelDelay = 0.9f;
    private const float StepSeconds = 1.4f;       // round the lectern to the reader's place
    private const float TurnSeconds = 0.3f;       // and the turn to face the room
    private const float DoorDelay = 1.0f;
    private const float PageSeconds = 1.8f;       // the lens down onto the open page

    // ── The founding's lens: each shot is an eye and a point it looks at ─
    private const float LensFov = 55f;
    private const float LensNear = 0.05f;
    private const float LensFar = 400f;
    private const float DoorsBack = 9.0f;         // inside the doors, from the lintel
    private const float DoorsEye = 1.9f;
    private const float DoorsAside = 1.2f;
    private const float LintelLook = 1.0f;        // below the lintel's middle: the doors and the name
    private const float WestBack = 4.0f;          // east of the lectern and north of it, looking west at
    private const float WestEye = 1.85f;          // it, the Roll beyond it and you beside it
    private const float WestAside = 2.4f;
    private const float WestLookBack = 0.4f;
    private const float ReaderBack = 5.5f;        // the reader, seen from the room
    private const float ReaderEye = 1.7f;
    private const float ReaderAside = -0.8f;
    private const float LookHeight = 1.5f;        // where the lens looks on a standing figure
    private const float PageWest = 0.45f;         // over the open page at the end, from just over the
    private const float PageRise = 0.85f;         // reader's shoulder (the slope faces the reader)
    private const float PageAside = 0.15f;

    // ── You, at the lectern ───────────────────────────────────────────────
    private const float FrontOfLectern = 0.7f;    // where you stand to set the Register down: its east
    private const float FrontAside = 0.55f;       // side, a little south, so the lens sees it past you
    private const float AroundLectern = 1.7f;     // the walk round it bows this far south (its candle is north)
    private const int WalkKeys = 5;               // keys on the walk's curve

    // ── Easing (Godot's per-key transition) ──────────────────────────────
    private const float EaseInOut = -2f;
    private const float EaseOut = 0.5f;
    private const float Linear = 1f;

    private const int FadeLayer = 90;

    // ── A stand-in for the editor (freed in the game) ────────────────────
    private const float StandInRadius = 0.22f;
    private const float StandInHeight = 1.75f;    // the shared rig's height (CompanionFigure.StandInHeight)
    private const float NoseHeight = 1.45f;
    private const float NoseReach = 0.25f;

    // ════════════════════════════════════════════════════════════════════════
    //  The founding (FoundingStage)
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>The founding's direction as S4 shot it, built in the Grand Hall <paramref name="hall"/>. With
    /// <paramref name="preview"/>, that preview's parts are instanced under SetPreview for the editor (the
    /// bake's copy); without, it is the copy a stage runs in memory.</summary>
    public static Node3D Founding(HallSet hall, string preview = null)
    {
        Vector3 origin = hall.SetOrigin;
        Vector3 lectern = hall.GrandLecternPoint;
        Vector3 reader = hall.GrandReaderPoint;
        Vector3 page = hall.GrandPagePoint;
        Vector3 lintel = hall.GrandLintelPoint;
        Vector3 doors = hall.GrandDoorsPoint;

        // The shots, as eye and point looked at, in the hall.
        var doorsEye = new Vector3(lintel.X - DoorsBack, doors.Y + DoorsEye, lintel.Z + DoorsAside);
        var doorsAt = lintel + new Vector3(0f, -LintelLook, 0f);
        var westEye = new Vector3(lectern.X + WestBack, lectern.Y + WestEye, lectern.Z + WestAside);
        var westAt = new Vector3(lectern.X - WestLookBack, lectern.Y + LookHeight, lectern.Z);
        var readerEye = new Vector3(lectern.X + ReaderBack, lectern.Y + ReaderEye, lectern.Z + ReaderAside);
        var readerAt = new Vector3(reader.X, reader.Y + LookHeight, reader.Z);
        var pageEye = page + new Vector3(-PageWest, PageRise, PageAside);

        // Where you stand: inside the doors facing them, then in front of the lectern facing it, then round it
        // (bowing south, away from its candle) to the reader's place, turned to face the room.
        var front = new Vector3(lectern.X + FrontOfLectern, lectern.Y, lectern.Z - FrontAside);
        var bow = new Vector3(lectern.X, lectern.Y, lectern.Z - AroundLectern);

        var root = new Node3D { Name = "Founding" };
        if (!string.IsNullOrEmpty(preview))
            AddPreview(root, preview);
        var lens = new Camera3D { Name = StageDirection.LensNode, Fov = LensFov, Near = LensNear, Far = LensFar };
        root.AddChild(lens);
        var cast = new Node3D { Name = StageDirection.CastNode };
        root.AddChild(cast);
        var candidate = new Node3D { Name = FoundingStage.CandidatePuppet };
        cast.AddChild(candidate);
        candidate.AddChild(StandIn(HallSet.ClassRobe));
        root.AddChild(new DirectionCues { Name = StageDirection.CuesNode });
        AddFade(root);

        var open = new Animation { Length = OpenHold + NameSeconds + NameHold + FadeSeconds };
        Pose3D(open, StageDirection.LensNode, false, (0f, doorsEye - origin, Look(doorsEye, doorsAt), Linear));
        Pose3D(open, CandidatePath, false, (0f, doors - origin, Facing(Vector3.Right), Linear));
        FadeKeys(open, (0f, 0f), (OpenHold + NameSeconds + NameHold, 0f), (open.Length, 1f));
        CueKeys(open, (OpenHold, FoundingStage.CueCutName, NameSeconds));

        float registerAt = FadeSeconds + WestHold;
        float rollAt = registerAt + RegisterSeconds + RollDelay;
        var west = new Animation { Length = rollAt + RollSeconds + PanelDelay };
        Pose3D(west, StageDirection.LensNode, false, (0f, westEye - origin, Look(westEye, westAt), Linear));
        Pose3D(west, CandidatePath, false, (0f, front - origin, Facing(lectern - front), Linear));
        FadeKeys(west, (0f, 1f), (FadeSeconds, 0f));
        CueKeys(west, (registerAt, FoundingStage.CueRegisterDown, RegisterSeconds),
                      (rollAt, FoundingStage.CueRollLine, RollSeconds));

        var take = new Animation { Length = StepSeconds + TurnSeconds };
        Pose3D(take, StageDirection.LensNode, false,
            (0f, westEye - origin, Look(westEye, westAt), EaseInOut),
            (take.Length, readerEye - origin, Look(readerEye, readerAt), Linear));
        WalkRound(take, CandidatePath, front - origin, bow - origin, reader - origin);
        CueKeys(take, (0f, FoundingStage.CueCandidateClip + "walk", 0f),
                      (StepSeconds, FoundingStage.CueCandidateClip + "idle", 0f));

        var doorWait = new Animation { Length = DoorDelay };

        var goOut = new Animation { Length = PageSeconds };
        Pose3D(goOut, StageDirection.LensNode, false,
            (0f, readerEye - origin, Look(readerEye, readerAt), EaseInOut),
            (goOut.Length, pageEye - origin, Look(pageEye, page), Linear));

        var player = new AnimationPlayer { Name = StageDirection.PlayerNode, Deterministic = false };
        root.AddChild(player);
        var library = new AnimationLibrary();
        library.AddAnimation(FoundingStage.Open, open);
        library.AddAnimation(FoundingStage.West, west);
        library.AddAnimation(FoundingStage.TakeLectern, take);
        library.AddAnimation(FoundingStage.DoorWait, doorWait);
        library.AddAnimation(FoundingStage.GoOutSequence, goOut);
        player.AddAnimationLibrary("", library);

        // As the scene opens in the editor: the doors shot, you inside the doors.
        lens.Position = doorsEye - origin;
        lens.Quaternion = Look(doorsEye, doorsAt);
        candidate.Position = doors - origin;
        candidate.Quaternion = Facing(Vector3.Right);
        return root;
    }

    private static string CandidatePath => StageDirection.CastNode + "/" + FoundingStage.CandidatePuppet;

    /// <summary>The walk round the lectern: the curve S4 walked (from the front, bowing toward
    /// <paramref name="bow"/>, to the reader's place), eased in and out, in WalkKeys keys facing along it; then
    /// the turn to face the room.</summary>
    private static void WalkRound(Animation anim, string path, Vector3 from, Vector3 bow, Vector3 to)
    {
        int position = anim.AddTrack(Animation.TrackType.Position3D);
        anim.TrackSetPath(position, path);
        anim.TrackSetInterpolationType(position, Animation.InterpolationType.Cubic);
        int rotation = anim.AddTrack(Animation.TrackType.Rotation3D);
        anim.TrackSetPath(rotation, path);
        for (int i = 0; i < WalkKeys; i++)
        {
            float u = (float)i / (WalkKeys - 1);
            float s = 0.5f - 0.5f * Mathf.Cos(Mathf.Pi * u);   // the walk's ease, in the keys' spacing
            float r = 1f - s;
            Vector3 at = r * r * from + 2f * r * s * bow + s * s * to;
            Vector3 along = 2f * r * (bow - from) + 2f * s * (to - bow);
            float time = StepSeconds * u;
            anim.PositionTrackInsertKey(position, time, at);
            int key = anim.RotationTrackInsertKey(rotation, time, Facing(along));
            if (i == WalkKeys - 1)
                anim.TrackSetKeyTransition(rotation, key, EaseOut);   // into the turn
        }
        anim.RotationTrackInsertKey(rotation, StepSeconds + TurnSeconds, Facing(Vector3.Right));
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Rig shots (SD3): the Sundering and the hall seen again
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>The Sundering's rig shots as its framings set them before it had a scene: Dais over the candidate
    /// as the fight opens (<paramref name="candidate"/>), looking west; Side and Climax looking south; WalkOut
    /// looking east (the fight uses only its turn, tilt and zoom). With <paramref name="preview"/>, that preview's
    /// parts are instanced under SetPreview for the editor.</summary>
    public static Node3D SunderingShots(HallSet hall, HexGridManager grid, CameraController camera, Vector3 candidate,
                                        string preview = null)
    {
        Vector3 min = grid.GridBoundsMin;
        Vector3 max = grid.GridBoundsMax;
        float midZ = (min.Z + max.Z) * 0.5f;
        var dais = new Vector3(candidate.X, max.Y, candidate.Z);
        var side = new Vector3(Mathf.Lerp(min.X, max.X, CombatManager.SunderingSideAlong), max.Y, midZ);
        var climax = new Vector3(WestStand(min, max, camera), max.Y, midZ);

        var root = new Node3D { Name = "Sundering" };
        var shots = ShotsHolder(root, preview);
        Vector3 origin = hall.SetOrigin;
        AddShot(shots, origin, camera, CombatManager.SunderingDaisShot, dais, dais + Vector3.Left,
                CombatManager.SunderingDaisZoom);
        AddShot(shots, origin, camera, CombatManager.SunderingSideShot, side, side + Vector3.Forward,
                CombatManager.SunderingSideZoom);
        AddShot(shots, origin, camera, CombatManager.SunderingClimaxShot, climax, climax + Vector3.Forward,
                CombatManager.SunderingClimaxZoom);
        // Where the walk begins: the west end, near the mark. Only its turn, tilt and zoom are used.
        AddShot(shots, origin, camera, CombatManager.SunderingWalkOutShot, climax, climax + Vector3.Right,
                CombatManager.SunderingWalkZoom);
        return root;
    }

    /// <summary>The hall seen again's opening frame as it was set before it had a scene: at the dais end, looking
    /// south, as the climax framed it. With <paramref name="preview"/>, that preview's parts are instanced under
    /// SetPreview for the editor.</summary>
    public static Node3D HallStageShots(HallSet hall, HexGridManager grid, CameraController camera, string preview = null)
    {
        Vector3 min = grid.GridBoundsMin;
        Vector3 max = grid.GridBoundsMax;
        var frame = new Vector3(WestStand(min, max, camera), max.Y, (min.Z + max.Z) * 0.5f);
        var root = new Node3D { Name = "HallStage" };
        var shots = ShotsHolder(root, preview);
        AddShot(shots, hall.SetOrigin, camera, HallStage.FrameShot, frame, frame + Vector3.Forward, HallStage.FrameZoom);
        return root;
    }

    /// <summary>As far west as the rig may stand: the board's west edge, inside the rig's pan inset.</summary>
    private static float WestStand(Vector3 min, Vector3 max, CameraController camera) =>
        min.X + Mathf.Min(camera.PanEdgeInset, (max.X - min.X) * 0.5f);

    /// <summary>The direction's Shots node under <paramref name="root"/>, after its SetPreview when there is a
    /// <paramref name="preview"/>.</summary>
    private static Node3D ShotsHolder(Node3D root, string preview)
    {
        if (!string.IsNullOrEmpty(preview))
            AddPreview(root, preview);
        var shots = new Node3D { Name = StageDirection.ShotsNode };
        root.AddChild(shots);
        return shots;
    }

    /// <summary>A rig shot named <paramref name="name"/>: the pose of the rig's camera standing at
    /// <paramref name="center"/>, looking toward <paramref name="toward"/>, at <paramref name="zoom"/> (reference
    /// units) and the tilt the rig shows there at its default pitch, with the rig's own lens, in the direction's
    /// frame (<paramref name="origin"/> its zero).</summary>
    private static void AddShot(Node3D shots, Vector3 origin, CameraController camera, string name, Vector3 center,
                                Vector3 toward, float zoom)
    {
        float yaw = CameraController.YawToward(center, toward);
        float pitch = camera.ShownPitch(CameraController.DefaultPitch, zoom);
        Transform3D pose = camera.PoseFor(center, yaw, pitch, zoom);
        var shot = new Camera3D { Name = name, Transform = new Transform3D(pose.Basis, pose.Origin - origin) };
        var rig = camera.RigCamera;
        if (rig != null)
        {
            shot.Fov = rig.Fov;
            shot.Near = rig.Near;
            shot.Far = rig.Far;
            shot.KeepAspect = rig.KeepAspect;
        }
        shots.AddChild(shot);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Pieces
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>The preview's parts under SetPreview, each a placeholder for its file (Load As Placeholder):
    /// the editor shows them, the game never loads them. A part not yet baked is left out.</summary>
    private static void AddPreview(Node3D root, string preview)
    {
        var set = new Node3D { Name = StageDirection.SetPreviewNode };
        root.AddChild(set);
        foreach (var (node, file) in StageDirection.PreviewParts)
        {
            string path = StageDirection.PreviewPartPath(preview, file);
            if (!FileAccess.FileExists(path))
                continue;
            var part = new Node3D { Name = node, SceneFilePath = path };
            part.SetSceneInstanceLoadPlaceholder(true);
            set.AddChild(part);
        }
    }

    /// <summary>A body you can see in the editor where a figure will stand, with a nose on the side it faces
    /// (+Z, as the shared rig faces). Freed in the game.</summary>
    private static Node3D StandIn(Color color)
    {
        var standIn = new Node3D { Name = StageDirection.EditorOnlyNode };
        var paint = new StandardMaterial3D { AlbedoColor = color };
        standIn.AddChild(new MeshInstance3D
        {
            Name = "Body",
            Mesh = new CapsuleMesh { Radius = StandInRadius, Height = StandInHeight },
            Position = new Vector3(0f, StandInHeight * 0.5f, 0f),
            MaterialOverride = paint,
        });
        standIn.AddChild(new MeshInstance3D
        {
            Name = "Nose",
            Mesh = new BoxMesh { Size = new Vector3(0.1f, 0.1f, 0.3f) },
            Position = new Vector3(0f, NoseHeight, NoseReach),
            MaterialOverride = paint,
        });
        return standIn;
    }

    /// <summary>The fade: a full-screen ColorRect over the stage, keyed by its alpha only (its color is set from
    /// UITheme when the direction loads).</summary>
    private static void AddFade(Node3D root)
    {
        var layer = new CanvasLayer { Name = StageDirection.FadeLayerNode, Layer = FadeLayer };
        root.AddChild(layer);
        var fade = new ColorRect
        {
            Name = "Fade",
            Color = new Color(UITheme.CardInk, 1f),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        fade.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        var clear = fade.Modulate;
        clear.A = 0f;
        fade.Modulate = clear;
        layer.AddChild(fade);
    }

    /// <summary>Position and rotation keys for the node at <paramref name="path"/>: each key's time, place, turn
    /// and easing into the next key.</summary>
    private static void Pose3D(Animation anim, string path, bool cubic,
                               params (float time, Vector3 at, Quaternion turn, float ease)[] keys)
    {
        int position = anim.AddTrack(Animation.TrackType.Position3D);
        anim.TrackSetPath(position, path);
        if (cubic)
            anim.TrackSetInterpolationType(position, Animation.InterpolationType.Cubic);
        int rotation = anim.AddTrack(Animation.TrackType.Rotation3D);
        anim.TrackSetPath(rotation, path);
        foreach (var (time, at, turn, ease) in keys)
        {
            anim.TrackSetKeyTransition(position, anim.PositionTrackInsertKey(position, time, at), ease);
            anim.TrackSetKeyTransition(rotation, anim.RotationTrackInsertKey(rotation, time, turn), ease);
        }
    }

    /// <summary>The fade's alpha keys.</summary>
    private static void FadeKeys(Animation anim, params (float time, float alpha)[] keys)
    {
        int track = anim.AddTrack(Animation.TrackType.Value);
        anim.TrackSetPath(track, StageDirection.FadeNode + ":modulate:a");
        anim.ValueTrackSetUpdateMode(track, Animation.UpdateMode.Continuous);
        foreach (var (time, alpha) in keys)
            anim.TrackInsertKey(track, time, alpha);
    }

    /// <summary>Cue keys on Cues: CueFor(id, seconds) when it has seconds, Cue(id) when not.</summary>
    private static void CueKeys(Animation anim, params (float time, string id, float seconds)[] keys)
    {
        int track = anim.AddTrack(Animation.TrackType.Method);
        anim.TrackSetPath(track, StageDirection.CuesNode);
        foreach (var (time, id, seconds) in keys)
        {
            var args = new Godot.Collections.Array { id };
            if (seconds > 0f)
                args.Add(seconds);
            anim.TrackInsertKey(track, time, new Godot.Collections.Dictionary
            {
                { "method", seconds > 0f ? StageDirection.CueForMethod : StageDirection.CueMethod },
                { "args", args },
            });
        }
    }

    /// <summary>The turn of a lens at <paramref name="eye"/> looking at <paramref name="at"/>.</summary>
    private static Quaternion Look(Vector3 eye, Vector3 at)
    {
        Vector3 forward = at - eye;
        if (forward.LengthSquared() < 1e-8f)
            return Quaternion.Identity;
        return Basis.LookingAt(forward, Vector3.Up).GetRotationQuaternion();
    }

    /// <summary>The turn of a figure facing along <paramref name="direction"/> (+Z forward, as
    /// HallSet.PlaceFigure turns one); level, the short way round.</summary>
    private static Quaternion Facing(Vector3 direction)
    {
        direction.Y = 0f;
        if (direction.LengthSquared() < 1e-8f)
            return Quaternion.Identity;
        return new Quaternion(Vector3.Up, Mathf.Atan2(direction.X, direction.Z));
    }
}
