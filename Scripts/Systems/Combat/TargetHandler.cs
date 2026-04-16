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
public sealed class SelectAreaTarget : ITargetSelector {
	public int radius; public bool enemiesOnly; public bool tiles;
	public SelectAreaTarget(int r,bool enemiesOnly,bool tiles){ radius=r; this.enemiesOnly=enemiesOnly; this.tiles=tiles; }
	public bool Select(GameState s, Entity caster, out TargetSet targets){
		targets = new TargetSet(); targets.Items.Add("Area"); return true;
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
