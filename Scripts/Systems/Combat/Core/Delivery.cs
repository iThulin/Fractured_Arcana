// ============================================================
// Delivery.cs
//
// Purpose:        How an attack or spell travels to its target. Cover
//                 keys off delivery, not off who sent the hit: an arrow
//                 and a straight-flying bolt both stop at a low wall, a
//                 lobbed or willed spell goes over it, a burst fills the
//                 open ground around it.
// Layer:          Systems / Combat / Core
// Collaborators:  TargetSet (carries the cast's delivery), Unit.ApplyDamage
//                 (cover armour absorbs Bolt hits), TargetSelectors
//                 (Bolt targeting refuses High cover; bursts flood fill),
//                 CombatManager martial attack paths (ranged = Bolt)
// See:            docs/cover_and_zoc_v1.md
// ============================================================

public enum Delivery
{
    /// <summary>No delivery rule applies: damage over time, terrain, self damage,
    /// retaliation, and every legacy call site. Cover never touches it.</summary>
    Untyped,

    /// <summary>Straight flight: arrows, martial ranged strikes, and any card whose
    /// unit targeting sets <c>"delivery": "bolt"</c>. Needs line of sight. High cover on
    /// the defender's facing side makes the shot impossible; Low cover on that side
    /// is absorbed by the defender's cover armour.</summary>
    Bolt,

    /// <summary>Lobbed or willed: the default for every school card that targets a
    /// unit. Needs line of sight when the card asks for it; ignores Low cover. This
    /// is the "magic moves around cover" rule.</summary>
    Arc,

    /// <summary>Fills space from an aim point: aoe, ring, cone. Spreads through open
    /// tiles, is stopped by High cover, and spends an extra step to cross Low cover.
    /// Ignores directional cover for every unit it reaches.</summary>
    Burst,

    /// <summary>Written onto tiles: imbue, summon, terrain shaping. No cover rule.</summary>
    Ground,

    /// <summary>A melee blow from an adjacent tile. Cover never applies (the attacker
    /// is already past the wall); kept distinct from Untyped so onAttack riders can
    /// tell a swing from a burn tick.</summary>
    Melee
}
