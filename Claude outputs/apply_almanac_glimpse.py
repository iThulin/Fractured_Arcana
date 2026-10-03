import sys, os, json
root = sys.argv[1]

def edit(rel, pairs):
    p = os.path.join(root, rel)
    s = open(p, encoding='utf-8').read()
    for old, new in pairs:
        n = s.count(old)
        assert n == 1, f"{rel}: expected 1 match, got {n} for:\n{old}"
        s = s.replace(old, new)
    open(p, 'w', encoding='utf-8').write(s)
    print("ok", rel)

# ── Almanac: entries carry a real name; internal timers are hidden ──
edit('Scripts/Cards/Effects/Effect.cs', [(
'''	/// <summary>Display name shown in the turn-track UI (optional).</summary>
	public string Label;
''',
'''	/// <summary>Display name shown in the Almanac list and on the target tile.</summary>
	public string Label;

	/// <summary>Bookkeeping timers (a decoy's expiry) that are not spells the player
	/// scheduled. Kept out of the Almanac view.</summary>
	public bool Hidden;
''')])

edit('Scripts/Systems/GameStateManager.cs', [(
'''    public List<AlmanacEntry> Almanac = new();
''',
'''    public List<AlmanacEntry> Almanac = new();

    /// <summary>Name of the card half the resolver is resolving right now (null between
    /// resolutions). Lets effects that outlive the cast, such as a scheduled spell,
    /// name themselves after the card that made them.</summary>
    public string ResolvingAbilityName;
''')])

edit('Scripts/Systems/Combat/Core/RulesManager.cs', [
(
'''        s.ResolutionDepth++;   // GameState.RequestCardChoice: a resolver pass is live
        try
        {
''',
'''        s.ResolutionDepth++;   // GameState.RequestCardChoice: a resolver pass is live
        var prevAbilityName = s.ResolvingAbilityName;
        s.ResolvingAbilityName = item.Ability?.Name;   // Almanac labels (schedule)
        try
        {
'''),
(
'''            s.ActiveCasterUnit = prevCaster;
            Unit.AmbientDamageSource = prevDamageSource;
        }
''',
'''            s.ActiveCasterUnit = prevCaster;
            Unit.AmbientDamageSource = prevDamageSource;
            s.ResolvingAbilityName = prevAbilityName;
        }
'''),
])

edit('Scripts/Cards/Effects/ChronomancerEffects.cs', [
(
'''			Snapshot = snap,
			Label = Child?.GetType().Name ?? "Scheduled"
		});
''',
'''			Snapshot = snap,
			// The card's own name, not the effect class: this is what the Almanac
			// list and the tile marker show the player.
			Label = !string.IsNullOrEmpty(s.ResolvingAbilityName) ? s.ResolvingAbilityName : "Scheduled spell"
		});
'''),
(
'''				Label = "Decoy Expire"
			};
''',
'''				Label = "Decoy Expire",
				Hidden = true,   // a timer, not a spell the player scheduled
			};
'''),
(
'''			s.ActiveEffects.Add(new DelayedDamageEffect(coord, DamagePerTick, Ticks, caster));
''',
'''			s.ActiveEffects.Add(new DelayedDamageEffect(coord, DamagePerTick, Ticks, caster)
			{
				Label = s.ResolvingAbilityName,   // shown in the Almanac view
			});
'''),
(
'''public class DelayedDamageEffect : PersistentEffect
{
	public Vector2I TargetCoord;
	public int DamagePerTick;
''',
'''public class DelayedDamageEffect : PersistentEffect
{
	public Vector2I TargetCoord;
	public int DamagePerTick;
	/// <summary>The card that made it (Foregone Conclusion), for the Almanac view.</summary>
	public string Label;
'''),
# ── Glimpse and every "reveal" card: peek_intent now reveals intents ──
(
'''/// <summary>
/// Reveals enemy intent (currently logs HP/status to console and grants
/// Foresight). Full HUD reveal is future UI work.
/// JSON: { "type": "peek_intent" }
/// </summary>
public sealed class PeekIntentEffect : EffectBase
{
	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var casterUnit = s.ActiveCasterUnit;

		if (s?.UnitsInPlay == null)
			return;

		s.Log("[PeekIntent] Enemy intel:");
		foreach (var unit in s.UnitsInPlay)
		{
			if (unit == null || !unit.Stats.IsAlive)
				continue;
			if (casterUnit != null && unit.TeamId == casterUnit.TeamId)
				continue;
''',
'''/// <summary>
/// Reveals enemy intents: value and marked tiles, until the enemy plans again.
/// <see cref="Range"/> limits it to enemies within that many tiles of the caster
/// (0 = every enemy). Also logs each enemy's condition, for the Adept cards whose text
/// promises it. (2026-10-03: this used to ONLY log, so Glimpse, Survey, Flare and
/// Arcane Sight revealed nothing on screen.)
/// JSON: { "type": "peek_intent", "range": n }
/// </summary>
public sealed class PeekIntentEffect : EffectBase
{
	public int Range;
	public PeekIntentEffect(int range = 0) { Range = Math.Max(0, range); }

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		var casterUnit = s.ActiveCasterUnit;

		if (s?.UnitsInPlay == null)
			return;

		s.Log("[PeekIntent] Enemy intel:");
		int revealed = 0;
		foreach (var unit in s.UnitsInPlay)
		{
			if (unit == null || !unit.Stats.IsAlive)
				continue;
			if (casterUnit != null && unit.TeamId == casterUnit.TeamId)
				continue;
			if (Range > 0 && casterUnit?.CurrentTile != null && unit.CurrentTile != null && s.Grid != null
				&& s.Grid.Distance(casterUnit.CurrentTile.Axial, unit.CurrentTile.Axial) > Range)
				continue;

			if (IntentTime.Reveal(unit))
				revealed++;
'''),
(
'''			s.Log($"  {unit.Name}: {unit.Stats.Health}/{unit.Stats.MaxHealth}HP " +
				  $"| Unit={unit.DefinitionId} | Status=[{statuses}]");
		}
	}
}
''',
'''			s.Log($"  {unit.Name}: {unit.Stats.Health}/{unit.Stats.MaxHealth}HP " +
				  $"| Unit={unit.DefinitionId} | Status=[{statuses}]");
		}
		s.Log($"[PeekIntent] Revealed {revealed} intent(s){(Range > 0 ? $" within {Range}" : "")}.");
	}
}
'''),
])

edit('Scripts/Cards/Loader/CardScriptRegistry.Chronomancer.cs', [(
'''        RegisterEffect("peek_intent", _ => new PeekIntentEffect().WithTag("Foresight"));
''',
'''        // { "type": "peek_intent", "range": n }  range 0 (default) = every enemy
        RegisterEffect("peek_intent", n =>
        {
            int range = n.TryGetProperty("range", out var r) ? r.GetInt32() : 0;
            return new PeekIntentEffect(range).WithTag("Foresight");
        });
'''),])

# ── UI: Almanac list in the unit panel ──────────────────────────────
edit('Scripts/UI/CombatUI.cs', [
(
'''		vbox.AddChild(_attunementSection);
''',
'''		vbox.AddChild(_attunementSection);

		// Almanac (class_identity_chronomancer_v1 §2c): the spells waiting to land.
		// Built once; SetAlmanac only retexts and shows or hides it.
		_almanacSection = new VBoxContainer { Name = "AlmanacSection", Visible = false };
		_almanacSection.AddThemeConstantOverride("separation", 2);
		_almanacSection.AddChild(MakeLabel("ALMANAC", UITheme.FontSizeSmall, ElementColors.Get("temporal")));
		_almanacText = MakeLabel("", UITheme.FontSizeSmall, UITheme.TextPrimary);
		_almanacText.AutowrapMode = TextServer.AutowrapMode.WordSmart;
		_almanacSection.AddChild(_almanacText);
		vbox.AddChild(_almanacSection);
'''),
(
'''	public VBoxContainer AttunementSection => _attunementSection;
''',
'''	public VBoxContainer AttunementSection => _attunementSection;

	private VBoxContainer _almanacSection;
	private Label _almanacText;

	/// <summary>Shows the scheduled-spell list (one entry per line), or hides the
	/// section when <paramref name="text"/> is null or empty.</summary>
	public void SetAlmanac(string text)
	{
		if (_almanacSection == null)
			return;
		bool any = !string.IsNullOrEmpty(text);
		_almanacText.Text = any ? text : "";
		_almanacSection.Visible = any;
	}
'''),
])

# ── Board: a marker on each scheduled target tile ──────────────────
edit('Scripts/Systems/Combat/Terrain/HexTile.cs', [
(
'''    private Label3D _growthPreviewLabel;     // next growth tick forecast (druid §2a)
''',
'''    private Label3D _growthPreviewLabel;     // next growth tick forecast (druid §2a)
    private Label3D _scheduleLabel;          // Almanac: a scheduled spell aimed here (chrono §2c)
'''),
(
'''    private void UpdateGrowthLabel(int stage)
''',
'''    /// <summary>
    /// Almanac marker (class_identity_chronomancer_v1 §2c): names the scheduled spell(s)
    /// that will land on this tile and when. Null or empty hides it. The label is
    /// created on first use and then only retexted, never freed.
    /// </summary>
    public void SetScheduleMark(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            if (_scheduleLabel != null)
                _scheduleLabel.Visible = false;
            return;
        }

        if (_scheduleLabel == null)
        {
            _scheduleLabel = new Label3D
            {
                Name = "ScheduleMark",
                FontSize = UITheme.Label3DSmall,
                OutlineSize = UITheme.Label3DOutlineSize,
                OutlineModulate = UITheme.Label3DOutline,
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                NoDepthTest = true,
                Position = new Vector3(0f, 0.9f, 0f),
                Modulate = ElementColors.Get("temporal").Lightened(0.2f),
            };
            CallDeferred("add_child", _scheduleLabel);
        }

        _scheduleLabel.Text = text;
        _scheduleLabel.Visible = true;
    }

    private void UpdateGrowthLabel(int stage)
'''),
])

edit('Scripts/Systems/Combat/Core/CombatManager.cs', [(
'''        SyncMoveZoneDim();   // derived every frame, so no hover-event ordering can strand it
''',
'''        SyncMoveZoneDim();   // derived every frame, so no hover-event ordering can strand it
        RefreshAlmanacView(); // Almanac list + tile markers; repaints only when it changed
''')])

# ── Glimpse base half: within 6, as printed ─────────────────────────
p = os.path.join(root, 'Data/Cards/chronomancer_glimpse.json')
d = json.load(open(p, encoding='utf-8'))
assert d['top']['effect'] == {"type": "peek_intent"}, d['top']['effect']
d['top']['effect'] = {"type": "peek_intent", "range": 6}
with open(p, 'w', encoding='utf-8') as f:
    f.write(json.dumps(d, indent='\t', ensure_ascii=False) + '\n')
print("ok", 'Data/Cards/chronomancer_glimpse.json')
