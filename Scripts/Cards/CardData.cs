using Godot;
using System.Collections.Generic;

// ============================================================
// CardData.cs
//
// Purpose:        Legacy CardData Node2D wrapper plus the shared
//                 enums (CardSchool, CardRarity, etc.) used across
//                 both the legacy data layer and the runtime layer.
// Layer:          Data
// Collaborators:  CardRuntime.cs (the actual runtime types live there)
// See:            README §7 — "CardData.cs vs CardRuntime.cs"
// ============================================================
//
// NOTE: The CardData class itself is legacy. New combat code should
// reference CardRuntime.Card / CardHalf instead. The enums in this
// file ARE the source of truth — both layers depend on them.

/// <summary>Top-level category used by the legacy CardData model. Runtime cards classify themselves via tags and effect types instead.</summary>
public enum CardType { Attack, Skill, Environment, Summon, Reaction }

/// <summary>Coarse-grained targeting bucket used by the legacy CardData model. Runtime targeting is expressed by <see cref="ITargetSelector"/> implementations.</summary>
public enum TargetType { None, SingleEnemy, AllEnemies, Tile, Self, Global }

/// <summary>Wizard school. Must match the `school` field in card JSON exactly (case-insensitive on load). Adding a school requires updating JSON cards, the card schema enum, and any UI that lists schools. Schools: Druid, Tinker, Chronomancer, Necromancer, Enchanter, Elementalist, Arcanist.</summary>
public enum CardSchool { Adept, Elementalist, Druid, Necromancer, Tinker, Enchanter, Arcanist, Chronomancer }

/// <summary>Card rarity tier. Drives loot tables, draft rates, and UI border colour.</summary>
public enum CardRarity { Common, Uncommon, Rare, Legendary }

/// <summary>Identifies whether a unit is controlled by the player or by the computer (enemy AI or environmental).</summary>
public enum Controller { Player, Computer }

/// <summary>Lifecycle zone a card currently occupies. Transitions are owned by <c>DeckManager</c>.</summary>
public enum Zone { Library, Hand, Grave, Stack, Exile }

/// <summary>
/// Legacy scene-graph card representation. Predates the runtime <see cref="Card"/> /
/// <see cref="CardHalf"/> split in <c>CardRuntime.cs</c> and is retained only for the
/// handful of scenes that still instantiate cards as Node2Ds directly. Do not add new
/// callers; prefer <see cref="Card"/> for any new combat code.
/// </summary>
public partial class CardData : Node2D
{
    /// <summary>Display name shown on the card face.</summary>
    public string CardName { get; set; }

    /// <summary>Rules text for the primary half.</summary>
    public string Description { get; set; }

    /// <summary>Rules text for the channeled variant, when present. Empty string when the card has no channel option.</summary>
    public string ChannelDescription { get; set; }

    /// <summary>Mana cost to play. Mirrors the per-half cost in <see cref="CardHalf.ManaCost"/>.</summary>
    public int ManaCost { get; set; }

    /// <summary>Legacy category (see <see cref="CardType"/>).</summary>
    public CardType Type { get; set; }

    /// <summary>Legacy targeting bucket (see <see cref="TargetType"/>).</summary>
    public TargetType Target { get; set; }

    /// <summary>School the card belongs to. Drives attunement, draft rates, and school-specific effects.</summary>
    public CardSchool School { get; set; }

    /// <summary>
    /// Loose key/value bag used by older cards to carry numeric parameters
    /// (e.g. "damage" → 4). New cards encode parameters into typed
    /// <see cref="IEffect"/> implementations instead.
    /// </summary>
    public Dictionary<string, float> Effects = new();
}