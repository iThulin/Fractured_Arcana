using Godot;

// ============================================================
// NegotiationBarks.cs
//
// Purpose:        Static content tables for negotiation v3:
//                 the NPC's reactions (agree / counter / refuse
//                 / concede bands), the two demand beats, the
//                 squeeze, the collapse, the player's spoken
//                 verbs, continuity openers (§6b) and school
//                 signature-move lines. Pure lookup, no game
//                 state. Every reaction band is an HONEST tell:
//                 the band is computed from the hidden valuation
//                 (NegotiationState.ValueBand / DeficitBand /
//                 ConcedeBand), so what they say is true.
// Layer:          Data (content)
// Collaborators:  NegotiationState.cs (caller),
//                 NegotiationManager.cs (player verb labels)
// See:            docs/negotiation_ledger_spec_v1.md §4
// ============================================================

/// <summary>How the last table with this counterpart ended, this life.</summary>
public enum NegotiationContinuityKind
{
    WarmReturn,      // signed at 4 stars or better
    CoolReturn,      // signed, unremarkably
    WalkedBefore,    // the player walked away
    TimedOutBefore,  // their patience ran out (TheyLeft)
    CollapsedBefore, // goodwill hit zero
}

public static class NegotiationBarks
{
    private static string Pick(params string[] lines) =>
        lines[(int)(GD.Randi() % (uint)lines.Length)];

    // ── Reactions to an Ask (spec §4a) ────────────────────────────────────

    /// <summary>band 0: worth 0–1 to them; 1: 2–3; 2: 4–5 (it cost them).</summary>
    public static string AgreeBark(NpcArchetypeType a, int band) => (a, band) switch
    {
        (NpcArchetypeType.Merchant, 0)    => Pick("“Take it. It's a season old anyway.”", "“Fine, fine. It's yours.”"),
        (NpcArchetypeType.Merchant, 1)    => Pick("“That's fair. Done.”", "“Agreed, at that.”"),
        (NpcArchetypeType.Merchant, _)    => Pick("“…Very well. You drive a hard line, wizard.”", "“That costs me. But… agreed.”"),
        (NpcArchetypeType.Commander, 0)   => Pick("“Granted. It's nothing.”", "“Take it.”"),
        (NpcArchetypeType.Commander, 1)   => Pick("“Acceptable.”", "“Agreed.”"),
        (NpcArchetypeType.Commander, _)   => Pick("“…So be it. Don't make me regret it.”", "“That is a real concession. Note that I made it.”"),
        (NpcArchetypeType.Scholar, 0)     => Pick("“Oh, that? Of course.”", "“Trivially, yes.”"),
        (NpcArchetypeType.Scholar, 1)     => Pick("“A reasonable request. Granted.”", "“Yes, that follows.”"),
        (NpcArchetypeType.Scholar, _)     => Pick("“…I had hoped to keep that. Very well.”", "“You ask a great deal. Agreed, nonetheless.”"),
        (NpcArchetypeType.Opportunist, 0) => Pick("“Sure. Costs me nothing.”", "“Have it.”"),
        (NpcArchetypeType.Opportunist, 1) => Pick("“Fine by me.”", "“Deal.”"),
        (NpcArchetypeType.Opportunist, _) => Pick("“…Ouch. All right. All right.”", "“You're good at this. That stings. Agreed.”"),
        (NpcArchetypeType.Idealist, 0)    => Pick("“Freely given.”", "“Of course. It was always yours to ask.”"),
        (NpcArchetypeType.Idealist, 1)    => Pick("“Yes. That is fair.”", "“Agreed, gladly.”"),
        (NpcArchetypeType.Idealist, _)    => Pick("“…It is a great deal to ask. But yes.”", "“That will be felt here. Still… yes.”"),
        (NpcArchetypeType.Survivor, 0)    => Pick("“Take it. Less to carry.”", "“Yeah. Fine.”"),
        (NpcArchetypeType.Survivor, 1)    => Pick("“Fair enough.”", "“Done.”"),
        (NpcArchetypeType.Survivor, _)    => Pick("“…That hurts. Agreed.”", "“You'd better be worth it. Agreed.”"),
        _                                 => "“Agreed.”",
    };

    public static string CounterBark(NpcArchetypeType a, string ask, string demand) => a switch
    {
        NpcArchetypeType.Merchant    => Pick($"“The {ask}? Only if you add the {demand}.”", $"“I'd give the {ask}, for the {demand}.”"),
        NpcArchetypeType.Commander   => Pick($"“The {ask} is possible. Put the {demand} in and it's done.”", $"“{Cap(ask)} for the {demand}. That's the trade.”"),
        NpcArchetypeType.Scholar     => Pick($"“The {ask}, in exchange for the {demand}. That would balance.”", $"“I could see my way to the {ask}, if the {demand} were included.”"),
        NpcArchetypeType.Opportunist => Pick($"“{Cap(ask)}? Sure, throw in the {demand}.”", $"“Tell you what. The {ask}, if I get the {demand}.”"),
        NpcArchetypeType.Idealist    => Pick($"“The {ask} could be yours, if the {demand} came to us.”", $"“For the {demand}, I would give the {ask}.”"),
        NpcArchetypeType.Survivor    => Pick($"“The {ask}. For the {demand}. Straight swap.”", $"“Give me the {demand} and you can have the {ask}.”"),
        _                            => $"“The {ask}, if you add the {demand}.”",
    };

    public static string DeclineBark(NpcArchetypeType a) => a switch
    {
        NpcArchetypeType.Merchant    => "“Suit yourself.”",
        NpcArchetypeType.Commander   => "“Then it stays as it is.”",
        NpcArchetypeType.Scholar     => "“As you like. The offer is withdrawn.”",
        NpcArchetypeType.Opportunist => "“Your loss. Maybe.”",
        NpcArchetypeType.Idealist    => "“Then we leave it there.”",
        NpcArchetypeType.Survivor    => "“Fine.”",
        _                            => "“Suit yourself.”",
    };

    /// <summary>band 0: one short; 1: two or three short; 2: insultingly far.</summary>
    public static string RefuseBark(NpcArchetypeType a, int band) => (a, band) switch
    {
        (NpcArchetypeType.Merchant, 0) => Pick("“Not quite. Come closer.”", "“Mm. Nearly.”"),
        (NpcArchetypeType.Merchant, 1) => Pick("“No.”", "“Not for what's on this table.”"),
        (NpcArchetypeType.Merchant, _) => Pick("“You insult me.”", "“Do you take me for a fool?”"),
        (NpcArchetypeType.Commander, 0) => Pick("“Not yet.”", "“Almost. Not yet.”"),
        (NpcArchetypeType.Commander, 1) => Pick("“Denied.”", "“No.”"),
        (NpcArchetypeType.Commander, _) => Pick("“You overreach. Badly.”", "“That is an insult, and I will remember it.”"),
        (NpcArchetypeType.Scholar, 0) => Pick("“Close. Not quite the sum.”", "“Nearly.”"),
        (NpcArchetypeType.Scholar, 1) => Pick("“No. The figures don't support it.”", "“I'm afraid not.”"),
        (NpcArchetypeType.Scholar, _) => Pick("“That is not a serious proposal.”", "“Do be serious.”"),
        (NpcArchetypeType.Opportunist, 0) => Pick("“Eh. Close.”", "“Almost had me.”"),
        (NpcArchetypeType.Opportunist, 1) => Pick("“Nah.”", "“No. Next.”"),
        (NpcArchetypeType.Opportunist, _) => Pick("“Ha! No. Wow.”", "“You're funny. No.”"),
        (NpcArchetypeType.Idealist, 0) => Pick("“Not as things stand. Nearly.”", "“Almost, friend.”"),
        (NpcArchetypeType.Idealist, 1) => Pick("“No. I cannot.”", "“That I can't give.”"),
        (NpcArchetypeType.Idealist, _) => Pick("“You ask too much of people who have little.”", "“That is unworthy of you.”"),
        (NpcArchetypeType.Survivor, 0) => Pick("“Not quite.”", "“Close. No.”"),
        (NpcArchetypeType.Survivor, 1) => Pick("“No.”", "“Can't.”"),
        (NpcArchetypeType.Survivor, _) => Pick("“Are you trying to get me killed?”", "“Get out of my camp.”"),
        _                                             => "“No.”",
    };

    // ── Reactions to a Concede (spec §4c) ─────────────────────────────────

    /// <summary>band 0 shrug (0), 1 nod (1), 2 worth something (2–3), 3 eyes light (4–5).</summary>
    public static string ConcedeBark(NpcArchetypeType a, int band) => (a, band) switch
    {
        (NpcArchetypeType.Merchant, 0)    => Pick("A shrug. “If you like.”", "“Mm. Noted.”"),
        (NpcArchetypeType.Merchant, 1)    => Pick("“I'll take it.”", "“That helps, a little.”"),
        (NpcArchetypeType.Merchant, 2)    => Pick("“Now that's worth something.”", "“Good. Good.”"),
        (NpcArchetypeType.Merchant, _)    => Pick("“That… yes. That changes things.”", "His eyes go straight to it. “You have my full attention.”"),
        (NpcArchetypeType.Commander, 0)   => Pick("“Noted.”", "A grunt."),
        (NpcArchetypeType.Commander, 1)   => Pick("“Useful.”", "“Accepted.”"),
        (NpcArchetypeType.Commander, 2)   => Pick("“That is worth having.”", "“Good. That I can use.”"),
        (NpcArchetypeType.Commander, _)   => Pick("“That changes the field.”", "For the first time, they sit forward. “Go on.”"),
        (NpcArchetypeType.Scholar, 0)     => Pick("“Ah. Hm.”", "A polite nod."),
        (NpcArchetypeType.Scholar, 1)     => Pick("“Of some interest.”", "“Noted, with thanks.”"),
        (NpcArchetypeType.Scholar, 2)     => Pick("“Oh… now that is interesting.”", "“That fills a gap. Yes.”"),
        (NpcArchetypeType.Scholar, _)     => Pick("“…Where did you get this?” They are already reading.", "The pen stops. “Say that again.”"),
        (NpcArchetypeType.Opportunist, 0) => Pick("“Cute.”", "“Sure.”"),
        (NpcArchetypeType.Opportunist, 1) => Pick("“Okay. Okay.”", "“I can move that.”"),
        (NpcArchetypeType.Opportunist, 2) => Pick("“Now we're talking.”", "“See, THAT I like.”"),
        (NpcArchetypeType.Opportunist, _) => Pick("“Oh, you beautiful thing.”", "She stops pretending not to care. “Yes. Yes.”"),
        (NpcArchetypeType.Idealist, 0)    => Pick("A gentle nod.", "“Thank you.”"),
        (NpcArchetypeType.Idealist, 1)    => Pick("“That is kind.”", "“It will be put to use.”"),
        (NpcArchetypeType.Idealist, 2)    => Pick("“That will do real good here.”", "“You have my thanks, truly.”"),
        (NpcArchetypeType.Idealist, _)    => Pick("Her hands come together. “Bless you. Truly.”", "“I did not think you would. Thank you.”"),
        (NpcArchetypeType.Survivor, 0)    => Pick("“Huh.”", "A shrug."),
        (NpcArchetypeType.Survivor, 1)    => Pick("“It'll do.”", "“Fine.”"),
        (NpcArchetypeType.Survivor, 2)    => Pick("“…That's real. Thanks.”", "“That gets us through.”"),
        (NpcArchetypeType.Survivor, _)    => Pick("The crossbow goes back on its hook. “All right. Talk.”", "“You don't know what that's worth out here. Maybe you do.”"),
        _                                 => "“Noted.”",
    };

    // ── The beats (spec §4d) ──────────────────────────────────────────────

    public static string MidDemand(NpcArchetypeType a, string want) => a switch
    {
        NpcArchetypeType.Merchant    => $"“Before we go further, I'll want the {want} in this. Understand that.”",
        NpcArchetypeType.Commander   => $"“Let me be clear. The {want} matters more to me than the rest.”",
        NpcArchetypeType.Scholar     => $"“I should say plainly: the {want} is the item I care about.”",
        NpcArchetypeType.Opportunist => $"“Just so we're square: the {want}. That's the one I want.”",
        NpcArchetypeType.Idealist    => $"“I will not pretend otherwise: the {want} is what we need.”",
        NpcArchetypeType.Survivor    => $"“The {want}. That's what I'm here for. Everything else is talk.”",
        _                            => $"“I'll want the {want} in this.”",
    };

    public static string FinalDemand(NpcArchetypeType a, string want) => a switch
    {
        NpcArchetypeType.Merchant    => $"“I've other buyers. The {want}, or we're finished here.”",
        NpcArchetypeType.Commander   => $"“Last word. The {want}. Then I go.”",
        NpcArchetypeType.Scholar     => $"“My time is spent. The {want}, or I return to my work.”",
        NpcArchetypeType.Opportunist => $"“Clock's run. The {want}, or I vanish. Poof.”",
        NpcArchetypeType.Idealist    => $"“The daylight is going. The {want}, or we part here.”",
        NpcArchetypeType.Survivor    => $"“I'm done waiting. The {want}. Now. Or go.”",
        _                            => $"“The {want}, or we're finished.”",
    };

    public static string FinalSatisfied(NpcArchetypeType a) => a switch
    {
        NpcArchetypeType.Merchant    => "He settles back into his chair. “Now. Where were we.”",
        NpcArchetypeType.Commander   => "“Good. Then we continue.”",
        NpcArchetypeType.Scholar     => "“Ah. Then there is time after all.”",
        NpcArchetypeType.Opportunist => "“See? Easy. Okay, I'm listening again.”",
        NpcArchetypeType.Idealist    => "“Thank you. Sit; there is time yet.”",
        NpcArchetypeType.Survivor    => "“…All right. All right. Sit down.”",
        _                            => "They settle back in.",
    };

    public static string OpportunistTwist(string target) =>
        Pick($"“Oh, the {target}? That comes with a finder's cut now. Standard.”",
             $"“Small thing. The {target} has a finder's cut on it. Everybody does it.”");

    public static string WarmReveal(NpcArchetypeType a, string want) => a switch
    {
        NpcArchetypeType.Merchant    => $"He leans in. “Between us, the {want} is the thing I actually want.”",
        NpcArchetypeType.Commander   => $"“I'll say this once, because you've earned it: the {want} is what I need.”",
        NpcArchetypeType.Scholar     => $"“Since we understand each other, it is the {want} I truly want.”",
        NpcArchetypeType.Opportunist => $"“Okay, cards down. The {want}. That's the one.”",
        NpcArchetypeType.Idealist    => $"“You have been kind. So I will be honest: the {want} is what we need most.”",
        NpcArchetypeType.Survivor    => $"“Straight with you, then. The {want}. That's what keeps us alive.”",
        _                            => $"“Between us, the {want} is what I want.”",
    };

    // ── Token reactions ───────────────────────────────────────────────────

    public static string CharmBark(NpcArchetypeType a, bool worked) => (a, worked) switch
    {
        (NpcArchetypeType.Commander, false) => "“Flattery. Get on with it.”",
        (NpcArchetypeType.Scholar, false)   => "“Yes, yes. The substance, please.”",
        (NpcArchetypeType.Merchant, _)      => "“Well. Aren't you pleasant. Doesn't change the numbers. Much.”",
        (NpcArchetypeType.Opportunist, _)   => "“Oh, I like you. Careful, that's expensive.”",
        (NpcArchetypeType.Idealist, _)      => "“You speak kindly. It is noticed.”",
        (NpcArchetypeType.Survivor, _)      => "Something in them unclenches. “…Yeah. Okay.”",
        _                                   => "The mood softens.",
    };

    public static string ArgueBark(NpcArchetypeType a) => a switch
    {
        NpcArchetypeType.Merchant    => "“…I suppose that's so.”",
        NpcArchetypeType.Commander   => "“Hm. That is a fair point.”",
        NpcArchetypeType.Scholar     => "“…Well argued. I concede the point.”",
        NpcArchetypeType.Opportunist => "“Okay, that's annoyingly true.”",
        NpcArchetypeType.Idealist    => "“You are right. I had not seen it so.”",
        NpcArchetypeType.Survivor    => "“…Maybe. Fine.”",
        _                            => "“…I suppose that's so.”",
    };

    public static string StrikeBark(NpcArchetypeType a) => a switch
    {
        NpcArchetypeType.Merchant    => "“Fine. Strike it. Sharp eyes.”",
        NpcArchetypeType.Commander   => "“Struck. It was a formality.”",
        NpcArchetypeType.Scholar     => "“An erratum. Consider it withdrawn.”",
        NpcArchetypeType.Opportunist => "“Worth a try. Struck.”",
        NpcArchetypeType.Idealist    => "“You are right; it was not fair. Struck.”",
        NpcArchetypeType.Survivor    => "“…Fine. Gone.”",
        _                            => "“Struck.”",
    };

    public static string PressBark(NpcArchetypeType a) => a switch
    {
        NpcArchetypeType.Merchant    => "“…As you say. The Combine will remember this.”",
        NpcArchetypeType.Commander   => "“Direct. I can respect that. Don't do it twice.”",
        NpcArchetypeType.Scholar     => "“Barbaric. Take it, then, and be gone soon.”",
        NpcArchetypeType.Opportunist => "“Whoa. Okay. Okay. It's yours. Bad form, though.”",
        NpcArchetypeType.Survivor    => "The crossbow comes up, then slowly down. “…Take it.”",
        _                            => "“…As you say.”",
    };

    public static string IdealistWalkout() =>
        "Threats. In this house. She rises without another word.";

    public static string CollapseLine(NpcArchetypeType a) => a switch
    {
        NpcArchetypeType.Merchant    => "The ledger snaps shut.",
        NpcArchetypeType.Commander   => "Their hand goes to the hilt, and stays there.",
        NpcArchetypeType.Scholar     => "The pen is set down with great precision.",
        NpcArchetypeType.Opportunist => "She is already standing.",
        NpcArchetypeType.Idealist    => "She looks at you with something worse than anger: disappointment.",
        NpcArchetypeType.Survivor    => "The crossbow is levelled, and this time it does not come down.",
        _                            => "The table is over.",
    };

    // ── The squeeze (spec §4e) ────────────────────────────────────────────

    public static string SqueezeOpen(NpcArchetypeType a, CardSchool school, string want) => a switch
    {
        NpcArchetypeType.Merchant    => $"He takes your hand, and holds it. “One more thing, {school}. The {want}. Then we sign.”",
        NpcArchetypeType.Commander   => $"They grip your hand and don't let go. “One condition, {school}. The {want}. Then we're done.”",
        NpcArchetypeType.Scholar     => $"“Before I put my name to this…” The pen hovers. “The {want}. Humour me, {school}.”",
        NpcArchetypeType.Opportunist => $"She shakes warmly, and doesn't let go. “Tiny thing, {school}. The {want}. Everyone does it.”",
        NpcArchetypeType.Idealist    => $"She holds your hand in both of hers. “One more kindness, {school}. The {want}. For the ones who need it.”",
        NpcArchetypeType.Survivor    => $"They pause mid-shake, grip tightening. “The {want}, {school}. Then we're square.”",
        _                            => $"They clasp your hand, and hold it. “One last thing. The {want}.”",
    };

    public static string SqueezeBlink(NpcArchetypeType a) => a switch
    {
        NpcArchetypeType.Merchant    => "A long pause… then he laughs and shakes properly. “Worth the try. As written.”",
        NpcArchetypeType.Commander   => "“…You've nerve. As written, then.”",
        NpcArchetypeType.Scholar     => "“Hm. Yes. Fine. As written.”",
        NpcArchetypeType.Opportunist => "“Ha. Fine. Fine! As written.”",
        NpcArchetypeType.Idealist    => "“…No, you are right to hold. As written.”",
        NpcArchetypeType.Survivor    => "“…Fine. As written.”",
        _                            => "“…Fine. As written.”",
    };

    public static string SqueezeBristle(NpcArchetypeType a) => a switch
    {
        NpcArchetypeType.Merchant    => "His grip tightens. “Then we are not as close as I hoped.”",
        NpcArchetypeType.Commander   => "“Then we're not finished after all.”",
        NpcArchetypeType.Scholar     => "“How disappointing.” The pen goes back in its case.",
        NpcArchetypeType.Opportunist => "“Don't test me twice.”",
        NpcArchetypeType.Idealist    => "She lets go of your hand. “I see.”",
        NpcArchetypeType.Survivor    => "“Wrong answer.”",
        _                            => "“Then we're not finished after all.”",
    };

    // ── The player's spoken verbs (the verb IS the sentence) ──────────────

    public static string PlayerAsk(string clause) => $"You ask for the {clause}.";
    public static string PlayerConcede(string clause) => $"You put the {clause} on the table.";
    public static string PlayerAcceptCounter(string ask, string demand) => $"“Done. The {ask} for the {demand}.”";
    public static string PlayerDeclineCounter() => "“No. Not for that.”";
    public static string PlayerProbe(string clause) => $"You watch them while you mention the {clause}.";
    public static string PlayerArgue(string clause) => $"You argue what the {clause} is really worth.";
    public static string PlayerStrikeRider(string clause) => $"“And the {clause} comes out. It never belonged in this.”";
    public static string PlayerCharm(NpcArchetypeType a) => a switch
    {
        NpcArchetypeType.Merchant    => "“A stall like this, in a port like this… you've done well for yourself.”",
        NpcArchetypeType.Commander   => "“Your people hold this ground well. I've noticed.”",
        NpcArchetypeType.Scholar     => "“I've read your work. It deserved a wider hearing.”",
        NpcArchetypeType.Opportunist => "“I heard you were the one to talk to. Now I see why.”",
        NpcArchetypeType.Idealist    => "“What you do here matters. I mean that.”",
        NpcArchetypeType.Survivor    => "“You kept them alive this far. That's not nothing.”",
        _                            => "You find something true and kind to say.",
    };
    public static string PlayerPress(string clause) => $"You lean in. “The {clause}. Now.”";
    public static string PlayerOffer() => "You set a purse on the table between you.";
    public static string PlayerDemonstrate(CardSchool s) => s switch
    {
        CardSchool.Elementalist => "You open one hand; the candleflames roar and hold, burning cold.",
        CardSchool.Necromancer  => "You speak a name, and for a moment the room is fuller than it was.",
        CardSchool.Tinker       => "A whir, a click: something small and marvelous sits on the felt.",
        CardSchool.Enchanter    => "You murmur three syllables, and the light in the room goes gold.",
        CardSchool.Arcanist     => "The pattern of the room unfolds for them, briefly, in the air.",
        CardSchool.Chronomancer => "The candle burns backward for a breath, and then forward again.",
        CardSchool.Druid        => "Something green and patient enters the room with your breath.",
        _                       => "You show them a little of what you can do.",
    };
    public static string PlayerCallIn() => "You mention a name you both know.";
    public static string PlayerWait() => "You let the silence stretch, unhurried.";

    private static string Cap(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..];

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

    // ── School signature moves ────────────────────────────────────────────

    public static string SchoolMoveLine(CardSchool s) => s switch
    {
        CardSchool.Adept        => "You adapt, the way students of every school learn to, improvising leverage from nothing.",
        CardSchool.Elementalist => "You open one hand. The candleflames roar to the ceiling and hold there, burning cold. The table goes very quiet.",
        CardSchool.Druid        => "You breathe out, and something green and patient enters the room. Shoulders lower. The air sweetens.",
        CardSchool.Necromancer  => "You tilt your head, listening to someone who isn't there. The dead have sat at this table before.",
        CardSchool.Tinker       => "Your hands are already working. A whir, a click, and something small and marvelous sits on the felt.",
        CardSchool.Enchanter    => "You murmur three syllables under your breath, and a memory of theirs goes soft at the edges.",
        CardSchool.Arcanist     => "The pattern of them unfolds before you: every want, every price, indexed and cross-referenced.",
        CardSchool.Chronomancer => "You reach back through the last few seconds and pull. The words unhappen. Only you remember.",
        _                       => "You reach for your school's deeper art.",
    };
}
