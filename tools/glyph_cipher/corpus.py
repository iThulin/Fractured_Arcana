import json, glob, os
REG = {
 'gain_weave':('Weave','GainWeaveEffect'),'damage_per_glyph':('Damage','DamagePerGlyphEffect'),
 'prepare_glyph':('Glyph','PrepareGlyphEffect'),'prepare_glyph_area':('Glyph','PrepareGlyphEffect'),
 'cascade_glyph':('Glyph','PrepareGlyphEffect'),'link_glyphs':('Glyph','LinkGlyphsEffect'),
 'rearm_glyphs':('Glyph','RearmGlyphsEffect'),'trigger_all_glyphs':('Glyph','TriggerAllGlyphsEffect'),
 'swap_glyphs':('Glyph','SwapGlyphsEffect'),'teleport_to_glyph':('Movement','TeleportToGlyphEffect'),
 'enchant_pillar':('Glyph','EnchantPillarEffect'),'reflect_ward':('Glyph','ReflectWardEffect'),
 'spell_anchor':('Glyph','SpellAnchorEffect'),'pull_to_glyph':('Movement','MoveToGlyphEffect'),
 'push_to_glyph':('Movement','MoveToGlyphEffect'),'dispel':('Control','DispelEffect'),
 'swap_units':('Movement','SwapUnitsEffect'),'geas':('Control','StatusApplyEffect'),
 'mana_tithe':('Control','StatusApplyEffect'),'dominate':('Control','DominateEffect'),
 'summon_illusion':('Summon','SummonIllusionEffect'),'grand_design_passive':('Glyph','GrandDesignPassiveLeafEffect'),
 'absolute_territory':('Control','AbsoluteTerritoryLeafEffect'),'apply_status':('Status','ApplyStatusEffect'),
 'move':('Movement','DashEffect'),'shield':('Defense','GiveShieldEffect'),'draw':('CardDraw','DrawCardsEffect'),
 'damage':('Damage','DealDamageEffect'),'push_aimed':('Displace','PushAimedEffect'),'scry':(None,'ScryEffect'),
}
COMPOSITE={'sequence','choose_one','conditional','for_each_target','retarget'}
INSCRIBE_CLASSES={'PrepareGlyphEffect','EnchantPillarEffect','ReflectWardEffect','SpellAnchorEffect'}
TAG2VERB={'Damage':'STRIKE','SelfDamage':'STRIKE','Control':'BIND','Status':'BIND','Debuff':'BIND',
 'Movement':'MOVE','Displace':'MOVE','Defense':'WARD','Heal':'WARD','Buff':'WARD','Summon':'WARD',
 'CardDraw':'INVOKE','Mana':'INVOKE','Foresight':'INVOKE'}
CLASS_OVERRIDE={'ScryEffect':'INVOKE'}
IGNORE={'Weave'}
RING=['WARD','MOVE','INSCRIBE','INVOKE','BIND','STRIKE']

def leaves(e,out):
    if isinstance(e,list):
        for x in e: leaves(x,out)
        return
    if not isinstance(e,dict): return
    t=e.get('type')
    if t in COMPOSITE:
        for k in ('steps','do','then','else'):
            if k in e: leaves(e[k],out)
        for o in e.get('options',[]): leaves(o.get('effect'),out)
        return
    if t: out.append(t)
    for k,v in e.items():
        if k=='targeting': continue
        if isinstance(v,(list,dict)): leaves(v,out)

def verbs_of(types):
    vs=set()
    for t in types:
        tag,cls=REG[t]
        if cls in CLASS_OVERRIDE: vs.add(CLASS_OVERRIDE[cls]); continue
        if tag in IGNORE: continue
        if tag=='Glyph': vs.add('INSCRIBE' if cls in INSCRIBE_CLASSES else 'INVOKE'); continue
        vs.add(TAG2VERB[tag])
    return [v for v in RING if v in vs]

def target_of(t):
    ty=t.get('type')
    if ty in (None,'self','none','global'): return 'SELF'
    if ty in ('unit','unit_then_direction','unit_then_tile'):
        return 'ALLY' if t.get('friendlies_only') else 'ENEMY'
    return 'TILE'

import os
_HERE = os.path.dirname(os.path.abspath(__file__))
_CARDS = os.path.normpath(os.path.join(_HERE, "..", "..", "Data", "Cards"))

def load(d=_CARDS):
    rows=[]
    for f in sorted(glob.glob(os.path.join(d,'enchanter_*.json'))):
        j=json.load(open(f))
        for half in ('top','bottom'):
            h=j.get(half)
            if not h: continue
            out=[];leaves(h.get('effect'),out)
            rows.append(dict(id=j['id'],half=half,name=h['name'],
                             target=target_of(h.get('targeting') or {}),verbs=verbs_of(out)))
    return rows
