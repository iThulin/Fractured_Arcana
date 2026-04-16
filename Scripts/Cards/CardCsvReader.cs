using Godot;
using System;
using System.IO;
using System.Text;
using System.Collections.Generic;

public sealed class HalfCsvData {
    public string Class="", Name="", EffectText="", ChannelText="";
    public int Mana=0;
    public string Keyword1="", Target1="", Type1=""; public float Amount1=0;
    public string Keyword2="", Target2="", Type2=""; public float Amount2=0;
    public string ChannelKeyword="", ChannelTarget="", ChannelType=""; public float ChannelAmount=0;
}
public sealed class RowCsvData { 
    public HalfCsvData Top=new(); 
    public HalfCsvData Bottom=new(); 
    public string Rarity="Common";  // <-- NEW
}

public static class CardCsvReader {
    public static List<RowCsvData> Load(string path, out List<string> diags){
        diags = new(); var rows = new List<RowCsvData>();
        var all = ReadAll(path); if (all.Count==0){ diags.Add("CSV empty"); return rows; }
        var h = all[0]; var idx = Index(h);
        for (int i=1;i<all.Count;i++){
            var f = all[i]; if (f.Length==0) continue;
            if (Get(f,idx,"Class [0]").Length==0) continue;
            var r = new RowCsvData();
            // Top (indices match your header)
            r.Top.Class = Get(f,idx,"Class [0]");
            r.Top.Name  = Get(f,idx,"Top Name [1]");
            r.Top.Mana  = GetInt(f,idx,"Mana [2]");
            r.Top.EffectText  = Get(f,idx,"Top Effect [3]");
            r.Top.ChannelText = Get(f,idx,"Top Channel [4]");
            r.Top.Keyword1 = Get(f,idx,"Keyword [5]");     r.Top.Target1 = Get(f,idx,"Target 1 [6]"); r.Top.Type1 = Get(f,idx,"Type 1 [7]"); r.Top.Amount1 = GetF(f,idx,"Amount 1 [8]");
            r.Top.Keyword2 = Get(f,idx,"Keyword 2 [9]");   r.Top.Target2 = Get(f,idx,"Target 2 [10]"); r.Top.Type2 = Get(f,idx,"Type 2 [11]"); r.Top.Amount2 = GetF(f,idx,"Amount 2 [12]");
            r.Top.ChannelKeyword = Get(f,idx,"Channel Keyword [13]");
            r.Top.ChannelTarget  = Get(f,idx,"Channel Target [14]");
            r.Top.ChannelType    = Get(f,idx,"Channel Type [15]");
            r.Top.ChannelAmount  = GetF(f,idx,"Channel Amount [16]");

            // Bottom
            r.Bottom.Class = r.Top.Class;
            r.Bottom.Name  = Get(f,idx,"Bottom Name [17]");
            r.Bottom.Mana  = GetInt(f,idx,"Mana [18]");
            r.Bottom.EffectText  = Get(f,idx,"Bottom Effect [19]");
            r.Bottom.ChannelText = Get(f,idx,"Bottom Channel [20]");
            r.Bottom.Keyword1 = Get(f,idx,"Keyword [21]");    r.Bottom.Target1 = Get(f,idx,"Target 1 [22]"); r.Bottom.Type1 = Get(f,idx,"Type 1 [23]"); r.Bottom.Amount1 = GetF(f,idx,"Amount 1 [24]");
            r.Bottom.Keyword2 = Get(f,idx,"Keyword 2 [25]");  r.Bottom.Target2 = Get(f,idx,"Target 2 [26]"); r.Bottom.Type2 = Get(f,idx,"Type 2 [27]"); r.Bottom.Amount2 = GetF(f,idx,"Amount 2 [28]");
            r.Bottom.ChannelKeyword = Get(f,idx,"Channel Keyword [29]");
            r.Bottom.ChannelTarget  = Get(f,idx,"Channel Target [30]");
            r.Bottom.ChannelType    = Get(f,idx,"Channel Type [31]");
            r.Bottom.ChannelAmount  = GetF(f,idx,"Channel Amount [32]");

            r.Rarity = Get(f, idx, "Rarity [33]");
            if (string.IsNullOrWhiteSpace(r.Rarity)) r.Rarity = "Common"; // default

            rows.Add(r);
        }
        return rows;

        // helpers
        static List<string[]> ReadAll(string p){
            using var fa = Godot.FileAccess.Open(p, Godot.FileAccess.ModeFlags.Read);
            var text = fa.GetAsText();
            var sr = new StringReader(text);
            var list = new List<string[]>(); string? line;
            while ((line = sr.ReadLine()) != null) list.Add(Split(line));
            return list;
        }
        static string[] Split(string line){
            var res = new List<string>(); var sb = new StringBuilder(); bool inQ=false;
            for (int i=0;i<line.Length;i++){
                char c = line[i];
                if (inQ){
                    if (c=='"'){ bool dbl=i+1<line.Length && line[i+1]=='"'; if (dbl){ sb.Append('"'); i++; } else inQ=false; }
                    else sb.Append(c);
                } else {
                    if (c==','){ res.Add(sb.ToString().Trim()); sb.Clear(); }
                    else if (c=='"') inQ=true; else sb.Append(c);
                }
            }
            res.Add(sb.ToString().Trim());
            return res.ToArray();
        }
        static Dictionary<string,int> Index(string[] header){ var m=new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase); for (int i=0;i<header.Length;i++) m[header[i]]=i; return m; }
        static string Get(string[] row, Dictionary<string,int> map, string key) => map.TryGetValue(key, out var i) && i<row.Length ? row[i] : "";
        static int GetInt(string[] row, Dictionary<string,int> map, string key) => int.TryParse(Get(row,map,key), out var v) ? v : 0;
        static float GetF(string[] row, Dictionary<string,int> map, string key) => float.TryParse(Get(row,map,key), out var v) ? v : 0f;
    }
}
