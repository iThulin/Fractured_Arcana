using Godot;
using System;

public sealed class SelectUnitTarget : ITargetSelector {
	public bool enemyOnly; public int range; public bool los; public bool friendlyOnly;
	public SelectUnitTarget(bool enemyOnly=true,int range=4,bool los=true,bool friendlyOnly=false){
		this.enemyOnly=enemyOnly; this.range=range; this.los=los; this.friendlyOnly=friendlyOnly;
	}
	public bool Select(GameState s, Entity caster, out TargetSet targets){
		targets = new TargetSet(); targets.Items.Add("DummyUnit"); // TODO: hook your 3D pick/grid
		return true;
	}
}
public sealed class SelectTileTarget : ITargetSelector {
	public int range; public SelectTileTarget(int r=4){ range=r; }
	public bool Select(GameState s, Entity caster, out TargetSet targets){
		targets = new TargetSet(); targets.Items.Add("Tile(0,0)"); return true;
	}
}
public sealed class SelectAreaTarget : ITargetSelector
{
    public int Radius;
    public bool EnemiesOnly;
    public bool Tiles;

    public SelectAreaTarget(int r, bool enemiesOnly, bool tiles)
    {
        Radius = r;
        EnemiesOnly = enemiesOnly;
        Tiles = tiles;
    }

    public bool Select(GameState s, Entity caster, out TargetSet targets)
    {
        targets = new TargetSet();

        // Find the center — for card-drop targeting this gets
        // overridden by TryCastWithTargets, but for auto-select
        // we use the caster's position
        Unit casterUnit = null;
        if (caster == s.PlayerA) casterUnit = s.PlayerUnit;
        else if (caster == s.PlayerB) casterUnit = s.EnemyUnit;

        if (casterUnit?.CurrentTile == null) return false;

        var center = casterUnit.CurrentTile.Axial;

        foreach (var unit in s.UnitsInPlay)
        {
            if (unit == null || !unit.Stats.IsAlive || unit.CurrentTile == null)
                continue;

            // Skip allies if enemies_only
            if (EnemiesOnly && unit.TeamId == casterUnit.TeamId)
                continue;

            int dist = s.Grid.Distance(center, unit.CurrentTile.Axial);
            if (dist <= Radius)
                targets.Items.Add(unit);
        }

        return true;
    }
}
public sealed class SelectSelfTarget : ITargetSelector {
	public bool Select(GameState s, Entity caster, out TargetSet targets){ targets=new TargetSet(); targets.Items.Add(caster); return true; }
}
public sealed class SelectGlobalTarget : ITargetSelector {
	public bool Select(GameState s, Entity caster, out TargetSet targets){ targets=new TargetSet(); return true; }
}
public sealed class SelectByTagTarget : ITargetSelector {
	public string tag; public bool enemyOnly;
	public SelectByTagTarget(string tag,bool enemyOnly=false){ this.tag=tag; this.enemyOnly=enemyOnly; }
	public bool Select(GameState s, Entity caster, out TargetSet targets){ targets=new TargetSet(); targets.Items.Add(tag); return true; }
}
