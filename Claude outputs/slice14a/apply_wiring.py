import pathlib
def edit(rel, pairs):
    p = pathlib.Path(rel); s = p.read_text(encoding='utf-8')
    for o, n in pairs:
        assert s.count(o) == 1, (rel, o[:80], s.count(o))
        s = s.replace(o, n)
    p.write_text(s, encoding='utf-8')

C = 'Scripts/Systems/Combat/Core/CombatManager.cs'
edit(C, [
('''        InstallEnchanterHooks(); // Names break on walk; breaks feed the Weave
''',
'''        InstallEnchanterHooks(); // Names break on walk; breaks feed the Weave
        InstallLeafHooks();      // Arcanist leaf: on-demand recital
'''),
('''        if (selectedUnit != null && !selectedUnit.CanAct())
        {
            GD.Print($"{selectedUnit.Name} is frozen and cannot act!");
            combatUI?.AppendActionLog($"{selectedUnit.Name} is frozen!");
            return;
        }
''',
'''        if (selectedUnit != null && !selectedUnit.CanAct())
        {
            GD.Print($"{selectedUnit.Name} is frozen and cannot act!");
            combatUI?.AppendActionLog($"{selectedUnit.Name} is frozen!");
            return;
        }

        // The Register's leaf (Arcanist): a page costs its mana plus 1 Charge.
        if (!LeafCastAllowed(cardUi.CardInstance, isTop, out var leafWhy))
        {
            CastFail($"{half.Name}: {leafWhy}.");
            return;
        }
'''),
('''        if (ok)
        {
            combatUI?.AppendActionLog($"Cast: {resolvedHalf.Name}.");''',
'''        if (ok)
        {
            combatUI?.AppendActionLog($"Cast: {resolvedHalf.Name}.");
            PayLeafCast(cardUi.CardInstance, isTop);   // before the discard below takes it off the page'''),
('''        foreach (var unit in playerUnits)
        {
            // Enchanter §2c: the Weave holds while you have prepared glyphs OR Named units.''',
'''        foreach (var unit in playerUnits)
        {
            // Arcanist §2b: a full leaf babbles; the memorized page recites.
            LeafEndOfTurn(unit);

            // Enchanter §2c: the Weave holds while you have prepared glyphs OR Named units.'''),
])

# Registry
edit('Scripts/Cards/Loader/CardScriptRegistry.Arcanist.cs', [
('''        RegisterEffect("gain_charge", n =>''',
'''        // ── The Register's leaf (slice 14a) ────────────────────────────
        // { "type": "pen", "from": "hand"|"deck_top"|"reveal", "count": n, "up_to": bool,
        //   "free_top": bool, "reveal": n, "charge_per_bottomed": n }
        RegisterEffect("pen", n =>
        {
            int Geti(string k, int d) => n.TryGetProperty(k, out var v) ? v.GetInt32() : d;
            bool Getb(string k) => n.TryGetProperty(k, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.True;
            string from = n.TryGetProperty("from", out var f) ? f.GetString() : "hand";
            return new PenEffect(from, Geti("count", 1), Getb("up_to"), Getb("free_top"),
                                 Geti("reveal", 4), Geti("charge_per_bottomed", 0)).WithTag("CardDraw");
        });
        // { "type": "free_leaf_cast", "count": n }
        RegisterEffect("free_leaf_cast", n =>
            new FreeLeafCastEffect(n.TryGetProperty("count", out var c) ? c.GetInt32() : 1).WithTag("Mana"));
        // { "type": "memorize" }
        RegisterEffect("memorize", _ => new MemorizeEffect().WithTag("CardDraw"));
        // { "type": "recite_oldest" }
        RegisterEffect("recite_oldest", _ => new ReciteOldestEffect().WithTag("CardDraw"));
        // { "type": "charge_lock", "turns": n }
        RegisterEffect("charge_lock", n =>
            new ChargeLockEffect(n.TryGetProperty("turns", out var t) ? t.GetInt32() : 2).WithTag("Mana"));
        // { "type": "spend_charge", "amount": n }
        RegisterEffect("spend_charge", n =>
            new SpendChargeEffect(n.TryGetProperty("amount", out var a) ? a.GetInt32() : 1).WithTag("Mana"));

        RegisterEffect("gain_charge", n =>'''),
])
print("wiring ok")
