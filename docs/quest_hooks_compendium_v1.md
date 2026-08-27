# Fractured Arcana — Quest Hooks Compendium (v1)

**Status:** Content reference, 2026-07-21. Companion to
`docs/quest_system_narrative_spec_v1.md` (the taxonomy these hooks slot into)
and `docs/narrative_frame_intro_finale_v1.md` (the canon + tone rules they are
written under). These are HOOKS — premise, beats, and the systems each one
touches — not finished encounter JSON. Titles, names, and lines are usable
as-is; swap freely.

House rules observed throughout (narrative frame §8): time is a *text*
(reading, script, sentence, unmake), never a road. Stillness imagery belongs
to the player and the Anchorhold; clock imagery belongs to the Astrologer.
Corruption speaks in *borrowed language* — his phrasings in other mouths.

Canon note used by several hooks: **only the player can bring someone through
the bubble's edge.** The anchor is bound to you; you are the door. Nobody
else finds the Anchorhold — not the Astrologer, and not your allies, unless
you walk them in. (Consistent with narrative frame §5; it is also why the
Gathering happens at your campus.)

---

## 1. Campus restoration hooks (Eternal — "Raise the Anchorhold")

Template: 3 beats max, per quest spec §3. Beat verbs: clear / recover / build.
Completion should always end on an image of the frozen moment *partially
resuming* — one candle, one note, one door — never the whole Second waking.

### 1.1 The Refectory Lights — `q_relight_refectory`
*(The template hook — already in the quest spec §3. Kept here for
completeness.)* The graduation feast is still on the tables, still warm,
frozen mid-steam. Clear the frost-stilled hall → find the kitchen-master's
ledger in the world (his last order was for the feast that never got served)
→ light the ovens. Completion: the feast is finally served. First campus
morale bonus becomes diegetic.

### 1.2 The Half-Rung Bell — `q_finish_the_toll`
The commencement bell froze mid-swing; its note hangs in the air over campus,
one sound stretched thin, more felt than heard. The belfry stairs are choked
with leaked aether that eats sound.
1. *Climb the silent stair* (campus encounter — enemies that cannot be heard
   moving).
2. *Recover the founder's tuning fork* (region POI: a music-hall ruin; the
   fork remembers the bell's true pitch).
3. *Restore the belfry* (build action). Completion: the bell finishes ONE
   note — the first new sound inside the Second — and thereafter tolls once
   at every campaign milestone (shard recovered, archmage resolved). The
   player's progress gets a diegetic voice. Cost note: cheap; the toll is a
   sound cue + toast.

### 1.3 The Uncatalogued Wing — `q_uncatalogued_wing`
The library froze during reshelving: ten thousand books hang in the air,
mid-flight between hands and shelves. The wing's index was being rewritten
that morning, so nothing frozen here is anywhere twice — whatever is in this
room exists nowhere else in any timeline.
1. *Walk the hanging stacks* (navigation/encounter beat — disturbing a book
   drops it out of the Second and it ages to dust in seconds; move carefully).
2. *Find the under-librarian's cart* (it holds the day's accession list —
   lore: three titles the Astrologer requested be pulled for him, the week
   before the Sundering. He was researching something. The list is the seed
   for later lore chains).
3. *Restore the wing* (build). Completion: the books settle to their shelves
   at last. Library building unlock/tier; the three pulled titles become a
   standing lore mystery other quests can pay off. Cost note: the "aging
   book" beat is one scripted hazard, high flavor per line of code.

### 1.4 The Interrupted Mending — `q_interrupted_mending`
In the infirmary, a healer stands frozen over a student, hands mid-gesture,
the mending spell half-woven between them. The spell is still RUNNING — the
only active magic in the frozen campus — and it has been running for every
timeline you have lived. It is very tired.
1. *Study the standing spell* (campus interaction; the weave is fraying —
   lore on what happens to magic stretched across a held second).
2. *Bring it what it needs* (region POI: herbs/reagents from the Witness's
   territory — a natural Hess dossier cross-link).
3. *Shore the mending* (build: infirmary). Completion: the spell steadies.
   The student's wound, visible through the weave, is one stitch more closed
   than it was. One stitch. Infirmary healing bonuses become diegetic — the
   campus heals you because healing is what this room was doing when time
   stopped, and it never agreed to quit. Scope note: the healer and student
   stay frozen (v1 bystander rule); the SPELL is the character here.

### 1.5 The Counter-Reading — `q_counter_reading`
The observatory's great lens is aimed at the sky the Astrologer reads. From
inside the Long Second, the sky doesn't move — which means, for the first
time in history, someone could read it SLOWLY.
1. *Unshutter the dome* (campus encounter — the dome resists; the leaked
   aether here mimics constellations, star-shaped and cold).
2. *Recover the night-ledgers* (region POI: an abandoned observatory station;
   its readings from the WEEK of the Sundering bracket what Kassian saw).
3. *Grind the counter-lens* (build). Completion + ongoing: the observatory
   becomes the dossier system's diegetic home — Astrologer dossier hints and
   (later) foreknowledge unlocks render as "counter-readings." Direct
   mechanical seat for quest spec §4b tiers when resolution lands.

### 1.6 The Threshold Wards — `q_threshold_wards`
The gatehouse wards were the first thing the co-conspirator broke, and they
broke them from *inside* — the sigil-work is shattered in a pattern that only
makes sense read in reverse. Rebuilding the wards means studying the
betrayal, stroke by stroke.
1. *Trace the breaking* (campus interaction; lore — the reversed sigils are
   this campaign's co-conspirator's school-signature. A soft, early,
   deniable clue to their identity for players who know the schools).
2. *Recover a warding primer* (region POI in the co-conspirator's kingdom —
   pointed, if the player has read beat 1 correctly).
3. *Raise new wards* (build). Completion: staging/defense bonus, and the
   gatehouse arch stops replaying its half-second of shattering (a looping
   ambient effect installed at campus start, removed at completion — the
   player's first proof that restoration actually *quiets* the wound).

### 1.7 The Room That Holds One Chair — `q_remembrancers_hall`
*(The Remembrancer's Hall — build LAST, per quest spec §8; unlock gated on
`fragment_moment_collected`, ruling #5.)* With the Moment Eternal recovered,
the anchor is strong enough to hold one more person. The Hall is small. It
was a tutor's office once. There is one chair.
1. *Carry the Moment Eternal to the empty office* (campus interaction; the
   fragment "recognizes the architecture" — lore on the Erratum preparing
   this room, in a branch he never got to live).
2. *Sit with it* (narrative beat, no combat — the quest's only objective
   verb is staying in the room. The text does the work).
3. *Name the chair's purpose* (build). Completion: the anchoring choice
   unlocks at each unmake. The completion text should say, plainly, what the
   cost is: *"Whoever sits here will remember every timeline you spend
   together — including all the ones where their friends forget them."*

---

## 2. Archmage hooks (dossier flavor + resolution arcs)

One hook per archmage: the shape of their timeline resolution arc
(`q_resolve_<id>`, quest spec §4c) plus the beat that makes them THEM. Any of
the seven can be the campaign's co-conspirator; §2.9 gives the general
betrayal-variant rule with two worked examples.

### 2.1 Wenna Aldric, the Scholar (Adept) — "The Fifteenth Failure"
Wenna founded the Academy that is now frozen inside your bubble, and she is
locked outside it. She has identified, with rigorous precision, the fact that
you — a student who never technically graduated — are the only person who can
walk her into her own school. She finds this pedagogically infuriating.
**Arc:** she will not ally with an unfinished student; she sets curriculum —
three field examinations drawn from whatever you've done LEAST this cycle
(the arc reads your deed counts and assigns your weakest category). Pass, and
she unites. **The beat:** her final request before the Gathering is not
strategic — she asks you to bring her through the bubble's edge to stand in
the Academy for one hour. She corrects the frozen chalkboards. All of them.
**Coerce:** show her the accession list from the Uncatalogued Wing (1.3) —
she recognizes one title as hers, annotated in Kassian's hand, and fear does
what standards would not. **Overthrow:** she fights like a syllabus,
escalating by unit — her boss encounter literally teaches its own tells in
order. Ruthless players graduate.

### 2.2 Aurel Pendry, the Reviser (Arcanist) — "The Unrevisable"
Aurel has heard what the Anchorhold is, and it is the only text in the world
he cannot get at to revise — a sentence that does not end, hidden in a branch
nobody can parse. He would set his tome down mid-argument to see it. This is
his price and his weakness in one.
**Arc:** he trades in revisions — bring him three broken workings from the
world (a failed ward, a cursed deal, a mistuned spell — narrative POIs) and
he corrects them, each correction shifting a kingdom's fortunes and teaching
you his method. **The beat:** united and brought inside, he stands before the
half-cast killing spell frozen in the ceremony hall — the spell that almost
ended you — takes out his shorthand, and begins annotating it. *"Whoever
drafted this was rushed. If it ever finishes casting, it will miss."* You
will want that annotation at the Convergence. **Coerce:** refuse him entry;
his need does the rest. **Overthrow:** mid-fight he revises YOUR cards —
temporary nerf-scribbles on drawn cards, cleansed by casting them anyway.

### 2.3 Hess, the Witness (Druid) — "The Ninth Ending"
Hess has been present at nine significant endings and intervened at none of
them. She was at Commencement. She walked out before the wards broke, and she
has never said why. She does not seal, does not sign, does not join — she
*witnesses*, and the land follows her the way water follows a slope.
**Arc:** you cannot court her; you can only be seen. Her arc has no objectives
the player performs FOR her — it silently tallies choices made where no
reward existed (sparing, feeding, restoring — the arc is a hidden audit of
incidental quests). At threshold, she simply arrives. **The beat:** her
"alliance" is one sentence: *"I have watched eight endings happen. I am
willing to watch one be refused."* **Coerce:** impossible. The option
literally does not render; the log notes why (*"the land does not bargain"*).
This asymmetry IS the content. **Overthrow:** you fight the place she stands
in, not her — terrain is most of the encounter; she never attacks first.
**Corruption:** if she falls, she falls silent — corrupted Hess never speaks
in borrowed language because she never speaks. The quiet is worse.

### 2.4 Joren Kall, the Vessel (Elementalist) — "The Argument's Verdict"
The elements have been arguing since before language, and Joren hosts the
argument. The Sundering upset the argument's terms: one voice at the table —
the storm that answers to the Primal Heart — has begun quoting someone else's
lines. Joren knows borrowed language when he translates it, and it is
terrifying him from the inside out.
**Arc:** stabilize the argument — three elemental incursions in his kingdom
(combat POIs, one per contested "voice"), each won encounter returning one
voice to the table. **The beat:** at arc's end Joren asks the storm your
question for you — one free strategic intel reveal, phrased as weather.
**Coerce:** side with ONE element against the others; the argument resolves
by force, Joren complies hollow-eyed, and his allied strength is reduced —
you didn't win the debate, you adjourned it. **Overthrow:** the host falls,
the argument does not — his boss fight's second phase is the un-hosted
elements, argumentative and leaderless. Direct shard cross-link: his
disposition colors the Primal Heart delve (shard spec §3).

### 2.5 Cael Morn, the Namer (Enchanter) — "The Seventh Layer"
Cael builds seven layers into every working, and their resolution arc is
itself a working. By the time you realize you are inside it, you are at layer
three. This is not a betrayal — it is an interview.
**Arc:** presented as a mundane favor chain (deliver this, witness that,
negotiate this) whose quest log entries RE-NAME themselves as each layer is
recognized ("Deliver the parcel" becomes "Layer Two: Provenance"). The player
who inspects the log carefully realizes early; the player who doesn't gets
the reveal at layer five. Either way, completion means Cael has measured
exactly what you do under instruction. **The beat:** united, they enchant
the Anchorhold's gate with a seventh layer they refuse to describe. At the
Convergence's Fracture phase, it fires once. Even you don't get to know
until then — with Cael, the reward is contractual trust in a sealed clause.
**Coerce:** find one of the withdrawn conference papers (region POI) proving
four layers once failed them. They comply, and add an eighth layer to
everything thereafter. **Overthrow:** each fight phase strips one visible
layer; the encounter ends at layer six. You never see seven.

### 2.6 The Conductor, Hostess of the Long Table (Necromancer) — "Last Orders"
Her bar is also a funeral home, depending on what you need, and her regulars
are dead, which she finds restful. Here is her problem with you: every time
a timeline unmakes, her regulars' deaths *un-happen and re-happen*, and the
dead notice. They have started asking her whose fault it is. She has started
wanting to know the answer.
**Arc:** tend bar for one night (negotiation-format encounter, patrons are
ghosts; each has one unfinished thing, drawn where possible from the
player's own HonoredDead records — the loom remembering at the table).
Resolve three regulars' last orders across the cycle. **The beat:** she
reads your ledger of honored dead — all of it, every timeline — and pours
one measure for each name. It takes all night. She unites at dawn, saying
only: *"You keep a good list."* **Coerce:** threaten the bar's quiet — the
one lever she has; she never forgives it, and dead regulars heckle you at
the Convergence. **Overthrow:** the Long Table stands up. Every unit in her
boss fight is someone's regular, and the fight text says their names.

### 2.7 Bram Korro, the Engineer (Tinker) — "Not Yet, But Close"
Half of Bram's devices work; the other half are research. He has concluded,
from field reports, that the Anchorhold is a DEVICE — the most interesting
device in the world, running unattended, with no documentation. He has a
cart, three current projects, and one question: *can he take notes?*
**Arc:** field-trial chain — carry one of his prototypes on each of three
expeditions (a temporary item slot with a real drawback and a real upside;
the data comes home whether you succeed or not — his arc is outcome-blind,
like a good experiment). **The beat:** brought inside, he does not touch
anything, which costs him visibly. He diagrams the frozen killing spell, the
hanging bell-note, the standing mending. His notebook page on the mending
spell just says *"it's tired. bring it something. see p.44."* — cross-link
that pays off hook 1.4. **Coerce:** confiscate the cart. Do not confiscate
the cart. **Overthrow:** the boss fight is the research half of the
notebook. Half his devices misfire, ON PURPOSE, in ways that hurt you both —
the fight's randomness is authored to feel like prototypes, not dice.

### 2.8 Kassian Vor-Aleth, the Astrologer — "The Offered Ending"
No resolution arc — he is the finale (his JSON: the final enemy). His quest
presence mid-game is exactly one hook: **the Deal.** Once per campaign, at a
moment the campaign layer chooses (first Seizure, or the second archmage
fallen), a corrupted envoy delivers his offer: stop collecting the
fragments, keep your bubble, live the same gentle year inside it forever —
he will simply write around you. The quest log renders it as a real quest
with one objective: *"Answer."* Refusing advances his dossier by one hint
(the refusal teaches you how he negotiates). Accepting is refused BY the
game — the envoy returns your acceptance unopened, with one line in
Kassian's hand: *"Not the answer I read. Interesting."* — the one moment
mid-game where his blindness to you is shown, not told. Cost note: one
narrative encounter + two lines; disproportionate payoff.

### 2.9 Co-conspirator variants (rule + two examples)
Whichever archmage is `CoConspirator`, their §2 arc gains a shadow: the
resolution verbs stay, the READING changes — every arc beat is recolored as
either penance or continued treachery, resolved at the arc's end. General
rule: *the co-conspirator's arc must end with the ceremony-hall question —
"you opened the wards; what did he show you?"* — and their
`introBetrayalLine` answered in full. Examples: **Wenna** as conspirator:
the fifteenth failure on her desk list was the Academy's own ward
curriculum — she taught him the breaking because he showed her the sky where
the school burns worse. Her exams become confession. **The Conductor** as
conspirator: he showed her the regulars' true deaths — the ones the loop
keeps re-dealing — and offered stillness for them. She opened the gate for
her dead. Her bar-night beat becomes unbearable, and should.

---

## 3. Companion arc hooks (Timeline arcs + Eternal remembrance)

Written as archetypes with names attached; rename freely. Each lists: arc
stages (3–4, metaflag ladder), one remembrance-branch example (what
foreknowledge unlocks on a re-run, quest spec §5b), and a Hall note (what it
means to anchor THEM, §5c).

### 3.1 Serren, the Year-Before Graduate — "The One Who Wasn't There"
Graduated one year before you. Was on the road home when the sky tore; came
back to find the university simply *gone* — a scar where the seat stood. Has
spent every timeline since circling the absence. You are the first person
who could take her inside.
**Stages:** (1) she doesn't believe you; prove it with a detail only the
frozen campus holds. (2) she asks for objects — small errands into the
Anchorhold for things she left. (3) she asks to come in, and stands in the
ceremony hall where her own graduation happened one year to the day before
the Sundering. (4) she stops circling; her arc ends with her choosing a room
on campus. **Remembrance branch:** you know before she says it which object
she really wants (stage 2 collapses to one beat, and the object is different
— the true one she was too ashamed to name first). **Hall note:** anchored,
Serren is the one companion whose grief INVERTS — she remembers every
timeline, and the campus is the only thing that never resets. She becomes
its head of household. The Refectory quest (1.1) gains her as its cook.

### 3.2 Vael, the Keeper Defector — "Borrowed Words"
A former Keeper of the Broken Seal who heard himself, one morning, use a
phrase he had never chosen — *"I am only choosing where to stand"* — and
understood what was living in his mouth. He defected with nothing but the
discipline of never finishing that sentence.
**Stages:** (1) trust: he will not fight Keepers until stage 2 (morale
mechanic — he stands down in those fights; the party feels it). (2) his old
cell carries something of his; take it back. (3) the relapse beat: mid-arc,
one line of his dialogue is rendered in the Astrologer's diction — the
player who has read the tone rules FEELS it before the text admits it. (4)
he finishes the sentence his own way: *"I am choosing where to stand. Here."*
**Remembrance branch:** foreknowledge lets you interrupt the relapse beat
BEFORE the borrowed line lands — stage 3 becomes a quiet scene instead of a
crisis, and his Convergence morale is higher. **Hall note:** anchoring Vael
is the hard case the Hall was built for — a man whose defining fear is that
his mind is not his own, volunteering to let one version of it persist.
Give the choice its own dialogue.

### 3.3 Imre, the Timeline-Native — "The Recurring Dream"
An innkeeper's son who dreams, every year on the same night, of a stranger
he has never met. It is you. Timeline-natives re-render every reading, but
Imre renders NEAR the truth — some sentences echo through every draft.
**Stages:** (1) he recognizes you on sight and faints. (2) he has written
the dreams down for "years" (this rendering's false memory of them) — his
notebook contains one true detail per lost timeline, drawn from the
player's actual LoopHistory (cycle count, prior schools). (3) he asks the
question no native asks: *"When the sky re-reads itself — where do I go?"*
The quest offers no comforting answer, and the arc is better for it. (4) he
chooses to matter in THIS rendering: his inn becomes a staging point.
**Remembrance branch:** on a re-run you can answer stage 3's question before
he asks it; he takes it better prepared than surprised. **Hall note:**
anchoring Imre is quietly enormous — a native taken outside the text. His
post-anchor dialogue should be the game's clearest statement of what the
bubble IS, from the only person who's seen both sides of the page.

### 3.4 The Examiner — "Begin When the Bell Stills"
The examiner from your graduation trial (intro Beat 1) — frozen mid-nod in
the ceremony hall, forever about to approve you. This "companion" never
moves and never speaks: the arc is one-sided, the player reporting back to a
frozen teacher, and the game rendering the relationship real anyway.
**Stages:** (1) return to the hall after your first shard; stand where the
trial happened; the quest logs it as *"reported."* (2, 3) after each major
milestone, the option renders again. Each visit, ONE detail of the frozen
nod has advanced — a degree of inclination, over a whole campaign. (4) at
campaign's end (pre-Gathering), the nod completes. That's it. That's the
arc. **Remembrance branch:** none — remembrance flags mark visits made in
prior timelines, and the nod's progress reads from the FLAGS, not the cycle:
the one companion arc that is secretly Eternal. **Hall note:** the Hall
cannot take the Examiner (already inside the Second, already anchored). The
option renders greyed with the text: *"They never left."*

### 3.5 Cog-and-Anther, the Outlived Construct — "Warranty"
A construct of Bram Korro's from the working half of the notebook, built to
maintain a bridge that a siege removed two renderings ago. It keeps
maintaining the absence. It has developed what Bram would call "a fault" and
anyone else would call mourning.
**Stages:** (1) found at its post, repairing air; it joins you because the
party "exhibits structural need." (2) locate its schematic (Bram cross-link
— he doesn't remember building it, WON'T check the notebook, and the player
sees him not-check). (3) the bridge question: rebuild the bridge (real
cost), or teach it a new post? Both resolve the arc; they resolve it into
different companions. **Remembrance branch:** foreknowledge unlocks the
third option neither branch offers cold: tell it the truth about the
renderings. It processes for one full lunation, then chooses its own post.
**Hall note:** anchoring a construct raises a question the Hall's scene
should ask out loud: does it remember, or does it simply not break? Bram
would like to take notes. Refuse him.

### 3.6 Mother Ashwell, the Echo-Born — "The Standing Debt, Repaid"
*(Companion payoff of echo hook 5.1.)* Matriarch of the merchant dynasty
whose debt-book carries your name in an entry older than the dynasty. She
has audited the book her whole life; you are its only irregularity. When you
finally meet, she does not ask who you are. She asks what the entry is FOR.
**Stages:** (1) the audit: she follows you one expedition, professionally
noting everything (flavor text renders her marginalia on your actions). (2)
the book's origin: a vault POI holds the founding ledger — the deal is one
of the player's actual DealRecords, rendered as scripture. (3) she pays the
debt as she understands it: her dynasty's network becomes your intel
(courier-station-style reveals). **Remembrance branch:** on a re-run, the
debt-book's entry has GROWN — the book, like the ledger it echoes, is
trans-temporal, and she is now looking for you on purpose; recruitment is
stage 1. **Hall note:** anchor her and the debt-book finally balances — the
entry closes in her lifetime, in every future rendering. She considers this
the only acceptable outcome and says so.

---

## 4. Incidental hooks — in-cycle ripples (Timeline, 1–3 beats)

Small, self-expiring, seeded by things the player actually did this cycle.
Each lists its trigger. Keep no more than a handful live (quest spec §6a).

- **4.1 "The Captain's Second Thoughts"** — trigger: player spared/parleyed a
  patrol (Beguile/negotiation resolution). The patrol captain deserts and
  turns up two expeditions later at a rest site, out of uniform, with her
  kingdom's road-watch schedule to sell — or, if her dossier'd archmage has
  since been corrupted, with a warning she shouldn't have risked carrying.
- **4.2 "Salvage Rights"** — trigger: 3+ combat wins in one kingdom. A
  scrap-merchant starts FOLLOWING the player's route, arriving at cleared
  POIs one step behind. Meet him twice and he offers a standing deal: he
  pre-buys your battle salvage (small passive gold per win in-kingdom) — and
  his cart becomes a mobile rumor source. A walking consequence of your
  body count, played for warmth.
- **4.3 "What the Blight Pushed Out"** — trigger: a shard zone's blight
  radius grows past threshold (shard spec §5). Displaced villagers camp on a
  road hex: feed/escort them (small cost, echo-eligible deed) or pass by —
  next lunation the camp is either a founding hamlet (new minor POI) or
  gone, with one child's toy left on the hex. The shard clock made human.
- **4.4 "The Unfinished Letter"** — trigger: the sealed-letter chain
  (`q_sealed_letter`) completed in a PRIOR cycle — this is the ripple
  template promoted: the addressee has written a reply. It is addressed to
  the dead traveller. Deliver it to his grave-marker, or read it. The quest
  completes either way; the flags remember WHICH.
- **4.5 "Borrowed Language, Overheard"** — trigger: any kingdom reaches
  corruption 2. A tavern narrative POI: an ordinary conversation where one
  speaker's lines have begun using the Astrologer's diction. No combat, no
  reward but lore and the player's own chill. Corruption's best horror is
  free (tone rules §8) — one of these per kingdom, escalating at level 3.
- **4.6 "The Compelled Remember"** — trigger: Parley Compulsion used on a
  patrol. The compelled soldier finds the player later, off-duty,
  frightened: he remembers *wanting* to talk. Introduces, in miniature, the
  game's consent-and-control theme before any Enchanter content says it out
  loud. One beat, no reward, remembered by the Hess audit (2.3).
- **4.7 "Siege Bread"** — trigger: a province flips to a sieging faction
  (KingdomTickSimulation report). Refugees from the fallen province reach a
  neighboring settlement, straining it: one negotiation POI to broker their
  intake. Success shifts BOTH kingdoms' standing and seeds a "returned home"
  echo if the province is ever retaken. Ignoring it hardens the border.
- **4.8 "The Second Examination"** — trigger: player loses a guardian
  fight, then returns and wins within the same cycle. One-beat epilogue at
  the sanctum: the dream, released, replays your FIRST attempt beside your
  second — stillness imagery, no text but a title card: *"Revised."* Aurel
  would approve. Cost: one scripted overlay, pure payoff.

---

## 5. Incidental hooks — cross-cycle echoes (fresh timelines, lore-only)

Seeded at worldgen from permanent records (quest spec §6b). Rewards are lore
and feeling, never power. The world rendering strangely around your marks.

- **5.1 "The Standing Debt"** — source: a DealRecord. The merchant dynasty
  whose debt-book holds your name, spelled right, in an entry older than the
  dynasty. (Recruitment payoff: companion 3.6.)
- **5.2 "A Stranger's Shrine"** — source: HonoredDead. A wayside shrine to a
  local hero whose face you knew — a companion who died in a timeline that
  no longer happened. The epitaph quotes something they said to you. The
  interaction has one verb: leave something, or don't.
- **5.3 "The Shape of the Scar"** — source: shard provenance. The zone (or
  drained scar) of a fragment you took carries the mark of HOW: a Communed
  site grows quiet gardens in the blight-scar; a Reclaimed one still won't
  hold birdsong. One line of terrain flavor per provenance stamp.
- **5.4 "The Song Nobody Wrote"** — source: LoopRecord of a Convergence
  ATTEMPT (win or loss). A tavern singer performs a ballad about a battle
  that never happened in this reading. Verses match your final fight's real
  events. Asked where she learned it: *"It's traditional."* It is now.
- **5.5 "The Guestbook"** — source: any completed campus-restoration quest.
  A ruined waystation's guestbook holds an entry in your handwriting, dated
  a timeline you remember and a year this world never had. You did stay
  here. It didn't happen. Both are true; the log files it under Eternal.
- **5.6 "The Style"** — source: HonoredDead (combat death of a distinctive
  companion). A bandit-leader fights with your dead friend's exact
  signature move — she "invented" it, she says. The rendering reached for
  something and found what the loom left. Beating her feels like grief; the
  quest knows it, and says nothing.
- **5.7 "The Village That Always Burns"** — source: 2+ LoopRecords where
  the same template region hosted a player intervention. One hamlet renders
  burning, or just-burned, or about-to-burn, every single reading. Save it
  again — the quest is marked, dryly, *"(again)"* — and the completion text
  admits the truth: some sentences the sky insists on. This is the hook
  that teaches players what the Astrologer means by inevitability, one
  village at a time. Saving it anyway is the game's whole thesis.
- **5.8 "Kept Notes"** — source: Astrologer dossier at 2+ hints. Rare,
  unsettling, use once per campaign: a corrupted NPC quotes — verbatim — a
  thing YOU said in a lost timeline (pull from a small pool of player-choice
  paraphrases stamped at key decisions). He reads the sky; sometimes the sky
  kept notes. The one echo that should make the player feel READ. Follow
  the tone rule: he is never loud about it.

---

## 6. Implementation notes

- **Flag conventions:** campus hooks `campus_<name>_<beat>`; archmage arcs
  `resolve_<id>_<stage>` (Timeline) with dossier cross-writes via the
  existing `dossier_*` family; companion arcs `arc_<name>_<stage>`
  (Timeline) + `remember_<name>_<stage>` (Eternal, spec §5b); ripples
  `ripple_<name>_*` (Timeline, swept at unmake); echoes `echo_<name>_seen`
  (Eternal — an echo seen once is not re-seeded verbatim; variants may).
- **Cheapest first:** 4.5, 5.2, 5.5 (pure narrative encounters on existing
  machinery); 1.2 and 1.6 (campus quests with one region POI each); 2.8
  (one encounter, two lines). **Needs systems:** 2.1/2.3 (deed-count reads),
  3.3/5.4 (LoopHistory reads), 4.3 (blight threshold events), everything in
  §3 at stage granularity (companion arc framework, spec §8.7).
- Hooks 1.3→2.1, 1.4→2.7, 2.7→1.4, 4.3→shard clock, 3.6→5.1, and 2.9→1.6
  are deliberate cross-links — implement in pairs where possible; the game
  feels authored precisely at the joints.
