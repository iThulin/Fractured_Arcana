using System;

// ============================================================
// TargetingSummary.cs
//
// Purpose:        One short label per targeting selector for the card
//                 face ("Bolt 6", "Arc 6", "Burst 2", "Cone 3", "Self"),
//                 plus a tooltip that says what the label means for
//                 cover. The card is the only place a player can learn
//                 that a spell flies straight or wraps around a wall
//                 BEFORE they drop it, so the summary lives beside the
//                 element tags on every half and above the rules text
//                 in the full view.
// Layer:          Cards / Targeting (pure, no Godot types)
// Collaborators:  TargetSelectors (the selector classes), CardUi
// See:            docs/cover_and_zoc_v1.md §10, card_text_style_guide
// ============================================================

public static class TargetingSummary
{
    public readonly struct Entry
    {
        public readonly string Label;
        public readonly string Tooltip;
        public readonly Delivery Delivery;
        public Entry(string label, string tooltip, Delivery delivery)
        { Label = label; Tooltip = tooltip; Delivery = delivery; }
    }

    private const string BoltTip = "Bolt: flies straight. Needs sight. Full cover on the target's facing side stops it; low cover soaks it.";
    private const string ArcTip = "Arc: lobbed or willed. Ignores low cover; only a wall in the way stops it.";
    private const string BurstTip = "Burst: spreads from the aim point through open ground. Wraps around pillars, stops at walls, one step slower over low cover.";

    /// <summary>Summarise a selector. Null selector: "Self".</summary>
    public static Entry Describe(ITargetSelector t)
    {
        switch (t)
        {
            case null:
            case SelectSelfTarget:
                return new Entry("Self", "Targets the caster.", Delivery.Ground);

            case SelectUnitTarget u:
            {
                bool bolt = u.delivery == Delivery.Bolt;
                string who = u.friendlyOnly ? "Ally" : u.enemyOnly ? "" : "Unit";
                string kind = u.friendlyOnly ? "" : bolt ? "Bolt" : "Arc";
                string label = Join(who, kind, u.range.ToString());
                string tip = u.friendlyOnly
                    ? $"An ally within {u.range}."
                    : (bolt ? BoltTip : ArcTip) + $" Range {u.range}." + (u.los || bolt ? "" : " Does not need sight.");
                return new Entry(label, tip, bolt ? Delivery.Bolt : Delivery.Arc);
            }

            case SelectTileTarget tt:
                return new Entry($"Tile {tt.range}", $"Any tile within {tt.range}.", Delivery.Ground);

            case SelectEmptyTileTarget et:
                return new Entry($"Open tile {et.Range}", $"An empty tile within {et.Range}.", Delivery.Ground);

            case SelectAreaTarget a:
                return new Entry($"Burst {a.Radius}", BurstTip + $" Reach {a.Radius}." + (a.EnemiesOnly ? " Enemies only." : " Hits everyone it reaches."), Delivery.Burst);

            case SelectRingTarget r:
                return new Entry($"Ring {r.Radius}", $"The outer edge of a burst at reach {r.Radius}: the ring bends around walls and low cover." + (r.EnemiesOnly ? " Enemies only." : ""), Delivery.Burst);

            case SelectConeTarget c:
                return new Entry($"Cone {c.Range}", $"A cone {c.Range} deep toward the aim, clipped where a wall blocks it.", Delivery.Burst);

            case SelectLineTarget l:
                return new Entry($"Line {l.Length}", $"A straight line {l.Length} long toward the aim. Stops at the first wall.", Delivery.Bolt);

            case SelectAdjacentToTarget:
                return new Entry("Adjacent", "The six tiles around the aim point.", Delivery.Burst);

            case SelectNearestToTarget n:
                return new Entry($"Nearest {n.Range}", $"The nearest enemy within {n.Range} of the previous target.", Delivery.Arc);

            case SelectElementTileTarget e:
                return new Entry($"{Cap(e.Element)} tile {e.Range}", $"An {e.Element} imbued tile within {e.Range}.", Delivery.Ground);

            case SelectUnitThenTileTarget ut:
                return new Entry($"Unit {ut.range}, tile {ut.destRange}", $"A unit within {ut.range}, then a tile within {ut.destRange} of it.", Delivery.Arc);

            case SelectUnitThenDirectionTarget ud:
                return new Entry($"Unit {ud.range}, direction", $"A unit within {ud.range}, then a direction.", Delivery.Arc);

            case SelectByTagTarget bt:
                return new Entry($"All {bt.tag}", $"Every {bt.tag} on the field.", Delivery.Ground);

            case SelectGlobalTarget:
                return new Entry("All", "Everything on the field.", Delivery.Ground);

            case SelectNearestMemorialTarget:
                return new Entry("Memorial", "The nearest memorial.", Delivery.Ground);

            default:
                return new Entry("", "", Delivery.Untyped);
        }
    }

    private static string Join(params string[] parts)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var p in parts)
        {
            if (string.IsNullOrEmpty(p)) continue;
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(p);
        }
        return sb.ToString();
    }

    private static string Cap(string s)
        => string.IsNullOrEmpty(s) ? "" : char.ToUpperInvariant(s[0]) + s.Substring(1);
}
