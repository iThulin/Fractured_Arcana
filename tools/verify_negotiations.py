#!/usr/bin/env python3
"""
verify_negotiations.py - negotiation table wiring (v3, the Ledger).

Run from anywhere:  python3 tools/verify_negotiations.py
Exits non-zero on any error.

Everything it checks against is DERIVED FROM SOURCE: the archetype enum,
ClauseSide/ClauseKind and the Clause / NegotiationEncounterData field lists
from NpcArchetype.cs, the region-suffix and generic-pool lists from
NegotiationEncounterLoader.cs, region ids and negotiationPOICount from
Data/Regions, spell ids from Data/OverworldSpells. The par solver mirrors
NegotiationState.ComputePar.

The two failure modes this exists for are both silent:
  * A file whose name is not {regionId}_{suffix} for a suffix in the loader's
    RegionSuffixes list, and is not in GenericPool, is NEVER LOADED.
  * An enum value outside the C# enum throws inside the converter, the loader
    catches the exception and returns null, and the file is dead.
"""
import json, re, os, glob, sys

R = os.path.dirname(os.path.dirname(os.path.abspath(__file__))) + os.sep
D = R + "Data/Negotiations/"
errs, warns = [], []

arch_src = open(R + "Scripts/Systems/Negotiation/NpcArchetype.cs").read()
def _read(rel):
    try: return open(R + rel).read()
    except OSError: return ""
enum_src = arch_src + _read("Scripts/Systems/Negotiation/ParleyDeck.cs") + _read("Scripts/Cards/CardData.cs")
def enum_values(name):
    m = re.search(r'enum ' + name + r'\s*\{([^}]*)\}', enum_src)
    if m is None: return None
    vals = set()
    body = re.sub(r'//[^\n]*', '', m.group(1))   # comments first: they may hold commas
    for part in body.split(","):
        part = part.strip()
        if part: vals.add(part.split("=")[0].strip())
    return vals
ARCHETYPES = enum_values("NpcArchetypeType")
SIDES = {v.lower() for v in enum_values("ClauseSide")}
KINDS = {v.lower() for v in enum_values("ClauseKind")}

def fields_of(class_name, stop_at):
    body = arch_src.split(f"class {class_name}")[1].split(stop_at)[0]
    names = set(re.findall(r'public (?:string|int|bool\??|float|List<string>|List<Clause>|List<LegacyDealTerm>|NpcArchetypeType|ClauseSide|ClauseKind) (\w+)', body))
    names |= set(re.findall(r'JsonPropertyName\("(\w+)"\)', body))
    return names
CLAUSE_FIELDS = fields_of("Clause", "public bool IsOpen")
ENC_FIELDS = fields_of("NegotiationEncounterData", "public CounterStyle ResolvedCounterStyle")
ENC_FIELDS.discard("CounterStyleId")   # JSON key is counterStyle (JsonPropertyName)

load_src = open(R + "Scripts/Systems/Negotiation/NegotiationEncounterLoader.cs").read()
SUFFIXES = set(re.search(r'RegionSuffixes\s*=\s*\{([^}]*)\}', load_src).group(1).replace('"', '').replace(" ", "").replace("\n", "").split(","))
SUFFIXES.discard("")
GENERIC = set(re.search(r'GenericPool\s*=\s*\{(.*?)\};', load_src, re.S).group(1).replace('"', '').replace(" ", "").replace("\n", "").split(","))
GENERIC.discard("")

_esc = re.search(r'EscalatesToCombat \?\? \(Archetype == NpcArchetypeType\.(\w+)\)', arch_src)
DEFAULT_ESCALATES = {_esc.group(1)} if _esc else set()

# Archetype tables, parsed from ArchetypeBehavior for the par solver.
def parse_row_table(fn_name):
    body = arch_src.split(f"public static int {fn_name}")[1].split("return")[0]
    rows = {}
    for m in re.finditer(r'NpcArchetypeType\.(\w+)\s*=>\s*new\[\]\s*\{([^}]*)\}', body):
        rows[m.group(1)] = [int(x) for x in m.group(2).split(",")]
    return rows
DEF_NPC = parse_row_table("DefaultNpcValue")
KIND_ORDER = [k for k in ["gold","goods","lore","service","passage","honor","favor","obligation","spell"]]
ARCH_GW = {}
for m in re.finditer(r'NpcArchetypeType\.(\w+)\s*=>\s*([+-]?\d+),', arch_src.split("StartGoodwillMod")[1].split("};")[0]):
    ARCH_GW[m.group(1)] = int(m.group(2))
BASE_GW = int(re.search(r'BaseGoodwill = (\d+)', open(R + "Scripts/Systems/Negotiation/NegotiationTuning.cs").read()).group(1))

BATTLE = {}
for _p in glob.glob(R + "Data/Regions/*.json"):
    _d = json.load(open(_p))
    BATTLE[_d["id"]] = len(_d.get("encounterPools", {}).get("battle", []))

REGIONS, POI = {}, {}
for p in glob.glob(R + "Data/Regions/*.json"):
    d = json.load(open(p)); REGIONS[d["id"]] = d; POI[d["id"]] = d.get("negotiationPOICount", 0)
SPELLS = {s["id"] for p in glob.glob(R + "Data/OverworldSpells/*.json") for s in json.load(open(p))}

lower_enc = {f.lower() for f in ENC_FIELDS}
lower_clause = {f.lower() for f in CLAUSE_FIELDS}

def npc_value(arch, c):
    v = c.get("npcValue", -1)
    if v is None or v < 0:
        return DEF_NPC[arch][KIND_ORDER.index(c["kind"].lower())]
    return v

def par(clauses, goodwill, arch):
    th = [c for c in clauses if c["side"].lower() == "theirs" and not c.get("requiresWarm")]
    yo = [c for c in clauses if c["side"].lower() == "yours"]
    best = 0
    for tm in range(1 << len(th)):
        taken = [th[i] for i in range(len(th)) if tm >> i & 1]
        forced = {c["rider"] for c in taken if c.get("rider")}
        for ym in range(1 << len(yo)):
            given = [yo[i] for i in range(len(yo)) if ym >> i & 1]
            if not forced <= {c["id"] for c in given}: continue
            if goodwill + sum(npc_value(arch, c) for c in given) - sum(npc_value(arch, c) for c in taken) < 0: continue
            best = max(best, sum(c["value"] for c in taken) - sum(c["value"] for c in given))
    naive = sum(c["value"] for c in th) - sum(c["value"] for c in yo)
    return best, naive

covered = {}
ESCALATING = []
BALANCE = []
TABLE_FILES = [p for p in sorted(glob.glob(D + "*.json")) if os.path.basename(p) != "parley_cards.json"]
for p in TABLE_FILES:
    fn = os.path.basename(p)[:-5]
    try:
        t = json.load(open(p))
    except Exception as e:
        errs.append(f"{fn}: does not parse - {e}"); continue

    if t.get("id") != fn:
        errs.append(f"{fn}: id is {t.get('id')!r}; the loader keys files by id, so they must match")

    if fn not in GENERIC:
        matched = next(((r, s) for s in SUFFIXES for r in REGIONS if fn == f"{r}_{s}"), None)
        if not matched:
            errs.append(f"{fn}: not in GenericPool and not {{regionId}}_{{suffix}} for any known region "
                        f"and suffix {sorted(SUFFIXES)} - this file is NEVER LOADED")
        else:
            covered.setdefault(matched[0], []).append(matched[1])

    arch = t.get("archetype")
    if arch not in ARCHETYPES:
        errs.append(f"{fn}: archetype {arch!r} is not in NpcArchetypeType {sorted(ARCHETYPES)}"); continue

    for k in t:
        if k.lower() not in lower_enc and k != "confidence":
            errs.append(f"{fn}: unknown encounter field {k!r}")
    for d in ("dialogueWalkaway", "dialogueAccept"):
        if not t.get(d): errs.append(f"{fn}: {d} is empty")
    if not t.get("openingText"): errs.append(f"{fn}: openingText is empty")
    if not t.get("npcName"): errs.append(f"{fn}: npcName is empty")
    if not t.get("factionId"): warns.append(f"{fn}: no factionId - reputation clauses key off an empty string")
    if "terms" in t:
        errs.append(f"{fn}: still carries a v2 'terms' array - rewrite it as 'clauses' (the loader shims it, with a warning, but authoring must move)")

    gw = t.get("startingGoodwill", BASE_GW)
    if gw < 0: gw = BASE_GW
    if not (0 <= gw <= 10): errs.append(f"{fn}: startingGoodwill {gw} outside [0,10]")
    if t.get("basePatience", -1) == 0: errs.append(f"{fn}: basePatience 0 - the table ends before it starts (use -1 for the archetype default)")
    cs = t.get("counterStyle", "")
    if cs and cs.lower() not in ("fair", "greedy"): errs.append(f"{fn}: counterStyle {cs!r} is not fair/greedy")
    for sname in t.get("distrustsSchools") or []:
        if sname not in ("Adept","Elementalist","Druid","Necromancer","Tinker","Enchanter","Arcanist","Chronomancer"):
            errs.append(f"{fn}: distrustsSchools entry {sname!r} is not a CardSchool")

    clauses = t.get("clauses", [])
    if not clauses: errs.append(f"{fn}: no clauses"); continue
    seen, ids = set(), {c.get("id") for c in clauses}
    n_theirs = n_yours = 0
    for c in clauses:
        tag = f"{fn}:{c.get('id','?')}"
        if c.get("id") in seen: errs.append(f"{tag}: duplicate clause id")
        seen.add(c.get("id"))
        if not c.get("id"): errs.append(f"{fn}: clause with no id")
        if not c.get("text"): errs.append(f"{tag}: no text")
        side = str(c.get("side", "")).lower()
        if side not in SIDES: errs.append(f"{tag}: side {c.get('side')!r} not in {sorted(SIDES)}")
        if side == "theirs": n_theirs += 1
        elif side == "yours": n_yours += 1
        kind = str(c.get("kind", "")).lower()
        if kind not in KINDS: errs.append(f"{tag}: kind {c.get('kind')!r} not in {sorted(KINDS)}")
        v = c.get("value", None)
        if v is None or not (0 <= v <= 6): errs.append(f"{tag}: value {v} outside [0,6]")
        nv = c.get("npcValue", -1)
        if not (-1 <= nv <= 5): errs.append(f"{tag}: npcValue {nv} outside [-1,5]")
        rider = c.get("rider", "")
        if rider:
            if side != "theirs": errs.append(f"{tag}: only a theirs clause can carry a rider")
            r = next((x for x in clauses if x.get("id") == rider), None)
            if r is None: errs.append(f"{tag}: rider {rider!r} names no clause in this file")
            elif str(r.get("side","")).lower() != "yours": errs.append(f"{tag}: rider {rider!r} must be a yours clause")
        if c.get("requiresWarm") and side != "theirs":
            warns.append(f"{tag}: requiresWarm on a yours clause has no effect")

        sn = c.get("shortName", "")
        if not sn:
            errs.append(f"{tag}: no shortName - barks would fall back to truncated text")
        else:
            if sn != sn.strip(): errs.append(f"{tag}: shortName has leading/trailing whitespace")
            if sn[:1].isupper(): errs.append(f"{tag}: shortName {sn!r} starts uppercase")
            first = sn.split(" ")[0].lower()
            if first in ("a", "an", "the", "his", "her", "their", "its", "your", "my", "our"):
                errs.append(f"{tag}: shortName {sn!r} starts with an article/pronoun; barks already prepend 'the'")
            if len(sn.split(" ")) > 3: errs.append(f"{tag}: shortName {sn!r} is over three words")
            if sn[-1:] in ".,;:!?": errs.append(f"{tag}: shortName {sn!r} ends with punctuation")
        for k in c:
            if k.lower() not in lower_clause: errs.append(f"{tag}: unknown clause field {k!r}")
        if c.get("spellId") and c["spellId"] not in SPELLS:
            errs.append(f"{tag}: spellId {c['spellId']!r} is not in Data/OverworldSpells")
        if c.get("spellId") and not c.get("requiresWarm"):
            warns.append(f"{tag}: a spell clause without requiresWarm - tuition is meant to be the Warm reward")
        for k in c.get("revealPoiKinds") or []:
            if k not in ("Combat","Rest","Narrative","Negotiation","Outpost","Settlement","Seat","SupplyCache"):
                errs.append(f"{tag}: revealPoiKinds entry {k!r} is not a PoiKind")
        if not any(c.get(k) for k in ("goldDelta", "reputationDelta", "suppliesDelta", "fuelDelta", "spellId", "loreUnlock",
                                      "revealsSupplyCaches", "chartRadius", "revealPoiKinds", "supplyAnchorHere", "safeConductSteps")):
            if side == "theirs":
                warns.append(f"{tag}: theirs clause has no payload - it pays nothing on sign")
    if n_theirs < 3: errs.append(f"{fn}: only {n_theirs} theirs clause(s) - the table has nothing to ask for")
    if n_yours < 3: errs.append(f"{fn}: only {n_yours} yours clause(s) - the table has nothing to give")

    gw_eff = gw + ARCH_GW.get(arch, 0)
    p_, naive = par(clauses, gw_eff, arch)
    BALANCE.append((fn, arch, gw_eff, n_theirs, n_yours, p_, naive))
    if p_ < 4: warns.append(f"{fn}: par is only +{p_} at opening goodwill {gw_eff} - stars will crowd the top")
    if naive >= p_: warns.append(f"{fn}: taking everything and giving everything ({naive:+d}) is par - no decision on the table")

    esc = t.get("escalatesToCombat")
    escalates = esc if esc is not None else (arch in DEFAULT_ESCALATES)
    if escalates:
        ESCALATING.append((fn, arch, "explicit" if esc is not None else "archetype default"))
        rid = next((r for r in REGIONS if fn.startswith(r + "_")), None)
        if rid and BATTLE.get(rid, 0) == 0:
            errs.append(f"{fn}: escalates to combat, but region {rid} has no Battle-tier compositions")

for rid, n in sorted(POI.items(), key=lambda kv: -kv[1]):
    if n > 0 and rid not in covered:
        warns.append(f"region {rid} has negotiationPOICount={n} but no bespoke table - falls back to the generic pool")

print(f"archetypes: {sorted(ARCHETYPES)}   sides: {sorted(SIDES)}   kinds: {sorted(KINDS)}")
print(f"tables: {len(TABLE_FILES)}  bespoke regions: {len(covered)}/{len(REGIONS)}")
for rid in sorted(covered): print(f"  {rid:20} {sorted(covered[rid])}")
print()
print(f"{'table':32s} {'arch':12s} gw  T  Y  par naive")
for fn, a, g, nt, ny, p_, nv in BALANCE:
    print(f"{fn:32s} {a:12s} {g:2d} {nt:2d} {ny:2d} {p_:4d} {nv:5d}")
print()
print(f"escalates to combat on collapse ({len(ESCALATING)}):")
for fn, a, why in sorted(ESCALATING): print(f"  {fn:34} {a:12} ({why})")

# ── v3.1 parley deck: Data/Negotiations/parley_cards.json ─────────────────
PC = D + "parley_cards.json"
if not os.path.exists(PC):
    errs.append("parley_cards.json missing (the parley deck will be empty)")
else:
    try:
        pc = json.load(open(PC, encoding="utf-8"))
    except Exception as e:
        pc = None
        errs.append(f"parley_cards.json does not parse: {e}")
    if pc:
        effects = set(enum_values("ParleyEffect")) if enum_values("ParleyEffect") else set()
        tones = {t.lower() for t in (enum_values("ParleyTone") or ["Warm","Level","Hard"])}
        cats = {t.lower() for t in (enum_values("ParleyCategory") or ["None","Auto","Coin","Access","Standing","Lore"])}
        reaches = {t.lower() for t in (enum_values("ParleyReach") or ["Some","All"])}
        schools = enum_values("CardSchool") or []
        tokens = enum_values("LeverageToken") or []
        ids = {}
        by_source = {}
        for c in pc.get("cards", []):
            cid = c.get("id", "")
            if not cid: errs.append("parley card with no id"); continue
            if cid in ids: errs.append(f"parley card id duplicated: {cid}")
            ids[cid] = c
            by_source.setdefault(c.get("source", ""), []).append(cid)
            if effects and c.get("effect") not in effects:
                errs.append(f"parley card {cid}: effect {c.get('effect')!r} not in ParleyEffect")
            if c.get("tone", "").lower() not in tones:
                errs.append(f"parley card {cid}: tone {c.get('tone')!r} not in ParleyTone")
            if c.get("category", "none").lower() not in cats:
                errs.append(f"parley card {cid}: category {c.get('category')!r} not in ParleyCategory")
            if c.get("reach", "some").lower() not in reaches:
                errs.append(f"parley card {cid}: reach {c.get('reach')!r} not in ParleyReach")
            if c.get("effect") in ("Read","Pull","Sweeten","Claim","Concede") and c.get("category","none").lower() == "none":
                errs.append(f"parley card {cid}: effect {c['effect']} sweeps and needs a category (or auto)")
            if c.get("source") == "universal" and c.get("effect") in ("Read","Argue","Sweeten") and c.get("reach","some").lower() != "some":
                warns.append(f"parley card {cid}: universal sweeps are meant to reach 'some'")
            if "target" in c:
                warns.append(f"parley card {cid}: 'target' is a v3.1 field; ignored")
            if not c.get("line"): warns.append(f"parley card {cid}: no line")
            if not c.get("name"): errs.append(f"parley card {cid}: no name")
        if len(by_source.get("universal", [])) != 10:
            errs.append(f"parley deck: universal set has {len(by_source.get('universal', []))} cards, expected 10")
        for sch in schools:
            n = len(by_source.get(f"school:{sch}", []))
            if n != 4: errs.append(f"parley deck: school:{sch} has {n} cards, expected 4")
            hold_or_draw = any(ids[i].get("effect") in ("Hold","Draw") for i in by_source.get(f"school:{sch}", []))
            if n and not hold_or_draw: warns.append(f"parley deck: school:{sch} has no Hold or Draw card")
        for src in by_source:
            if src != "universal" and not src.startswith("school:"):
                errs.append(f"parley card source {src!r} is neither universal nor school:<CardSchool>")
            if src.startswith("school:") and src[7:] not in schools:
                errs.append(f"parley card source {src!r}: unknown school")
        for trait, cid in pc.get("companionCards", {}).items():
            if cid not in ids: errs.append(f"companionCards[{trait}] -> unknown card {cid}")
        for tok in tokens:
            if tok not in pc.get("tokenCards", {}):
                errs.append(f"tokenCards missing an entry for LeverageToken.{tok} (buildings/patrons with that token type would add nothing)")
        for tok, cid in pc.get("tokenCards", {}).items():
            if cid not in ids: errs.append(f"tokenCards[{tok}] -> unknown card {cid}")
        archs = enum_values("NpcArchetypeType") or []
        for a in archs:
            row = pc.get("npcCards", {}).get(a)
            if not row or "mid" not in row or "final" not in row:
                errs.append(f"npcCards[{a}] needs 'mid' and 'final' names")
        if "name" not in pc.get("npcCards", {}).get("squeeze", {}):
            errs.append("npcCards.squeeze needs a 'name'")
        print(f"parley deck: {len(ids)} cards; universal {len(by_source.get('universal', []))}; "
              + ", ".join(f"{s[7:]} {len(v)}" for s, v in sorted(by_source.items()) if s.startswith("school:")))

print()
print(f"== {len(errs)} error(s), {len(warns)} warning(s) ==")
for e in errs: print("  ERR  ", e)
for w in warns: print("  WARN ", w)
sys.exit(1 if errs else 0)
