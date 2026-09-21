#!/usr/bin/env python3
"""
convert_negotiations_v3.py — one-shot migration of the v2 'terms' encounter
files into v3 'clauses' (docs/negotiation_ledger_spec_v1.md §8/§10), plus
archetype-flavoured augmentation so every table has a real knapsack
(≥4 clauses a side). Hand-authored overrides for the two canonical tables
(jade_coast_merchant, generic_idealist) are applied last.

Reads  ../legacy/*.json   (the v2 files: recover them with
         `git show <pre-v3 commit>:Data/Negotiations/<id>.json > legacy/<id>.json`)
Writes ../Data/Negotiations/*.json
Then runs the par solver over every output and prints a balance table.
"""
import json, glob, os, itertools, sys

HERE = os.path.dirname(os.path.abspath(__file__))
SRC = os.path.join(HERE, "..", "legacy")
OUT = os.path.join(HERE, "..", "Data", "Negotiations")
os.makedirs(OUT, exist_ok=True)

KINDS = ["gold","goods","lore","service","passage","honor","favor","obligation","spell"]
DEF_NPC = {
    "Merchant":   [4,3,1,2,2,1,3,3,1],
    "Commander":  [2,2,1,4,3,3,2,4,1],
    "Scholar":    [1,1,4,2,1,2,2,2,4],
    "Opportunist":[3,3,2,2,2,0,4,3,2],
    "Idealist":   [1,1,2,3,2,4,2,1,2],
    "Survivor":   [4,4,1,3,3,2,1,2,1],
}
def npc_default(arch, kind): return DEF_NPC[arch][KINDS.index(kind)]

def guess_kind(t):
    if t.get("spellId"): return "spell"
    if t.get("loreUnlock") or t.get("revealsSupplyCaches"): return "lore"
    if t.get("suppliesDelta"): return "goods"
    if t.get("stepsDelta"): return "passage" if t["favorPlayer"] else "service"
    if t.get("goldDelta"): return "gold"
    if t.get("reputationDelta"): return "honor"
    return "service" if t["favorPlayer"] else "favor"

def derive_value(t):
    v = round(abs(t.get("goldDelta",0))/20) + 2*abs(t.get("reputationDelta",0)) \
        + (2 if t.get("loreUnlock") else 0) + abs(t.get("stepsDelta",0)) \
        + round(abs(t.get("suppliesDelta",0))/10) + (3 if t.get("spellId") else 0) \
        + (2 if t.get("revealsSupplyCaches") else 0)
    return max(1, min(6, v))

# Hidden v2 penalties that were narrative TRUTHS (rumours about the counterpart,
# not obligations you could sign): they have no slip in v3. Their text becomes the
# 'confidence' the counterpart shares when the table turns Warm (spec §2c).
TRUTHS = {"she_expects_a_war","she_is_recruiting","no_relief_coming","the_detail_is_watching",
          "she_will_not_last","the_other_buyer","he_is_not_going_down","someone_else_is_sorting",
          "the_field_is_overdue","he_has_no_reserve","she_is_short_handed","they_are_leaving",
          "the_opinion_is_formed","already_promised"}

def migrate(d):
    clauses, hidden_pen = [], []
    d["_confidence"] = ""
    for t in d["terms"]:
        theirs = t["favorPlayer"]
        if t["id"] in TRUTHS:
            d["_confidence"] = t["description"]
            continue
        c = {
            "id": t["id"], "text": t["description"], "shortName": t.get("shortName",""),
            "side": "theirs" if theirs else "yours", "kind": guess_kind(t),
            "value": derive_value(t),
        }
        for k in ["goldDelta","reputationDelta","factionId","loreUnlock","suppliesDelta","revealsSupplyCaches","spellId"]:
            if t.get(k): c[k] = t[k]
        if t.get("stepsDelta"): c["fuelDelta"] = t["stepsDelta"]
        # Theirs-side "passage/escort" steps become map verbs where the text says so.
        txt = t["description"].lower()
        if theirs and t.get("stepsDelta", 0) > 0:
            if any(w in txt for w in ["escort", "safe passage", "safe conduct", "unmolested", "stood down", "called ahead", "nothing on the road"]):
                c["safeConductSteps"] = 20 + 4 * t["stepsDelta"]; c["kind"] = "passage"
                c["text"] = t["description"]
                c.pop("fuelDelta", None); c["fuelDelta"] = max(0, t["stepsDelta"] - 2) or None
                if c["fuelDelta"] is None: c.pop("fuelDelta")
            elif any(w in txt for w in ["chart", "survey", "map", "line across", "which route", "wells", "ford", "path", "causeway", "corridor", "pilot", "tables"]):
                c["chartRadius"] = 6 + 2 * t["stepsDelta"]; c["kind"] = "lore"
                c["fuelDelta"] = max(1, t["stepsDelta"] - 1)
        if t.get("isHidden") and not theirs:
            hidden_pen.append(c)
        elif t.get("isHidden") and theirs:
            c["requiresWarm"] = True
        if t.get("spellId"):
            c["requiresWarm"] = True   # tuition is the Warm reward (spec S4)
        clauses.append(c)
    carriers = sorted([c for c in clauses if c["side"]=="theirs" and not c.get("requiresWarm")], key=lambda c: -c["value"])
    ci = 0
    for pen in hidden_pen:
        while ci < len(carriers) and carriers[ci].get("rider"): ci += 1
        if ci >= len(carriers): break
        carriers[ci]["rider"] = pen["id"]; ci += 1
    return clauses

# ── Augmentation pools (archetype-flavoured, map-paying) ─────────────────
THEIRS_POOL = {
 "Merchant": [
  ("charts",   "lore",    3, "Their trade charts: every road and ford their factors use",       "trade charts",   {"chartRadius": 10}),
  ("customs",  "passage", 2, "A customs seal: their patrols look the other way this run",       "customs seal",   {"safeConductSteps": 30}),
  ("berth",    "passage", 3, "A berth at their depot: a supply anchor for the rest of the run", "depot berth",    {"supplyAnchorHere": True}),
 ],
 "Commander": [
  ("dispositions","lore", 3, "Their dispositions: where every picket and camp on this ground stands", "dispositions", {"chartRadius": 10, "revealPoiKinds": ["Combat"]}),
  ("safe_conduct","passage",3,"Safe conduct under their banner: no patrol will trouble you",   "safe conduct",   {"safeConductSteps": 35}),
  ("camp_right","passage", 3, "Right to camp inside their lines: a supply anchor while you're here", "camp right", {"supplyAnchorHere": True}),
 ],
 "Scholar": [
  ("survey",   "lore",    3, "Their survey of the district, annotated: the ground and what lies under it", "survey", {"chartRadius": 12}),
  ("site",     "lore",    3, "The location of a site they have catalogued and no one else has walked", "catalogued site", {"revealPoiKinds": ["Narrative"]}),
  ("lodging",  "passage", 2, "Lodging at their station: a supply anchor for the rest of the run", "lodging", {"supplyAnchorHere": True}),
 ],
 "Opportunist": [
  ("blind_spot","passage",2, "The gap in the patrol rota, and the hour it opens",                "blind spot",     {"safeConductSteps": 25}),
  ("cache_map","lore",    3, "Where the locals park what they don't want found",                "cache map",      {"revealPoiKinds": ["SupplyCache"]}),
  ("backdoor", "lore",    2, "A back way in, drawn on a napkin",                                 "back way",       {"chartRadius": 8}),
 ],
 "Idealist": [
  ("shelter",  "passage", 3, "Shelter for the run: their door is a supply anchor while it stands", "shelter",     {"supplyAnchorHere": True, "fuelDelta": 2}),
  ("pilgrim_roads","lore",2, "The pilgrim roads, charted: where the ground is kind",             "pilgrim roads",  {"chartRadius": 9}),
  ("word_ahead","passage",2, "Word sent ahead: their people will not hinder you",               "word ahead",     {"safeConductSteps": 20}),
 ],
 "Survivor": [
  ("the_route","lore",    3, "The route that still runs, and the ones that don't",               "route",      {"chartRadius": 9, "fuelDelta": 1}),
  ("hide",     "passage", 2, "Their hide: a place to lie up, and a supply anchor while you use it", "hide",    {"supplyAnchorHere": True}),
  ("what_they_saw","lore",2, "What they saw on the way here, marked on your map",               "sightings",      {"revealPoiKinds": ["Narrative"]}),
 ],
}
YOURS_POOL = {
 "Merchant": [
  ("gold_purse",  "gold",   3, "A purse of 60 gold",                                      "gold",        {"goldDelta": -60}),
  ("escort_duty", "service",2, "Your column rides escort for their next shipment",        "escort duty", {"fuelDelta": -4}),
  ("exclusive",   "favor",  2, "Exclusive dealing with them this cycle",                  "exclusivity", {}),
  ("favor_owed",  "favor",  2, "A favor owed, to be named later",                         "favor owed",  {}),
  ("your_charts", "lore",   1, "Your own charts of the road you came in by",              "your charts", {}),
 ],
 "Commander": [
  ("provisions",  "goods",  2, "Provisions from your stores for their line",              "provisions",  {"suppliesDelta": -12}),
  ("stand_watch", "service",2, "Your party stands a watch on their line",                 "watch duty",     {"fuelDelta": -3}),
  ("dispatch",    "service",1, "You carry a dispatch to the next post",                   "dispatch",    {"fuelDelta": -2}),
  ("non_aggression","favor",2, "A pledge of non-aggression toward their people this cycle", "pledge",    {}),
  ("intel",       "lore",   1, "What you saw of the enemy on the road in",                "field report",{}),
 ],
 "Scholar": [
  ("field_notes", "lore",   1, "Your field notes from the road",                          "field notes", {}),
  ("samples",     "service",2, "You carry their samples to the next waystone",            "samples",     {"fuelDelta": -3}),
  ("library_access","favor",2, "Reading rights in your guild's library",                  "library access", {}),
  ("gold_stipend","gold",   2, "A stipend of 40 gold toward their work",                  "stipend",     {"goldDelta": -40}),
  ("demonstration_promise","honor",1, "A promise to return and teach what you learn here", "promise",   {}),
 ],
 "Opportunist": [
  ("cut",         "gold",   2, "A cut: 40 gold, no receipt",                              "cut",       {"goldDelta": -40}),
  ("package",     "service",2, "You carry a small sealed package two towns on",           "package", {"fuelDelta": -2}),
  ("look_away",   "favor",  2, "You look the other way, once, when asked",                "blind eye",   {}),
  ("introduction","favor",  1, "An introduction to someone your guild knows",             "introduction",{}),
  ("surplus",     "goods",  1, "Your surplus salvage, at their price",                    "salvage",     {"suppliesDelta": -6}),
 ],
 "Idealist": [
  ("poor_box",    "gold",   2, "30 gold for the poor box",                                "poor box",    {"goldDelta": -30}),
  ("vow",         "honor",  1, "A vow to harm no one under their protection",             "vow",     {}),
  ("tend_sick",   "service",2, "Your party tends the sick here for a day",                "day's care",{"fuelDelta": -3}),
  ("stores",      "goods",  2, "Supplies from your stores for their kitchen",             "stores",      {"suppliesDelta": -10}),
  ("carry_word",  "service",1, "You carry their word to the next settlement",             "carried word",  {"fuelDelta": -1}),
 ],
 "Survivor": [
  ("rations",     "goods",  2, "Rations from your stores",                                "rations",     {"suppliesDelta": -10}),
  ("walk_out",    "service",2, "You walk them to the next safe place",                    "escort out",  {"fuelDelta": -3}),
  ("weapon",      "goods",  1, "A spare blade from your armory",                          "spare blade",     {}),
  ("coin",        "gold",   1, "20 gold, which is worth less out here than you'd think",  "coin",        {"goldDelta": -20}),
  ("carry_names", "service",1, "You carry three names inland and ask after them",        "three names",   {"fuelDelta": -1}),
  ("your_word",   "honor",  1, "Your word that you'll send someone back for them",        "your word",   {}),
  ("the_road_in", "lore",   1, "The road you came in by, and what's on it",               "road in",     {}),
 ],
}
# Every pool ends with the same two universal fillers so no table stays thin.
for _k in YOURS_POOL:
    YOURS_POOL[_k] = YOURS_POOL[_k] + [
        ("favor_later", "favor", 2, "A favor, to be named later", "favor owed", {}),
        ("good_word",   "honor", 1, "A good word for them wherever your guild is heard", "good word", {}),
    ]

def augment(d, clauses):
    arch = d["archetype"]
    ids = {c["id"] for c in clauses}
    theirs = [c for c in clauses if c["side"]=="theirs"]
    yours  = [c for c in clauses if c["side"]=="yours"]
    have_anchor = any(c.get("supplyAnchorHere") for c in theirs)
    have_chart  = any(c.get("chartRadius") for c in theirs)
    have_safe   = any(c.get("safeConductSteps") for c in theirs)
    for (id_, kind, val, text, short, extra) in THEIRS_POOL[arch]:
        if len(theirs) >= 4: break
        if extra.get("supplyAnchorHere") and have_anchor: continue
        if extra.get("chartRadius") and have_chart: continue
        if extra.get("safeConductSteps") and have_safe: continue
        if id_ in ids: continue
        c = {"id": id_, "text": text, "shortName": short, "side": "theirs", "kind": kind, "value": val}
        c.update(extra); clauses.append(c); theirs.append(c); ids.add(id_)
        have_anchor |= bool(extra.get("supplyAnchorHere")); have_chart |= bool(extra.get("chartRadius")); have_safe |= bool(extra.get("safeConductSteps"))
    for (id_, kind, val, text, short, extra) in YOURS_POOL[arch]:
        if len(yours) >= 4: break
        if id_ in ids: continue
        if any(y["kind"] == kind and y.get("fuelDelta") == extra.get("fuelDelta") and y.get("suppliesDelta") == extra.get("suppliesDelta") and y.get("goldDelta") == extra.get("goldDelta") for y in yours): continue
        c = {"id": id_, "text": text, "shortName": short, "side": "yours", "kind": kind, "value": val}
        c.update(extra); clauses.append(c); yours.append(c); ids.add(id_)
    return clauses

def goodwill_from_tension(t):
    return {1:5, 2:5, 3:4, 4:3, 5:3, 6:2, 7:2, 8:1, 9:1, 10:1}.get(int(t), 3)

def convert(d):
    out = {
        "id": d["id"], "title": d["title"], "npcName": d["npcName"], "archetype": d["archetype"],
        "factionId": d.get("factionId",""),
        "basePatience": d.get("basePatience", -1),
        "startingGoodwill": goodwill_from_tension(d.get("startingTension", 4)),
        "openingText": d.get("openingText",""),
    }
    for k in ["openingTextLate","dialogueWalkawayLate","escalatesToCombat","counterStyle","distrustsSchools"]:
        if k in d: out[k] = d[k]
    out["dialogueWalkaway"] = d.get("dialogueWalkaway","")
    out["dialogueAccept"] = d.get("dialogueAccept","")
    clauses = augment(d, migrate(d))
    if d.get("_confidence"): out["confidence"] = d["_confidence"]
    for c in clauses:
        tw = TWEAKS.get((d["id"], c["id"]))
        if tw: c.update(tw)
    # Clean: drop None/empty
    for c in clauses:
        for k in list(c.keys()):
            if c[k] in (None, "", [], False, 0) and k not in ("value",):
                del c[k]
    out["clauses"] = clauses
    return out

# ── Hand-authored canonical tables (spec §8 / prototype) ─────────────────
def override_jade(out):
    out["startingGoodwill"] = 3
    out["counterStyle"] = "greedy"
    out["clauses"] = [
      {"id":"harbor_charts","text":"Charts of the drowned harbor and the mangrove passes: twelve hexes of coast, and the sunken customs house marked","shortName":"harbor charts","side":"theirs","kind":"lore","value":3,"npcValue":1,"chartRadius":12,"revealPoiKinds":["Narrative"],"loreUnlock":"drowned_harbor_charts"},
      {"id":"saffron_contract","text":"A bulk contract on eastern saffron for the guild kitchens and alchemists","shortName":"saffron contract","side":"theirs","kind":"gold","value":5,"npcValue":4,"goldDelta":90},
      {"id":"berth","text":"A berth on the Combine's next sloop: Tidewrack harbor becomes a supply anchor this run","shortName":"berth","side":"theirs","kind":"passage","value":3,"npcValue":2,"supplyAnchorHere":True},
      {"id":"customs_seal","text":"A customs seal of the Combine: their patrols lose your trail, and the Combine remembers your name","shortName":"customs seal","side":"theirs","kind":"honor","value":2,"npcValue":2,"reputationDelta":1,"factionId":"saldini_combine","safeConductSteps":30,"rider":"favor_clause"},
      {"id":"gold_purse","text":"60 gold, counted onto the lacquer","shortName":"sixty gold","side":"yours","kind":"gold","value":3,"npcValue":3,"goldDelta":-60},
      {"id":"escort_fee","text":"Your wizards guard his next caravan to Tidewrack, unpaid","shortName":"guard duty","side":"yours","kind":"service","value":2,"npcValue":4,"fuelDelta":-4},
      {"id":"tide_tables","text":"Your tide tables for the coast, copied fair","shortName":"tide tables","side":"yours","kind":"lore","value":1,"npcValue":1},
      {"id":"exclusivity","text":"Exclusive dealing with the Combine this cycle","shortName":"exclusivity","side":"yours","kind":"favor","value":2,"npcValue":3},
      {"id":"favor_clause","text":"The guild owes the Combine one unnamed favor, on demand","shortName":"favor clause","side":"yours","kind":"favor","value":2,"npcValue":2,"reputationDelta":0},
    ]
    return out

def override_maretta(out):
    out["startingGoodwill"] = 3
    out["clauses"] = [
      {"id":"shelter","text":"Shelter at the waystation for the run: a supply anchor while its door is open, and a warm night","shortName":"shelter","side":"theirs","kind":"service","value":3,"npcValue":1,"supplyAnchorHere":True,"fuelDelta":3},
      {"id":"circle_marks","text":"The Circle marks your guild a friend of the road, and charts the pilgrim ways for you","shortName":"circle mark","side":"theirs","kind":"honor","value":2,"npcValue":2,"reputationDelta":1,"factionId":"wayfarers_circle","chartRadius":9},
      {"id":"tithe_box","text":"The Circle's tithe box, opened for a friend","shortName":"tithe box","side":"theirs","kind":"gold","value":3,"npcValue":4,"goldDelta":40},
      {"id":"reliquary_key","text":"The key to the old reliquary, and a word about the novice Tamsin who keeps it","shortName":"reliquary key","side":"theirs","kind":"honor","value":4,"npcValue":3,"revealPoiKinds":["Narrative"]},
      {"id":"provision_camp","text":"30 gold for the poor box","shortName":"poor box","side":"yours","kind":"gold","value":2,"npcValue":1,"goldDelta":-30},
      {"id":"vow","text":"A vow to harm no pilgrim on the road","shortName":"vow","side":"yours","kind":"honor","value":1,"npcValue":4},
      {"id":"tend_sick","text":"Your party tends the sick in the cloister for a day","shortName":"day's care","side":"yours","kind":"service","value":2,"npcValue":3,"fuelDelta":-3},
      {"id":"relic","text":"The relic you carried out of the Sunken Archive","shortName":"relic","side":"yours","kind":"lore","value":3,"npcValue":2},
    ]
    return out

TWEAKS = {
  ("amber_downs_idealist","wardens_writ"):        {"npcValue": 3},
  ("amber_downs_idealist","the_true_reading"):    {"npcValue": 3},
  ("amber_downs_idealist","leave_the_stones"):    {"kind": "honor", "npcValue": 4, "value": 2},
  ("amber_downs_idealist","provision_the_wardens"):{"npcValue": 3},
  ("generic_scholar","marginalia"):               {"npcValue": 4},
  ("generic_scholar","site"):                     {"npcValue": 3},
  ("generic_scholar","survey_data"):              {"npcValue": 3},
  ("generic_scholar","salvage_claim"):            {"kind": "obligation", "value": 2, "npcValue": 3},
  ("cogwork_reach_opportunist","cache_lines"):    {"value": 3, "revealPoiKinds": ["SupplyCache"], "kind": "lore"},
  ("cogwork_reach_opportunist","who_is_buying"):  {"value": 3},
  ("cogwork_reach_opportunist","gap_in_the_ledger"):{"value": 3, "safeConductSteps": 20},
  ("generic_survivor","dead_wells"):              {"npcValue": 1},
  ("generic_survivor","caravan_stash"):           {"requiresWarm": False, "npcValue": 2, "value": 3},
  ("generic_survivor","hide"):                    {"npcValue": 2},
  ("obsidian_waste_commander","pact_standing"):   {"npcValue": 4},
  ("obsidian_waste_commander","the_glass_rights"):{"npcValue": 3},
  ("sunken_archive_scholar","reading_rights"):    {"npcValue": 3, "value": 4},
  ("sunken_archive_scholar","catalog_labor"):     {"npcValue": 4, "value": 2},
  ("generic_scholar","field_stipend"):            {"npcValue": 2},
  ("generic_scholar","samples"):                  {"npcValue": 4},
  ("sunken_archive_scholar","duplicate_folio"):   {"npcValue": 2},
  ("sunken_archive_scholar","sealed_wing_waiver"):{"kind": "obligation", "value": 2, "npcValue": 2},
  ("generic_merchant","hidden_fee"):              {"kind": "obligation", "value": 1, "npcValue": 2},
  ("cogwork_reach_merchant","assay_fee"):         {"kind": "obligation", "value": 1, "npcValue": 2},
  ("cogwork_reach_merchant","bulk_purchase"):     {"npcValue": 3},
  ("cogwork_reach_merchant","seal_of_passage"):   {"npcValue": 3, "safeConductSteps": 25},
  ("dustreach_commander","water_tithe"):          {"kind": "obligation", "value": 2, "npcValue": 3},
  ("frontier_wilds_commander","tribute"):         {"kind": "obligation", "value": 2, "npcValue": 3},
  ("generic_opportunist","smugglers_path"):       {"value": 3, "safeConductSteps": 15},
  ("generic_opportunist","fence_goods"):          {"value": 3},
  ("generic_opportunist","cache_map"):            {"value": 3},
  ("generic_opportunist","finders_cut"):          {"kind": "obligation", "value": 1, "npcValue": 3},
  ("generic_opportunist","deniability"):          {"kind": "obligation", "value": 2, "npcValue": 3},
  ("the_crags_commander","the_toll"):             {"npcValue": 3},
  ("the_crags_commander","through_passage"):      {"npcValue": 3},
  ("the_crags_commander","cache_rights"):         {"npcValue": 3},
  ("verdant_deep_idealist","no_felling"):         {"kind": "honor", "npcValue": 5},
  ("verdant_deep_idealist","untamed_standing"):   {"npcValue": 4},
  ("verdant_deep_idealist","the_green_reading"):  {"npcValue": 2},
  ("hollow_mire_idealist","take_nothing"):        {"kind": "honor", "npcValue": 5},
  ("hollow_mire_idealist","finish_a_section"):    {"npcValue": 4},
  ("tidewrack_coast_opportunist","split_the_strand"):{"npcValue": 4},
  ("tidewrack_coast_opportunist","hold_the_lantern"):{"kind": "favor", "npcValue": 4},
  ("glacial_threshold_scholar","archive_credit"): {"npcValue": 3},
  ("glacial_threshold_scholar","the_reading_lens"):{"npcValue": 4},
  ("boreal_march_survivor","hearth_right"):       {"npcValue": 2, "supplyAnchorHere": True, "value": 4},
  ("boreal_march_survivor","restock_the_cairn"):  {"npcValue": 5},
}

OVERRIDES = {"jade_coast_merchant": override_jade, "generic_idealist": override_maretta}

# ── Par solver (mirror of NegotiationState.ComputePar) ───────────────────
ARCH_GW = {"Opportunist": -1, "Idealist": 1, "Survivor": 2}
def par(clauses, goodwill, arch):
    goodwill = goodwill + ARCH_GW.get(arch, 0)
    th = [c for c in clauses if c["side"]=="theirs" and not c.get("requiresWarm")]
    yo = [c for c in clauses if c["side"]=="yours"]
    nv = lambda c: c.get("npcValue", npc_default(arch, c["kind"]))
    best = 0; naive = None
    for tm in range(1 << len(th)):
        taken=[th[i] for i in range(len(th)) if tm>>i&1]
        forced={c["rider"] for c in taken if c.get("rider")}
        for ym in range(1 << len(yo)):
            given=[yo[i] for i in range(len(yo)) if ym>>i&1]
            if not forced <= {c["id"] for c in given}: continue
            if goodwill + sum(nv(c) for c in given) - sum(nv(c) for c in taken) < 0: continue
            s = sum(c["value"] for c in taken) - sum(c["value"] for c in given)
            best = max(best, s)
    naive = sum(c["value"] for c in th) - sum(c["value"] for c in yo)
    return best, naive

rows = []
for f in sorted(glob.glob(os.path.join(SRC, "*.json"))):
    d = json.load(open(f))
    out = convert(d)
    if out["id"] in OVERRIDES: out = OVERRIDES[out["id"]](out)
    dump = json.loads(json.dumps(out))
    for c in dump["clauses"]:
        c["side"] = c["side"].capitalize(); c["kind"] = c["kind"].capitalize()
    json.dump(dump, open(os.path.join(OUT, os.path.basename(f)), "w"), indent=1, ensure_ascii=False)
    p, naive = par(out["clauses"], out["startingGoodwill"], out["archetype"])
    nth = sum(1 for c in out["clauses"] if c["side"]=="theirs"); nyo = len(out["clauses"]) - nth
    rows.append((out["id"], out["archetype"], out["startingGoodwill"], nth, nyo, p, naive))

print(f"{'table':32s} {'arch':12s} gw  T  Y  par  naive")
bad = 0
for r in rows:
    flag = ""
    if r[5] < 4: flag += " LOW-PAR"
    if r[6] > 0.7 * r[5]: flag += " NAIVE-EASY"
    if r[3] < 4 or r[4] < 4: flag += " THIN"
    if flag: bad += 1
    print(f"{r[0]:32s} {r[1]:12s} {r[2]:2d} {r[3]:2d} {r[4]:2d} {r[5]:4d} {r[6]:5d}{flag}")
print("tables with flags:", bad)
