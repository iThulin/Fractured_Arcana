using System;
using System.Text.Json;

// ============================================================
// CardScriptRegistry.Tinker.cs
//
// Purpose:        Tinker school effect registrations — maps the
//                 school's JSON `type` keys to effect factories.
//                 Called from CardScriptRegistry.RegisterBuiltins().
// Layer:          Loader
// Collaborators:  TinkerEffects.cs (the effect classes),
//                 JsonCardLoader.cs (registry infrastructure)
// ============================================================

public static partial class CardScriptRegistry
{
    /// <summary>Registers all Tinker-school effect factories.</summary>
    private static void RegisterTinkerEffects()
    {
        // ═══════════════════════════════════════════════════════════
        // TINKER EFFECTS
        // ═══════════════════════════════════════════════════════════

        // Overclock: { "type": "overclock", "heat": 2 }
        RegisterEffect("overclock", n =>
            new OverclockEffect(n.TryGetProperty("heat", out var h) ? h.GetInt32() : 1).WithTag("Construct"));

        // Salvage: { "type": "salvage_construct", "draw": 1 }
        RegisterEffect("salvage_construct", n =>
            new SalvageConstructEffect(n.TryGetProperty("draw", out var d) ? d.GetInt32() : 1).WithTag("Construct"));

        // Emergency Scuttle: { "type": "scuttle_constructs", "blast": 5 }
        RegisterEffect("scuttle_constructs", n =>
            new ScuttleConstructsEffect(n.TryGetProperty("blast", out var b) ? b.GetInt32() : 4).WithTag("Construct"));

        // Repair Pulse: { "type": "repair_constructs", "amount": 6 }
        RegisterEffect("repair_constructs", n =>
            new RepairConstructsEffect(n.TryGetProperty("amount", out var a) ? a.GetInt32() : 4).WithTag("Construct"));

        // Capacity: { "type": "set_construct_cap", "cap": 8 }
        RegisterEffect("set_construct_cap", n =>
            new SetConstructCapEffect(n.TryGetProperty("cap", out var c) ? c.GetInt32() : 8).WithTag("Construct"));

        // Master Schematic: { "type": "master_schematic", "charges": 2, "amount": 2 }
        RegisterEffect("master_schematic", n =>
            new MasterSchematicEffect(
                n.TryGetProperty("charges", out var ch) ? ch.GetInt32() : 2,
                n.TryGetProperty("amount", out var am) ? am.GetInt32() : 2).WithTag("Construct"));

        // Assembly Line: { "type": "assembly_line", "turns": 3 }
        RegisterEffect("assembly_line", n =>
            new AssemblyLineEffect(n.TryGetProperty("turns", out var t) ? t.GetInt32() : 3).WithTag("Construct"));

        // Disruption Field: { "type": "disruption_field", "radius": 1, "damage": 3, "slows": true, "turns": 3 }
        RegisterEffect("disruption_field", n =>
            new DisruptionFieldEffect(
                n.TryGetProperty("radius", out var r) ? r.GetInt32() : 1,
                n.TryGetProperty("damage", out var dd) ? dd.GetInt32() : 3,
                n.TryGetProperty("slows", out var sl) && sl.GetBoolean(),
                n.TryGetProperty("turns", out var tt) ? tt.GetInt32() : 3).WithTag("Construct"));

        // Conduit Link: { "type": "create_link", "mode": "split"|"mirror", "line_damage": 0 }
        RegisterEffect("create_link", n =>
        {
            var modeStr = n.TryGetProperty("mode", out var m) ? m.GetString() : "split";
            var mode = string.Equals(modeStr, "mirror", StringComparison.OrdinalIgnoreCase)
                ? LinkMode.Mirror : LinkMode.Split;
            int line = n.TryGetProperty("line_damage", out var ld) ? ld.GetInt32() : 0;
            return new CreateConduitLinkEffect(mode, line).WithTag("Link");
        });

        // Arc Bolt: { "type": "arc_damage", "amount": 5, "arc": 2 }
        RegisterEffect("arc_damage", n =>
            new ArcDamageEffect(
                n.TryGetProperty("amount", out var aa) ? aa.GetInt32() : 4,
                n.TryGetProperty("arc", out var ar) ? ar.GetInt32() : 2).WithTag("Link"));

        // Conduit Singularity: { "type": "conduit_singularity", "per_link": 4 }
        RegisterEffect("conduit_singularity", n =>
            new ConduitSingularityEffect(
                n.TryGetProperty("per_link", out var pl) ? pl.GetInt32() : 4).WithTag("Link"));

        // Etched Ward: { "type": "etch_ward", "amount": 3 }
        RegisterEffect("etch_ward", n =>
            new EtchWardEffect(n.TryGetProperty("amount", out var wa) ? wa.GetInt32() : 2).WithTag("Construct"));

        // Redirector Field: { "type": "redirector_field" }
        RegisterEffect("redirector_field", _ => new RedirectorFieldEffect().WithTag("Construct"));

        // Wire Trap: { "type": "place_trap", "damage": 5, "status": "rooted", "duration": 1 }
        RegisterEffect("place_trap", n =>
            new PlaceTrapEffect(
                n.TryGetProperty("damage", out var td) ? td.GetInt32() : 5,
                n.TryGetProperty("status", out var ts) ? ts.GetString() : null,
                n.TryGetProperty("duration", out var tu) ? tu.GetInt32() : 1).WithTag("Construct"));

        // Full Salvo: { "type": "construct_volley" }
        RegisterEffect("construct_volley", _ => new ConstructVolleyEffect().WithTag("Construct"));

        // Conduit Feedback: { "type": "damage_per_construct", "amount_per": 2, "max": 10 }
        RegisterEffect("damage_per_construct", n =>
            new DamagePerConstructEffect(
                n.TryGetProperty("amount_per", out var pp) ? pp.GetInt32() : 2,
                n.TryGetProperty("max", out var mx) ? mx.GetInt32() : 0).WithTag("Damage"));

        // Etched Masterwork: { "type": "enhance_construct", "hp": 3, "damage": 2, "range": 1 }
        RegisterEffect("enhance_construct", n =>
            new EnhanceConstructEffect(
                n.TryGetProperty("hp", out var eh) ? eh.GetInt32() : 0,
                n.TryGetProperty("damage", out var ed) ? ed.GetInt32() : 0,
                n.TryGetProperty("range", out var er) ? er.GetInt32() : 0).WithTag("Construct"));
    }
}
