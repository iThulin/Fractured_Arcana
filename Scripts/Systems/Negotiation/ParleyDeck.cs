using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

// ============================================================
// ParleyDeck.cs
//
// Purpose:        v3.1 "The Parley Deck". The player's leverage
//                 at a negotiation is a deck of cards drawn from
//                 who they are (school, companions, buildings,
//                 court) instead of a stock of tokens. This file
//                 holds the card model, the card library loaded
//                 from Data/Negotiations/parley_cards.json, the
//                 deck builder, the NPC's face-down cards, and
//                 the posture / band vocabulary the table shows
//                 instead of numbers.
// Layer:          Data
// Collaborators:  NegotiationState.cs (plays the cards),
//                 NegotiationManager.cs (renders hand + NPC row),
//                 NpcArchetype.cs (LeverageToken kept as the
//                 legacy key for buildings, patrons, telemetry)
// See:            docs/negotiation_parley_deck_spec_v1.md
// ============================================================

/// <summary>What a card does when played (spec §2b).</summary>
public enum ParleyEffect
{
    Read,      // reveal a slip's valuation as a band (Amount = extra top slips too)
    ReadTop,   // reveal their top want as a band
    Pull,      // v3.3: move touched open terms one step toward you (both sides); conditions come off
    Warm,      // goodwill +Amount (archetype-shaped)
    Force,     // next Ask ignores credit; goodwill cost; grievance
    Purse,     // put a purse on your side (Amount = gold)
    Display,   // put a display of magic on your side (Amount = +npc over the table value, 1 = none)
    Hold,      // this turn's table action costs no patience
    Flip,      // turn one of their face-down cards face-up
    Bundle,    // next Ask names two slips, judged on the sum
    Sweeten,   // +Amount to one of your slips' worth to them
    Bluff,     // threaten to walk: if the package is worth enough to them, they concede the cheapest open slip
    Draw,      // draw Amount
    Recall,    // return the last card you played to your hand
    Claim,     // v3.4: lock every term of theirs in the category at "yours" (Amount 2: at contested too), one credit check
    Concede,   // v3.4: lock every term of yours in the category at "theirs" as theirs (Amount 2: at contested too)
    Shake,     // v3.4 fixed card: shake hands (settlement preview, then the handshake)
    Walk,      // v3.4 fixed card: walk away
    SchoolMove,// v3.4 fixed card: the once-per-table signature move
}

/// <summary>How a card lands: the portrait reacts, and hard cards earn a grievance.</summary>
public enum ParleyTone { Warm, Level, Hard }

/// <summary>v3.2: the four things a table is about. Every clause is in
/// exactly one; cards act on a category, never on a single slip.</summary>
public enum ClauseCategory { Coin, Access, Standing, Lore }

/// <summary>What a card sweeps. Auto = the category where it would do the
/// most right now (the player never picks a target). None = table-wide.</summary>
public enum ParleyCategory { None, Auto, Coin, Access, Standing, Lore }

/// <summary>How much of the category a sweep touches (spec v3.2 §2):
/// universal cards reach Some (a random SweepSomeCount), school cards All.</summary>
public enum ParleyReach { Some, All }

public static class ClauseCategories
{
    public static ClauseCategory Of(ClauseKind k) => k switch
    {
        ClauseKind.Gold => ClauseCategory.Coin,
        ClauseKind.Goods => ClauseCategory.Coin,
        ClauseKind.Service => ClauseCategory.Access,
        ClauseKind.Passage => ClauseCategory.Access,
        ClauseKind.Honor => ClauseCategory.Standing,
        ClauseKind.Favor => ClauseCategory.Standing,
        ClauseKind.Obligation => ClauseCategory.Standing,
        ClauseKind.Lore => ClauseCategory.Lore,
        ClauseKind.Spell => ClauseCategory.Lore,
        _ => ClauseCategory.Lore,
    };

    public static string Word(ClauseCategory c) => c switch
    {
        ClauseCategory.Coin => "coin",
        ClauseCategory.Access => "access",
        ClauseCategory.Standing => "standing",
        _ => "lore",
    };

    public static ClauseCategory? Fixed(ParleyCategory p) => p switch
    {
        ParleyCategory.Coin => ClauseCategory.Coin,
        ParleyCategory.Access => ClauseCategory.Access,
        ParleyCategory.Standing => ClauseCategory.Standing,
        ParleyCategory.Lore => ClauseCategory.Lore,
        _ => null,
    };
}

/// <summary>How they sit. Derived from Credit; shown instead of the number.</summary>
public enum Posture { Guarded, Level, Eager }

/// <summary>One card definition (authored) or one instance in a deck (cloned
/// with a unique InstanceId so duplicates in hand stay distinguishable).</summary>
public class ParleyCard
{
    public string Id = "";
    public string Name = "";
    public string Line = "";
    public ParleyTone Tone = ParleyTone.Level;
    public ParleyCategory Category = ParleyCategory.None;
    public ParleyReach Reach = ParleyReach.Some;
    public ParleyEffect Effect = ParleyEffect.Read;
    public int Amount = 1;
    public bool Once = true;
    public string Source = "";

    [JsonIgnore] public int InstanceId = 0;
    /// <summary>Read / Pull / Sweeten sweep a category; the rest act on the table.</summary>
    [JsonIgnore] public bool Sweeps => Effect is ParleyEffect.Read or ParleyEffect.Pull or ParleyEffect.Sweeten or ParleyEffect.Claim or ParleyEffect.Concede;
    /// <summary>Deck manipulation and the held clock don't spend the turn.</summary>
    [JsonIgnore] public bool IsFree => Effect is ParleyEffect.Hold or ParleyEffect.Draw or ParleyEffect.Recall;
    /// <summary>The three cards that are always in hand and never drawn.</summary>
    [JsonIgnore] public bool IsFixed => Effect is ParleyEffect.Shake or ParleyEffect.Walk or ParleyEffect.SchoolMove;

    public ParleyCard CloneInstance(int instanceId) => new()
    {
        Id = Id, Name = Name, Line = Line, Tone = Tone, Category = Category, Reach = Reach, Effect = Effect,
        Amount = Amount, Once = Once, Source = Source, InstanceId = instanceId,
    };

    /// <summary>"coin" / "the busiest category" (no reach word).</summary>
    [JsonIgnore] public string SweepWordBare
    {
        get
        {
            var fixedCat = ClauseCategories.Fixed(Category);
            return fixedCat.HasValue ? ClauseCategories.Word(fixedCat.Value) : "the busiest category";
        }
    }

    /// <summary>"all coin" / "some standing" / "the busiest category".</summary>
    [JsonIgnore] public string SweepWord
    {
        get
        {
            if (!Sweeps) return "";
            string reach = Reach == ParleyReach.All ? "all" : "some";
            var fixedCat = ClauseCategories.Fixed(Category);
            return fixedCat.HasValue ? $"{reach} {ClauseCategories.Word(fixedCat.Value)}" : $"{reach} of the busiest category";
        }
    }

    /// <summary>The legacy token this card counts as, for telemetry and the
    /// building / patron / debug bridges. Null for the v3.1-only effects.</summary>
    public LeverageToken? AsToken => Effect switch
    {
        ParleyEffect.Read => LeverageToken.Insight,
        ParleyEffect.ReadTop => LeverageToken.Connections,
        ParleyEffect.Pull => LeverageToken.Persuade,
        ParleyEffect.Warm => LeverageToken.Charm,
        ParleyEffect.Force => LeverageToken.Intimidate,
        ParleyEffect.Purse => LeverageToken.Offering,
        ParleyEffect.Display => LeverageToken.Demonstration,
        ParleyEffect.Hold => LeverageToken.Patience,
        _ => null,
    };

    /// <summary>One line of rules text for the card face tooltip.</summary>
    public string RulesText => Effect switch
    {
        ParleyEffect.Read => $"Learn roughly what {SweepWord} slips are worth to them, on both sides.",
        ParleyEffect.ReadTop => "Learn roughly what they want most of yours.",
        ParleyEffect.Pull => $"Pull {SweepWord} terms a step toward you; conditions on them come off.",
        ParleyEffect.Warm => Amount > 1 ? $"Goodwill up by {Amount}, if they're the kind who warms." : "Goodwill up, if they're the kind who warms.",
        ParleyEffect.Force => "Your next ask goes through whether they like it or not. They won't like it.",
        ParleyEffect.Purse => $"Set {Amount} gold on your side of the table.",
        ParleyEffect.Display => "Put a display of your magic on your side of the table.",
        ParleyEffect.Hold => "Your next move won't tick their clock.",
        ParleyEffect.Flip => "Turn one of their face-down cards face-up.",
        ParleyEffect.Bundle => "Your next ask takes everything they hold in that slip's category, judged as one.",
        ParleyEffect.Sweeten => $"{char.ToUpperInvariant(SweepWord[0])}{SweepWord.Substring(1)} slips of yours become worth more to them.",
        ParleyEffect.Bluff => "Threaten to walk. If the deal so far matters to them, they give up their cheapest slip; if not, goodwill falls.",
        ParleyEffect.Draw => $"Draw {Amount}. Doesn't spend the turn.",
        ParleyEffect.Recall => "Take back the last card you played. Doesn't spend the turn.",
        ParleyEffect.Claim => Amount >= 2
            ? $"Lock every term of theirs in {SweepWordBare} that you've pulled to contested or yours. One ask, judged together."
            : $"Lock every term of theirs in {SweepWordBare} that you've pulled to yours. One ask, judged together. If none is there yet, the nearest one.",
        ParleyEffect.Concede => Amount >= 2
            ? $"Give up every term of yours in {SweepWordBare} they've pulled to contested or theirs. Credit rises by what they're worth to them."
            : $"Give up every term of yours in {SweepWordBare} they've pulled to theirs. Credit rises by what they're worth to them. If none is there, what they want most.",
        ParleyEffect.Shake => "Shake hands: everything locked signs, what you've pulled to yours comes with you if credit covers it, what sits at theirs is theirs.",
        ParleyEffect.Walk => "Walk away. Nothing signs; reputation unharmed.",
        ParleyEffect.SchoolMove => "Your school's signature move, once per table.",
        _ => "",
    };
}

/// <summary>The three fixed cards (v3.4): built in code, never in the JSON.</summary>
public static class ParleyFixedCards
{
    public static ParleyCard Shake() => new() { Id = "fixed_shake", Name = "Shake Hands", Line = "You put out your hand.", Effect = ParleyEffect.Shake, Tone = ParleyTone.Level, Source = "fixed", Once = false, InstanceId = -1 };
    public static ParleyCard Walk() => new() { Id = "fixed_walk", Name = "Walk Away", Line = "You step back from the table.", Effect = ParleyEffect.Walk, Tone = ParleyTone.Level, Source = "fixed", Once = false, InstanceId = -2 };
    public static ParleyCard School(string name) => new() { Id = "fixed_school", Name = "★ " + name, Line = "", Effect = ParleyEffect.SchoolMove, Tone = ParleyTone.Level, Source = "fixed", Once = false, InstanceId = -3 };
}

/// <summary>The cards on THEIR side of the table: the beats made visible.</summary>
public enum NpcCardKind { Mid, Final, Squeeze }

public class NpcCard
{
    public NpcCardKind Kind;
    public string Name = "";
    public bool FaceUp = false;
    public bool Played = false;
    /// <summary>What the card will name when it fires, as far as is known
    /// now (set on Flip; refreshed when the beat fires).</summary>
    public string TargetClauseId = "";
}

/// <summary>The authored card set and the lookup tables around it.</summary>
public static class ParleyCardLibrary
{
    private const string PATH = "res://Data/Negotiations/parley_cards.json";

    public class FileShape
    {
        public int Version = 1;
        public List<ParleyCard> Cards = new();
        public Dictionary<string, string> CompanionCards = new();
        public Dictionary<string, string> TokenCards = new();
        public Dictionary<string, Dictionary<string, string>> NpcCards = new();
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        IncludeFields = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    private static FileShape _file;
    private static Dictionary<string, ParleyCard> _byId;

    private static void EnsureLoaded()
    {
        if (_file != null) return;
        _file = new FileShape();
        _byId = new Dictionary<string, ParleyCard>();
        if (!FileAccess.FileExists(PATH))
        {
            GD.PrintErr($"[ParleyDeck] {PATH} missing. The parley deck will be empty.");
            return;
        }
        try
        {
            using var f = FileAccess.Open(PATH, FileAccess.ModeFlags.Read);
            var parsed = JsonSerializer.Deserialize<FileShape>(f.GetAsText(), JsonOptions);
            if (parsed != null) _file = parsed;
        }
        catch (Exception e)
        {
            GD.PrintErr($"[ParleyDeck] failed to parse {PATH}: {e.Message}");
        }
        foreach (var c in _file.Cards)
            if (!string.IsNullOrEmpty(c.Id)) _byId[c.Id] = c;
        GD.Print($"[ParleyDeck] {_byId.Count} cards loaded.");
    }

    public static ParleyCard Get(string id)
    {
        EnsureLoaded();
        return id != null && _byId.TryGetValue(id, out var c) ? c : null;
    }

    public static IEnumerable<ParleyCard> BySource(string source)
    {
        EnsureLoaded();
        return _file.Cards.Where(c => c.Source == source);
    }

    public static string CompanionCardId(string trait)
    {
        EnsureLoaded();
        return trait != null && _file.CompanionCards.TryGetValue(trait, out var id) ? id : null;
    }

    /// <summary>The card a legacy LeverageToken maps to (buildings, patron, debug).</summary>
    public static string TokenCardId(LeverageToken t)
    {
        EnsureLoaded();
        return _file.TokenCards.TryGetValue(t.ToString(), out var id) ? id : null;
    }

    public static string NpcCardName(NpcArchetypeType a, NpcCardKind kind)
    {
        EnsureLoaded();
        if (kind == NpcCardKind.Squeeze)
            return _file.NpcCards.TryGetValue("squeeze", out var sq) && sq.TryGetValue("name", out var n) ? n : "One More Thing";
        string key = kind == NpcCardKind.Mid ? "mid" : "final";
        if (_file.NpcCards.TryGetValue(a.ToString(), out var row) && row.TryGetValue(key, out var name)) return name;
        return kind == NpcCardKind.Mid ? "Their Price" : "Last Word";
    }

    /// <summary>Test seam: install a card set without touching the file.</summary>
    public static void InstallForTests(List<ParleyCard> cards)
    {
        _file = new FileShape { Cards = cards };
        _byId = cards.Where(c => !string.IsNullOrEmpty(c.Id)).ToDictionary(c => c.Id, c => c);
    }
}

/// <summary>Builds the deck for one table from the player's sources (spec §2d).</summary>
public static class ParleyDeckBuilder
{
    private static string SchoolSource(CardSchool s) => $"school:{s}";

    /// <summary>Everyone's seven, the school's four, one per companion trait,
    /// the building rows, the patron, all as fresh instances, unshuffled.</summary>
    public static List<ParleyCard> Build(CardSchool school, List<Companion> party,
                                         LeverageToken patronToken, int patronCount,
                                         out List<string> provenance)
    {
        provenance = new List<string>();
        var ids = new List<(string id, string why)>();

        foreach (var c in ParleyCardLibrary.BySource("universal")) ids.Add((c.Id, "universal"));
        var schoolCards = ParleyCardLibrary.BySource(SchoolSource(school)).ToList();
        if (schoolCards.Count == 0) schoolCards = ParleyCardLibrary.BySource(SchoolSource(CardSchool.Adept)).ToList();
        foreach (var c in schoolCards) ids.Add((c.Id, school.ToString()));

        if (party != null)
            foreach (var companion in party)
            {
                var id = ParleyCardLibrary.CompanionCardId(companion.PersonalityTrait);
                if (id != null) ids.Add((id, companion.Name ?? companion.PersonalityTrait));
            }

        var save = SaveManager.ActiveSave;
        if (save != null)
            foreach (var b in save.Buildings)
            {
                if (b.Tier <= 0) continue;
                var tierData = BuildingDatabase.GetCurrentTierData(b.Id, save);
                if (tierData == null || tierData.BonusNegotiationTokens <= 0) continue;
                if (!Enum.TryParse<LeverageToken>(tierData.BonusTokenType, out var tok)) continue;
                var id = ParleyCardLibrary.TokenCardId(tok);
                if (id == null) continue;
                for (int i = 0; i < tierData.BonusNegotiationTokens; i++) ids.Add((id, b.Name));
            }

        if (patronCount > 0)
        {
            var id = ParleyCardLibrary.TokenCardId(patronToken);
            if (id != null) for (int i = 0; i < patronCount; i++) ids.Add((id, "a patron at court"));
        }

        var deck = new List<ParleyCard>();
        int inst = 1;
        foreach (var (id, why) in ids)
        {
            var def = ParleyCardLibrary.Get(id);
            if (def == null) continue;
            deck.Add(def.CloneInstance(inst++));
            provenance.Add($"{def.Name} ({why})");
        }
        return deck;
    }

    public static ParleyCard Instance(string id, int instanceId)
    {
        var def = ParleyCardLibrary.Get(id);
        return def?.CloneInstance(instanceId);
    }

    public static void Shuffle<T>(List<T> list, Func<int, int> rollBelow)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = rollBelow(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }
}

/// <summary>Words for numbers (spec §5).</summary>
public static class ParleyTells
{
    /// <summary>cheap (0–1) / fair (2–3) / dear (4–5).</summary>
    public static string Band(int npcValue) => npcValue <= 1 ? "cheap" : npcValue <= 3 ? "fair" : "dear";

    public static Posture PostureFor(int credit) =>
        credit <= NegotiationTuning.PostureGuardedCredit ? Posture.Guarded
      : credit >= NegotiationTuning.PostureEagerCredit ? Posture.Eager
      : Posture.Level;

    public static string PostureWord(Posture p) => p switch
    {
        Posture.Guarded => "arms folded",
        Posture.Eager => "leaning in",
        _ => "sitting level",
    };

    /// <summary>What their face would do if you asked now.</summary>
    public static string TellFor(ReactionKind predicted) => predicted switch
    {
        ReactionKind.Agree => "they'd nod",
        ReactionKind.Counter => "they'd hesitate",
        _ => "they'd bristle",
    };
}
