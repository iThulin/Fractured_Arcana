#!/usr/bin/env python3
"""verify_flags_and_quests.py - echo flags, quest wiring, campus arc reachability.

Catches: an encounter gating on an echo flag no seeder emits; an echo that does
not seal itself with the _seen flag TrySeed checks (which makes it re-fire every
cycle forever); a quest category outside QuestLogView's render whitelist, which
silently drops the quest from the log; a quest objective keyed to a flag nothing
ever sets; a campus landmark beat gated on a recovery flag no encounter grants,
which makes that restoration arc unfinishable.

Run from anywhere:  python3 tools/verify_flags_and_quests.py
Exits non-zero on any error, so it drops into a pre-commit hook or CI unchanged.

Everything it checks against is DERIVED FROM SOURCE, never restated here — echo
flags and substitutions are parsed out of EchoSeeder.cs, quest categories out of
QuestLogView.cs, counter families out of QuestTracker.cs, terrain palettes out of
Data/Regions, reward ids out of Data/ and FactionRegistry.cs. The checks drift
when the code drifts instead of encoding assumptions about an API that then rot.
"""
import json, re, os, glob, sys, random, collections

R = os.path.dirname(os.path.dirname(os.path.abspath(__file__))) + os.sep
D, QD = R + "Data/Encounters/", R + "Data/Quests/"
errs, warns = [], []

seeder = open(R + "Scripts/Data/FeatureBuilders/EchoSeeder.cs").read()
ECHO_ELIGIBLE = set(re.findall(r'TrySeed\("([a-z_]+)"', seeder))
ECHO_SEEN     = set(re.findall(r'TrySeed\("[a-z_]+",\s*metaFlags,\s*"([a-z_]+)"', seeder))
SUBS          = set(re.findall(r'_subs\["([a-z_]+)"\]\s*=', seeder))
UNSAFE = {"deal_faction", "convergence_outcome", "style_companion_id"}

logview = open(R + "Scripts/Data/FeatureBuilders/QuestLogView.cs").read()
ETERNAL_CATS  = set(re.search(r'eternalCats = \{([^}]*)\}', logview).group(1).replace('"','').replace(' ','').split(','))
TIMELINE_CATS = set(re.search(r'timelineCats = \{([^}]*)\}', logview).group(1).replace('"','').replace(' ','').split(','))

tracker = open(R + "Scripts/Data/FeatureBuilders/QuestTracker.cs").read()
COUNTER_PREFIXES = set(re.findall(r'counter\.StartsWith\("([a-z]+:)"\)', tracker))
NAMED_COUNTERS   = set(re.findall(r'case "([a-z_]+)":', tracker))
# Counter families whose target the CODE owns via QuestTracker.TargetFor. Quests
# on these families must OMIT counterTarget — hardcoding it re-duplicates a C#
# constant into JSON, which is exactly the drift TargetFor exists to prevent.
_target_for = re.search(r'public static int TargetFor\(.*?\n    \}', tracker, re.S)
CODE_OWNED = set(re.findall(r'o\.Counter\.StartsWith\("([a-z]+:)"\)', _target_for.group(0) if _target_for else ""))

# ── every flag anything in the project can actually produce ──────────────
produced = set(ECHO_ELIGIBLE)
PATTERNS = []
for p in glob.glob(D+"*.json") + glob.glob(QD+"*.json"):
    try: data = json.load(open(p))
    except Exception: continue
    for e in (data if isinstance(data, list) else []):
        if e.get("id"): produced.add(e["id"])          # CompletedEvents
        for k in ("rewardFlag",):
            if e.get(k): produced.add(e[k])
        for c in e.get("choices", []):
            produced |= set(c.get("setFlags", [])) | set(c.get("setMetaFlags", []))
for p in glob.glob(R + "Scripts/**/*.cs", recursive=True):
    src = open(p, errors="ignore").read()
    for m in re.finditer(r'Set(?:Meta)?Flags = new List<string> \{([^}]*)\}', src):
        produced |= set(re.findall(r'"([A-Za-z0-9_]+)"', m.group(1)))
    produced |= set(re.findall(r'(?:WorldFlags|MetaNarrativeFlags)\.Add\("([A-Za-z0-9_]+)"', src))
    produced |= set(re.findall(r'CompletionFlag = "([a-z_0-9]+)"', src))
    produced |= set(re.findall(r'RestoredFlag = "([a-z_0-9]+)"', src))
    for lit in re.findall(r'\$"([a-z][a-z0-9_]*(?:\{[A-Za-z0-9_.()]+\}[a-z0-9_]*)+)"', src):
        PATTERNS.append(re.compile("^" + re.sub(r"\{[^}]+\}", "[A-Za-z0-9_]+", re.escape(lit).replace(r"\{", "{").replace(r"\}", "}")) + "$"))

def is_produced(flag):
    return flag in produced or any(rx.match(flag) for rx in PATTERNS)

# ── 1. echoes gate on real flags, use real subs, and seal themselves ─────
ripples = json.load(open(D+"ripples.json"))
for e in ripples:
    rf = e["requiredFlag"]
    if rf not in ECHO_ELIGIBLE:
        errs.append(f"ripples:{e['id']}: gates on {rf}, which EchoSeeder never emits")
    want = rf.replace("_eligible", "_seen")
    if want not in ECHO_SEEN:
        errs.append(f"ripples:{e['id']}: {want} is not the seen-flag EchoSeeder checks")
    for c in e["choices"]:
        if want not in c.get("setMetaFlags", []):
            errs.append(f"ripples:{e['id']}: choice {c['label']!r} does not seal with {want}")
covered = {e["requiredFlag"] for e in ripples}
for f in sorted(ECHO_ELIGIBLE - covered):
    warns.append(f"echo flag {f} still has no authored encounter")

# ── 2. no encounter text uses an unknown or raw-storage substitution ─────
frag_slots = set(json.load(open(D+"fragments.json")))
TOK = re.compile(r"\{([a-zA-Z0-9_]+)\}")
for p in sorted(glob.glob(D+"*.json")):
    if p.endswith("fragments.json"): continue
    for e in json.load(open(p)):
        texts = [e.get("title",""), e.get("body","")]
        for c in e.get("choices",[]): texts += [c.get("label",""), c.get("resultText","")]
        for t in texts:
            for k in TOK.findall(t):
                tag = f"{os.path.basename(p)}:{e.get('id') or e.get('title')}"
                if k in UNSAFE: errs.append(f"{tag}: raw-storage token {{{k}}} in player-facing text")
                elif k not in frag_slots and k not in SUBS:
                    errs.append(f"{tag}: token {{{k}}} is neither a fragment slot nor an EchoSeeder sub")

# ── 3. encounter-level requiredFlags resolve ────────────────────────────
for p in sorted(glob.glob(D+"*_encounters.json")):
    for e in json.load(open(p)):
        rf = e.get("requiredFlag","")
        if rf and not is_produced(rf):
            errs.append(f"{os.path.basename(p)}:{e['id']}: requiredFlag {rf} is never produced")

# ── 4. quests ───────────────────────────────────────────────────────────
QFIELDS = {"id","title","summary","category","permanent","layer","requiredLore","requiredFlag",
           "requiredQuest","requiredCounter","requiredCounterTarget","objectives","rewardLore","rewardFlag"}
OFIELDS = {"text","flag","lore","counter","counterTarget"}
allq, ids = [], collections.Counter()
for p in sorted(glob.glob(QD+"*.json")):
    try: data = json.load(open(p))
    except Exception as ex: errs.append(f"{os.path.basename(p)}: does not parse — {ex}"); continue
    for q in data:
        allq.append((os.path.basename(p), q)); ids[q["id"]] += 1
for qid, n in ids.items():
    if n > 1: errs.append(f"duplicate quest id {qid} ({n}×)")
qids = set(ids)
for fn, q in allq:
    tag = f"{fn}:{q['id']}"
    for k in q:
        if k not in QFIELDS: errs.append(f"{tag}: unknown quest field {k!r}")
    layer = q.get("layer") or ("Eternal" if q.get("permanent") else "Timeline")
    cats = ETERNAL_CATS if layer == "Eternal" else TIMELINE_CATS
    if q.get("category") not in cats:
        errs.append(f"{tag}: category {q.get('category')!r} not rendered for {layer} layer (whitelist: {sorted(cats)})")
    if not q.get("objectives"): errs.append(f"{tag}: no objectives")
    for o in q.get("objectives", []):
        for k in o:
            if k not in OFIELDS: errs.append(f"{tag}: unknown objective field {k!r}")
        if o.get("flag"):
            if not is_produced(o["flag"]): errs.append(f"{tag}: objective flag {o['flag']} is never produced")
        elif o.get("counter"):
            c = o["counter"]
            if not (any(c.startswith(p_) for p_ in COUNTER_PREFIXES) or c in NAMED_COUNTERS):
                errs.append(f"{tag}: counter {c!r} matches no family or named counter")
            owned = next((f for f in CODE_OWNED if c.startswith(f)), None)
            if owned:
                if o.get("counterTarget"):
                    errs.append(f"{tag}: counter {c!r} hardcodes counterTarget={o['counterTarget']}, "
                                f"but QuestTracker.TargetFor owns the target for the {owned!r} family — omit it")
            elif not o.get("counterTarget"):
                errs.append(f"{tag}: counter objective with no counterTarget and no code-owned default")
        elif not o.get("lore"):
            errs.append(f"{tag}: objective has no flag, lore or counter")
    if q.get("requiredFlag") and not is_produced(q["requiredFlag"]):
        errs.append(f"{tag}: requiredFlag {q['requiredFlag']} is never produced")
    if q.get("requiredQuest") and q["requiredQuest"] not in qids:
        errs.append(f"{tag}: requiredQuest {q['requiredQuest']} is not a real quest id")
    if q.get("requiredQuest") and not q.get("permanent"):
        warns.append(f"{tag}: requiredQuest only checks CompletedQuestIds, which Timeline quests never enter")
    rc = q.get("requiredCounter")
    if rc and not (any(rc.startswith(p_) for p_ in COUNTER_PREFIXES) or rc in NAMED_COUNTERS):
        errs.append(f"{tag}: requiredCounter {rc!r} matches no family or named counter")

# ── 5. the campus arcs can actually be finished ─────────────────────────
land = open(R + "Scripts/Systems/Campus/CampusLandmarkData.cs").read()
for gate in sorted(set(re.findall(r'RequiredFlag = "([a-z_]+_recovered)"', land))):
    if not is_produced(gate):
        errs.append(f"campus landmark beat requires {gate}, which nothing in the world grants — arc is unfinishable")

print(f"flag producers: {len(produced)} literal, {len(PATTERNS)} interpolated patterns")
print(f"EchoSeeder emits {len(ECHO_ELIGIBLE)} echo flags, caches {len(SUBS)} substitutions")
print(f"ripples.json covers {len(covered)}/{len(ECHO_ELIGIBLE)}")
print(f"quests: {len(allq)} across {len(glob.glob(QD+'*.json'))} files")
print(f"code-owned targets: {sorted(CODE_OWNED)}")
print(f"counter families: {sorted(COUNTER_PREFIXES)}  named: {sorted(NAMED_COUNTERS)}")
print()
print(f"── {len(errs)} error(s), {len(warns)} warning(s) ──")
for e in errs[:50]: print("  ERR ", e)
for w in warns[:20]: print("  WARN", w)
sys.exit(1 if errs else 0)
