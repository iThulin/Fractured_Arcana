using Godot;
using System.Collections.Generic;

// ============================================================
// CanopyOcclusion.cs
//
// Per-frame feeder for painterly_canopy.gdshader's unit-occlusion cutout.
// Writes the active units' positions into the canopy material's `occluders`
// array so the canopy crown clears above each unit and the board stays readable.
//
// SETUP:
//   - Assign CanopyMaterial = the SAME ShaderMaterial instance the canopy
//     MultiMeshInstance(s) use. (When the scatter lands, cache one material and
//     hand it here; for a hand-placed test, assign the .tres on the blobs.)
//   - Put your units in the OccluderGroup (default "units"), or set UnitsRoot
//     and they'll be gathered from its Node3D children.
//   - Feet Y is the unit's GlobalPosition.Y; if your unit origin is at the
//     chest/centre, set FeetYOffset negative to drop the clear-from height.
// ============================================================

public partial class CanopyOcclusion : Node3D
{
	private const int MaxOccluders = 16; // must match the shader array size

	[Export] public ShaderMaterial CanopyMaterial;
	[Export] public string OccluderGroup = "units";
	[Export] public NodePath UnitsRoot;            // optional: gather Node3D children instead of a group
	[Export] public float FeetYOffset = 0.0f;      // shift the clear-from height relative to unit origin

	private readonly Vector4[] _buf = new Vector4[MaxOccluders];

	public override void _Process(double delta)
	{
		if (CanopyMaterial == null)
			return;

		int count = 0;

		void Add(Node3D n)
		{
			if (count >= MaxOccluders || n == null || !n.IsInsideTree())
				return;
			Vector3 p = n.GlobalPosition;
			_buf[count++] = new Vector4(p.X, p.Z, p.Y + FeetYOffset, 1.0f);
		}

		if (UnitsRoot != null && !UnitsRoot.IsEmpty)
		{
			var root = GetNodeOrNull(UnitsRoot);
			if (root != null)
				foreach (Node c in root.GetChildren())
					if (c is Node3D n3)
						Add(n3);
		}
		else
		{
			foreach (Node n in GetTree().GetNodesInGroup(OccluderGroup))
				if (n is Node3D n3)
					Add(n3);
		}

		// Pad the unused tail so stale entries can't leak (w = 0 = inactive).
		for (int i = count; i < MaxOccluders; i++)
			_buf[i] = Vector4.Zero;

		// Godot marshals a Vector4[] to a vec4[] uniform.
		CanopyMaterial.SetShaderParameter("occluders", _buf);
		CanopyMaterial.SetShaderParameter("occluder_count", count);
	}
}