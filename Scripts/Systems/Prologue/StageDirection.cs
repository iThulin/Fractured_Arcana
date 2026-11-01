using Godot;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

// ============================================================
// StageDirection.cs
//
// Purpose:        A staging's direction scene, in the game (stage
//                 direction, docs/stage_direction_v1.md sections 4 and
//                 5): the editor scene that says how each moment is shot
//                 and how things move, loaded and played by its stage.
//
//                 A direction scene is Scenes/Prologue/Direction/ID.tscn,
//                 read by node names only:
//                   SetPreview      the hall, for the editor; freed here
//                   Lens            the staging's camera (Camera3D)
//                   Shots/NAME      rig shots: Camera3D poses for the combat
//                                   camera, with its lens (SD3, section 6)
//                   Cast/NAME       puppets: a real figure follows one (Bind)
//                   Cues            the cue sheet (DirectionCues)
//                   FadeLayer/Fade  a ColorRect the sequences key by its alpha
//                   Direction       the AnimationPlayer: the sequences
//                   EditorOnly      anything only the editor shows; freed here
//
//                 It stands at the set's origin (HallSet.SetOrigin), the
//                 board's center on the hall floor, as the previews do.
//                 When its scene is missing, or lacks what its stage needs
//                 (Needs), the stage's built-in direction runs instead
//                 (DefaultDirections): the same one the bake writes, so the
//                 defaults are written down once.
//
//                 The AnimationPlayer runs non-deterministic, so a sequence
//                 that does not key something leaves it where the last one
//                 put it. (Godot's deterministic blending sets every track
//                 the playing sequence lacks back to its RESET key, or to
//                 zero.) A sequence is played once, never looped.
//
//                 A direction of rig shots (SD3: the Sundering, the hall
//                 seen again) is read for its shots only (LoadShots) and
//                 freed: its cameras never enter the tree, so none can
//                 become the current camera. Its stage hands each shot to
//                 the combat camera (ShotSet.Frame, CameraController
//                 SwingToPose and TakePose).
//
//                 Also: where the previews are kept (StageBake writes
//                 them; directions instance them), and the assert.
// Layer:          Prologue / presentation
// Collaborators:  DirectionCues, DefaultDirections, FoundingStage,
//                 CombatManager.Sundering and HallStage (rig shots),
//                 CameraController, StageBake (writes the previews and
//                 the missing directions), PrologueDirector (the assert)
// ============================================================

/// <summary>A staging's direction scene, loaded and played (see the file header).</summary>
public sealed class StageDirection
{
    public const string DirectionDir = "res://Scenes/Prologue/Direction/";
    public const string SetsDir = "res://Scenes/Prologue/Sets/";
    public const string FoundingId = "founding";

    // ── The names a direction is read by (the design, section 4) ─────────
    public const string SetPreviewNode = "SetPreview";
    public const string BodyPart = "Body";
    public const string LensNode = "Lens";
    public const string ShotsNode = "Shots";
    public const string CastNode = "Cast";
    public const string CuesNode = "Cues";
    public const string FadeLayerNode = "FadeLayer";
    public const string FadeNode = "FadeLayer/Fade";
    public const string PlayerNode = "Direction";
    public const string EditorOnlyNode = "EditorOnly";
    /// <summary>The RemoteTransform3D a puppet carries its figure with (Bind).</summary>
    public const string CarryNode = "Carry";
    /// <summary>The animation Godot's editor keeps first values in: never a sequence.</summary>
    public const string ResetAnimation = "RESET";
    public const string CueMethod = "Cue";
    public const string CueForMethod = "CueFor";

    /// <summary>A preview's parts: their node names under a direction's SetPreview, and their files. The walls
    /// and the ceiling are parts of their own so the editor can hide whichever is in the way, as the game's
    /// cutaway does.</summary>
    public static readonly (string node, string file)[] PreviewParts =
    {
        (BodyPart, "body"),
        ("WallNorth", "wall_north"),
        ("WallSouth", "wall_south"),
        ("WallEast", "wall_east"),
        ("WallWest", "wall_west"),
        ("Ceiling", "ceiling"),
    };

    public static string ScenePath(string id) => DirectionDir + id + ".tscn";

    public static string PreviewPartPath(string preview, string file) => SetsDir + preview + "/" + file + ".scn";

    /// <summary>What a stage needs of its direction. Load runs the built-in direction instead of a scene that
    /// lacks any of it, and the assert names what is missing.</summary>
    public sealed class Needs
    {
        public string Id = "";
        public bool Lens;
        public string[] Puppets = Array.Empty<string>();
        public string[] Sequences = Array.Empty<string>();
        /// <summary>The cues the stage performs: an id, or a prefix that ends in a colon.</summary>
        public string[] Cues = Array.Empty<string>();
        /// <summary>The rig shots the stage frames: Shots/NAME, each a Camera3D.</summary>
        public string[] Shots = Array.Empty<string>();
    }

    /// <summary>Every stage that plays a direction, for the assert.</summary>
    private static Needs[] KnownNeeds() =>
        new[] { FoundingStage.DirectionNeeds, CombatManager.SunderingDirectionNeeds, HallStage.DirectionNeeds };

    public string Id { get; }
    public Node3D Root { get; }
    public Camera3D Lens { get; }
    public AnimationPlayer Player { get; }
    /// <summary>True when the direction came from its scene; false for the built-in one.</summary>
    public bool FromScene { get; }

    /// <summary>A cue from the sequence playing (DirectionCues): its id, and its seconds.</summary>
    public event Action<string, float> Cued;

    private readonly HashSet<string> _warned = new();
    /// <summary>The frame Pose last set the direction in: a cue it sets off then is not passed on.</summary>
    private ulong _posedFrame = ulong.MaxValue;

    // ════════════════════════════════════════════════════════════════════════
    //  Loading
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>The direction <paramref name="id"/> under <paramref name="parent"/>, at
    /// <paramref name="origin"/>: its scene, when there is one and it has what <paramref name="needs"/>
    /// lists, or else <paramref name="builtIn"/>'s. Null only when neither can be had. The editor's nodes
    /// (SetPreview, EditorOnly) are freed before it enters the tree.</summary>
    public static StageDirection Load(string id, Node parent, Vector3 origin, Func<Node3D> builtIn, Needs needs)
    {
        if (parent == null || !GodotObject.IsInstanceValid(parent))
            return null;
        var root = Read(id, builtIn, needs, out bool fromScene, out var advice);
        if (root == null)
            return null;
        FreeEditorNodes(root);
        parent.AddChild(root);
        root.GlobalPosition = origin;
        Report(id, fromScene, advice, "");
        return new StageDirection(id, root, fromScene);
    }

    /// <summary>The rig shots of the direction <paramref name="id"/> (SD3), placed as the direction stands at
    /// <paramref name="origin"/>: its scene's, when there is one with every shot <paramref name="needs"/> lists, or
    /// else <paramref name="builtIn"/>'s. The direction is read and freed, never entering the tree, so none of its
    /// cameras can become the current camera. Null only when neither can be had.</summary>
    public static ShotSet LoadShots(string id, Vector3 origin, Func<Node3D> builtIn, Needs needs)
    {
        var root = Read(id, builtIn, needs, out bool fromScene, out var advice);
        if (root == null)
            return null;
        var shots = new Dictionary<string, Shot>();
        if (root.GetNodeOrNull(ShotsNode) is Node holder)
            foreach (Node child in holder.GetChildren())
                if (child is Camera3D camera)
                    shots[camera.Name.ToString()] = new Shot(Placed(root, camera, origin), camera.Fov);
        root.Free();
        Report(id, fromScene, advice, $" {shots.Count} shot(s).");
        return new ShotSet(id, fromScene, shots);
    }

    /// <summary>The direction <paramref name="id"/>'s tree, outside the scene tree: its scene's, when there is one
    /// with all <paramref name="needs"/> lists (<paramref name="fromScene"/>, with its <paramref name="advice"/>),
    /// or else <paramref name="builtIn"/>'s. Null only when neither can be had.</summary>
    private static Node3D Read(string id, Func<Node3D> builtIn, Needs needs, out bool fromScene, out List<string> advice)
    {
        Node3D root = null;
        advice = new List<string>();
        string path = ScenePath(id);
        if (ResourceLoader.Exists(path))
        {
            // Read from the file every time, so a change saved in the editor is the one the next run plays.
            // Nothing else holds a scene read this way, so it is kept alive until Instantiate returns (D23).
            var packed = ResourceLoader.Load<PackedScene>(path, null, ResourceLoader.CacheMode.Ignore);
            var instance = packed?.Instantiate();
            GC.KeepAlive(packed);
            root = instance as Node3D;
            if (root == null)
            {
                instance?.Free();
                GD.PushWarning($"[StageDirection] {path} does not load as a Node3D scene: the built-in {id} direction runs.");
            }
            else
            {
                var missing = Missing(needs, root);
                if (missing.Count > 0)
                {
                    GD.PushWarning($"[StageDirection] {path}: {string.Join("; ", missing)}. The built-in {id} direction runs.");
                    root.Free();
                    root = null;
                }
                else
                {
                    advice = Advice(needs, root);
                }
            }
        }
        else
        {
            GD.Print($"[StageDirection] {id}: no scene at {path} yet, so the built-in direction runs (Bake Stage Previews writes it).");
        }
        fromScene = root != null;
        root ??= builtIn?.Invoke();
        return root;
    }

    /// <summary>The log's lines for a direction just loaded: its advice, then where it came from.</summary>
    private static void Report(string id, bool fromScene, List<string> advice, string tail)
    {
        string path = ScenePath(id);
        foreach (var note in advice)
            GD.PushWarning($"[StageDirection] {path}: {note}.");
        GD.Print($"[StageDirection] {id}: " + (fromScene ? $"from {path}." : "the built-in direction.") + tail);
    }

    /// <summary>Where <paramref name="node"/> stands in the world once its direction's root stands at
    /// <paramref name="origin"/> (keeping the root's own turn), worked out without the scene tree.</summary>
    private static Transform3D Placed(Node3D root, Node3D node, Vector3 origin)
    {
        var local = Transform3D.Identity;
        for (Node at = node; at != null && at != root; at = at.GetParent())
            if (at is Node3D spatial)
                local = spatial.Transform * local;
        return new Transform3D(root.Basis, origin) * local;
    }

    /// <summary>A rig shot (SD3): the pose of its camera in the world, and its lens.</summary>
    public readonly struct Shot
    {
        public readonly Transform3D Pose;
        public readonly float Fov;

        public Shot(Transform3D pose, float fov)
        {
            Pose = pose;
            Fov = fov;
        }
    }

    /// <summary>A direction's rig shots (SD3), by name: the framings a stage hands the combat camera.</summary>
    public sealed class ShotSet
    {
        private readonly Dictionary<string, Shot> _shots;
        private readonly HashSet<string> _warned = new();

        public ShotSet(string id, bool fromScene, Dictionary<string, Shot> shots)
        {
            Id = id ?? "";
            FromScene = fromScene;
            _shots = shots ?? new Dictionary<string, Shot>();
        }

        public string Id { get; }
        /// <summary>True when the shots came from the direction's scene; false for the built-in ones.</summary>
        public bool FromScene { get; }

        public bool Has(string name) => _shots.ContainsKey(name ?? "");

        /// <summary>Frames <paramref name="camera"/> on the shot <paramref name="name"/>: at once
        /// (<paramref name="instant"/>), or swung to, taking the shot's center or not
        /// (<paramref name="moveCenter"/>). Where the rig cannot match the shot, the log says what it did instead,
        /// once a shot. False when there is no such shot, or it cannot be taken: the caller frames as it did
        /// before the shot existed.</summary>
        public bool Frame(CameraController camera, string name, bool instant = false, bool moveCenter = true)
        {
            if (camera == null || !GodotObject.IsInstanceValid(camera) || !_shots.TryGetValue(name ?? "", out var shot))
                return false;
            string fit;
            bool taken = instant
                ? camera.TakePose(shot.Pose, shot.Fov, out fit)
                : camera.SwingToPose(shot.Pose, shot.Fov, moveCenter, out fit);
            if (fit.Length > 0 && _warned.Add(name))
                GD.PushWarning($"[StageDirection] {Id}: {ShotsNode}/{name}: {fit}.");
            return taken;
        }
    }

    private StageDirection(string id, Node3D root, bool fromScene)
    {
        Id = id;
        Root = root;
        FromScene = fromScene;
        Lens = root.GetNodeOrNull<Camera3D>(LensNode);
        Player = root.GetNodeOrNull<AnimationPlayer>(PlayerNode);
        if (Player != null)
        {
            // A sequence that does not key something leaves it where the last one put it (the file header).
            Player.Deterministic = false;
            foreach (string name in Player.GetAnimationList())
            {
                var anim = Player.GetAnimation(name);
                if (anim != null && anim.LoopMode != Animation.LoopModeEnum.None)
                    anim.LoopMode = Animation.LoopModeEnum.None;   // played once (Advice names it)
            }
        }
        if (root.GetNodeOrNull(CuesNode) is DirectionCues cues)
            cues.Cued += OnCued;
        if (root.GetNodeOrNull(FadeNode) is ColorRect fade)
        {
            fade.Color = new Color(UITheme.CardInk, 1f);   // UITheme's ink; the sequences key only its alpha
            fade.MouseFilter = Control.MouseFilterEnum.Ignore;
        }
    }

    /// <summary>Frees what only the editor shows: the set's preview (its parts are placeholders the game never
    /// loads, and are freed here even if one has lost that flag) and every EditorOnly node.</summary>
    private static void FreeEditorNodes(Node root)
    {
        var doomed = new List<Node>();
        var preview = root.GetNodeOrNull(SetPreviewNode);
        if (preview != null)
            doomed.Add(preview);
        CollectEditorOnly(root, root, doomed);
        foreach (var node in doomed)
        {
            if (!GodotObject.IsInstanceValid(node))
                continue;
            node.GetParent()?.RemoveChild(node);
            node.Free();
        }
    }

    private static void CollectEditorOnly(Node node, Node root, List<Node> doomed)
    {
        foreach (Node child in node.GetChildren())
        {
            if (child.Name == EditorOnlyNode)
                doomed.Add(child);
            else if (!(node == root && child.Name == SetPreviewNode))
                CollectEditorOnly(child, root, doomed);
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Playing
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>True when the direction has <paramref name="sequence"/>.</summary>
    public bool Has(string sequence) =>
        !string.IsNullOrEmpty(sequence) && Player != null && GodotObject.IsInstanceValid(Player)
        && Player.HasAnimation(sequence);

    /// <summary>Plays <paramref name="sequence"/> from its start and returns when it has ended. A sequence the
    /// direction lacks is passed over at once, with a warning the first time.</summary>
    public async Task Play(string sequence)
    {
        if (!Has(sequence))
        {
            WarnOnce("sequence:" + sequence, $"[StageDirection] {Id}: no sequence '{sequence}'; passed over.");
            return;
        }
        _posedFrame = ulong.MaxValue;
        var anim = Player.GetAnimation(sequence);
        Player.Stop(true);
        if (anim == null || anim.Length <= 0f)
        {
            Player.Play(sequence);
            Player.Advance(0);
            return;
        }
        var ended = Player.ToSignal(Player, AnimationMixer.SignalName.AnimationFinished);
        Player.Play(sequence);
        Player.Advance(0);   // its first instant now, not a frame later
        await ended;
    }

    /// <summary>Sets everything <paramref name="sequence"/> keys as it stands at its start, or at its end, and
    /// holds it there. Pose shows a moment and does not play it: a cue at that instant does not happen.</summary>
    public void Pose(string sequence, bool atEnd)
    {
        if (!Has(sequence))
        {
            WarnOnce("sequence:" + sequence, $"[StageDirection] {Id}: no sequence '{sequence}'; passed over.");
            return;
        }
        _posedFrame = Engine.GetProcessFrames();
        Player.Stop(true);
        Player.Play(sequence);
        Player.Seek(atEnd ? Player.GetAnimation(sequence).Length : 0.0, true);
        Player.Pause();
    }

    /// <summary>The puppet Cast/<paramref name="puppet"/> carries <paramref name="figure"/> from now on: its
    /// position and its turn, never its scale, so a model keeps its own. False, with a warning, when there is no
    /// such puppet: the figure then stands where it was placed.</summary>
    public bool Bind(string puppet, Node3D figure)
    {
        var holder = Root.GetNodeOrNull<Node3D>($"{CastNode}/{puppet}");
        if (holder == null || figure == null || !GodotObject.IsInstanceValid(figure)
            || !figure.IsInsideTree() || !holder.IsInsideTree())
        {
            WarnOnce("bind:" + puppet, $"[StageDirection] {Id}: no {CastNode}/{puppet} to carry the figure; it stands where it was placed.");
            return false;
        }
        var carry = new RemoteTransform3D
        {
            Name = CarryNode,
            UseGlobalCoordinates = true,
            UpdatePosition = true,
            UpdateRotation = true,
            UpdateScale = false,
        };
        holder.AddChild(carry);
        carry.RemotePath = carry.GetPathTo(figure);
        carry.ForceUpdateCache();
        return true;
    }

    /// <summary>A cue its stage does not perform: a warning, the first time.</summary>
    public void UnknownCue(string id) =>
        WarnOnce("cue:" + id, $"[StageDirection] {Id}: no stage performs the cue '{id}'; passed over.");

    /// <summary>Plays <paramref name="clip"/> on the first AnimationPlayer under <paramref name="figure"/> (a
    /// model's own clips). False when it has none, or not that clip: the stand-ins have none.</summary>
    public static bool PlayClip(Node figure, string clip)
    {
        if (figure == null || !GodotObject.IsInstanceValid(figure) || string.IsNullOrEmpty(clip))
            return false;
        var player = FindAnimationPlayer(figure);
        if (player == null || !player.HasAnimation(clip))
            return false;
        player.Play(clip);
        return true;
    }

    private static AnimationPlayer FindAnimationPlayer(Node node)
    {
        if (node is AnimationPlayer player)
            return player;
        foreach (Node child in node.GetChildren())
        {
            var found = FindAnimationPlayer(child);
            if (found != null)
                return found;
        }
        return null;
    }

    private void OnCued(string id, float seconds)
    {
        if (_posedFrame != ulong.MaxValue && Engine.GetProcessFrames() <= _posedFrame)
            return;   // a key at the instant Pose set: Pose shows a moment, it does not play it
        Cued?.Invoke(id, seconds);
    }

    private void WarnOnce(string key, string message)
    {
        if (_warned.Add(key))
            GD.PushWarning(message);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Checks
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>What <paramref name="root"/> lacks of <paramref name="needs"/>: the stage cannot play the scene
    /// without any of these.</summary>
    public static List<string> Missing(Needs needs, Node root)
    {
        var missing = new List<string>();
        if (needs == null || root == null)
            return missing;
        if (needs.Lens && root.GetNodeOrNull(LensNode) is not Camera3D)
            missing.Add($"it has no {LensNode} (a Camera3D)");
        foreach (var puppet in needs.Puppets)
            if (root.GetNodeOrNull($"{CastNode}/{puppet}") is not Node3D)
                missing.Add($"it has no {CastNode}/{puppet} (a Node3D)");
        foreach (var shot in needs.Shots)
            if (root.GetNodeOrNull($"{ShotsNode}/{shot}") is not Camera3D)
                missing.Add($"it has no {ShotsNode}/{shot} (a Camera3D)");
        if (needs.Sequences.Length == 0)
            return missing;
        if (root.GetNodeOrNull(PlayerNode) is not AnimationPlayer player)
        {
            missing.Add($"it has no {PlayerNode} (an AnimationPlayer)");
            return missing;
        }
        foreach (var sequence in needs.Sequences)
            if (!player.HasAnimation(sequence))
                missing.Add($"it has no sequence '{sequence}'");
        return missing;
    }

    /// <summary>What is wrong in <paramref name="root"/> that its stage can still play around: a cue no stage
    /// performs, a method key that is not Cue or CueFor on Cues, a key on a node that is not there, a sequence
    /// that loops.</summary>
    public static List<string> Advice(Needs needs, Node root)
    {
        var notes = new List<string>();
        foreach (var shot in needs?.Shots ?? Array.Empty<string>())
            if (root?.GetNodeOrNull($"{ShotsNode}/{shot}") is Camera3D camera
                && -Placed(root, camera, Vector3.Zero).Basis.Orthonormalized().Z.Y > -0.02f)
                notes.Add($"{ShotsNode}/{shot} does not look down at the board, so the rig keeps its stance there");
        if (root?.GetNodeOrNull(PlayerNode) is not AnimationPlayer player)
            return notes;
        Node trackRoot = player.GetNodeOrNull(player.RootNode) ?? root;
        bool hasCues = root.GetNodeOrNull(CuesNode) is DirectionCues;
        foreach (string name in player.GetAnimationList())
        {
            if (name == ResetAnimation)
                continue;
            var anim = player.GetAnimation(name);
            if (anim == null)
                continue;
            if (anim.LoopMode != Animation.LoopModeEnum.None)
                notes.Add($"sequence '{name}' is set to loop (it is played once)");
            for (int t = 0; t < anim.GetTrackCount(); t++)
            {
                string nodePart = anim.TrackGetPath(t).GetConcatenatedNames();
                if (anim.TrackGetType(t) != Animation.TrackType.Method)
                {
                    if (nodePart.Length > 0 && trackRoot.GetNodeOrNull(nodePart) == null)
                        notes.Add($"sequence '{name}' keys '{nodePart}', which is not in the scene");
                    continue;
                }
                if (nodePart != CuesNode || !hasCues)
                {
                    notes.Add($"sequence '{name}' has method keys on '{nodePart}'; cues go on {CuesNode} (a DirectionCues)");
                    continue;
                }
                for (int k = 0; k < anim.TrackGetKeyCount(t); k++)
                {
                    string method = anim.MethodTrackGetName(t, k);
                    string id = CueId(anim, t, k);
                    double at = anim.TrackGetKeyTime(t, k);
                    if ((method != CueMethod && method != CueForMethod) || id.Length == 0)
                        notes.Add($"sequence '{name}' calls {method} at {at:0.##}s; a cue is Cue(id) or CueFor(id, seconds)");
                    else if (!Performs(needs, id))
                        notes.Add($"sequence '{name}' cues '{id}' at {at:0.##}s, which its stage does not perform");
                }
            }
        }
        return notes;
    }

    /// <summary>The first argument of method key <paramref name="key"/> on <paramref name="track"/>, as text: a
    /// cue's id, or empty. It is read from the key's value, a Dictionary of "method" and "args".
    /// Animation.MethodTrackGetParams is never called: in Godot 4.6.2 it stops the game from C#, because it
    /// resizes its answer in place and C# hands it an Array that was never built (D25).</summary>
    private static string CueId(Animation anim, int track, int key)
    {
        var value = anim.TrackGetKeyValue(track, key);
        if (value.VariantType != Variant.Type.Dictionary)
            return "";
        if (!value.AsGodotDictionary().TryGetValue("args", out var args) || args.VariantType != Variant.Type.Array)
            return "";
        var list = args.AsGodotArray();
        return list.Count > 0 ? list[0].AsString() : "";
    }

    /// <summary>True when <paramref name="needs"/> lists <paramref name="id"/> as a cue its stage performs.</summary>
    public static bool Performs(Needs needs, string id)
    {
        if (needs == null || string.IsNullOrEmpty(id))
            return false;
        foreach (var cue in needs.Cues)
        {
            bool prefix = cue.EndsWith(":", StringComparison.Ordinal);
            if (prefix ? id.Length > cue.Length && id.StartsWith(cue, StringComparison.Ordinal) : id == cue)
                return true;
        }
        return false;
    }

    /// <summary>The assert's check (PrologueDirector.AssertRoundTrip): each direction scene present loads, has
    /// what its stage needs, and cues only what its stage performs. A direction with no scene yet is a note, not
    /// a failure: its built-in direction runs. Empty when it passes.</summary>
    public static List<string> AssertFailures()
    {
        var fails = new List<string>();
        foreach (var needs in KnownNeeds())
        {
            string path = ScenePath(needs.Id);
            if (!ResourceLoader.Exists(path))
            {
                GD.Print($"[StageDirection] {needs.Id}: no scene at {path} yet; its built-in direction runs (Bake Stage Previews writes it).");
                continue;
            }
            var packed = ResourceLoader.Load<PackedScene>(path, null, ResourceLoader.CacheMode.Ignore);
            var instance = packed?.Instantiate();
            GC.KeepAlive(packed);   // as Load does (D23)
            if (instance is not Node3D root)
            {
                instance?.Free();
                fails.Add($"{path} does not load as a Node3D scene");
                continue;
            }
            foreach (var missing in Missing(needs, root))
                fails.Add($"{path}: {missing}, so the built-in direction runs instead");
            foreach (var note in Advice(needs, root))
                fails.Add($"{path}: {note}");
            root.Free();
        }
        return fails;
    }
}
