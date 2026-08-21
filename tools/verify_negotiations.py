#!/usr/bin/env python3
"""
verify_negotiations.py - negotiation table wiring.

Run from anywhere:  python3 tools/verify_negotiations.py
Exits non-zero on any error.

Everything it checks against is DERIVED FROM SOURCE: the archetype enum from
NpcArchetype.cs, the region-suffix and generic-pool lists from
NegotiationEncounterLoader.cs, the tension bounds from NegotiationState.cs,
region ids and negotiationPOICount from Data/Regions, spell ids from
Data/OverworldSpells.

The two failure modes this exists for are both silent:
  * A file whose name is not {regionId}_{suffix} for a suffix in the loader's
    RegionSuffixes list, and is not in GenericPool, is NEVER LOADED. It is not
    an error anywhere - the table simply never appears in the game.
  * An "archetype" value outside NpcArchetypeType throws inside the enum
    converter, NegotiationEncounterLoader catches the exception and returns
    null, and the file is dead with one line in the log.
"""
import json, re, os, glob, sys

R = os.path.dirname(os.path.dirname(os.path.abspath(__file__))) + os.sep
D = R + "Data/Negotiations/"
errs, warns = [], []

arch_src = open(R + "Scripts/Systems/Negotiation/NpcArchetype.cs").read()
ARCHETYPES = set(re.search(r'enum NpcArchetypeType\s*\{([^}]*)\}', arch_src).group(1).replace(" ", "").replace("\n", "").split(","))
ARCHETYPES.discard("")
ENC_FIELDS = set(re.findall(r'public (?:string|int|bool\??|List<DealTerm>|NpcArchetypeType) (\w+)', arch_src.split("class NegotiationEncounterData")[1].split("}")[0]))
TERM_FIELDS = set(re.findall(r'public (?:string|int|bool\??|float) (\w+)', arch_src.split("class DealTerm")[1].split("public float PlayerFraction")[0]))

load_src = open(R + "Scripts/Systems/Negotiation/NegotiationEncounterLoader.cs").read()
SUFFIXES = set(re.search(r'RegionSuffixes\s*=\s*\{([^}]*)\}', load_src).group(1).replace('"', '').replace(" ", "").replace("\n", "").split(","))
SUFFIXES.discard("")
GENERIC = set(re.search(r'GenericPool\s*=\s*\{(.*?)\};', load_src, re.S).group(1).replace('"', '').replace(" ", "").replace("\n", "").split(","))
GENERIC.discard("")

state_src = open(R + "Scripts/Systems/Negotiation/NegotiationState.cs").read()
TMIN = int(re.search(r'TensionMin = (\d+)', state_src).group(1))
TMAX = int(re.search(r'TensionMax = (\d+)', state_src).group(1))

# The archetype that escalates by default, parsed out of the Escalates property
# so this follows the code if the default is ever retuned.
_esc = re.search(r'EscalatesToCombat \?\? \(Archetype == NpcArchetypeType\.(\w+)\)', arch_src)
DEFAULT_ESCALATES = {_esc.group(1)} if _esc else set()

# A region must have Battle-tier compositions for an escalation to become a real
# fight. EncounterPoolLoader.Pick falls back to a generic roster rather than
# returning null, so an empty regional pool would not error - it would quietly
# hand the player two generic soldiers instead of the region's own forces.
BATTLE = {}
for _p in glob.glob(R + "Data/Regions/*.json"):
    _d = json.load(open(_p))
    BATTLE[_d["id"]] = len(_d.get("encounterPools", {}).get("battle", []))

REGIONS, POI = {}, {}
for p in glob.glob(R + "Data/Regions/*.json"):
    d = json.load(open(p)); REGIONS[d["id"]] = d; POI[d["id"]] = d.get("negotiationPOICount", 0)
SPELLS = {s["id"] for p in glob.glob(R + "Data/OverworldSpells/*.json") for s in json.load(open(p))}

DIALOGUE = ["dialogueCordial", "dialogueStrained", "dialogueHostile", "dialogueWalkaway", "dialogueAccept"]
lower_enc = {f.lower() for f in ENC_FIELDS}
lower_term = {f.lower() for f in TERM_FIELDS}

covered = {}
ESCALATING = []
for p in sorted(glob.glob(D + "*.json")):
    fn = os.path.basename(p)[:-5]
    try:
        t = json.load(open(p))
    except Exception as e:
        errs.append(f"{fn}: does not parse - {e}"); continue

    if t.get("id") != fn:
        errs.append(f"{fn}: id is {t.get('id')!r}; the loader keys files by id, so they must match")

    # reachability: the single most important check here
    if fn not in GENERIC:
        region, _, suffix = fn.rpartition("_")
        # region ids contain underscores, so walk the suffix list instead of splitting
        matched = next(((r, s) for s in SUFFIXES for r in REGIONS if fn == f"{r}_{s}"), None)
        if not matched:
            errs.append(f"{fn}: not in GenericPool and not {{regionId}}_{{suffix}} for any known region "
                        f"and suffix {sorted(SUFFIXES)} - this file is NEVER LOADED")
        else:
            covered.setdefault(matched[0], []).append(matched[1])

    if t.get("archetype") not in ARCHETYPES:
        errs.append(f"{fn}: archetype {t.get('archetype')!r} is not in NpcArchetypeType {sorted(ARCHETYPES)} "
                    f"- the enum converter throws and the loader silently returns null")

    for k in t:
        if k.lower() not in lower_enc:
            errs.append(f"{fn}: unknown encounter field {k!r}")
    for d in DIALOGUE:
        if not t.get(d): errs.append(f"{fn}: {d} is empty")
    if not t.get("openingText"): errs.append(f"{fn}: openingText is empty")
    if not t.get("npcName"): errs.append(f"{fn}: npcName is empty")
    if not t.get("factionId"): warns.append(f"{fn}: no factionId - reputation terms will key off an empty string")

    tension = t.get("startingTension", 4)
    if not (TMIN <= tension <= TMAX):
        errs.append(f"{fn}: startingTension {tension} outside [{TMIN},{TMAX}]")
    if t.get("basePatience", 0) <= 0:
        errs.append(f"{fn}: basePatience must be positive")

    terms = t.get("terms", [])
    if not terms: errs.append(f"{fn}: no terms")
    seen = set()
    for tm in terms:
        tag = f"{fn}:{tm.get('id','?')}"
        if tm.get("id") in seen: errs.append(f"{tag}: duplicate term id")
        seen.add(tm.get("id"))
        if not tm.get("id"): errs.append(f"{fn}: term with no id")
        if not tm.get("description"): errs.append(f"{tag}: no description")
        for k in tm:
            if k.lower() not in lower_term: errs.append(f"{tag}: unknown term field {k!r}")
        sp = tm.get("startingPosition", -1)
        if sp != -99 and not (-2 <= sp <= 2):
            errs.append(f"{tag}: startingPosition {sp} outside [-2,2] (-99 = unauthored)")
        if tm.get("spellId") and tm["spellId"] not in SPELLS:
            errs.append(f"{tag}: spellId {tm['spellId']!r} is not in Data/OverworldSpells")
        if not any(tm.get(k) for k in ("goldDelta", "reputationDelta", "suppliesDelta", "stepsDelta",
                                       "spellId", "loreUnlock", "revealsSupplyCaches")):
            warns.append(f"{tag}: term has no outcome - it costs and pays nothing")
    # escalation roster + reachability of a real composition
    esc = t.get("escalatesToCombat")
    escalates = esc if esc is not None else (t.get("archetype") in DEFAULT_ESCALATES)
    if escalates:
        ESCALATING.append((fn, t.get("archetype"), "explicit" if esc is not None else "archetype default"))
        rid = next((r for r in REGIONS if fn.startswith(r + "_")), None)
        if rid and BATTLE.get(rid, 0) == 0:
            errs.append(f"{fn}: escalates to combat, but region {rid} has no Battle-tier "
                        f"compositions - the fight would fall back to a generic roster")
    if not any(tm.get("isHidden") for tm in terms):
        warns.append(f"{fn}: no hidden term - Insight tokens have nothing to reveal at this table")

# coverage against how often a table is actually reached
for rid, n in sorted(POI.items(), key=lambda kv: -kv[1]):
    if n > 0 and rid not in covered:
        warns.append(f"region {rid} has negotiationPOICount={n} but no bespoke table - falls back to the generic pool")

print(f"archetypes: {sorted(ARCHETYPES)}")
print(f"region suffixes: {sorted(SUFFIXES)}")
print(f"tables: {len(glob.glob(D+'*.json'))}  bespoke regions: {len(covered)}/{len(REGIONS)}")
for rid in sorted(covered): print(f"  {rid:20} {sorted(covered[rid])}")
print()
print(f"escalates to combat on collapse ({len(ESCALATING)}):")
for fn, a, why in sorted(ESCALATING): print(f"  {fn:34} {a:12} ({why})")
print()
print(f"== {len(errs)} error(s), {len(warns)} warning(s) ==")
for e in errs: print("  ERR  ", e)
for w in warns: print("  WARN ", w)
sys.exit(1 if errs else 0)
