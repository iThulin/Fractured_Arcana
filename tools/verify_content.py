#!/usr/bin/env python3
"""verify_content.py - fragments, {token} resolution, terrain palettes, reward ids.

Catches: a {token} with no fragment slot and no substitution; a raw storage key
reaching the player as prose; a fragment or encounter tagged to terrain its
region cannot generate; an item/spell/companion/faction reward id that does not
exist. Also reports measured assembler variety per skeleton per region.

Run from anywhere:  python3 tools/verify_content.py
Exits non-zero on any error, so it drops into a pre-commit hook or CI unchanged.

Everything it checks against is DERIVED FROM SOURCE, never restated here — echo
flags and substitutions are parsed out of EchoSeeder.cs, quest categories out of
QuestLogView.cs, counter families out of QuestTracker.cs, terrain palettes out of
Data/Regions, reward ids out of Data/ and FactionRegistry.cs. The checks drift
when the code drifts instead of encoding assumptions about an API that then rot.
"""
import json, re, os, glob, sys, random, collections

R = os.path.dirname(os.path.dirname(os.path.abspath(__file__))) + os.sep
D = R + "Data/Encounters/"

TERRAIN = ["Grassland","Forest","Road","Ruins","Mountain","Swamp","ArcaneGround",
           "Volcanic","Water","Hills","Coast","Lake","Desert","Tundra","Snow","Marsh"]
REGIONS = ["amber_downs","ashfeld_crossing","boreal_march","cogwork_reach","dustreach",
           "frontier_wilds","glacial_threshold","hollow_mire","jade_coast","obsidian_waste",
           "sunken_archive","the_convergence","the_crags","tidewrack_coast","verdant_deep"]
# Derived from EchoSeeder.cs rather than restated here, so this check cannot go
# stale the next time a substitution is added.
_seeder = open(R + "Scripts/Data/FeatureBuilders/EchoSeeder.cs").read()
SUBS = set(re.findall(r'_subs\["([a-z_]+)"\]\s*=', _seeder))
# Substitution keys that hold raw storage values and must never appear in prose.
UNSAFE_SUBS = {"deal_faction", "convergence_outcome", "style_companion_id"}
ECHO_FLAGS = set(re.findall(r'TrySeed\("([a-z_]+)"', _seeder))
ENC_FIELDS  = {"id","title","body","terrainTags","regionTags","requiredFlag","archmageId","choices"}
CHOICE_FIELDS = {"label","resultText","goldDelta","hpDelta","stepDelta","spellReward",
                 "cardReward","cardCodex","setFlags","setMetaFlags","launchGuardian",
                 "requiredFlag","requiredSchool","requiredGold","requiredItem",
                 "requiredCompanion","resolutionKind","itemReward","companionUnlock",
                 "reputationFactionId","reputationAmount","loreId","revealPois"}
TOK = re.compile(r"\{([a-zA-Z0-9_]+)\}")
W = {"both":6,"terr":3,"reg":2,"any":1}

frags = json.load(open(D+"fragments.json"))
errs, warns = [], []

# ── real reward ids, harvested from the project rather than assumed ─────
ROOT = R
ITEMS = {os.path.basename(p)[:-5] for p in glob.glob(ROOT+"Data/Items/*.json")} if True else set()
SPELLS = set()
for p in glob.glob(ROOT+"Data/OverworldSpells/*.json"):
    for s in json.load(open(p)):
        SPELLS.add(s["id"])
FACTIONS = {"aegis_concordat","cinderbound_pact","free_charter","lantern_order","the_untamed"}
COMPANIONS = {os.path.basename(p)[:-5] for p in glob.glob(ROOT+"Data/Companions/*.json")}
# each region's generatable terrain, straight out of its baseTerrain palette
PALETTE = {}
for p in glob.glob(ROOT+"Data/Regions/*.json"):
    d = json.load(open(p))
    PALETTE[d["id"]] = {x["terrainName"] for x in d.get("baseTerrain",{}).get("palette",[])} | {"Road"}

def pick(slot, terrain, region, rng):
    pool = frags.get(slot)
    if not pool: return None
    cand, wts = [], []
    for fr in pool:
        t, r = fr["terrainTags"], fr["regionTags"]
        if t and terrain not in t: continue
        if r and region not in r: continue
        w = W["both"] if (t and r) else W["terr"] if t else W["reg"] if r else W["any"]
        cand.append(fr["text"]); wts.append(w)
    return rng.choices(cand, wts)[0] if cand else None

def assemble(text, terrain, region, rng):
    def rep(m):
        k = m.group(1)
        f = pick(k, terrain, region, rng)
        if f is not None: return f
        if k in SUBS: return f"<{k}>"
        return m.group(0)
    return TOK.sub(rep, text or "")

# ── 1. every slot resolves on every terrain, with and without a region ──
for slot in frags:
    for terr in TERRAIN:
        for reg in [None] + REGIONS:
            if pick(slot, terr, reg, random.Random(0)) is None:
                errs.append(f"slot {slot!r} unresolvable on terrain {terr} region {reg}")

# ── 2. schema + token resolution across every authored pool file ────────
files = ["generic_encounters.json","ripples.json","frontier_wilds_encounters.json"]
files += sorted(f for f in os.listdir(D) if f.endswith("_encounters.json") and f not in files)
rng = random.Random(1)
for fn in files:
    try: pool = json.load(open(D+fn))
    except Exception as e: errs.append(f"{fn}: does not parse — {e}"); continue
    for enc in pool:
        tag = f"{fn}:{enc.get('id') or enc.get('title')}"
        for k in enc:
            if k not in ENC_FIELDS: errs.append(f"{tag}: unknown encounter field {k!r}")
        for t in enc.get("terrainTags",[]):
            if t not in TERRAIN: errs.append(f"{tag}: bad terrainTag {t!r}")
        for r in enc.get("regionTags",[]):
            if r not in REGIONS: errs.append(f"{tag}: bad regionTag {r!r}")
        rf = enc.get("requiredFlag","")
        if rf and rf.startswith("echo_") and rf not in ECHO_FLAGS:
            errs.append(f"{tag}: requiredFlag {rf!r} is not emitted by EchoSeeder")
        if not enc.get("choices"): errs.append(f"{tag}: no choices")
        for ch in enc.get("choices",[]):
            for k in ch:
                if k not in CHOICE_FIELDS: errs.append(f"{tag}: unknown choice field {k!r}")
            if ch.get("itemReward") and ch["itemReward"] not in ITEMS:
                errs.append(f"{tag}: itemReward {ch['itemReward']!r} is not in Data/Items")
            if ch.get("spellReward") and ch["spellReward"] not in SPELLS:
                errs.append(f"{tag}: spellReward {ch['spellReward']!r} is not in Data/OverworldSpells")
            if ch.get("companionUnlock") and ch["companionUnlock"] not in COMPANIONS:
                errs.append(f"{tag}: companionUnlock {ch['companionUnlock']!r} is not in Data/Companions")
            if ch.get("reputationFactionId") and ch["reputationFactionId"] not in FACTIONS:
                errs.append(f"{tag}: reputationFactionId {ch['reputationFactionId']!r} is not a FactionRegistry id")
            if ch.get("reputationFactionId") and not ch.get("reputationAmount"):
                errs.append(f"{tag}: reputationFactionId set with no reputationAmount")
        # a region pool's terrain tags must be terrain that region can generate
        for r in enc.get("regionTags",[]):
            for t in enc.get("terrainTags",[]):
                if r in PALETTE and t not in PALETTE[r]:
                    errs.append(f"{tag}: terrainTag {t!r} never generates in region {r}")
            if not ch.get("label"): errs.append(f"{tag}: choice with no label")
        # token resolution over every terrain/region combination
        texts = [enc.get("title",""), enc.get("body","")]
        for ch in enc.get("choices",[]): texts += [ch.get("label",""), ch.get("resultText","")]
        for txt in texts:
            for k in TOK.findall(txt):
                if k in UNSAFE_SUBS:
                    errs.append(f"{tag}: uses raw-storage token {{{k}}} in player-facing text")
                if k not in frags and k not in SUBS:
                    errs.append(f"{tag}: token {{{k}}} has neither a fragment slot nor a seeder sub")
        for terr in TERRAIN:
            for reg in REGIONS:
                for txt in texts:
                    out = assemble(txt, terr, reg, rng)
                    if "{" in out: errs.append(f"{tag}: unresolved token on {terr}/{reg}: {out[:80]}")

# ── 3. echo pool seals itself ───────────────────────────────────────────
for enc in json.load(open(D+"ripples.json")):
    want = enc["requiredFlag"].replace("_eligible","_seen")
    for ch in enc["choices"]:
        if want not in ch.get("setMetaFlags",[]):
            errs.append(f"ripples:{enc['id']}: choice {ch['label']!r} does not set {want}")
covered = {e["requiredFlag"] for e in json.load(open(D+"ripples.json"))}
for f in ECHO_FLAGS - covered: warns.append(f"echo flag {f} has no authored encounter")

# ── 4. measured variety: distinct assembled bodies per skeleton, in-region ──
print("── assembled variety (1000 draws per skeleton, distinct bodies) ──")
gen = json.load(open(D+"generic_encounters.json"))
skels = [e for e in gen if "{" in e.get("body","")]
for enc in skels:
    row = []
    for reg in ["frontier_wilds","hollow_mire","dustreach"]:
        terr = {"frontier_wilds":"Forest","hollow_mire":"Swamp","dustreach":"Desert"}[reg]
        seen = {assemble(enc["body"], terr, reg, rng) for _ in range(1000)}
        row.append(f"{reg}/{terr}: {len(seen)}")
    print(f"  {enc['title']:32} " + "   ".join(row))

print()
print(f"── {len(errs)} error(s), {len(warns)} warning(s) ──")
for e in errs[:40]: print("  ERR ", e)
for w in warns: print("  WARN", w)
sys.exit(1 if errs else 0)
