using Godot;

public partial class ImbuementOverlay : Node3D
{
    [Export] public MeshInstance3D Mesh;

    private ShaderMaterial _material;
    private TileElementType _current = TileElementType.None;

    // Tints per element. These are deliberately punchy so the shimmer reads
    // at a glance even when many tiles are imbued.
    private static readonly System.Collections.Generic.Dictionary<TileElementType, Color> ElementTints =
        new()
        {
            { TileElementType.Fire,      new Color(1.00f, 0.35f, 0.08f, 1.0f) },
            { TileElementType.Frost,     new Color(0.55f, 0.85f, 1.00f, 1.0f) },
            { TileElementType.Lightning, new Color(0.85f, 0.75f, 1.00f, 1.0f) },
            { TileElementType.Earth,     new Color(0.65f, 0.45f, 0.20f, 1.0f) },
            { TileElementType.Water,     new Color(0.25f, 0.55f, 0.95f, 1.0f) },
            { TileElementType.Air,       new Color(0.85f, 0.95f, 0.80f, 1.0f) },
            { TileElementType.Arcane,    new Color(0.80f, 0.30f, 1.00f, 1.0f) },
            { TileElementType.Shadow,    new Color(0.35f, 0.15f, 0.45f, 1.0f) },
        };

    public override void _Ready()
    {
        if (Mesh == null)
            Mesh = GetNodeOrNull<MeshInstance3D>("Mesh");

        if (Mesh != null)
        {
            // Duplicate the shader material so each overlay has its own tint.
            var src = Mesh.GetActiveMaterial(0) as ShaderMaterial;
            if (src != null)
            {
                _material = (ShaderMaterial)src.Duplicate();
                Mesh.SetSurfaceOverrideMaterial(0, _material);
            }
        }

        Visible = false;
    }

    public void SetElement(TileElementType element)
    {
        _current = element;

        if (element == TileElementType.None)
        {
            Visible = false;
            return;
        }

        if (_material != null && ElementTints.TryGetValue(element, out var tint))
        {
            _material.SetShaderParameter("tint_color", tint);
        }

        Visible = true;
    }

    public TileElementType CurrentElement => _current;
}
