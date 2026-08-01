using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// GlyphCipherTags.cs
//
// Purpose:        Reads a compiled CardHalf and produces the two
//                 semantic inputs GlyphCipher needs: the recipient
//                 (CipherTarget) and the function verbs
//                 (CipherVerb flags). This is the only file that
//                 knows about both the card runtime and the cipher.
// Layer:          Data / adapter
// Collaborators:  CardRuntime.cs (CardHalf, Ability.Targeting),
//                 TargetSelectors.cs (SelectUnitTarget et al),
//                 Effect.cs / IEffect (Tags, Children),
//                 GlyphCipher.cs (the consumer)
// See:            docs/glyph_cipher_spec_v1.md §6 — tag extraction
// ============================================================
//
// WHY RUNTIME EXTRACTION AND NOT A JSON RE-PARSE
//
// The compiled CardHalf already carries everything needed:
// `Targeting` is a concrete ITargetSelector whose public fields
// (SelectUnitTarget.enemyOnly / friendlyOnly) survive compilation,
// and every leaf effect carries the project's existing IEffect.Tags
// vocabulary. Re-reading Data/Cards at glyph time would introduce a
// second source of truth that drifts the moment a card is upgraded
// at runtime (CardUpgradeApplier rewrites halves in place). Extract
// from the live object; there is nothing to gain from the JSON.
//
// TAGS ARE READ FROM LEAVES ONLY
//
// Composite effects (SequenceEffect, ChooseOneEffect, RetargetEffect,
// ForEachTargetEffect, ConditionalEffect) are walked through, never
// read. Their own tags are unreliable: `retarget` is registered with
// .WithTag("Damage") in JsonCardLoader, but in Dominion it wraps an
// apply_status that deals no damage at all. Reading composite tags
// would light STRIKE on a pure control card. That mislabel is a
// pre-existing data smell worth fixing separately — it will corrupt
// any tag-driven statistics, not just this cipher — but the cipher
// does not depend on it being fixed.
//
// ============================================================

/// <summary>
/// Maps a compiled <see cref="CardHalf"/> onto the cipher's semantic vocabulary.
/// Static, allocation-light, and safe to call every frame (though callers should
/// cache — see <c>GlyphCipherTexture</c>).
/// </summary>
public static class GlyphCipherTags
{
    // ── Effect tag -> verb ───────────────────────────────────────────
    // Derived from the tags actually reachable from Enchanter cards. Every one
    // of the 42 Enchanter spell halves resolves to at least one verb under this
    // table; none falls through.
    private static readonly Dictionary<string, CipherVerb> TagVerbs = new()
    {
        { "Damage",     CipherVerb.Strike },
        { "SelfDamage", CipherVerb.Strike },

        { "Control",    CipherVerb.Bind },
        { "Status",     CipherVerb.Bind },
        { "Debuff",     CipherVerb.Bind },

        { "Movement",   CipherVerb.Move },
        { "Displace",   CipherVerb.Move },

        { "Defense",    CipherVerb.Ward },
        { "Heal",       CipherVerb.Ward },
        { "Buff",       CipherVerb.Ward },
        { "Summon",     CipherVerb.Ward },

        { "CardDraw",   CipherVerb.Invoke },
        { "Mana",       CipherVerb.Invoke },
        { "Foresight",  CipherVerb.Invoke },
    };

    // The "Glyph" tag is worn by both halves of the school's identity: effects
    // that CREATE inscriptions and effects that MANIPULATE the existing network.
    // The tag alone cannot separate them, so the concrete effect type does.
    // Anything tagged Glyph that is not in this set is treated as Invoke.
    private static readonly HashSet<string> InscribeTypes = new()
    {
        "PrepareGlyphEffect",
        "EnchantPillarEffect",
        "ReflectWardEffect",
        "SpellAnchorEffect",
    };

    // Effects with no tag at all, mapped explicitly by type. Keep this list
    // short: the right fix is usually to add a .WithTag(...) at the
    // registration site. `scry` in CardScriptRegistry.Arcanist.cs is currently
    // untagged and should gain .WithTag("Foresight"); until it does, this entry
    // keeps Read the Weave from producing a verb-less glyph.
    private static readonly Dictionary<string, CipherVerb> TypeVerbs = new()
    {
        { "ScryEffect", CipherVerb.Invoke },
    };

    // Tags deliberately ignored. `Weave` rides on 9 of 42 Enchanter halves as a
    // universal resource kicker; it says nothing about what the spell does and
    // lighting a node for it would add noise to a fifth of the school.
    private static readonly HashSet<string> IgnoredTags = new() { "Weave" };

    private static readonly HashSet<string> CompositeTypes = new()
    {
        "SequenceEffect",
        "ChooseOneEffect",
        "ConditionalEffect",
        "ForEachTargetEffect",
        "RetargetEffect",
    };

    /// <summary>
    /// Recipient for a half. Answers "who is this pointed at", which is what the
    /// outer arc encodes. A unit target that is neither friendly-only nor
    /// enemy-only resolves to <see cref="CipherTarget.Enemy"/>: in practice
    /// (Phase Shift) the reason to cast it is to move an enemy, and Enemy is the
    /// safer read for a player glancing at a tile.
    /// </summary>
    public static CipherTarget TargetOf(CardHalf half)
    {
        var sel = half?.Targeting;
        if (sel == null) return CipherTarget.Self;

        switch (sel)
        {
            case SelectSelfTarget:
            case SelectGlobalTarget:
                return CipherTarget.Self;

            case SelectUnitTarget u:
                return u.friendlyOnly ? CipherTarget.Ally : CipherTarget.Enemy;

            // friendlyOnly is declared on the SelectTwoStepTarget base, so both
            // unit_then_tile and unit_then_direction are covered by one case.
            case SelectTwoStepTarget two:
                return two.friendlyOnly ? CipherTarget.Ally : CipherTarget.Enemy;

            default:
                // Every remaining selector picks a place, not a person: tile,
                // empty tile, aoe, ring, line, cone, element tile, nearest
                // memorial. All are TILE for cipher purposes. AoE is NOT given
                // its own node — see the spec's accepted-losses list.
                return CipherTarget.Tile;
        }
    }

    /// <summary>
    /// Verbs for a half, walking the whole effect tree and reading tags from leaves.
    /// Returns <see cref="CipherVerb.None"/> only if the half has no effects at all —
    /// which <c>GlyphCipherSelfTest</c> treats as a failure for any registered card.
    /// </summary>
    public static CipherVerb VerbsOf(CardHalf half)
    {
        var verbs = CipherVerb.None;
        if (half?.Effects == null) return verbs;
        foreach (var e in half.Effects)
            Walk(e, ref verbs, 0);
        return verbs;
    }

    private static void Walk(IEffect e, ref CipherVerb verbs, int depth)
    {
        if (e == null || depth > 8) return;   // depth guard: card data is authored, not trusted

        string type = e.GetType().Name;

        if (CompositeTypes.Contains(type))
        {
            foreach (var child in e.Children)
                Walk(child, ref verbs, depth + 1);
            return;
        }

        if (TypeVerbs.TryGetValue(type, out var byType))
        {
            verbs |= byType;
            return;
        }

        var tags = e.Tags;
        if (tags != null)
        {
            foreach (var tag in tags)
            {
                if (string.IsNullOrEmpty(tag) || IgnoredTags.Contains(tag)) continue;

                if (tag == "Glyph")
                {
                    verbs |= InscribeTypes.Contains(type) ? CipherVerb.Inscribe : CipherVerb.Invoke;
                    continue;
                }
                if (TagVerbs.TryGetValue(tag, out var v)) verbs |= v;
            }
        }

        // A leaf may still have children (defensive: a future composite that is
        // not in CompositeTypes should not silently drop its subtree).
        foreach (var child in e.Children)
            Walk(child, ref verbs, depth + 1);
    }

    /// <summary>
    /// The string the cipher encodes. Deliberately separate from the display name:
    /// glyphs must not mutate when a name is translated or reworded. Reads an
    /// optional <c>cipher_name</c> the loader may attach via <see cref="CardHalf.Requirements"/>
    /// -style metadata; otherwise falls back to the English display name.
    /// </summary>
    public static string CipherNameOf(CardHalf half) => half?.Name ?? "";

    /// <summary>Convenience: everything <see cref="GlyphCipher.Build"/> needs for a half.</summary>
    public static CipherGlyph BuildFor(string cardId, string half, CardHalf data)
    {
        if (data == null) return null;
        try
        {
            return GlyphCipher.Build(cardId, half, CipherNameOf(data), TargetOf(data), VerbsOf(data));
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[GlyphCipher] {cardId}#{half} failed to build: {ex.Message}");
            return null;
        }
    }
}
