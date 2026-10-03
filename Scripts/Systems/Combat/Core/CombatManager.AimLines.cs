using Godot;
using System.Collections.Generic;
using System.Text;

// ============================================================
// CombatManager.AimLines.cs  (partial of CombatManager)
//
// Purpose:        Ties each ranged attack to the tile it is aimed at
//                 (2026-10-03). Threat tiles say WHERE a blow lands
//                 but not WHO throws it: a melee threat sits beside
//                 its enemy, so that reads, but a ranger's or a
//                 wizard's lands far away, often under the player,
//                 with nothing linking it back. Every intent whose
//                 locked tile is more than 1 away now draws an arc from
//                 the enemy to that tile, ending in an arrowhead.
//                 Hidden-value intents draw dim, revealed ones draw
//                 hot, deferred ones draw in the temporal colour.
//                 One persistent MeshInstance3D (ImmediateMesh), created
//                 once at combat start (never from _Process), rebuilt only
//                 when an aimed intent, its enemy's TILE, or its visibility
//                 changes (not its animated world position), and never
//                 while a card or anything else is being dragged: the
//                 drag hang of 2026-10-02 came back with these arcs
//                 (2026-10-03, slice 7c), so the scene is left alone for
//                 the whole drag and catches up when it ends.
// Layer:          Combat (presentation)
// Collaborators:  EnemyIntent.TargetTile, RefreshThreatTiles (same
//                 visibility rules), HexGridManager.GetTileView
// ============================================================

public partial class CombatManager
{
    private MeshInstance3D _aimLines;
    private ImmediateMesh _aimMesh;
    private string _aimSignature;

    private const float AimRibbonWidth = 0.07f;
    private const float AimStartLift = 0.75f;
    private const float AimEndLift = 0.12f;
    private const int AimSegments = 18;

    /// <summary>Builds the arc node. Called once from _Ready (InstallAnchorHooks'
    /// neighbour), never from _Process.</summary>
    private void EnsureAimLines()
    {
        if (_aimLines != null && IsInstanceValid(_aimLines))
            return;
        _aimMesh = new ImmediateMesh();
        var mat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            VertexColorUseAsAlbedo = true,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            NoDepthTest = true,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            RenderPriority = 1,
        };
        _aimLines = new MeshInstance3D
        {
            Name = "IntentAimLines",
            Mesh = _aimMesh,
            MaterialOverride = mat,
            TopLevel = true,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(_aimLines);
    }

    /// <summary>Per frame: redraw the aim arcs when an aimed intent, its enemy's
    /// position, or its visibility changed.</summary>
    private void RefreshAimLines()
    {
        if (grid == null || enemyUnits == null || _aimLines == null || !IsInstanceValid(_aimLines))
            return;
        if (DragPayloadManager.IsDragging || GetViewport().GuiIsDragging())
            return;   // never touch the scene mid-drag; the signature catches up after

        var aims = new List<(Vector3 from, Vector3 to, Color color)>();
        var sig = new StringBuilder();
        foreach (var enemy in enemyUnits)
        {
            if (!IsValidActor(enemy) || enemy.CurrentTile == null)
                continue;
            var intent = enemy.CurrentIntent;
            if (intent?.TargetTile == null || enemy.AttackTiming == IntentTiming.Spent)
                continue;
            if (!intent.Revealed && !ShowIntentKindByDefault)
                continue;
            var target = intent.TargetTile.Value;
            if (grid.Distance(enemy.CurrentTile.Axial, target) <= 1)
                continue;   // melee reads already: the threat sits beside its enemy
            var view = grid.GetTileView(target);
            if (view == null)
                continue;

            var fromView = grid.GetTileView(enemy.CurrentTile.Axial);
            if (fromView == null)
                continue;
            Vector3 from = fromView.GlobalPosition + new Vector3(0f, AimStartLift, 0f);
            Vector3 to = view.GlobalPosition + new Vector3(0f, AimEndLift, 0f);
            Color c = enemy.AttackTiming == IntentTiming.Deferred
                ? ElementColors.Get("temporal")
                : intent.Revealed ? UITheme.TileThreatReticle : UITheme.TileThreatReticleDim;
            c.A = intent.Revealed ? 0.85f : 0.45f;
            aims.Add((from, to, c));
            sig.Append(enemy.GetInstanceId()).Append(':').Append(enemy.CurrentTile.Axial).Append('>')
               .Append(target).Append(intent.Revealed ? 'R' : 'h').Append((int)enemy.AttackTiming).Append(';');
        }

        string s = sig.ToString();
        if (s == _aimSignature)
            return;
        _aimSignature = s;

        _aimMesh.ClearSurfaces();
        if (aims.Count == 0)
        {
            _aimLines.Visible = false;
            return;
        }
        _aimLines.Visible = true;
        _aimMesh.SurfaceBegin(Mesh.PrimitiveType.Triangles);
        foreach (var (from, to, color) in aims)
            AddAimArc(from, to, color);
        _aimMesh.SurfaceEnd();
    }

    /// <summary>A flat ribbon along a raised arc, ending in an arrowhead on the tile.</summary>
    private void AddAimArc(Vector3 from, Vector3 to, Color color)
    {
        Vector3 flat = new Vector3(to.X - from.X, 0f, to.Z - from.Z);
        float length = flat.Length();
        if (length < 0.01f)
            return;
        Vector3 side = new Vector3(-flat.Z, 0f, flat.X) / length * (AimRibbonWidth * 0.5f);
        float arch = Mathf.Clamp(length * 0.22f, 0.4f, 2.2f);

        Vector3 Point(float t) =>
            from.Lerp(to, t) + new Vector3(0f, 4f * arch * t * (1f - t), 0f);

        // Stop the ribbon short of the tile so the arrowhead owns the end.
        const float headT = 0.88f;
        for (int i = 0; i < AimSegments; i++)
        {
            float t0 = headT * i / AimSegments;
            float t1 = headT * (i + 1) / AimSegments;
            Vector3 a = Point(t0), b = Point(t1);
            Color c0 = color, c1 = color;
            c0.A *= 0.35f + 0.65f * (t0 / headT);   // fades in from the enemy
            c1.A *= 0.35f + 0.65f * (t1 / headT);
            Quad(a - side, a + side, b + side, b - side, c0, c1);
        }

        Vector3 baseCentre = Point(headT);
        Vector3 wing = side * 3.2f;
        _aimMesh.SurfaceSetColor(color);
        _aimMesh.SurfaceAddVertex(baseCentre - wing);
        _aimMesh.SurfaceSetColor(color);
        _aimMesh.SurfaceAddVertex(baseCentre + wing);
        _aimMesh.SurfaceSetColor(color);
        _aimMesh.SurfaceAddVertex(to);
    }

    private void Quad(Vector3 a0, Vector3 a1, Vector3 b1, Vector3 b0, Color ca, Color cb)
    {
        _aimMesh.SurfaceSetColor(ca); _aimMesh.SurfaceAddVertex(a0);
        _aimMesh.SurfaceSetColor(ca); _aimMesh.SurfaceAddVertex(a1);
        _aimMesh.SurfaceSetColor(cb); _aimMesh.SurfaceAddVertex(b1);
        _aimMesh.SurfaceSetColor(ca); _aimMesh.SurfaceAddVertex(a0);
        _aimMesh.SurfaceSetColor(cb); _aimMesh.SurfaceAddVertex(b1);
        _aimMesh.SurfaceSetColor(cb); _aimMesh.SurfaceAddVertex(b0);
    }
}
