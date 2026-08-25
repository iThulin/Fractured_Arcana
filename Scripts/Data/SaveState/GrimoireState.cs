using System.Collections.Generic;
using System.Text.Json;
using Godot;

// ============================================================
// GrimoireState.cs  (S1, 2026-07-15)
//
// Purpose:        The wizard's noncombat spell state for one
//                 cycle: known spells, the prepared loadout,
//                 the per-expedition Essence pool, once-per-
//                 expedition cast counts, scroll inventory, and
//                 active Wayfarer's Beacons. Lives on CycleState
//                 (tier 2): spell knowledge is timeline knowledge
//                 and dies with the cycle, like items and cards.
//                 Essence / cast counts / beacons are expedition-
//                 scoped: reset on fresh deploy, but SERIALIZED
//                 so a mid-expedition save (autosave, combat
//                 round-trip) restores them exactly.
// Layer:          Data
// Collaborators:  CycleState.cs (owner),
//                 OverworldSpellManager.cs (accounting),
//                 OverworldSpellRegistry.cs (id resolution),
//                 SaveManager.cs (JsonOptions round-trip)
// See:            overworld_spell_system_v1_1.docx §5, §13
//
// S4 (2026-07-16): the interim General seed is REMOVED. A
// fresh cycle knows nothing and prepares nothing until spells
// are acquired (§11: lore POIs, negotiation tuition, Speak with
// the Fallen). Existing saves are grandfathered: their seeded
// Generals load from the save file as "already learned" (ruled
// with the user, this session). Preparation happens on the
// deploy dialog (StrategicView); scrolls are scribed at the
// campus Scriptorium and cast Essence-free from the panel.
// ============================================================

/// <summary>Noncombat spell state for one cycle. Plain data; serializes
/// into the cycle file. See header for scoping rules.</summary>
public class GrimoireState
{
    // ── Timeline knowledge ───────────────────────────────────────────────
    /// <summary>Every overworld spell the guild knows this cycle. School
    /// innates and Attunements are NOT listed; they derive from the school.
    /// S4: starts EMPTY; filled only by acquisition (§11).</summary>
    public List<string> KnownSpellIds = new();

    /// <summary>Spells filling the prepared slots this expedition (base 2;
    /// Adept 3 via Versatility). Chosen on the deploy dialog (S4 prep UI);
    /// sanitized there against KnownSpellIds and the slot cap.</summary>
    public List<string> PreparedSpellIds = new();

    /// <summary>Scroll inventory: spellId → count. Scribed at the campus
    /// Scriptorium (S4); consumed on a successful Essence-free cast.</summary>
    public Dictionary<string, int> ScrollInventory = new();

    // ── Expedition-scoped (reset on fresh deploy; serialized for
    //    mid-expedition saves and combat round-trips) ─────────────────────
    public int EssenceCurrent = 0;
    public int EssenceMax = 0;

    /// <summary>Casts per spell this expedition. Enforces OncePerExpedition
    /// caps (Retrace, Parley Compulsion in S3).</summary>
    public Dictionary<string, int> PerExpeditionCastCounts = new();

    /// <summary>Wayfarer's Beacon marks, as world offset "col,row" strings.
    /// Redrawn on scene build; cleared on fresh deploy.</summary>
    public List<string> ActiveBeacons = new();

    // ── S3 expedition-scoped state ───────────────────────────────────────

    /// <summary>Remnants (Necromancer): world "col,row" of every combat the
    /// party has won this expedition. Deathsight marks them; Bone Scout and
    /// Speak with the Fallen cast from them.</summary>
    public List<string> ActiveRemnants = new();

    /// <summary>Deployed Waystations (Tinker), world "col,row". One rest use
    /// each; also a supply anchor while standing (W-track ruling #2).</summary>
    public List<string> ActiveWaystations = new();

    /// <summary>Last spell resolved this expedition, Emulate's target.</summary>
    public string LastCastSpellId = "";

    /// <summary>Parley Compulsion armed: the next patrol interception becomes
    /// a negotiation. Once per expedition (the cast carries the cap).</summary>
    public bool ParleyArmed = false;

    /// <summary>Beguile armed: the next negotiation starts a band more favorable.</summary>
    public bool BeguileArmed = false;

    // ── Expedition lifecycle ─────────────────────────────────────────────

    /// <summary>Fresh-deploy reset: full pool, no casts, no beacons. Combat
    /// round-trips must NOT call this; the pool rides the save.</summary>
    public void BeginExpedition(int essenceMax)
    {
        EssenceMax = essenceMax;
        EssenceCurrent = essenceMax;
        PerExpeditionCastCounts.Clear();
        ActiveBeacons.Clear();
        ActiveRemnants.Clear();
        ActiveWaystations.Clear();
        LastCastSpellId = "";
        ParleyArmed = false;
        BeguileArmed = false;
    }

    // ── Round-trip assertion (house rule for save-adjacent fields;
    //    the EchoesInFlight precedent: IncludeFields mismatches fail
    //    silently, so probe once per session with the REAL options) ───────
    private static bool _roundTripAsserted = false;

    public static void AssertRoundTripOnce()
    {
        if (_roundTripAsserted)
            return;
        _roundTripAsserted = true;

        var probe = new GrimoireState { EssenceCurrent = 7, EssenceMax = 10 };
        probe.KnownSpellIds.Add("probe_spell");
        probe.PerExpeditionCastCounts["probe_spell"] = 2;
        probe.ActiveBeacons.Add("12,34");
        probe.ScrollInventory["probe_scroll"] = 3;
        probe.ActiveRemnants.Add("56,78");
        probe.ActiveWaystations.Add("9,10");
        probe.LastCastSpellId = "probe_spell";
        probe.ParleyArmed = true;
        probe.BeguileArmed = true;

        var back = JsonSerializer.Deserialize<GrimoireState>(
            JsonSerializer.Serialize(probe, SaveManager.JsonOptions), SaveManager.JsonOptions);

        bool ok = back != null &&
                  back.EssenceCurrent == 7 && back.EssenceMax == 10 &&
                  back.KnownSpellIds.Contains("probe_spell") &&
                  back.PerExpeditionCastCounts.TryGetValue("probe_spell", out int c) && c == 2 &&
                  back.ActiveBeacons.Contains("12,34") &&
                  back.ScrollInventory.TryGetValue("probe_scroll", out int s) && s == 3 &&
                  back.ActiveRemnants.Contains("56,78") &&
                  back.ActiveWaystations.Contains("9,10") &&
                  back.LastCastSpellId == "probe_spell" &&
                  back.ParleyArmed && back.BeguileArmed;

        if (!ok)
            GD.PrintErr("[S1 RoundTrip] GrimoireState FAILED to round-trip through " +
                        "SaveManager.JsonOptions. Spell state will not persist!");
        else
            GD.Print("[S1 RoundTrip] GrimoireState round-trips (essence, casts, beacons, scrolls).");
    }
}
