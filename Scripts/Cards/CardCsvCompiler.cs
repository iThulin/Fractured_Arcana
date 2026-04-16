using Godot;
using System;
using System.Collections.Generic;

public static class CardCsvCompiler
{
    public static Card Compile(RowCsvData row)
    {
        var card = new Card { CardName = row.Top.Name };
        card.TopHalf    = CompileHalf(row.Top, card);
        card.BottomHalf = CompileHalf(row.Bottom, card);
        
        // Parse rarity
        if (Enum.TryParse<CardRarity>(row.Rarity, ignoreCase: true, out var rarity))
            card.Rarity = rarity;
        else
            card.Rarity = CardRarity.Common;
        
        return card;
    }

static CardHalf CompileHalf(HalfCsvData src, Card owner)
{
    if (string.IsNullOrWhiteSpace(src.Name)) return null;

    // ✅ Normalize and parse School from CSV Class column
    var rawClass = (src.Class ?? "").Trim().Trim('\uFEFF');
    if (!Enum.TryParse<CardSchool>(rawClass, ignoreCase: true, out var school))
    {
        GD.PrintErr($"Unknown Class '{src.Class}' (normalized '{rawClass}') for card '{src.Name}'. Defaulting to Engineer.");
        school = CardSchool.Tinker;
    }

    var k1 = Canon.Kw(src.Keyword1);
    var t1 = Canon.Tgt(src.Target1);
    var y1 = Canon.Typ(src.Type1);

    var k2 = Canon.Kw(src.Keyword2);
    var t2 = Canon.Tgt(src.Target2);
    var y2 = Canon.Typ(src.Type2);

    var ck = Canon.Kw(src.ChannelKeyword);
    var ct = Canon.Tgt(src.ChannelTarget);
    var cy = Canon.Typ(src.ChannelType);

    var half = new CardHalf {
        OwnerCard = owner,
        Name = src.Name,
        Speed = InferSpeed(k1,k2,src.EffectText),
        Costs = new ICost[]{ new ManaCost(src.Mana) },
        Targeting = BuildTarget(k1,t1,y1, (int)src.Amount1) ?? BuildTarget(k2,t2,y2, (int)src.Amount2) ?? FallbackTarget(src.EffectText),
        Effects = BuildEffects(k1,y1,(int)src.Amount1, k2,y2,(int)src.Amount2, src.EffectText),

        // ✅ set school on the runtime half
        School = school
    };

    half.RulesText = src.EffectText;

    if (!string.IsNullOrEmpty(ck) || !string.IsNullOrWhiteSpace(src.ChannelText))
    {
        half.ChannelVariant = new CardHalf {
            OwnerCard = owner,
            Name = src.Name + " (Channel)",
            Speed = PlaySpeed.Reaction,
            Costs = new ICost[]{ new ManaCost(src.Mana) },
            Targeting = BuildTarget(ck,ct,cy, (int)src.ChannelAmount) ?? FallbackTarget(src.ChannelText),
            Effects = BuildEffects(ck,cy,(int)src.ChannelAmount, "", "", 0, src.ChannelText),

            // ✅ carry school onto the channel variant
            School = school
        };

        // ✅ set channel rules text for UI
        half.ChannelVariant.RulesText = src.ChannelText;
    }

    return half;
}

    static PlaySpeed InferSpeed(string k1, string k2, string text){
        if (k1=="COUNTER" || k2=="COUNTER" || text.Contains("reflect", StringComparison.OrdinalIgnoreCase)) return PlaySpeed.Reaction;
        if (k1=="MOVE" || k2=="MOVE" || text.Contains("Move", StringComparison.OrdinalIgnoreCase)) return PlaySpeed.Instant;
        if (text.Contains("shield", StringComparison.OrdinalIgnoreCase)) return PlaySpeed.Instant;
        return PlaySpeed.Sorcery;
    }

    static ITargetSelector BuildTarget(string kw, string tgt, string type, int amt){
        if (string.IsNullOrEmpty(tgt)) return null;
        if (tgt.StartsWith("AOE_")){
            int r = int.TryParse(tgt.Split('_')[1], out var v) ? v : 1;
            return new SelectAreaTarget(r, enemiesOnly:true, tiles:false);
        }
        if (tgt.StartsWith("RANGE_")){
            int r = int.TryParse(tgt.Split('_')[1], out var v) ? v : 1;
            return new SelectUnitTarget(true, r, true, false);
        }
        return tgt switch {
            "LOS"    => new SelectUnitTarget(true, 6, true, false),
            "MELEE"  => new SelectUnitTarget(true, 1, false, false),
            "ENEMY"  => new SelectUnitTarget(true),
            "ANY"    => new SelectUnitTarget(false),
            "SELF" or "PLAYER" => new SelectSelfTarget(),
            "TILE"   => new SelectTileTarget(4),
            "GLOBAL" => new SelectGlobalTarget(),
            "CURRENT"=> new SelectByTagTarget(type.Length>0?type:"MECH", false),
            _ => null
        };
    }

    static ITargetSelector FallbackTarget(string text){
        var t = (text??"").ToLowerInvariant();
        if (t.Contains("damage")) return new SelectUnitTarget(true,4,true,false);
        if (t.Contains("move"))   return new SelectTileTarget(4);
        if (t.Contains("summon") || t.Contains("deploy")) return new SelectAreaTarget(1,false,true);
        return null;
    }

    static IEffect[] BuildEffects(string k1,string y1,int a1, string k2,string y2,int a2, string text){
        var list = new List<IEffect>();
        AddEffect(list,k1,y1,a1,text);
        AddEffect(list,k2,y2,a2,text);
        if (list.Count==0) list.Add(TextToEffect(text));
        return list.ToArray();
    }

    static void AddEffect(List<IEffect> list, string kw, string type, int amt, string srcText){
        if (string.IsNullOrWhiteSpace(kw)) return;
        switch(kw){
            case "DEAL": list.Add(new DealDamageEffect(amt).WithTag("Damage")); break;
            case "MOVE": list.Add(new DashEffect(Math.Max(1,amt)).WithTag("Movement")); break;
            case "GAIN":
                if (type=="ARMOR") list.Add(new GiveShieldEffect(amt).WithTag("Defense")); // quick shared; add armor effect later
                else if (type=="SHIELD") list.Add(new GiveShieldEffect(amt).WithTag("Defense"));
                else if (type=="MANA") list.Add(new DrawCardsEffect(0).WithTag("Resource")); // replace with GainManaEffect when you add it
                else list.Add(new NoOpEffect($"{kw} {type} {amt}"));
                break;
            case "BUFF": list.Add(new NoOpEffect($"BUFF {type} {amt}")); break;
            case "IMMOBILIZE": list.Add(new NoOpEffect("Immobilize")); break;
            case "SUMMON": list.Add(new SummonEffect(type.Length>0?type:"Token", Math.Max(1,amt)).WithTag("Summon")); break;
            case "DRAW": list.Add(new DrawCardsEffect(Math.Max(1,amt)).WithTag("CardDraw")); break;
            case "TELEPORT": list.Add(new NoOpEffect($"Teleport {amt}")); break;
            case "COUNTER": list.Add(new NoOpEffect("Counter").WithTag("Counter")); break;
            default: list.Add(TextToEffect(srcText)); break;
        }
    }

    static IEffect TextToEffect(string text){
        var t = (text??"").ToLowerInvariant();
        int n = System.Text.RegularExpressions.Regex.Match(text, @"\d+").Success
            ? int.Parse(System.Text.RegularExpressions.Regex.Match(text, @"\d+").Value) : 1;
        if (t.Contains("deal") && t.Contains("damage")) return new DealDamageEffect(n).WithTag("Damage");
        if (t.Contains("move")) return new DashEffect(n).WithTag("Movement");
        if (t.Contains("draw")) return new DrawCardsEffect(n).WithTag("CardDraw");
        if (t.Contains("summon") || t.Contains("deploy")) return new SummonEffect("Token", Math.Max(1,n)).WithTag("Summon");
        if (t.Contains("shield")) return new GiveShieldEffect(n).WithTag("Defense");
        return new NoOpEffect(text);
    }
}
