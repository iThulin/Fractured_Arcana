import pathlib
def edit(rel, pairs):
    p = pathlib.Path(rel); s = p.read_text(encoding='utf-8')
    for o, n in pairs:
        assert s.count(o) == 1, (rel, o[:80], s.count(o))
        s = s.replace(o, n)
    p.write_text(s, encoding='utf-8')

edit('Scripts/Cards/Loader/CardScriptRegistry.Enchanter.cs', [
('''                PerTrigger = B("per_trigger"),
                Grouped = B("grouped"),
            };''',
'''                PerTrigger = B("per_trigger"),
                Grouped = B("grouped"),
                Allies = B("allies"),
                AllAllies = B("all_allies"),
                AllEnemies = B("all_enemies"),
                ImmuneWeakened = B("immune_weakened"),
                Warding = B("warding"),
                BonusDamage = n.TryGetProperty("bonus_damage", out var bd) ? bd.GetInt32() : 0,
                FirstCardDiscount = n.TryGetProperty("first_card_discount", out var fc) ? fc.GetInt32() : 0,
            };'''),
('''        // { "type": "target_named" } / { "type": "name_broken_recently" }''',
'''        // ── Slice 13b: Benefactor, Wardwright, The Seventh Name ─────────
        // { "type": "weave_per_named", "max": n }
        RegisterEffect("weave_per_named", n =>
            new WeavePerNamedEffect(n.TryGetProperty("max", out var m) ? m.GetInt32() : 3).WithTag("Weave"));
        // { "type": "move_name" } with unit_then_unit + friendlies_only
        RegisterEffect("move_name", _ => new MoveNameEffect().WithTag("Buff"));
        // { "type": "shield_named_allies", "per_weave": n }
        RegisterEffect("shield_named_allies", n =>
            new ShieldNamedAlliesEffect(n.TryGetProperty("per_weave", out var p) ? p.GetInt32() : 2).WithTag("Defense"));
        // { "type": "inscribe", "fallback_damage": n, "weave_on_trigger": n }
        RegisterEffect("inscribe", n =>
            new InscribeEffect(n.TryGetProperty("fallback_damage", out var f) ? f.GetInt32() : 3,
                               n.TryGetProperty("weave_on_trigger", out var w) ? w.GetInt32() : 0).WithTag("Glyph"));
        // { "type": "seventh_name", "extra_turns": n, "glyph_damage": n }
        RegisterEffect("seventh_name", n =>
            new SeventhNameEffect(n.TryGetProperty("extra_turns", out var e) ? e.GetInt32() : 1,
                                  n.TryGetProperty("glyph_damage", out var g) ? g.GetInt32() : 4).WithTag("Control"));

        // { "type": "target_named" } / { "type": "name_broken_recently" }'''),
])
print("registry ok")

# Inscribe counts as preparing a glyph (warmup, Architecture discount, cipher verb).
edit('Scripts/Systems/Combat/Glyphs/GlyphManager.cs', [(
 "if (e is PrepareGlyphEffect or ReflectWardEffect or SpellAnchorEffect or EnchantPillarEffect)",
 "if (e is PrepareGlyphEffect or ReflectWardEffect or SpellAnchorEffect or EnchantPillarEffect or InscribeEffect)")])
edit('Scripts/Systems/Combat/Glyphs/GlyphCipherTags.cs', [(
 '''        "SpellAnchorEffect",
    };''', '''        "SpellAnchorEffect",
        "InscribeEffect",
    };''')])
