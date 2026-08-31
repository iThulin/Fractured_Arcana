// ============================================================
// NegotiationBarks.cs
//
// Purpose:        Module D (spoken moves) + Module A (tells)
//                 content tables. Every mechanical action is
//                 presented as a line the wizard actually says,
//                 flavored by the NPC's current stance; the NPC's
//                 turn and stance changes bark from here too.
//                 One table serves both the action UI (Phase 3b)
//                 and the log (Phase 4), written once, per the
//                 redesign doc.
// Layer:          Data (content only, no state)
// Collaborators:  NegotiationState.cs (NPC-turn barks),
//                 NegotiationManager.cs (spoken-move buttons)
// See:            negotiation_redesign_v1.md §3c, Phase 3b
// ============================================================

/// <summary>How the last table with this counterpart, in THIS life, ended:
/// drives their re-meeting opener (spec §6b). Mapped from DealRecord.Outcome
/// by NegotiationState.ApplyContinuity.</summary>
public enum NegotiationContinuityKind
{
    WarmReturn,      // signed at 4 stars or better
    CoolReturn,      // signed, unremarkably
    WalkedBefore,    // the player walked away
    TimedOutBefore,  // their patience ran out (TheyLeft)
    CollapsedBefore, // the table hit maximum tension
}

/// <summary>Static content tables for the v2 negotiation: spoken-move lines
/// (token × stance, with "{term}" substituted), stance tells, NPC-turn barks,
/// and squeeze lines. Pure lookup, no game state.</summary>
public static class NegotiationBarks
{
    // ── Continuity openers (§6b): consequences walk back in the door ─────

    public static string ContinuityLine(NpcArchetypeType a, NegotiationContinuityKind k)
    {
        return (a, k) switch
        {
            (NpcArchetypeType.Merchant,    NegotiationContinuityKind.WarmReturn) => "“Back again. Our last arrangement paid exactly as written, which is my favourite kind. Sit, sit.”",
            (NpcArchetypeType.Commander,   NegotiationContinuityKind.WarmReturn) => "“You kept terms. That is not forgotten on my ground. Say your piece.”",
            (NpcArchetypeType.Scholar,     NegotiationContinuityKind.WarmReturn) => "“Ah, the precise one. Our last agreement is filed under 'satisfactory', which from me is high praise.”",
            (NpcArchetypeType.Opportunist, NegotiationContinuityKind.WarmReturn) => "“My favourite guild returns. Last time worked out lovely for everybody. Mostly everybody.”",
            (NpcArchetypeType.Idealist,    NegotiationContinuityKind.WarmReturn) => "“You dealt fairly with us once. I remember it, and so do the people I answer to.”",
            (NpcArchetypeType.Survivor,    NegotiationContinuityKind.WarmReturn) => "The crossbow stays on its hook this time. “You dealt straight before. Talk.”",
            (_,                            NegotiationContinuityKind.WarmReturn) => "“You again. Our last dealings ended well. Let us see if that holds.”",

            (NpcArchetypeType.Merchant,    NegotiationContinuityKind.CoolReturn) => "“Back again? Last time was… adequate. Let's improve on adequate.”",
            (NpcArchetypeType.Commander,   NegotiationContinuityKind.CoolReturn) => "“We have dealt before. It was acceptable. Begin.”",
            (NpcArchetypeType.Scholar,     NegotiationContinuityKind.CoolReturn) => "“Our previous agreement was serviceable, if unremarkable. Proceed.”",
            (NpcArchetypeType.Opportunist, NegotiationContinuityKind.CoolReturn) => "“Round two. No hard feelings about last time. Mostly.”",
            (NpcArchetypeType.Idealist,    NegotiationContinuityKind.CoolReturn) => "“We have sat here before. It ended fairly, if not warmly.”",
            (NpcArchetypeType.Survivor,    NegotiationContinuityKind.CoolReturn) => "“You've been here before. Nobody bled. Out here that counts for something.”",
            (_,                            NegotiationContinuityKind.CoolReturn) => "“We have done business before. It went as business goes.”",

            (NpcArchetypeType.Merchant,    NegotiationContinuityKind.WalkedBefore) => "“Last time you left my table with empty hands. Buyers who walk twice rarely get a third chair.”",
            (NpcArchetypeType.Commander,   NegotiationContinuityKind.WalkedBefore) => "“You walked away from me once. State why this time is different.”",
            (NpcArchetypeType.Scholar,     NegotiationContinuityKind.WalkedBefore) => "“As I recall, you declined to conclude. I have annotated my expectations accordingly.”",
            (NpcArchetypeType.Opportunist, NegotiationContinuityKind.WalkedBefore) => "“The one that got away, back again. They usually come back.”",
            (NpcArchetypeType.Idealist,    NegotiationContinuityKind.WalkedBefore) => "“You turned from us once. I hope the road has changed your mind, and not just your route.”",
            (NpcArchetypeType.Survivor,    NegotiationContinuityKind.WalkedBefore) => "“You walked once. People who walk make me careful.”",
            (_,                            NegotiationContinuityKind.WalkedBefore) => "“You have sat here before, and you left. Let us see what has changed.”",

            (NpcArchetypeType.Merchant,    NegotiationContinuityKind.TimedOutBefore) => "“You, again. Last time you talked until I ran out of afternoon. Brevity, this time.”",
            (NpcArchetypeType.Commander,   NegotiationContinuityKind.TimedOutBefore) => "“Last time you spent my patience and bought nothing with it. Not today.”",
            (NpcArchetypeType.Scholar,     NegotiationContinuityKind.TimedOutBefore) => "“Our last session ended un-concluded, at considerable cost to my schedule.”",
            (NpcArchetypeType.Opportunist, NegotiationContinuityKind.TimedOutBefore) => "“Slow play, last time. The market moved without you. Quicker now, yes?”",
            (NpcArchetypeType.Idealist,    NegotiationContinuityKind.TimedOutBefore) => "“Last time the daylight ran out before agreement did. Let us do better by each other.”",
            (NpcArchetypeType.Survivor,    NegotiationContinuityKind.TimedOutBefore) => "“You dithered once. Out here, dithering is how people become landmarks.”",
            (_,                            NegotiationContinuityKind.TimedOutBefore) => "“Last time, my patience ran out before your answer arrived. Begin.”",

            (NpcArchetypeType.Merchant,    NegotiationContinuityKind.CollapsedBefore) => "“You. My ledger remembers how our last meeting ended. One raised voice and this one ends the same way.”",
            (NpcArchetypeType.Commander,   NegotiationContinuityKind.CollapsedBefore) => "“Our last exchange ended in shouting. On my ground, it will not end that way twice.”",
            (NpcArchetypeType.Scholar,     NegotiationContinuityKind.CollapsedBefore) => "“I have not forgotten how our last conversation… concluded. Do regulate yourself.”",
            (NpcArchetypeType.Opportunist, NegotiationContinuityKind.CollapsedBefore) => "“Look who's back. Last time got loud. Loud is bad for business.”",
            (NpcArchetypeType.Idealist,    NegotiationContinuityKind.CollapsedBefore) => "“I remember the anger you brought to this table. Leave it outside, or leave with it.”",
            (NpcArchetypeType.Survivor,    NegotiationContinuityKind.CollapsedBefore) => "The crossbow is already levelled when you sit. “Give me one reason to think this ends quieter.”",
            (_,                            NegotiationContinuityKind.CollapsedBefore) => "“Our last meeting ended badly. Prove this one won't.”",
            _                                                                        => "“We have met before. Sit.”",
        };
    }

    // ── Stance tells (portrait caption / log line on stance change) ──────

    public static string StanceTell(NpcArchetypeType a, NpcStance s)
    {
        // Archetype-flavored where it's cheap; generic fallback otherwise.
        return (a, s) switch
        {
            (NpcArchetypeType.Merchant,    NpcStance.Eager)     => "Their eyes keep drifting to your coin purse.",
            (NpcArchetypeType.Commander,   NpcStance.Eager)     => "They lean over the map. They want what you're offering.",
            (NpcArchetypeType.Scholar,     NpcStance.Eager)     => "They've stopped pretending not to be curious.",
            (NpcArchetypeType.Opportunist, NpcStance.Eager)     => "They're already counting their cut of something.",
            (NpcArchetypeType.Idealist,    NpcStance.Eager)     => "Hope, plain and unguarded, crosses their face.",
            (NpcArchetypeType.Survivor,    NpcStance.Eager)     => "For a moment the wariness lifts. They NEED this.",
            (_,                            NpcStance.Eager)     => "They lean in, appetite plain on their face.",
            (NpcArchetypeType.Commander,   NpcStance.Guarded)   => "Parade rest. They're giving you nothing.",
            (NpcArchetypeType.Opportunist, NpcStance.Guarded)   => "The easy patter stops. They're recalculating you.",
            (NpcArchetypeType.Survivor,    NpcStance.Guarded)   => "Their hand hasn't left the crossbow since you sat down.",
            (_,                            NpcStance.Guarded)   => "Arms crossed. They're giving nothing away.",
            (NpcArchetypeType.Scholar,     NpcStance.Wavering)  => "They re-read the clause a third time, pen hovering.",
            (NpcArchetypeType.Idealist,    NpcStance.Wavering)  => "They look back toward the people they answer to, torn.",
            (_,                            NpcStance.Wavering)  => "They glance at the terms again, uncertain.",
            (NpcArchetypeType.Commander,   NpcStance.Irritated) => "Their jaw sets. You are spending their patience.",
            (NpcArchetypeType.Scholar,     NpcStance.Irritated) => "They correct your grammar. It's not a good sign.",
            (NpcArchetypeType.Survivor,    NpcStance.Irritated) => "They shift their weight toward the exit. And the trigger.",
            (_,                            NpcStance.Irritated) => "Their jaw is tight. Tread carefully.",
            (NpcArchetypeType.Merchant,    NpcStance.Expansive) => "They pour you a drink. Business is pleasure.",
            (NpcArchetypeType.Idealist,    NpcStance.Expansive) => "They share their bread with you. It's not a tactic.",
            (NpcArchetypeType.Survivor,    NpcStance.Expansive) => "They almost smile. Out here, that's an embrace.",
            (_,                            NpcStance.Expansive) => "Open hands, easy smile. The table is warm.",
            _                                                   => "They study you in silence.",
        };
    }

    // ── Spoken moves (Module D): what YOUR wizard says ────────────────────
    // "{term}" is replaced with the targeted clause's short name.

    public static string SpokenLine(LeverageToken token, NpcStance stance, NpcArchetypeType a)
    {
        switch (token)
        {
            case LeverageToken.Charm:
                return stance switch
                {
                    NpcStance.Eager     => "“Profit shared with a friend is profit twice.” You smile like you mean it.",
                    NpcStance.Guarded   => "“You keep your cards close. I respect that too much to bluff you.”",
                    NpcStance.Wavering  => "“You already know these terms are fair. I can see it.” You hold their gaze, warm and steady.",
                    NpcStance.Irritated => "“Come now. Surely we're past bristling at one another.”",
                    _                   => "You raise the cup they poured. “To long associations, and to the {term}.”",
                };
            case LeverageToken.Persuade:
                return stance switch
                {
                    NpcStance.Eager     => "“Run the numbers yourself. The {term} pays you better my way.”",
                    NpcStance.Guarded   => "You lay the argument out plainly, point by point, nothing hidden.",
                    NpcStance.Wavering  => "“You've half-agreed already. Let me give you the other half.”",
                    NpcStance.Irritated => "“Forget the rhetoric. Here is why the {term} is wrong as written.”",
                    _                   => "“Between reasonable people, the {term} argues itself.”",
                };
            case LeverageToken.Connections:
                return stance switch
                {
                    NpcStance.Eager     => "“The Guild still talks about your last run. Imagine what they'd say about this deal.”",
                    NpcStance.Guarded   => "“The guild's word is good. Ask anyone whose word YOU trust.”",
                    NpcStance.Wavering  => "“Half the Exchange has signed with us already. You'd be in fine company.”",
                    NpcStance.Irritated => "“I was warned you drive hard bargains. I was also told you were fair.”",
                    _                   => "“We already share friends, and friends share terms.”",
                };
            case LeverageToken.Intimidate:
                return stance switch
                {
                    NpcStance.Wavering  => "You let the silence go cold. “The {term}, as I've drawn it. Sign, while the offer stands.”",
                    NpcStance.Guarded   => "“Don't mistake my patience for a lack of alternatives.”",
                    _                   => "You set both hands on the table. “Consider carefully what the guild does to broken deals.”",
                };
            case LeverageToken.Demonstration:
                return stance switch
                {
                    NpcStance.Guarded   => "A flick of your fingers, and the candleflames bend toward you and hold there.",
                    NpcStance.Eager     => "You let a sliver of the guild's power play across your knuckles. “This is what you'd be buying.”",
                    _                   => "You demonstrate, briefly and precisely, why guild wizards are worth their fee.",
                };
            case LeverageToken.Offering:
                return stance switch
                {
                    NpcStance.Eager     => "You open the lacquered case slowly, letting the contents catch the light. “A sample of what partnership yields.”",
                    NpcStance.Guarded   => "You slide the case across without ceremony. “No strings. Weigh it yourself.”",
                    NpcStance.Wavering  => "“Perhaps this settles the doubt.” You push the case gently across the table.",
                    NpcStance.Irritated => "“A gesture of good faith, and no more words until you've seen it.”",
                    _                   => "“A gift between friends, then, and friends talk the {term} honestly.”",
                };
            case LeverageToken.Insight:
                return "You say nothing for a moment. You just watch their hands, their eyes, the set of their jaw.";
            case LeverageToken.Patience:
                return "You refill their cup, sit back, and let the moment stretch.";
            default:
                return "You make your move.";
        }
    }

    /// <summary>Insight's other use has its own line (flip vs read).</summary>
    public const string InsightFlipLine =
        "“And this clause, face-down at your elbow. Shall we read it together?” You tap the hidden parchment.";

    // ── Mechanical previews (shown under each spoken line) ───────────────

    public static string PressPreview(NpcStance s) => s switch
    {
        NpcStance.Irritated => "Badly timed. In this mood it will backfire and only raise the tension.",
        NpcStance.Wavering  => "They're wavering: the clause moves your way and the room cools further.",
        NpcStance.Guarded   => "The clause moves your way, but they resent the pressure and tension climbs.",
        NpcStance.Expansive => "The clause moves your way, and the warmth in the room deepens.",
        _                   => "The clause moves one step your way.",
    };

    public static string OfferPreview(NpcStance s, string resolveName) => s switch
    {
        NpcStance.Eager   => $"They're eager. The clause jumps two steps your way and tension falls, though the gift feeds their {resolveName}.",
        NpcStance.Guarded => $"They pocket it coldly: the clause moves your way, but you earn no warmth and their {resolveName} still grows.",
        _                 => $"The clause moves your way and tension eases, but the gift feeds their {resolveName}.",
    };

    public const string InsightFlipPreview =
        "Turns the selected face-down clause face up.";
    public const string InsightReadPreview =
        "Reads how their mood will turn next.";
    public const string PatiencePreview =
        "The moment stretches. Their patience holds, they make no move, and their mood shifts.";

    // ── Player-move resolution barks (how it landed) ─────────────────────

    public static string PressResolution(NpcStance s, bool backfired) =>
        backfired
            ? "Badly timed. Their eyes narrow. You've pressed a raw nerve."
            : s switch
            {
                NpcStance.Wavering  => "It lands while they waver. They give ground, and the air softens.",
                NpcStance.Guarded   => "They concede the point, but resent the pressure. The room cools.",
                NpcStance.Expansive => "They laugh and wave the clause your way. “For a friend of the table!”",
                _                   => "The clause shifts your way.",
            };

    public static string OfferResolution(NpcStance s, string resolveName) => s switch
    {
        NpcStance.Eager   => $"Their eyes light up, and the goods vanish quickly. (Their {resolveName} grows.)",
        NpcStance.Guarded => $"They pocket it without a flicker of thanks. (Their {resolveName} grows, and you get no warmth for it.)",
        _                 => $"A tangible offer. This they can work with. (Their {resolveName} grows.)",
    };

    // ── NPC-turn barks ────────────────────────────────────────────────────

    public static string NpcPullBark(NpcArchetypeType a, string termName, bool hard)
    {
        // The hard pull (Hostile zone / hardened) reads as its own trailing
        // sentence: grafting it mid-clause broke half the lines' grammar.
        string tail = hard ? " The ugly mood puts weight behind it." : "";
        return a switch
        {
            NpcArchetypeType.Merchant    => $"They tap the {termName} and slide it back toward themselves. “My costs, you understand.”{tail}",
            NpcArchetypeType.Commander   => $"“The {termName} is not negotiable at that figure.” They drag it back.{tail}",
            NpcArchetypeType.Scholar     => $"“Your reading of the {termName} is… generous.” They correct it.{tail}",
            NpcArchetypeType.Opportunist => $"Somewhere between two sentences, the {termName} slid back their way. You almost didn't catch it.{tail}",
            NpcArchetypeType.Idealist    => $"“The {termName} feeds people. I won't soften it.” They pull it back, unapologetic.{tail}",
            NpcArchetypeType.Survivor    => $"“I've been burned on the {termName} before.” They claw it back.{tail}",
            _                            => $"They pull the {termName} back toward their side.{tail}",
        };
    }

    public static string NpcGuileBark(NpcArchetypeType a, string termName)
    {
        return a switch
        {
            NpcArchetypeType.Merchant    => $"“Ah. Did I mention the {termName} carries a handling clause? Standard practice.”",
            NpcArchetypeType.Commander   => $"“And the {termName} will be on MY schedule, not yours.”",
            NpcArchetypeType.Opportunist => $"“Small print on the {termName}, nothing to worry about.” Their smile says otherwise.",
            NpcArchetypeType.Scholar     => $"“Per the standard Warden rider, which the {termName} incorporates by reference, naturally.”",
            NpcArchetypeType.Idealist    => $"“And the {termName} must be sworn before witnesses. All of them.”",
            NpcArchetypeType.Survivor    => $"“The {termName} happens on my terms, my route, my hours. Non-negotiable.”",
            _                            => $"They rework the {termName} while you're mid-sentence.",
        };
    }

    public static string NpcThreatBark(NpcArchetypeType a) => a switch
    {
        NpcArchetypeType.Merchant    => "“I hear your rivals pay better for this sort of arrangement…” They let the threat hang.",
        NpcArchetypeType.Commander   => "“I have other ways of resolving jurisdiction problems.”",
        NpcArchetypeType.Opportunist => "“Be a shame if certain people learned your route. Anyway. Where were we?”",
        NpcArchetypeType.Idealist    => "“The Circle will hear how the guild bargains with the desperate. All of it.”",
        NpcArchetypeType.Survivor    => "The crossbow shifts, not quite at you. Not quite away, either.",
        _                            => "They let a pointed silence do the threatening for them.",
    };

    public static string NpcPoiseBark(NpcArchetypeType a) => a switch
    {
        NpcArchetypeType.Merchant  => "They exhale slowly, smoothing their coat. “Let us… not ruin a profitable afternoon.”",
        NpcArchetypeType.Commander => "They step back from the table and unclench, deliberately. “From the top, then.”",
        NpcArchetypeType.Scholar   => "They remove their spectacles, polish them, and begin again in a level voice.",
        NpcArchetypeType.Idealist  => "They close their eyes, breathe, and forgive you. It's somehow worse than anger.",
        NpcArchetypeType.Survivor  => "They step back out of arm's reach and lower their voice. “Again. Slower.”",
        _                          => "They visibly master themselves and step back from the brink.",
    };

    /// <summary>Pure speech and stage direction; the mechanical grant is
    /// logged separately by NegotiationState (Dialogue speaks, the grant
    /// line counts; spec §3b).</summary>
    public static string NpcGiftBark(NpcArchetypeType a) => a switch
    {
        NpcArchetypeType.Merchant    => "They push something across the table, unasked. “To a long association.”",
        NpcArchetypeType.Scholar     => "“You argue well. Here, something I noticed that may help us both.”",
        NpcArchetypeType.Opportunist => "“On the house. First one always is.”",
        NpcArchetypeType.Idealist    => "“The road is kinder when walked together.”",
        NpcArchetypeType.Survivor    => "They hand you something without a word. Out here, that means everything.",
        _                            => "A gesture of goodwill crosses the table.",
    };

    /// <summary>The tip of the hand (spec §4c): fired once per table, on the
    /// NPC's first Hold or Guile move while a face-down clause remains. An
    /// archetype-voiced hint that the small print exists, for players who
    /// listen; never repeated, never an alarm.</summary>
    public static string SmallPrintHint(NpcArchetypeType a) => a switch
    {
        NpcArchetypeType.Merchant    => "“Read everything before you sign. I tell all my partners that. Almost all.”",
        NpcArchetypeType.Commander   => "“The written terms are the terms. All of them.” They square the papers, including one you haven't seen.",
        NpcArchetypeType.Scholar     => "“You have, of course, read the incorporated appendices.” It is not phrased as a question.",
        NpcArchetypeType.Opportunist => "“It's all in the paperwork somewhere.” Their smile rests on a page you can't see.",
        NpcArchetypeType.Idealist    => "“Nothing here is hidden from those who trouble to look.” They mean it kindly. It is still a warning.",
        NpcArchetypeType.Survivor    => "One paper stays angled away from you. An old habit, from people who've been robbed by contracts before.",
        _                            => "Their fingers rest, briefly, on a paper they haven't turned over.",
    };

    public static string NpcHoldBark(NpcArchetypeType a) => a switch
    {
        NpcArchetypeType.Merchant    => "They stroke their beard and hold their position, watching you.",
        NpcArchetypeType.Commander   => "They wait, unmoving, letting you commit first.",
        NpcArchetypeType.Scholar     => "They make a small note in the margin and wait for you to say something worth recording.",
        NpcArchetypeType.Opportunist => "They shuffle something from hand to hand, watching your eyes instead of the table.",
        NpcArchetypeType.Idealist    => "They wait with the patience of someone who believes you'll do the right thing.",
        NpcArchetypeType.Survivor    => "They go very still, the way prey does. Or predators.",
        _                            => "They hold their position, watching you.",
    };

    // ── Module B: the closing squeeze ─────────────────────────────────────

    public static string SqueezeOpen(NpcArchetypeType a, string termName) => a switch
    {
        NpcArchetypeType.Merchant    => $"They take your hand, and hold it, smiling. “One amendment, and we're done: the {termName} tilts a little my way. Between partners, surely that's nothing.”",
        NpcArchetypeType.Commander   => $"They grip your hand and don't let go. “One condition. The {termName}, on my terms. Then we're done.”",
        NpcArchetypeType.Scholar     => $"“Before we sign, an erratum.” The pen hovers over the {termName}. “Purely editorial, you understand.”",
        NpcArchetypeType.Opportunist => $"They shake warmly, and you feel the {termName} shift somewhere in the fine print. “Standard closing adjustment. Everyone does it.”",
        NpcArchetypeType.Idealist    => $"They hold your hand in both of theirs. “One more kindness. The {termName}, for the ones who need it. You won't miss it.”",
        NpcArchetypeType.Survivor    => $"They pause mid-shake, grip tightening. “The {termName}. Sweeten it. Then we're square.”",
        _                            => $"They clasp your hand, and hold it. “One last adjustment to the {termName}. A trifle.”",
    };

    public const string SqueezeBlink =
        "A long pause… then they laugh and shake properly. “Worth the try. Done as written.”";
    public const string SqueezeBristle =
        "Their grip tightens. “Then we are not as close as I hoped.”";

    // ── Phase 5: school signature moves ──────────────────────────────────

    public static string SchoolMoveLine(CardSchool s) => s switch
    {
        CardSchool.Adept        => "You adapt, the way students of every school learn to, improvising leverage from nothing.",
        CardSchool.Elementalist => "You open one hand. The candleflames roar to the ceiling and hold there, burning cold. The table goes very quiet.",
        CardSchool.Druid        => "You breathe out, and something green and patient enters the room. Shoulders lower. The air sweetens.",
        CardSchool.Necromancer  => "You tilt your head, listening to someone who isn't there. The dead have sat at this table before.",
        CardSchool.Tinker       => "Your hands are already working. A whir, a click, and something small and marvelous sits on the felt.",
        CardSchool.Enchanter    => "You murmur three syllables under your breath, and their mood turns like a key in a lock.",
        CardSchool.Arcanist     => "The pattern of them unfolds before you: every tell, every tic, indexed and cross-referenced.",
        CardSchool.Chronomancer => "You reach back through the last few seconds and pull. The words unhappen. Only you remember.",
        _                       => "You reach for your school's deeper art.",
    };
}
