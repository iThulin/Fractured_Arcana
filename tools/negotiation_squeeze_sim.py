#!/usr/bin/env python3
"""Monte Carlo check for the N3 squeeze gate (negotiation_narrative_spec_v1
section 5b, ruled: Resolve-only). Python port of NegotiationState's rules,
run over the real encounter JSONs, comparing gate OFF (old behavior: squeeze
whenever a target exists) vs gate ON (squeeze only while NPC Resolve > 0).
Relative signals only, per negotiation_tuning_v1 s5 caveats."""
import json, glob, random, statistics as st

import os
DIR = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "Data", "Negotiations", "*.json")

CORDIAL_MAX, STRAINED_MAX = 3, 7
STANCES = ["Eager", "Guarded", "Wavering", "Irritated", "Expansive"]
BAGS = {
    "Cordial":  ["Expansive", "Expansive", "Eager", "Wavering", "Wavering", "Guarded"],
    "Hostile":  ["Irritated", "Irritated", "Guarded", "Guarded", "Eager", "Wavering"],
    "Strained": ["Eager", "Eager", "Guarded", "Guarded", "Wavering", "Irritated"],
}
DEFAULT_POOL = {"Merchant": (2, 2, 1), "Commander": (3, 1, 1), "Scholar": (1, 2, 2),
                "Opportunist": (2, 3, 0), "Idealist": (1, 1, 2), "Survivor": (2, 1, 2)}

def zone(t):
    return "Cordial" if t <= CORDIAL_MAX else ("Strained" if t <= STRAINED_MAX else "Hostile")

def tension_delta(arch, token):
    tab = {
        ("Idealist", "Charm"): -2, ("Commander", "Charm"): 0,
        ("Idealist", "Intimidate"): 10, ("Commander", "Intimidate"): 1, ("Scholar", "Intimidate"): 3,
        ("Scholar", "Persuade"): -2, ("Opportunist", "Persuade"): 0,
        ("Merchant", "Offering"): -2,
        ("Commander", "Demonstration"): -1, ("Scholar", "Demonstration"): -1, ("Idealist", "Demonstration"): 1,
    }
    if (arch, token) in tab: return tab[(arch, token)]
    return {"Charm": -1, "Intimidate": 2, "Persuade": -1, "Insight": 0,
            "Connections": -1, "Patience": 0, "Offering": -1, "Demonstration": 0}[token]

def derive_weight(t):
    w = round(abs(t.get("goldDelta", 0)) / 15) + abs(t.get("reputationDelta", 0)) \
        + (2 if t.get("spellId") else 0) + -(-abs(t.get("stepsDelta", 0)) // 2) \
        + round(abs(t.get("suppliesDelta", 0)) / 10)
    return max(1, w)

class Table:
    def __init__(self, enc, gate_on):
        self.arch = enc["archetype"]
        self.tension = max(1, min(10, enc.get("startingTension", 4)))
        r, g, p = DEFAULT_POOL[self.arch]
        self.res = enc.get("npcResolve", -1); self.res = self.res if self.res >= 0 else r
        self.gui = enc.get("npcGuile", -1);   self.gui = self.gui if self.gui >= 0 else g
        self.poi = enc.get("npcPoise", -1);   self.poi = self.poi if self.poi >= 0 else p
        self.patience = max(enc.get("basePatience", 8), self.res + self.gui + 3)
        self.terms = []
        for t in enc["terms"]:
            sp = t.get("startingPosition", -99)
            self.terms.append({
                "pos": -1 if sp == -99 else max(-2, min(2, sp)),
                "w": t.get("weight", 0) or derive_weight(t),
                "hidden": t.get("isHidden", False),
                "favor": t.get("favorPlayer", True),
                "locked": False,
            })
        # Player pool: Enchanter per tuning doc (~7 tokens)
        self.tokens = {"Charm": 2, "Connections": 2, "Persuade": 1, "Offering": 1,
                       "Demonstration": 1, "Insight": 0, "Intimidate": 0, "Patience": 0}
        self.stance = random.choice(BAGS[zone(self.tension)])
        self.next_stance = random.choice(BAGS[zone(self.tension)])
        self.hardened = False
        self.gift_given = False
        self.squeeze_spent = False
        self.gate_on = gate_on
        self.turns = 0
        self.outcome = None       # Signed / WalkedAway / TheyLeft / Collapsed
        self.squeeze_offered = False
        self.squeeze_blinked = False

    def pullable(self):
        return [t for t in self.terms if not t["hidden"] and not t["locked"] and t["pos"] < 2]

    def apply_tension(self, d):
        self.tension = max(1, min(10, self.tension + d))
        self.update_locks()
        if self.tension >= 10:
            self.outcome = "Collapsed"

    def update_locks(self):
        for t in self.terms: t["locked"] = False
        if zone(self.tension) != "Hostile": return
        cand = [t for t in self.terms if not t["hidden"] and t["pos"] > 0]
        if cand:
            max(cand, key=lambda t: t["pos"] * t["w"])["locked"] = True

    def pull(self, term, steps, by_player):
        term["pos"] = max(-2, min(2, term["pos"] + (steps if by_player else -steps)))

    def press(self, token, term):
        self.tokens[token] -= 1
        if self.arch == "Idealist" and token == "Intimidate":
            self.outcome = "WalkedAway"; return
        base = tension_delta(self.arch, token)
        pull, delta = 1, base
        if token == "Intimidate":
            if self.stance == "Wavering": pull, delta = 2, base + 2
            elif self.stance == "Guarded": self.hardened = True; delta = base + 1
        else:
            if self.stance == "Irritated": pull, delta = 0, 1
            elif self.stance == "Wavering": delta = base - 1
            elif self.stance == "Guarded": delta = base + 1
            elif self.stance == "Expansive": delta = base - 1
        if pull: self.pull(term, pull, True)
        self.apply_tension(delta)
        if not self.outcome: self.finish_action()

    def offer(self, term):
        self.tokens["Offering"] -= 1
        self.res += 1
        base = tension_delta(self.arch, "Offering")
        pull, delta = 1, base
        if self.stance == "Eager": pull, delta = 2, base - 1
        elif self.stance == "Guarded": delta = 0
        self.pull(term, pull, True)
        self.apply_tension(delta)
        if not self.outcome: self.finish_action()

    def insight_flip(self):
        self.tokens["Insight"] -= 1
        for t in self.terms:
            if t["hidden"]: t["hidden"] = False; break
        self.finish_action()

    def npc_predict(self):
        if self.tension >= 9 and self.poi > 0: return ("Poise", None)
        cand = [t for t in self.terms if not t["hidden"] and 0 <= t["pos"] and t["pos"] > -2]
        if cand and self.res > 0:
            return ("Pull", max(cand, key=lambda t: t["pos"] * t["w"]))
        if self.gui > 0:
            c2 = [t for t in self.terms if not t["hidden"] and t["pos"] > -2]
            if c2: return ("Rework", min(c2, key=lambda t: (t["w"], t["pos"])))
            return ("Threat", None)
        if zone(self.tension) == "Cordial" and not self.gift_given: return ("Gift", None)
        return ("Hold", None)

    def finish_action(self):
        self.turns += 1
        kind, target = self.npc_predict()
        if kind == "Poise": self.poi -= 1; self.apply_tension(-1)
        elif kind == "Pull":
            self.res -= 1
            steps = (2 if zone(self.tension) == "Hostile" else 1) + (1 if self.hardened else 0)
            self.hardened = False
            self.pull(target, steps, False)
        elif kind == "Rework": self.gui -= 1; self.pull(target, 1, False)
        elif kind == "Threat": self.gui -= 1; self.apply_tension(1)
        elif kind == "Gift":
            self.gift_given = True
            self.tokens["Connections"] += 1
        if self.outcome: return
        self.patience -= 1
        if self.patience <= 0:
            self.outcome = "TheyLeft"; return
        self.stance = self.next_stance
        self.next_stance = random.choice(BAGS[zone(self.tension)])

    # ── closing ──────────────────────────────────────────────────────────
    def squeeze_target(self):
        if self.squeeze_spent: return None
        if self.gate_on and self.res <= 0: return None
        cand = [t for t in self.terms if not t["hidden"] and t["pos"] > -2]
        return max(cand, key=lambda t: t["pos"] * t["w"]) if cand else None

    def shake(self, skilled):
        tgt = self.squeeze_target()
        if tgt is None:
            self.outcome = "Signed"; return
        self.squeeze_offered = True
        z = zone(self.tension)
        odds = {"Cordial": 75, "Hostile": 30, "Strained": 55}[z]
        odds += {"Wavering": 15, "Irritated": -15, "Guarded": -10}.get(self.stance, 0)
        odds = max(5, min(95, odds))
        hold = (odds >= 55) if skilled else (random.random() < 0.5)
        if hold:
            self.squeeze_spent = True
            if random.random() * 100 < odds:
                self.squeeze_blinked = True
                self.outcome = "Signed"
            else:
                self.apply_tension(2)
                if not self.outcome:
                    self.shake(skilled)   # next handshake signs squeeze-free
        else:
            self.pull(tgt, 1, False)
            self.outcome = "Signed"

    def score(self):
        s = sum(t["pos"] * t["w"] for t in self.terms)
        z = zone(self.tension)
        s += 2 if z == "Cordial" else (-3 if z == "Hostile" else 0)
        return s

    def stars(self):
        s = self.score()
        return 5 if s >= 8 else 4 if s >= 5 else 3 if s >= 2 else 2 if s >= -2 else 1


def play(enc, skilled, gate_on):
    tb = Table(enc, gate_on)
    while tb.outcome is None:
        spendable = [k for k, v in tb.tokens.items() if v > 0
                     and k not in ("Insight", "Patience")]
        targets = tb.pullable()
        # shake decision
        proj = tb.stars()
        if (not spendable or not targets or
                tb.patience <= (2 if skilled else 0) or
                (skilled and proj >= 4)):
            tb.shake(skilled)
            break
        if skilled:
            # timing: never press into Irritated; offer on Eager if held
            if tb.stance == "Irritated":
                tb.finish_action()   # pass
                continue
            if tb.stance == "Eager" and tb.tokens["Offering"] > 0:
                tb.offer(max(targets, key=lambda t: (2 - t["pos"]) * t["w"]))
                continue
            tok = max((k for k in spendable if k != "Offering"),
                      key=lambda k: tb.tokens[k], default=None) or "Offering"
            tb.press(tok, max(targets, key=lambda t: (2 - t["pos"]) * t["w"])) \
                if tok != "Offering" else tb.offer(max(targets, key=lambda t: (2 - t["pos"]) * t["w"]))
        else:
            tok = random.choice(spendable)
            tgt = random.choice(targets)
            tb.offer(tgt) if tok == "Offering" else tb.press(tok, tgt)
    return tb


def run(skilled, gate_on, n_per=400):
    encs = [json.load(open(p)) for p in sorted(glob.glob(DIR))]
    R = {"tables": 0, "turns": [], "signed": 0, "collapsed": 0, "left": 0, "walked": 0,
         "sq_offered": 0, "sq_blinked": 0, "stars": []}
    for enc in encs:
        for _ in range(n_per):
            tb = play(enc, skilled, gate_on)
            R["tables"] += 1
            R["turns"].append(tb.turns)
            if tb.outcome == "Signed":
                R["signed"] += 1; R["stars"].append(tb.stars())
            elif tb.outcome == "Collapsed": R["collapsed"] += 1
            elif tb.outcome == "TheyLeft": R["left"] += 1
            else: R["walked"] += 1
            R["sq_offered"] += tb.squeeze_offered
            R["sq_blinked"] += tb.squeeze_blinked
    return R


random.seed(63)
for skilled in (False, True):
    for gate in (False, True):
        r = run(skilled, gate)
        n = r["tables"]; sg = max(1, r["signed"])
        print(f"{'skilled' if skilled else 'naive  '} gate={'ON ' if gate else 'OFF'}  "
              f"turns_med={st.median(r['turns']):.0f}  "
              f"signed={r['signed']/n:.0%}  collapsed={r['collapsed']/n:.1%}  "
              f"left={r['left']/n:.1%}  "
              f"squeeze@close={r['sq_offered']/max(1,r['signed']+r['collapsed']):.0%}  "
              f"stars_med={st.median(r['stars']) if r['stars'] else 0}  "
              f"stars>=4={sum(1 for s in r['stars'] if s>=4)/sg:.0%}  "
              f"5*={sum(1 for s in r['stars'] if s==5)/sg:.1%}")
