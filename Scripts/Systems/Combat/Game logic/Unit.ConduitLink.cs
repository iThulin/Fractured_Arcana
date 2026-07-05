using Godot;

// ============================================================
// Unit.ConduitLink.cs
//
// Purpose:        Tinker damage-interception hooks on the Unit
//                 partial: the Conduit Link re-entrancy guard +
//                 ApplyDamageSkippingLinks entry, and the
//                 Redirector Field one-shot damage reroute.
// Layer:          System
// Collaborators:  ConduitLinkSystem.cs (calls
//                 ApplyDamageSkippingLinks), TinkerPipelineEffects.cs
//                 (RedirectorFieldEffect sets RedirectNextDamageTo),
//                 Unit.cs (ApplyDamage reads both)
// ============================================================

public partial class Unit
{
    /// <summary>When true, ApplyDamage skips link redistribution for this call. Set only around link-routed damage so redistribution is one hop and never recurses.</summary>
    private bool _skipLinkRedistribution = false;

    /// <summary>Redirector Field: if set, the next incoming damage instance is rerouted in full to this construct, then cleared. Null = no redirect pending.</summary>
    public Unit RedirectNextDamageTo = null;

    /// <summary>Applies damage to this unit without triggering link redistribution. Used by the link system for partner shares and line-cross zaps.</summary>
    public void ApplyDamageSkippingLinks(int amount)
    {
        if (amount <= 0)
            return;
        _skipLinkRedistribution = true;
        ApplyDamage(amount);
        _skipLinkRedistribution = false;
    }
}
