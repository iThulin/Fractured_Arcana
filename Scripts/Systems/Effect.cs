using System;

public abstract class EffectBase : IEffect {
	protected string[] _tags = Array.Empty<string>();
	public string[] Tags => _tags;
	public IEffect WithTag(string t){ _tags = new[]{t}; return this; }
	public abstract void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap);
}

public sealed class DealDamageEffect : EffectBase {
	public int Amount; public DealDamageEffect(int a){ Amount=a; }
	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
{
    int hit = 0;

	s.Log($"[DealDamageEffect] resolving for Amount={Amount}");
	if (targets == null) { s.Log("targets == null"); return; }

	s.Log($"targets.Items.Count={targets.Items.Count}");
	foreach (var obj in targets.Items)
	{
		s.Log($"  item: {(obj == null ? "null" : obj.GetType().Name)}");
		if (obj is Unit u) s.Log($"    -> Unit: {u.Name} HP {u.Stats.Health}/{u.Stats.MaxHealth}");
		if (obj is HexTile tile) s.Log($"    -> Tile: {tile.Axial} occupant={(tile.Occupant!=null?tile.Occupant.Name:"null")}");
	}

    foreach (var obj in targets.Items)
    {
        if (obj is Unit u)
        {
            u.ApplyDamage(Amount);
			s.Log($"HIT unit {u.Name}");
            hit++;
        }
        else if (obj is HexTile t && t.Occupant != null)
        {
            t.Occupant.ApplyDamage(Amount);
			s.Log($"HIT tile occupant {t.Occupant.Name} on {t.Axial}");
            hit++;
        }
    }

    s.Log($"Resolve: Deal {Amount} damage to {hit} target(s).");
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
