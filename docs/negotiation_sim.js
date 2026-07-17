// Monte Carlo tuning harness for Fractured Arcana negotiation v2.
// Faithful port of NegotiationState.cs / NpcArchetype.cs rules (2026-07-17).
// Run: node negotiation_sim.js [runsPerCell]
"use strict";
const fs = require("fs");
const path = require("path");

// ── Tuning knobs (mirror of the C# values; override via sensitivity runs) ──
const BASE_KNOBS = {
  cordialMax: 3, strainedMax: 7,
  // press stance modifiers
  irritatedBackfire: +1, waveringEase: -1, guardedResent: +1, expansiveEase: -1,
  intimWaveringPull: 2, intimWaveringTension: +2, intimGuardedTension: +1,
  // offering
  offerEagerPull: 2, offerEagerEase: -1,
  // npc turn
  poiseTrigger: 9, hostilePullSteps: 2, hardenedBonus: 1,
  // squeeze
  oddsCordial: 75, oddsStrained: 55, oddsHostile: 30,
  oddsWavering: 15, oddsIrritated: -15, oddsGuarded: -10, bristleTension: 2,
  // scoring
  scoreCordialBonus: 2, scoreHostilePenalty: -3,
  starT5: 8, starT4: 5, starT3: 2, starT2: -2,
  // school moves
  showOfPowerPull: 2, quietGroveEase: 2,
  // patience override (null = use encounter JSON)
  basePatienceDelta: 0,
  // economy experiment knobs
  schoolTokenBonus: 0,   // +N to each school-innate token
  baseOffering: 0,       // universal Offering floor
  basePress: 0,          // +N Persuade for everyone (generalist floor)
};

// ── Data ────────────────────────────────────────────────────────────────
const ENC_DIR = "/home/claude/out/Data/Negotiations";
const ENCOUNTERS = {};
for (const f of fs.readdirSync(ENC_DIR)) {
  const d = JSON.parse(fs.readFileSync(path.join(ENC_DIR, f), "utf8"));
  ENCOUNTERS[d.id] = d;
}
const ARCH_ENCOUNTER = {
  Merchant: "generic_merchant", Commander: "frontier_wilds_commander",
  Scholar: "generic_scholar", Opportunist: "generic_opportunist",
  Idealist: "generic_idealist", Survivor: "generic_survivor",
};
const SCHOOLS = ["Adept","Elementalist","Druid","Necromancer","Tinker","Enchanter","Arcanist","Chronomancer"];
const PRESS = ["Charm","Persuade","Connections","Intimidate","Demonstration"];

const DEFAULT_NPC_POOL = {
  Merchant:[2,2,1], Commander:[3,1,1], Scholar:[1,2,2],
  Opportunist:[2,3,0], Idealist:[1,1,2], Survivor:[2,1,2],
};

function tensionDelta(arch, tok) { // ArchetypeBehavior.GetTensionDelta
  const T = {
    Charm:      {Idealist:-2, Commander:0, _:-1},
    Intimidate: {Idealist:10, Commander:1, Scholar:3, _:2},
    Persuade:   {Scholar:-2, Opportunist:0, _:-1},
    Insight:    {_:0}, Connections:{_:-1}, Patience:{_:0},
    Offering:   {Merchant:-2, _:-1},
    Demonstration:{Commander:-1, Scholar:-1, Idealist:1, _:0},
  }[tok];
  return T[arch] ?? T._;
}
function schoolPool(school) {
  const p = {Charm:0,Intimidate:0,Persuade:0,Insight:0,Connections:0,Patience:0,Offering:0,Demonstration:0};
  switch (school) {
    case "Enchanter":    p.Charm++; p.Connections++; break;
    case "Arcanist":     p.Persuade++; p.Insight++; break;
    case "Necromancer":  p.Intimidate++; p.Persuade++; break;
    case "Elementalist": p.Intimidate++; p.Demonstration++; break;
    case "Tinker":       p.Offering++; break;
    case "Chronomancer": p.Patience++; p.Insight++; break;
    default:             p.Persuade++; break; // Adept, Druid
  }
  if (p.Demonstration === 0) p.Demonstration++;
  return p;
}
function rollStance(zone) {
  const bags = {
    Cordial:  ["Expansive","Expansive","Eager","Wavering","Wavering","Guarded"],
    Hostile:  ["Irritated","Irritated","Guarded","Guarded","Eager","Wavering"],
    Strained: ["Eager","Eager","Guarded","Guarded","Wavering","Irritated"],
  }[zone];
  return bags[(Math.random()*bags.length)|0];
}
const GIFT = {Merchant:"Offering",Commander:"Demonstration",Scholar:"Insight",
  Opportunist:"Connections",Idealist:"Charm",Survivor:"Patience"};

// ── Engine ──────────────────────────────────────────────────────────────
function newTable(archetype, school, K) {
  const enc = ENCOUNTERS[ARCH_ENCOUNTER[archetype]];
  const terms = enc.terms.map(t => ({
    id: t.id, fav: t.favorPlayer, hidden: !!t.isHidden,
    gold: t.goldDelta|0, rep: t.reputationDelta|0,
    pos: Math.max(-2, Math.min(2, t.startingPosition ?? -1)),
    w: t.weight || 2, locked: false,
  }));
  const [res, gui, poi] = [
    enc.npcResolve ?? DEFAULT_NPC_POOL[archetype][0],
    enc.npcGuile   ?? DEFAULT_NPC_POOL[archetype][1],
    enc.npcPoise   ?? DEFAULT_NPC_POOL[archetype][2],
  ];
  const basePool = schoolPool(school);
  if (K.schoolTokenBonus) for (const k of Object.keys(basePool))
    if (basePool[k] > 0 && k !== "Demonstration") basePool[k] += K.schoolTokenBonus;
  if (K.baseOffering) basePool.Offering = Math.max(basePool.Offering, K.baseOffering);
  if (K.basePress) basePool.Persuade += K.basePress;
  const s = {
    K, archetype, school, terms,
    tension: Math.max(1, Math.min(10, enc.startingTension)),
    patience: Math.max(1, enc.basePatience + K.basePatienceDelta),
    turn: 0, pool: basePool,
    npc: {Resolve: res, Guile: gui, Poise: poi},
    stance: null, next: null, nextKnown: false,
    hardened: false, gift: false, squeezeSpent: false,
    resolved: false, accepted: false, walked: false, collapsed: false,
    schoolMoveUsed: false, omni: false, freeOffer: false, rewind: null,
    played: {}, tensionMax: 0, squeezeOffered: 0, squeezeHeld: 0, squeezeBlinked: 0,
  };
  s.stance = rollStance(zone(s)); s.next = rollStance(zone(s));
  s.tensionMax = s.tension;
  return s;
}
const zone = s => s.tension <= s.K.cordialMax ? "Cordial" : s.tension <= s.K.strainedMax ? "Strained" : "Hostile";
const pullable = s => s.terms.filter(t => !t.hidden && !t.locked && t.pos < 2);
function pull(s, t, steps, byPlayer) { t.pos = Math.max(-2, Math.min(2, t.pos + (byPlayer ? steps : -steps))); }
function applyTension(s, d) {
  s.tension = Math.max(1, Math.min(10, s.tension + d));
  s.tensionMax = Math.max(s.tensionMax, s.tension);
  s.terms.forEach(t => t.locked = false);
  if (zone(s) === "Hostile") {
    const guard = s.terms.filter(t => !t.hidden && t.pos > 0).sort((a,b)=>b.pos*b.w-a.pos*a.w)[0];
    if (guard) guard.locked = true;
  }
  if (s.tension >= 10) { s.resolved = true; s.collapsed = true; }
}
function advanceStance(s) { s.stance = s.next; s.next = rollStance(zone(s)); s.nextKnown = s.omni; }
function npcTurn(s) {
  if (s.tension >= s.K.poiseTrigger && s.npc.Poise > 0) { s.npc.Poise--; applyTension(s, -1); return; }
  const target = s.terms.filter(t => !t.hidden && t.pos >= 0 && t.pos > -2)
    .sort((a,b)=>b.pos*b.w-a.pos*a.w)[0];
  if (target && s.npc.Resolve > 0) {
    s.npc.Resolve--;
    const steps = (zone(s)==="Hostile" ? s.K.hostilePullSteps : 1) + (s.hardened ? s.K.hardenedBonus : 0);
    s.hardened = false; pull(s, target, steps, false); return;
  }
  if (s.npc.Guile > 0) {
    s.npc.Guile--;
    const g = s.terms.filter(t=>!t.hidden&&t.pos>-2).sort((a,b)=>a.pos*a.w-b.pos*b.w)[0];
    if (g) pull(s, g, 1, false); else applyTension(s, +1);
    return;
  }
  if (zone(s)==="Cordial" && !s.gift) { s.gift = true; s.pool[GIFT[s.archetype]]++; return; }
}
function finishAction(s) {
  if (s.resolved) return;
  s.turn++;
  npcTurn(s);
  if (s.resolved) return;
  s.patience--;
  if (s.patience <= 0) { s.resolved = true; return; }   // TheyLeft
  advanceStance(s);
}
function captureRewind(s) {
  if (s.school !== "Chronomancer") return;
  s.rewind = {
    tension: s.tension, patience: s.patience, turn: s.turn,
    stance: s.stance, next: s.next, nextKnown: s.nextKnown,
    hardened: s.hardened, gift: s.gift, squeezeSpent: s.squeezeSpent,
    pool: {...s.pool}, npc: {...s.npc},
    terms: s.terms.map(t => ({pos:t.pos, hidden:t.hidden, locked:t.locked})),
  };
}
function playPress(s, tok, target) {
  if (s.pool[tok] <= 0 || !pullable(s).includes(target)) return false;
  captureRewind(s); s.pool[tok]--; s.played[tok]=(s.played[tok]||0)+1;
  if (s.archetype === "Idealist" && tok === "Intimidate") { s.resolved = true; return true; } // instant walkaway
  const base = tensionDelta(s.archetype, tok);
  let steps = 1, delta = base;
  if (tok === "Intimidate") {
    if (s.stance === "Wavering") { steps = s.K.intimWaveringPull; delta = base + s.K.intimWaveringTension; }
    else if (s.stance === "Guarded") { delta = base + s.K.intimGuardedTension; s.hardened = true; }
  } else {
    if (s.stance === "Irritated") { steps = 0; delta = s.K.irritatedBackfire; }
    else if (s.stance === "Wavering") delta = base + s.K.waveringEase;
    else if (s.stance === "Guarded") delta = base + s.K.guardedResent;
    else if (s.stance === "Expansive") delta = base + s.K.expansiveEase;
  }
  if (steps > 0) pull(s, target, steps, true);
  applyTension(s, delta);
  finishAction(s);
  return true;
}
function playOffering(s, target) {
  if (s.pool.Offering <= 0 || !pullable(s).includes(target)) return false;
  captureRewind(s); s.pool.Offering--; s.played.Offering=(s.played.Offering||0)+1;
  if (s.freeOffer) s.freeOffer = false; else s.npc.Resolve++;
  const base = tensionDelta(s.archetype, "Offering");
  let steps = 1, delta = base;
  if (s.stance === "Eager") { steps = s.K.offerEagerPull; delta = base + s.K.offerEagerEase; }
  else if (s.stance === "Guarded") delta = 0;
  pull(s, target, steps, true);
  applyTension(s, delta);
  finishAction(s);
  return true;
}
function playInsightFlip(s) {
  if (s.pool.Insight <= 0) return false;
  captureRewind(s); s.pool.Insight--; s.played.Insight=(s.played.Insight||0)+1;
  const h = s.terms.find(t => t.hidden);
  if (h) h.hidden = false;
  finishAction(s); return true;
}
function playInsightRead(s) {
  if (s.pool.Insight <= 0) return false;
  captureRewind(s); s.pool.Insight--; s.played.Insight=(s.played.Insight||0)+1;
  s.nextKnown = true;
  finishAction(s); return true;
}
function playPatience(s) {
  if (s.pool.Patience <= 0) return false;
  captureRewind(s); s.pool.Patience--; s.played.Patience=(s.played.Patience||0)+1;
  s.turn++;
  advanceStance(s); return true;
}
function playPass(s) { captureRewind(s); finishAction(s); return true; }
function useSchoolMove(s, arg) {
  if (s.resolved || s.schoolMoveUsed) return false;
  switch (s.school) {
    case "Chronomancer": {
      if (!s.rewind) return false;
      s.schoolMoveUsed = true; const r = s.rewind; s.rewind = null;
      s.tension = r.tension; s.patience = r.patience; s.turn = r.turn;
      s.stance = r.stance; s.next = r.next; s.nextKnown = r.nextKnown;
      s.hardened = r.hardened; s.gift = r.gift; s.squeezeSpent = r.squeezeSpent;
      s.pool = {...r.pool}; s.npc = {...r.npc};
      s.terms.forEach((t,i)=>{ t.pos=r.terms[i].pos; t.hidden=r.terms[i].hidden; t.locked=r.terms[i].locked; });
      return true;
    }
    case "Necromancer": { s.schoolMoveUsed = true; s.nextKnown = true;
      const h = s.terms.find(t=>t.hidden); if (h) h.hidden = false; return true; }
    case "Enchanter": s.schoolMoveUsed = true; s.stance = arg || "Eager"; return true;
    case "Arcanist": s.schoolMoveUsed = true; s.omni = true; s.nextKnown = true; return true;
    case "Druid": s.schoolMoveUsed = true; applyTension(s, -s.K.quietGroveEase);
      if (!s.resolved) advanceStance(s); return true;
    case "Tinker": s.schoolMoveUsed = true; s.pool.Offering++; s.freeOffer = true; return true;
    case "Adept": s.schoolMoveUsed = true; s.pool[arg || "Charm"]++; return true;
    case "Elementalist": {
      const t = arg; if (!t || !pullable(s).includes(t)) return false;
      s.schoolMoveUsed = true; pull(s, t, s.K.showOfPowerPull, true);
      if (s.npc.Resolve > 0) s.npc.Resolve--;
      applyTension(s, +1); if (!s.resolved) finishAction(s); return true;
    }
  }
  return false;
}
function shake(s, policy) {
  // squeeze
  const target = s.terms.filter(t=>!t.hidden&&t.pos>-2).sort((a,b)=>b.pos*b.w-a.pos*a.w)[0];
  if (s.squeezeSpent || !target) { s.resolved = true; s.accepted = true; return; }
  s.squeezeOffered = 1;
  let odds = zone(s)==="Cordial"?s.K.oddsCordial:zone(s)==="Hostile"?s.K.oddsHostile:s.K.oddsStrained;
  if (s.stance==="Wavering") odds += s.K.oddsWavering;
  if (s.stance==="Irritated") odds += s.K.oddsIrritated;
  if (s.stance==="Guarded") odds += s.K.oddsGuarded;
  odds = Math.max(5, Math.min(95, odds));
  const holdIt = policy.squeeze(s, odds, target);
  if (!holdIt) { pull(s, target, 1, false); s.resolved = true; s.accepted = true; return; } // concede
  s.squeezeHeld = 1; s.squeezeSpent = true;
  if (Math.random()*100 < odds) { s.squeezeBlinked = 1; s.resolved = true; s.accepted = true; return; }
  applyTension(s, s.K.bristleTension);
  // continues (or collapsed inside applyTension)
}
function playerFraction(t) { const pulled = (t.pos + 2) / 4; return t.fav ? pulled : 1 - pulled; }
function score(s) {
  let sc = s.terms.reduce((a,t)=>a+t.pos*t.w, 0);
  sc += zone(s)==="Cordial"?s.K.scoreCordialBonus:zone(s)==="Hostile"?s.K.scoreHostilePenalty:0;
  return sc;
}
function stars(s) { const x = score(s); const K=s.K;
  return x>=K.starT5?5:x>=K.starT4?4:x>=K.starT3?3:x>=K.starT2?2:1; }
function gold(s) {
  let g = s.terms.reduce((a,t)=>a+t.gold*playerFraction(t), 0);
  g *= zone(s)==="Cordial"?1.2:zone(s)==="Hostile"?0.8:1;
  return Math.round(g);
}

// ── Bot policies ────────────────────────────────────────────────────────
const bestTarget = s => pullable(s).sort((a,b)=>(2-b.pos)*b.w-(2-a.pos)*a.w)[0];
const pressTokens = s => PRESS.filter(t => s.pool[t] > 0 && !(s.archetype==="Idealist" && t==="Intimidate"));

const POLICIES = {
  naive: { // presses the biggest clause with whatever's in hand; no reads, no timing
    act(s) {
      const t = bestTarget(s);
      const press = pressTokens(s);
      if (t && press.length) return playPress(s, press[0], t);
      if (t && s.pool.Offering > 0) return playOffering(s, t);
      if (!s.schoolMoveUsed) { // uses it mindlessly, immediately
        if (useSchoolMove(s, s.school==="Elementalist"?bestTarget(s):s.school==="Adept"?"Persuade":"Eager")) return true;
      }
      return "shake";
    },
    squeeze: () => true, // always hold — never leaves value on the table
  },
  greedy: { // dumps everything as fast as possible, then shakes
    act(s) {
      const t = bestTarget(s);
      if (t && s.pool.Offering > 0) return playOffering(s, t);
      const press = pressTokens(s);
      if (t && press.length) return playPress(s, press[press.length-1], t);
      return "shake";
    },
    squeeze: () => true,
  },
  skilled: { // stance-aware timing, insight use, school move at the right moment
    act(s) {
      const t = bestTarget(s);
      const sc = score(s);
      // close when the package is good or the clock is dying
      const tokensLeft = PRESS.concat(["Offering"]).reduce((a,k)=>a+s.pool[k],0);
      if (sc >= s.K.starT4 && zone(s) !== "Hostile") return "shake";
      if (s.patience <= 2 && (sc >= 0 || tokensLeft === 0)) return "shake";
      if (tokensLeft === 0 && s.pool.Insight === 0 && s.pool.Patience === 0) return "shake";
      // school move timing
      if (!s.schoolMoveUsed) {
        if (s.school==="Arcanist") { useSchoolMove(s); return true; }
        if (s.school==="Necromancer" && s.terms.some(x=>x.hidden)) { useSchoolMove(s); return true; }
        if (s.school==="Enchanter" && s.stance!=="Eager" && s.pool.Offering>0) { useSchoolMove(s,"Eager"); return true; }
        if (s.school==="Druid" && s.tension >= 7) { useSchoolMove(s); return true; }
        if (s.school==="Tinker" && s.stance==="Eager") { useSchoolMove(s); return true; }
        if (s.school==="Elementalist" && t && zone(s)==="Cordial") { useSchoolMove(s, t); return true; }
        if (s.school==="Adept" && s.pool.Offering===0) { useSchoolMove(s, "Offering"); return true; }
        if (s.school==="Chronomancer" && s.rewind && s.stance==="Irritated" && zone(s)==="Hostile") { useSchoolMove(s); return true; }
      }
      // flip hidden info early
      if (s.pool.Insight > 0 && s.terms.some(x=>x.hidden)) return playInsightFlip(s);
      // stance play
      if (s.stance === "Eager" && s.pool.Offering > 0 && t) return playOffering(s, t);
      if (s.stance === "Irritated") {
        if (s.pool.Patience > 0) return playPatience(s);
        if (s.pool.Insight > 0) return playInsightRead(s);
        return playPass(s);
      }
      if ((s.stance === "Wavering" || s.stance === "Expansive") && t) {
        const press = pressTokens(s);
        if (press.length) return playPress(s, press[0], t);
      }
      // default: cheapest safe pull, or wait for a better moment
      if (t) {
        const press = pressTokens(s).filter(k=>k!=="Intimidate");
        if (press.length) return playPress(s, press[0], t);
        if (s.pool.Offering > 0) return playOffering(s, t);
      }
      if (s.pool.Patience > 0) return playPatience(s);
      return playPass(s);
    },
    squeeze: (s, odds, target) => {
      if (odds >= 60) return true;                      // hold
      if (target.pos * target.w <= 2) return false;     // cheap concession — sign
      return zone(s) !== "Hostile";                     // mid odds: hold unless hot
    },
  },
};

// ── Runner ──────────────────────────────────────────────────────────────
function runOne(archetype, school, policyName, K) {
  const s = newTable(archetype, school, K);
  const policy = POLICIES[policyName];
  let guard = 200;
  while (!s.resolved && guard-- > 0) {
    const r = policy.act(s);
    if (r === "shake") shake(s, policy);
    else if (r === false) shake(s, policy); // couldn't act — close out
  }
  const outcome = s.accepted ? "Signed" : s.collapsed ? "Collapsed" : s.walked ? "Walked" : "TheyLeft";
  return {
    outcome, stars: s.accepted ? stars(s) : 0, score: score(s), gold: s.accepted ? gold(s) : 0,
    turns: s.turn, zone: zone(s), tensionMax: s.tensionMax,
    played: s.played, squeezeOffered: s.squeezeOffered, squeezeHeld: s.squeezeHeld, squeezeBlinked: s.squeezeBlinked,
  };
}
function aggregate(rows) {
  const n = rows.length, out = {};
  const signed = rows.filter(r=>r.outcome==="Signed");
  out.n = n;
  out.signedPct = +(100*signed.length/n).toFixed(1);
  out.collapsedPct = +(100*rows.filter(r=>r.outcome==="Collapsed").length/n).toFixed(1);
  out.theyLeftPct = +(100*rows.filter(r=>r.outcome==="TheyLeft").length/n).toFixed(1);
  out.avgTurns = +(rows.reduce((a,r)=>a+r.turns,0)/n).toFixed(1);
  out.avgStars = signed.length ? +(signed.reduce((a,r)=>a+r.stars,0)/signed.length).toFixed(2) : 0;
  out.fiveStarPct = +(100*signed.filter(r=>r.stars===5).length/n).toFixed(1);
  out.avgGold = signed.length ? Math.round(signed.reduce((a,r)=>a+r.gold,0)/signed.length) : 0;
  out.cordialClosePct = signed.length ? +(100*signed.filter(r=>r.zone==="Cordial").length/signed.length).toFixed(1) : 0;
  out.squeezeBlinkPct = (()=>{const h=rows.filter(r=>r.squeezeHeld); return h.length? +(100*h.filter(r=>r.squeezeBlinked).length/h.length).toFixed(1):0;})();
  return out;
}

module.exports = { BASE_KNOBS, ARCH_ENCOUNTER, SCHOOLS, runOne, aggregate };
if (require.main === module) main();
function main() {
const N = parseInt(process.argv[2] || "2000", 10);
const ARCHES = Object.keys(ARCH_ENCOUNTER);

// 1. Policy × archetype sweep (school = Enchanter as social baseline)
console.log("== POLICY × ARCHETYPE (school=Enchanter, n=" + N + " each) ==");
console.log("arch,policy,signed%,collapsed%,left%,avgTurns,avgStars,5star%,avgGold,cordial%");
for (const a of ARCHES) for (const p of Object.keys(POLICIES)) {
  const rows = Array.from({length:N},()=>runOne(a,"Enchanter",p,BASE_KNOBS));
  const g = aggregate(rows);
  console.log([a,p,g.signedPct,g.collapsedPct,g.theyLeftPct,g.avgTurns,g.avgStars,g.fiveStarPct,g.avgGold,g.cordialClosePct].join(","));
}

// 2. School sweep (skilled policy, Merchant + Commander)
console.log("\n== SCHOOL × {Merchant,Commander} (skilled, n=" + N + ") ==");
console.log("school,arch,signed%,avgStars,5star%,avgTurns,avgGold");
for (const sc of SCHOOLS) for (const a of ["Merchant","Commander"]) {
  const rows = Array.from({length:N},()=>runOne(a,sc,"skilled",BASE_KNOBS));
  const g = aggregate(rows);
  console.log([sc,a,g.signedPct,g.avgStars,g.fiveStarPct,g.avgTurns,g.avgGold].join(","));
}

// 3. Sensitivity: key knobs, skilled+naive on Merchant
console.log("\n== SENSITIVITY (Merchant, n=" + N + ") ==");
const SWEEPS = [
  ["basePatienceDelta", [-2, 0, 2]],
  ["oddsStrained",      [45, 55, 65]],
  ["hostilePullSteps",  [1, 2]],
  ["starT4",            [4, 5, 6]],
  ["offerEagerPull",    [1, 2]],
  ["scoreCordialBonus", [1, 2, 3]],
];
console.log("knob,value,policy,signed%,avgStars,5star%,avgTurns,collapsed%,blink%");
for (const [knob, values] of SWEEPS) for (const v of values) for (const p of ["naive","skilled"]) {
  const K = {...BASE_KNOBS, [knob]: v};
  const rows = Array.from({length:N},()=>runOne("Merchant","Enchanter",p,K));
  const g = aggregate(rows);
  console.log([knob,v,p,g.signedPct,g.avgStars,g.fiveStarPct,g.avgTurns,g.collapsedPct,g.squeezeBlinkPct].join(","));
}

// 4. Token usage under skilled play (is every token worth playing?)
console.log("\n== TOKEN USAGE (skilled, all archetypes pooled, n=" + N*3 + ") ==");
const usage = {};
for (const a of ARCHES) for (let i=0;i<N/2;i++) {
  const r = runOne(a,"Enchanter","skilled",BASE_KNOBS);
  for (const [k,v] of Object.entries(r.played)) usage[k]=(usage[k]||0)+v;
}
console.log(JSON.stringify(usage));

}
