using System;

public abstract class EffectBase : IEffect {
	protected string[] _tags = Array.Empty<string>();
	public string[] Tags => _tags;
	public IEffect WithTag(string t)
	{ 
		_tags = new[]{t};
		 return this;
	}
	public abstract void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap);
}

public sealed class DealDamageEffect : EffectBase {
	public int Amount;
	public DealDamageEffect(int a)
	{
		Amount=a;
}
	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		int hit = 0;

		s.Log($"[DealDamageEffect] resolving for Amount={Amount}");
		if (targets == null)
		{
			s.Log("targets == null");
			return;
		}

		s.Log($"targets.Items.Count={targets.Items.Count}");


        foreach (var obj in targets.Items)
        {
            s.Log($"  item: {(obj == null ? "null" : obj.GetType().Name)}");

            if (obj is Unit u)
                s.Log($"    -> Unit: {u.Name} HP {u.Stats.Health}/{u.Stats.MaxHealth}");

            if (obj is TileData td)
                s.Log($"    -> TileData: {td.Axial} occupant={(td.Occupant != null ? td.Occupant.Name : "null")}");

            if (obj is HexTile tile)
                s.Log($"    -> TileView: {tile.Axial}");
        }

		foreach (var obj in targets.Items)
        {
            if (obj is Unit u)
            {
                u.ApplyDamage(Amount);
                s.Log($"HIT unit {u.Name}");
                hit++;
            }
            else if (obj is TileData td && td.Occupant != null)
            {
                td.Occupant.ApplyDamage(Amount);
                s.Log($"HIT tile occupant {td.Occupant.Name} on {td.Axial}");
                hit++;
            }
            else if (obj is HexTile tileView)
            {
                var tileData = ResolveTileDataFromView(s, tileView);
                if (tileData != null && tileData.Occupant != null)
                {
                    tileData.Occupant.ApplyDamage(Amount);
                    s.Log($"HIT tile occupant {tileData.Occupant.Name} on {tileData.Axial}");
                    hit++;
                }
            }
        }

		s.Log($"Resolve: Deal {Amount} damage to {hit} target(s).");
	}
	private TileData ResolveTileDataFromView(GameState s, HexTile tileView)
	{
		if (tileView == null)
			return null;

		var grid = GetGridManager(s);
		if (grid == null)
		{
			s.Log("ResolveTileDataFromView: could not find HexGridManager.");
			return null;
		}

		return grid.GetTile(tileView.Axial);
	}

	private HexGridManager GetGridManager(GameState s)
	{
		return s?.Grid;
	}
}
public sealed class DashEffect : EffectBase {
	public int Tiles; public DashEffect(int t){ Tiles=t; }
	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap) {
		s.Log($"Resolve: Move {Tiles} tile(s).");
	}
}
public sealed class GiveShieldEffect : EffectBase {
	public int Shield; public GiveShieldEffect(int v){ Shield=v; }
	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap) {
		s.Log($"Resolve: Gain {Shield} shield.");
	}
}
public sealed class DrawCardsEffect : EffectBase {
	public int Count; public DrawCardsEffect(int n){ Count=n; }
	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap) {
		s.Draw(caster, Count);
		s.Log($"Resolve: Draw {Count}.");
	}
}
public sealed class SummonEffect : EffectBase {
	public string UnitKind; public int Count;
	public SummonEffect(string kind, int count) { UnitKind=kind; Count=count; }
	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap) {
		s.Log($"Resolve: Summon {Count}x {UnitKind}.");
	}
}
public sealed class NoOpEffect : EffectBase {
	public string Text; public NoOpEffect(string t){ Text=t; }
	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap) {
		s.Log($"Resolve: NoOp ({Text}).");
	}
}
