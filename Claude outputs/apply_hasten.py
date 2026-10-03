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

edit('Scripts/Cards/Effects/ChronomancerEffects.cs', [(
'''public sealed class FastForwardEffect : EffectBase
{
	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		if (s.Almanac == null || s.Almanac.Count == 0)
		{
			s.Log("[FastForward] No scheduled entries.");
			return;
		}

		var entry = s.Almanac
			.Where(e => e.Caster == caster)
			.OrderBy(e => e.TurnsRemaining)
			.FirstOrDefault()
			?? s.Almanac.OrderBy(e => e.TurnsRemaining).First();

		s.Log($"[FastForward] Firing scheduled entry immediately.");
		entry.Child?.Resolve(s, entry.Caster, entry.Targets, entry.Snapshot);
		s.Almanac.Remove(entry);

		// Spend 1 Foresight
		var casterUnit = s.ActiveCasterUnit;
		if (casterUnit?.Attunement is FateAttunement fate)
			fate.SpendCharges(1);
	}
}
''',
'''public sealed class FastForwardEffect : EffectBase
{
	/// <summary>How many scheduled entries fire, soonest first (99 = all of them).</summary>
	public int Count;
	/// <summary>Foresight spent when at least one entry fires (2026-10-03: was a fixed 1,
	/// which made "resolve one now, gain 1 Foresight" ladders quietly net zero).</summary>
	public int ForesightCost;

	public FastForwardEffect(int count = 1, int foresightCost = 1)
	{
		Count = Math.Max(1, count);
		ForesightCost = Math.Max(0, foresightCost);
	}

	public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
	{
		if (s.Almanac == null || s.Almanac.Count == 0)
		{
			s.Log("[FastForward] No scheduled entries.");
			return;
		}

		int fired = 0;
		while (fired < Count && s.Almanac.Count > 0)
		{
			// The caster's own entries first, soonest first; anyone's as a fallback.
			var entry = s.Almanac
				.Where(e => e.Caster == caster)
				.OrderBy(e => e.TurnsRemaining)
				.FirstOrDefault()
				?? s.Almanac.OrderBy(e => e.TurnsRemaining).First();

			s.Log($"[FastForward] Firing scheduled entry immediately.");
			s.Almanac.Remove(entry);   // removed first, so an entry that schedules again cannot loop
			entry.Child?.Resolve(s, entry.Caster, entry.Targets, entry.Snapshot);
			fired++;
		}

		var casterUnit = s.ActiveCasterUnit;
		if (ForesightCost > 0 && casterUnit?.Attunement is FateAttunement fate)
			fate.SpendCharges(ForesightCost);
	}
}
'''),])

edit('Scripts/Cards/Loader/CardScriptRegistry.Chronomancer.cs', [(
'''        RegisterEffect("fast_forward", _ => new FastForwardEffect());
''',
'''        // Fire your soonest scheduled spell(s) now.
        // { "type": "fast_forward", "count": n (99 = all), "foresight_cost": n (default 1) }
        RegisterEffect("fast_forward", n =>
        {
            int count = n.TryGetProperty("count", out var c) ? c.GetInt32() : 1;
            int cost = n.TryGetProperty("foresight_cost", out var fc) ? fc.GetInt32() : 1;
            return new FastForwardEffect(count, cost);
        });
'''),])

# ── Card data ───────────────────────────────────────────────────────
def load(name):
    return json.load(open(os.path.join(root, 'Data/Cards', name + '.json'), encoding='utf-8'))

def save(name, d):
    with open(os.path.join(root, 'Data/Cards', name + '.json'), 'w', encoding='utf-8') as f:
        f.write(json.dumps(d, indent='\t', ensure_ascii=False) + '\n')

def ff(count=1, cost=1):
    e = {"type": "fast_forward"}
    if count != 1: e["count"] = count
    if cost != 1: e["foresight_cost"] = cost
    return e
def seq(*steps): return {"type": "sequence", "steps": list(steps)}
def fore(n): return {"type": "gain_foresight", "amount": n}

def set_top_tier(d, tier, name, desc, effect, text):
    for u in d['upgrades']:
        if u['tier'] == tier and u['half'] in ('top', 'both') and u.get('top_name') == name:
            u['changes'] = [c for c in u['changes'] if c['half'] != 'top']
            u['changes'][0:0] = [
                {"half": "top", "field": "effect", "value": effect},
                {"half": "top", "field": "rules_text", "value": text},
            ]
            if u['half'] == 'top' and desc:
                u['description'] = desc
            return
    raise SystemExit(f"{d['id']}: no top tier {tier} '{name}'")

# Hasten: the text now says what fast_forward does.
d = load('chronomancer_hasten')
d['top']['effect'] = ff()
d['top']['rules_text'] = "Your soonest scheduled spell resolves now. Spend 1 Foresight."
set_top_tier(d, 1, 'Hasten+', None, ff(cost=0),
             "Your soonest scheduled spell resolves now.")
set_top_tier(d, 2, 'Snap Cast', "Free, and your next spell is cheaper.",
             seq(ff(cost=0), {"type": "cost_modify", "amount": 1, "scope": "self_next"}),
             "Your soonest scheduled spell resolves now. Your next spell costs 1 less.")
set_top_tier(d, 3, 'Time Compression', "Two at once.",
             seq(ff(count=2, cost=0), fore(1)),
             "Your two soonest scheduled spells resolve now. Gain 1 Foresight.")
set_top_tier(d, 4, 'Outside Time', "Everything you have set in motion arrives.",
             seq(ff(count=99, cost=0), fore(1)),
             "Every spell you have scheduled resolves now. Gain 1 Foresight.")
save('chronomancer_hasten', d)

# Sands of Time: the fast_forward rungs said "gain 1 Foresight" while spending 1.
d = load('chronomancer_sands_of_time')
set_top_tier(d, 2, 'Quicksilver', "Resolve it immediately.",
             seq(ff(cost=0), fore(1)),
             "Your soonest scheduled spell resolves now. Gain 1 Foresight.")
set_top_tier(d, 3, 'Time Skip', "Two scheduled spells at once.",
             seq(ff(count=2, cost=0), fore(1)),
             "Your two soonest scheduled spells resolve now. Gain 1 Foresight.")
set_top_tier(d, 4, 'Seize the Moment', "Everything scheduled, now.",
             seq(ff(count=99, cost=0), fore(2)),
             "Every spell you have scheduled resolves now. Gain 2 Foresight.")
save('chronomancer_sands_of_time', d)
print("cards ok")
