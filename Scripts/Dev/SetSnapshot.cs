using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// SetSnapshot.cs   (dev tooling)
//
// Purpose:        Saves a built hall as a preview for the editor (stage
//                 direction SD1, docs/stage_direction_v1.md section 3),
//                 and saves a node tree as a scene (the bake's direction
//                 scenes too).
//
//                 A preview is a copy of the hall, its board, the light
//                 and the environment, as plain nodes: no scripts, so
//                 nothing runs when the editor opens it. It is saved in
//                 six parts (StageDirection.PreviewParts): the walls and
//                 the ceiling one each, so the editor can hide whichever
//                 is in the way as the game's cutaway does, and the body.
//                 Each part is laid out from the set's origin
//                 (HallSet.SetOrigin), as direction scenes are. The body
//                 also carries a marker for each of the hall's named
//                 points (HallSet.Landmarks).
//
//                 A model from res://Assets/ stays linked as an instance,
//                 so a model exported again shows the next time the
//                 preview opens; anything else is copied in. Tints and
//                 other changes the game makes to a model are not kept.
//                 Collision shapes are left out.
//
//                 Saving: into the project; when it cannot be written (a
//                 game not run from the editor), under user://stage_bake/
//                 at the same path.
// Layer:          Dev tooling
// Collaborators:  StageBake, HallSet, StageDirection (the parts and
//                 paths), HexGridManager, CameraController
// ============================================================

/// <summary>Saves hall previews and bake scenes (see the file header).</summary>
public static class SetSnapshot
{
    private const string ArtRoot = "res://Assets/";
    private const string FallbackRoot = "user://stage_bake/";
    private const string SetNode = "Set";
    private const string LandmarksNode = "Landmarks";
    private const float MarkerSize = 0.5f;

    /// <summary>Saves the preview <paramref name="preview"/> of <paramref name="hall"/>, as it stands in
    /// <paramref name="battlefield"/>, in its six parts. Returns where each file went; <paramref name="errors"/>
    /// lists any that could not be written.</summary>
    public static List<string> SavePreview(Node3D battlefield, HallSet hall, string preview, out List<string> errors)
    {
        errors = new List<string>();
        var written = new List<string>();
        Vector3 origin = hall.SetOrigin;
        var walls = new HashSet<string>();
        foreach (var (node, _) in StageDirection.PreviewParts)
            if (node != StageDirection.BodyPart)
                walls.Add(node);

        // The body: the board, the light and the environment, and the hall but for its walls and ceiling.
        var body = new Node3D { Name = StageDirection.BodyPart };
        var bodySet = new Node3D { Name = SetNode, Position = -origin };
        body.AddChild(bodySet);
        foreach (Node child in battlefield.GetChildren())
        {
            if (child is CameraController || child is Camera3D || child is CanvasLayer)
                continue;
            if (child is HexGridManager grid)
            {
                CopyGrid(grid, hall, bodySet);
                continue;
            }
            if (child is not WorldEnvironment && !HasVisuals(child))
                continue;
            var copy = Copy(child);
            if (copy == null)
                continue;
            if (child is Node3D live3 && copy is Node3D copy3)
                copy3.Transform = live3.GlobalTransform;
            if (copy is WorldEnvironment world && world.Environment != null)
                world.Environment = (Godot.Environment)world.Environment.Duplicate();   // as the recipe dressed it
            bodySet.AddChild(copy);
        }
        foreach (Node child in hall.GetChildren())
        {
            if (walls.Contains(child.Name) || !HasVisuals(child))
                continue;
            var copy = Copy(child);
            if (copy == null)
                continue;
            if (child is Node3D live3 && copy is Node3D copy3)
                copy3.Transform = live3.GlobalTransform;
            bodySet.AddChild(copy);
        }
        var landmarks = new Node3D { Name = LandmarksNode };
        body.AddChild(landmarks);
        foreach (var (name, point) in hall.Landmarks())
            landmarks.AddChild(new Marker3D { Name = name, Position = point - origin, GizmoExtents = MarkerSize });
        SaveFreeing(body, StageDirection.PreviewPartPath(preview, PartFile(StageDirection.BodyPart)), written, errors);

        // The walls and the ceiling, a part each.
        foreach (var (node, file) in StageDirection.PreviewParts)
        {
            if (node == StageDirection.BodyPart)
                continue;
            var live = hall.GetNodeOrNull<Node3D>(node);
            if (live == null)
            {
                errors.Add($"{preview}: the hall has no {node}");
                continue;
            }
            if (Copy(live) is not Node3D copy)
            {
                errors.Add($"{preview}: {node} would not copy");
                continue;
            }
            copy.Transform = live.GlobalTransform;
            copy.Visible = true;
            var part = new Node3D { Name = node };
            var set = new Node3D { Name = SetNode, Position = -origin };
            part.AddChild(set);
            set.AddChild(copy);
            SaveFreeing(part, StageDirection.PreviewPartPath(preview, file), written, errors);
        }
        return written;
    }

    /// <summary>Saves <paramref name="root"/> and everything under it as a scene at <paramref name="path"/> (a
    /// .scn is saved compressed). With <paramref name="inlineNonArt"/>, an instance of anything outside
    /// res://Assets/ is saved as plain nodes; without, every instance stays one (a direction's preview
    /// placeholders). Returns where it went, or "" with the reason in <paramref name="error"/>. The tree is
    /// left as it is; the caller frees it.</summary>
    public static string SaveScene(Node root, string path, bool inlineNonArt, out string error)
    {
        error = "";
        Own(root, root, inlineNonArt);
        var packed = new PackedScene();
        var packing = packed.Pack(root);
        if (packing != Error.Ok)
        {
            error = $"{path}: it would not pack ({packing})";
            return "";
        }
        var flags = path.EndsWith(".scn", StringComparison.Ordinal)
            ? ResourceSaver.SaverFlags.Compress
            : ResourceSaver.SaverFlags.None;
        foreach (string target in new[] { path, FallbackRoot + path.Substring("res://".Length) })
        {
            DirAccess.MakeDirRecursiveAbsolute(ProjectSettings.GlobalizePath(target.GetBaseDir()));
            var saving = ResourceSaver.Save(packed, target, flags);
            if (saving == Error.Ok)
                return target;
            error = $"{target}: it would not save ({saving})";
        }
        return "";
    }

    private static string PartFile(string node)
    {
        foreach (var (part, file) in StageDirection.PreviewParts)
            if (part == node)
                return file;
        return node.ToLowerInvariant();
    }

    private static void SaveFreeing(Node root, string path, List<string> written, List<string> errors)
    {
        string where = SaveScene(root, path, true, out string error);
        root.Free();
        if (where.Length > 0)
            written.Add(where);
        else
            errors.Add(error);
    }

    /// <summary>The grid's board, without the hall (which is copied in parts), placed as it stands.</summary>
    private static void CopyGrid(HexGridManager grid, HallSet hall, Node3D into)
    {
        if (Copy(grid) is not Node3D copy)
            return;
        var hallCopy = copy.GetNodeOrNull(new NodePath(hall.Name));
        if (hallCopy != null)
        {
            copy.RemoveChild(hallCopy);
            hallCopy.Free();
        }
        copy.Transform = grid.GlobalTransform;
        into.AddChild(copy);
    }

    /// <summary>A copy of <paramref name="live"/> with no scripts, signals or groups, its collision shapes and
    /// its empty branches left out.</summary>
    private static Node Copy(Node live)
    {
        var copy = live.Duplicate(0);
        if (copy != null)
            Prune(copy);
        return copy;
    }

    /// <summary>Takes out of <paramref name="node"/> its collision shapes and every branch with nothing to see.
    /// A model from res://Assets/ is kept whole.</summary>
    private static void Prune(Node node)
    {
        if (IsArt(node))
            return;
        foreach (Node child in node.GetChildren())
        {
            if (child is CollisionShape3D || child is CollisionPolygon3D || (child is not WorldEnvironment && !HasVisuals(child)))
            {
                node.RemoveChild(child);
                child.Free();
                continue;
            }
            Prune(child);
        }
    }

    /// <summary>True when <paramref name="node"/> is, or holds, something the editor would draw.</summary>
    private static bool HasVisuals(Node node)
    {
        if (node is VisualInstance3D || node is WorldEnvironment || IsArt(node))
            return true;
        foreach (Node child in node.GetChildren())
            if (HasVisuals(child))
                return true;
        return false;
    }

    private static bool IsArt(Node node) => (node.SceneFilePath ?? "").StartsWith(ArtRoot, StringComparison.Ordinal);

    /// <summary>Makes <paramref name="root"/> the owner of everything under it, so it is saved. An instance's own
    /// children belong to its scene: a model from res://Assets/ (and, without <paramref name="inlineNonArt"/>, any
    /// instance) is saved as a link to its file; with it, any other instance becomes plain nodes.</summary>
    private static void Own(Node node, Node root, bool inlineNonArt)
    {
        foreach (Node child in node.GetChildren())
        {
            child.Owner = root;
            string scene = child.SceneFilePath ?? "";
            if (scene.Length > 0)
            {
                if (!inlineNonArt || scene.StartsWith(ArtRoot, StringComparison.Ordinal))
                    continue;
                child.SceneFilePath = "";
            }
            Own(child, root, inlineNonArt);
        }
    }
}
