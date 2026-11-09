// ============================================================
// TroopEffects.cs
//
// Purpose:        The Orders card's one effect (docs/provinces_v1_12_
//                 troops.md, slice PV7, PR92): every troop line on the
//                 field takes the order for this turn, Charge! or Hold
//                 Fast, and plans again at once. Registered by the code
//                 that builds the card (CombatManager.BuildOrdersCard),
//                 not among the school effects, because no card in the
//                 database carries it.
// Layer:          Effects
// Collaborators:  CombatManager.Troops.cs (IssueTroopOrder, the card),
//                 Effect.cs (EffectBase), TroopLines.cs (the orders)
// See:            docs/provinces_v1_12_troops.md section 5
// ============================================================

/// <summary>{ "type": "troop_order", "order": "charge" | "hold" }</summary>
public sealed class TroopOrderEffect : EffectBase
{
    /// <summary>The effect's JSON type key.</summary>
    public const string Key = "troop_order";

    /// <summary>TroopLines.OrderCharge or TroopLines.OrderHold.</summary>
    public string Order { get; }

    public TroopOrderEffect(string order)
    {
        Order = order ?? "";
    }

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        string said = CombatManager.IssueTroopOrder(Order);
        if (!string.IsNullOrEmpty(said))
            s?.Log($"[Orders] {said}");
    }
}
