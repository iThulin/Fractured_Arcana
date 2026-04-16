using Godot;
using System;
using System.Collections.Generic;

public static class Canon {
    static readonly Dictionary<string,string> KW = new(StringComparer.OrdinalIgnoreCase){
        ["Deal"]="DEAL", ["Move"]="MOVE", ["Gain"]="GAIN", ["Buff"]="BUFF",
        ["Summon"]="SUMMON", ["Deploy"]="SUMMON", ["Immobilize"]="IMMOBILIZE", ["Draw"]="DRAW",
        ["Teleport"]="TELEPORT", ["Counter"]="COUNTER"
    };
    static readonly Dictionary<string,string> TGT = new(StringComparer.OrdinalIgnoreCase){
        ["Enemy"]="ENEMY", ["Any"]="ANY", ["Self"]="SELF", ["Player"]="PLAYER", ["Tile"]="TILE",
        ["LOS"]="LOS", ["Melee"]="MELEE", ["AOE_1"]="AOE_1", ["AOE_2"]="AOE_2", ["Range_1"]="RANGE_1",
        ["Current"]="CURRENT", ["Global"]="GLOBAL"
    };
    static readonly Dictionary<string,string> TYP = new(StringComparer.OrdinalIgnoreCase){
        ["Armor"]="ARMOR", ["Shield"]="SHIELD", ["Mana"]="MANA",
        ["Mech"]="MECH", ["Drone"]="DRONE", ["Turret"]="TURRET", ["Cannon"]="CANNON",
        ["Trap"]="TRAP", ["Shield_wall"]="SHIELD_WALL", ["Tesla_trap"]="TESLA_TRAP"
    };
    public static string Kw(string s)=> Map(s,KW);
    public static string Tgt(string s)=> Map(s,TGT);
    public static string Typ(string s)=> Map(s,TYP);

    static string Map(string s, Dictionary<string,string> map){
        s = (s??"").Trim(); if (s.Length==0) return "";
        if (map.TryGetValue(s, out var v)) return v;
        var up = s.ToUpperInvariant().Replace(' ','_');
        if (map.TryGetValue(up, out v)) return v;
        if (up.StartsWith("AOE_") || up.StartsWith("RANGE_")) return up;
        return up; // tolerant fallback
    }
}
