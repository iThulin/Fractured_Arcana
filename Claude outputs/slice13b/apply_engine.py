import pathlib
def edit(rel, pairs):
    p = pathlib.Path(rel); s = p.read_text(encoding='utf-8')
    for o, n in pairs:
        assert s.count(o) == 1, (rel, o[:80], s.count(o))
        s = s.replace(o, n)
    p.write_text(s, encoding='utf-8')

E = 'Scripts/Cards/Effects/EnchanterEngine.cs'
edit(E, [
# NameCondition boon fields
('''    public List<(Unit unit, NameCondition name)> Group;
''',
'''    public List<(Unit unit, NameCondition name)> Group;

    // ── Boons: Names on allies (Benefactor, slice 13b). An ally's Name never breaks. ──
    /// <summary>Name of Courage: +N damage to its spells and attacks.</summary>
    public int BonusDamage;
    /// <summary>Name of Courage: it cannot be Weakened.</summary>
    public bool ImmuneWeakened;
    /// <summary>Name of Warding: whoever strikes it is Weakened 1 turn and the Namer gains 1 Weave.</summary>
    public bool Warding;
    /// <summary>Litany of Names: its first card each turn costs N less.</summary>
    public int FirstCardDiscount;

    /// <summary>True for a Name that helps its bearer (written on an ally).</summary>
    public bool IsBoon => BonusDamage > 0 || ImmuneWeakened || Warding || FirstCardDiscount > 0;
'''),
('''        var parts = new List<string>();
        string words = TriggerWords();
        if (Damage > 0 || (!Backfire && Shuns == null && !Misdirect))
        {''',
'''        var parts = new List<string>();
        string words = TriggerWords();
        if (IsBoon)
        {
            if (BonusDamage > 0) parts.Add($"+{BonusDamage} damage");
            if (ImmuneWeakened) parts.Add("Can't be Weakened");
            if (Warding) parts.Add("Attackers Weakened");
            if (FirstCardDiscount > 0) parts.Add($"First card -{FirstCardDiscount}");
            return string.Join("; ", parts);
        }
        if (Damage > 0 || (!Backfire && Shuns == null && !Misdirect))
        {'''),
('''        var parts = new List<string>();
        string words = TriggerWords();
        if (Damage > 0)
        {''',
'''        var parts = new List<string>();
        string words = TriggerWords();
        if (IsBoon)
        {
            if (BonusDamage > 0) parts.Add($"its spells and attacks deal {BonusDamage} more damage");
            if (ImmuneWeakened) parts.Add("it cannot be Weakened");
            if (Warding) parts.Add("whoever strikes it is Weakened for 1 turn and its Namer gains 1 Weave");
            if (FirstCardDiscount > 0) parts.Add($"its first card each turn costs {FirstCardDiscount} less");
            return $"Named by {Source}: " + string.Join(", ", parts) + ".";
        }
        if (Damage > 0)
        {'''),
# Names statics: seventh name, boons
('''    /// <summary>Combat start: forget the last fight's rest-of-fight effects.</summary>
    public static void ResetForCombat()
    {
        LastBreakRound = -99;
        CompoundGrowth.Clear();
        DrawOnBreak.Clear();
    }''',
'''    /// <summary>The Seventh Name (rest of fight): team -> (extra turns on every Name it
    /// writes, damage of the glyph prepared beneath each enemy it Names).</summary>
    public static readonly Dictionary<int, (int extraTurns, int glyphDamage)> SeventhName = new();

    /// <summary>Combat start: forget the last fight's rest-of-fight effects.</summary>
    public static void ResetForCombat()
    {
        LastBreakRound = -99;
        CompoundGrowth.Clear();
        DrawOnBreak.Clear();
        SeventhName.Clear();
    }

    /// <summary>Before a Name is written: The Seventh Name lengthens it.</summary>
    public static void Lengthen(NameCondition name)
    {
        if (name != null && SeventhName.TryGetValue(name.OwnerTeam, out var sn) && sn.extraTurns > 0)
            name.TurnsRemaining += sn.extraTurns;
    }

    /// <summary>After an enemy is Named: The Seventh Name prepares a glyph beneath it
    /// (it pays at the start of its turn if it is still standing there).</summary>
    public static void GlyphBeneath(GameState s, Unit owner, Unit target)
    {
        if (s?.Glyphs == null || owner == null || target?.CurrentTile == null || target.TeamId == owner.TeamId)
            return;
        if (!SeventhName.TryGetValue(owner.TeamId, out var sn) || sn.glyphDamage <= 0)
            return;
        var g = s.Glyphs.Prepare(target.CurrentTile, owner, ng =>
        {
            ng.Trigger = GlyphTrigger.StartOfTurn;
            ng.Damage = sn.glyphDamage;
            ng.DurationTurns = 3;
        });
        if (g != null)
        {
            g.SourceName = "The Seventh Name";
            g.GlyphText = "If it starts its turn here, it takes {damage}.";
            s.Log($"[SeventhName] A glyph is written beneath {target.Name}.");
        }
    }

    /// <summary>A boon starts: Courage's damage is added (and taken off again in
    /// <see cref="EndBoon"/>), and Courage clears any Weakened already on it.</summary>
    public static void StartBoon(Unit u, NameCondition n)
    {
        if (u == null || n == null || !GodotObject.IsInstanceValid(u))
            return;
        if (n.BonusDamage > 0)
        {
            u.BonusSpellDamage += n.BonusDamage;
            u.AttackDamage += n.BonusDamage;
        }
        if (n.ImmuneWeakened && u.HasStatus("weakened"))
            u.RemoveStatus("weakened");
    }

    public static void EndBoon(Unit u, NameCondition n)
    {
        if (u == null || n == null || !GodotObject.IsInstanceValid(u))
            return;
        if (n.BonusDamage > 0)
        {
            u.BonusSpellDamage -= n.BonusDamage;
            u.AttackDamage -= n.BonusDamage;
        }
    }

    /// <summary>Litany of Names: the discount on this unit's first card this turn.</summary>
    public static int FirstCardDiscount(Unit u)
    {
        if (u == null || u.Stats.HasPlayedCardThisTurn || u.Names.Count == 0)
            return 0;
        int best = 0;
        foreach (var n in u.Names)
            if (n.TurnsRemaining > 0 && n.FirstCardDiscount > best)
                best = n.FirstCardDiscount;
        return best;
    }

    /// <summary>True when a living boon on the unit makes it immune to Weakened.</summary>
    public static bool ImmuneToWeakened(Unit u) =>
        u != null && u.Names.Any(n => n.ImmuneWeakened && n.TurnsRemaining > 0);

    /// <summary>Moves a Name from one unit to another (Name of Warding's bottom).</summary>
    public static bool Move(Unit from, Unit to, NameCondition n)
    {
        if (from == null || to == null || n == null || from == to || !from.Names.Remove(n))
            return false;
        EndBoon(from, n);
        to.Names.Add(n);
        StartBoon(to, n);
        from.RefreshHealthBar();
        to.RefreshHealthBar();
        return true;
    }'''),
('''        if (target == null || name == null || !target.Stats.IsAlive)
            return;
        target.Names.Add(name);''',
'''        if (target == null || name == null || !target.Stats.IsAlive)
            return;
        target.Names.Add(name);
        StartBoon(target, name);'''),
('''            foreach (var n in u.Names)
                n.TurnsRemaining--;
            if (u.Names.RemoveAll(n => n.TurnsRemaining <= 0) > 0)
                u.RefreshHealthBar();''',
'''            foreach (var n in u.Names)
                n.TurnsRemaining--;
            foreach (var n in u.Names)
                if (n.TurnsRemaining <= 0)
                    EndBoon(u, n);
            if (u.Names.RemoveAll(n => n.TurnsRemaining <= 0) > 0)
                u.RefreshHealthBar();'''),
# NameConditionEffect: allies / all / boons / seventh name
('''    /// <summary>Slice 13a flags: see the matching <see cref="NameCondition"/> fields.</summary>
    public bool Backfire, Shun, PerTrigger, Grouped;
''',
'''    /// <summary>Slice 13a flags: see the matching <see cref="NameCondition"/> fields.</summary>
    public bool Backfire, Shun, PerTrigger, Grouped;
    /// <summary>Slice 13b: Name allies instead of enemies; or every ally / every enemy
    /// on the board, whatever the targets.</summary>
    public bool Allies, AllAllies, AllEnemies;
    /// <summary>Slice 13b boons (see <see cref="NameCondition"/>).</summary>
    public int BonusDamage, FirstCardDiscount;
    public bool ImmuneWeakened, Warding;
'''),
('''        var group = Grouped ? new List<(Unit, NameCondition)>() : null;
        foreach (var obj in targets.Items)
        {
            if (obj is not Unit u || !GodotObject.IsInstanceValid(u) || !u.Stats.IsAlive || u.TeamId == team)
                continue;
            var written = new NameCondition''',
'''        var group = Grouped ? new List<(Unit, NameCondition)>() : null;
        bool allies = Allies || AllAllies;
        IEnumerable<object> pool = targets.Items;
        if ((AllAllies || AllEnemies) && s?.UnitsInPlay != null)
            pool = s.UnitsInPlay.Cast<object>().ToList();
        foreach (var obj in pool)
        {
            if (obj is not Unit u || !GodotObject.IsInstanceValid(u) || !u.Stats.IsAlive || u.CurrentTile == null)
                continue;
            if (allies ? u.TeamId != team : u.TeamId == team)
                continue;
            if (allies && (u.IsStructure || u.IsObjectiveWard || u.IsMapObject))
                continue;
            var written = new NameCondition'''),
('''                Group = group,
            };
            group?.Add((u, written));
            Names.Write(u, written, s.Log);''',
'''                Group = group,
                BonusDamage = BonusDamage,
                ImmuneWeakened = ImmuneWeakened,
                Warding = Warding,
                FirstCardDiscount = FirstCardDiscount,
            };
            if (allies)
                written.Triggers = NameTrigger.None;   // a boon never breaks
            Names.Lengthen(written);
            group?.Add((u, written));
            Names.Write(u, written, s.Log);
            if (!allies)
                Names.GlyphBeneath(s, owner, u);'''),
('''        if (!any)
            s?.Log("[Name] No enemy to Name.");''',
'''        if (!any)
            s?.Log(allies ? "[Name] No ally to Name." : "[Name] No enemy to Name.");'''),
])

# Misdirect: Seventh Name applies too
edit('Scripts/Cards/Effects/EnchanterNameCardEffects.cs', [
('''            string err = IntentTime.RedirectLock(u);
            Names.Write(u, new NameCondition
            {
                Triggers = NameTrigger.Attack,
                Damage = Damage,
                TurnsRemaining = Duration,
                OwnerUnit = owner,
                OwnerTeam = team,
                Source = s.ResolvingAbilityName ?? "a Name",
                Misdirect = err == null,
            }, s.Log);''',
'''            string err = IntentTime.RedirectLock(u);
            var name = new NameCondition
            {
                Triggers = NameTrigger.Attack,
                Damage = Damage,
                TurnsRemaining = Duration,
                OwnerUnit = owner,
                OwnerTeam = team,
                Source = s.ResolvingAbilityName ?? "a Name",
                Misdirect = err == null,
            };
            Names.Lengthen(name);
            Names.Write(u, name, s.Log);
            Names.GlyphBeneath(s, owner, u);'''),
])

# Unit: immune to weakened
edit('Scripts/Systems/Combat/Core/Unit.cs', [
('''        // R22 sim gate: preview never applies real statuses.
        if (CombatSim.Active)
            return;

        // If already has this status, take the longer duration''',
'''        // R22 sim gate: preview never applies real statuses.
        if (CombatSim.Active)
            return;

        // Name of Courage (Enchanter boon): cannot be Weakened.
        if (status == "weakened" && global::Names.ImmuneToWeakened(this))
            return;

        // If already has this status, take the longer duration'''),
])

# Cost: Litany first-card discount
edit('Scripts/Systems/Combat/Core/RuntimeInterfaces.cs', [
('''            int glyphDiscount = GlyphManager.StandingCostReduction(u)''',
'''            int glyphDiscount = Names.FirstCardDiscount(u)
                + GlyphManager.StandingCostReduction(u)'''),
])

# Warding: struck allies Weaken the attacker
edit('Scripts/Systems/Combat/Core/CombatManager.EnemyIntents.cs', [
('''                CombatPresenter.EmitStrike(attacker, victim, ranged ? Delivery.Bolt : Delivery.Melee);   // spell_vfx_pipeline_v1 §5 phase 2
                victim.ApplyDamage(damage, attacker, ranged ? Delivery.Bolt : Delivery.Melee);
                LastStrikeVictim = victim;''',
'''                CombatPresenter.EmitStrike(attacker, victim, ranged ? Delivery.Bolt : Delivery.Melee);   // spell_vfx_pipeline_v1 §5 phase 2
                victim.ApplyDamage(damage, attacker, ranged ? Delivery.Bolt : Delivery.Melee);
                LastStrikeVictim = victim;
                OnWardedStruck(attacker, victim);'''),
])
edit('Scripts/Systems/Combat/Core/CombatManager.Enchanter.cs', [
('''    // ── Strikes (slice 12a) ─────────────────────────────────────────────''',
'''    // ── Strikes (slice 12a) ─────────────────────────────────────────────

    /// <summary>Name of Warding: an ally carrying it was struck. The attacker is Weakened
    /// for 1 turn and the Namer gains 1 Weave (once per Warding Name per strike).</summary>
    private void OnWardedStruck(Unit attacker, Unit victim)
    {
        if (attacker == null || victim == null || !IsInstanceValid(attacker) || !IsInstanceValid(victim))
            return;
        foreach (var n in victim.Names.ToList())
        {
            if (!n.Warding || n.TurnsRemaining <= 0)
                continue;
            if (attacker.Stats.IsAlive)
                attacker.ApplyStatus("weakened", 1);
            if (n.OwnerUnit != null && IsInstanceValid(n.OwnerUnit) && n.OwnerUnit.Attunement is WeaveAttunement w)
                w.Add(1);
            LogName($"{victim.Name}'s Name of Warding answers: {attacker.Name} is Weakened.");
        }
    }'''),
])

# Glyphs: layers and pairs
edit('Scripts/Systems/Combat/Glyphs/GlyphData.cs', [
('''    public int LinkId;''',
'''    public int LinkId;

    /// <summary>Seven-Layer Ward (slice 13b): each trigger peels one; the glyph stays
    /// until the last is gone. 0 = not layered.</summary>
    public int Layers;

    /// <summary>Tripwire Sentence (slice 13b): glyphs prepared together. When one is
    /// triggered the others are spent with it (the wire snaps). 0 = unpaired.</summary>
    public int PairId;'''),
])
GM = 'Scripts/Systems/Combat/Glyphs/GlyphManager.cs'
edit(GM, [
('''            s.Log($"[GlyphManager] Cascade spread to {spread} tile(s).");
        }

        return g.Reusable;
    }''',
'''            s.Log($"[GlyphManager] Cascade spread to {spread} tile(s).");
        }

        // Tripwire Sentence: the rest of the pair is spent with it.
        if (g.PairId != 0 && _grid?.Tiles != null)
            foreach (var other in _grid.Tiles.Values.ToList())
                if (other != tile && other.Glyph != null && other.Glyph.PairId == g.PairId)
                    Remove(other);

        // Seven-Layer Ward: peel a layer; it stays until the last one is gone.
        if (g.Layers > 0)
        {
            g.Layers--;
            s.Log(g.Layers > 0
                ? $"[Glyph] A layer peels away: {g.Layers} left."
                : "[Glyph] The last layer peels away.");
            return g.Layers > 0;
        }

        return g.Reusable;
    }

    /// <summary>A fresh id for glyphs prepared together (Tripwire Sentence pairs).</summary>
    public int NewPairId() => _nextLinkId++;'''),
])

# PrepareGlyphEffect: layers + pair
EF = 'Scripts/Cards/Effects/EnchanterEffects.cs'
edit(EF, [
('''	public bool InstantCopies, HaltsMovement;''',
'''	public bool InstantCopies, HaltsMovement;
	/// <summary>Slice 13b: Seven-Layer Ward's layers; Tripwire Sentence's pairing.</summary>
	public int Layers;
	public bool Pair;
	private int _pairId;'''),
('''		g.SpawnGlyphDamage = SpawnGlyphDamage;
	}''',
'''		g.SpawnGlyphDamage = SpawnGlyphDamage;
		if (Layers > 0)
		{
			g.Layers = Layers;
			g.Reusable = true;   // it stays between triggers; OnGlyphFired removes it at 0
		}
		g.PairId = _pairId;
	}'''),
('''		var owner = FindCasterUnit(s, caster) ?? s.ActiveCasterUnit;
		if (s?.Glyphs == null)
		{ s?.Log("[PrepareGlyph] no GlyphManager on GameState."); return; }

		int placed = 0;''',
'''		var owner = FindCasterUnit(s, caster) ?? s.ActiveCasterUnit;
		if (s?.Glyphs == null)
		{ s?.Log("[PrepareGlyph] no GlyphManager on GameState."); return; }

		_pairId = Pair ? s.Glyphs.NewPairId() : 0;
		int placed = 0;'''),
])

# Loader: layers / pair on prepare_glyph
edit('Scripts/Cards/Loader/JsonCardLoader.cs', [
('''            SpawnGlyphDamage = n.TryGetProperty("on_trigger_spawn_glyph", out var sg)''',
'''            Layers = n.TryGetProperty("layers", out var ly) ? ly.GetInt32() : 0,
            Pair = n.TryGetProperty("pair", out var pr) && pr.ValueKind == JsonValueKind.True,
            SpawnGlyphDamage = n.TryGetProperty("on_trigger_spawn_glyph", out var sg)'''),
('''        RegisterTargeter("unit_then_unit", n =>
        {
            bool enemyOnly = n.TryGetProperty("enemies_only", out var eo) && eo.GetBoolean();
            int range = n.TryGetProperty("range", out var r) ? r.GetInt32() : 4;
            int destRange = n.TryGetProperty("dest_range", out var d) ? d.GetInt32() : range;
            return new SelectUnitThenUnitTarget(enemyOnly, range, destRange);''',
'''        RegisterTargeter("unit_then_unit", n =>
        {
            bool enemyOnly = n.TryGetProperty("enemies_only", out var eo) && eo.GetBoolean();
            bool friendlyOnly = n.TryGetProperty("friendlies_only", out var fo) && fo.GetBoolean();
            int range = n.TryGetProperty("range", out var r) ? r.GetInt32() : 4;
            int destRange = n.TryGetProperty("dest_range", out var d) ? d.GetInt32() : range;
            return new SelectUnitThenUnitTarget(enemyOnly, range, destRange) { friendlyOnly = friendlyOnly };'''),
])

# Tooltip: layers left
edit('Scripts/Systems/TooltipManager.cs', [
('''        AddRow(g.Reusable ? "Reusable" : "Single use", life);''',
'''        AddRow(g.Layers > 0 ? (g.Layers == 1 ? "1 layer left" : $"{g.Layers} layers left")
               : g.Reusable ? "Reusable" : "Single use", life);'''),
])

# Two-step legal: friendly-only second unit
edit('Scripts/Systems/Combat/Core/CombatManager.cs', [
('''                if (uu.enemyOnly && u.TeamId == selectedUnit?.TeamId)
                    continue;''',
'''                if (uu.enemyOnly && u.TeamId == selectedUnit?.TeamId)
                    continue;
                if (uu.friendlyOnly && u.TeamId != selectedUnit?.TeamId)
                    continue;'''),
])
print("engine ok")
