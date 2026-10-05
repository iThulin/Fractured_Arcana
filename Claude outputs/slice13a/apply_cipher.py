import pathlib
p = pathlib.Path('Scripts/Systems/Combat/Glyphs/GlyphCipherTags.cs'); s = p.read_text(encoding='utf-8')
o = '''        foreach (var e in half.Effects)
            Walk(e, ref verbs, 0);
        return verbs;
    }'''
n = '''        foreach (var e in half.Effects)
            Walk(e, ref verbs, 0);
        // A half whose only effect is Weave (Echo of Breaking, slice 13a) still needs a
        // verb: Weave is ignored above to keep it off the 9 halves that merely ride it.
        if (verbs == CipherVerb.None)
            foreach (var e in half.Effects)
                if (HasTag(e, "Weave", 0))
                { verbs = CipherVerb.Invoke; break; }
        return verbs;
    }

    private static bool HasTag(IEffect e, string tag, int depth)
    {
        if (e == null || depth > 8) return false;
        if (e.Tags != null && Array.IndexOf(e.Tags, tag) >= 0) return true;
        if (e.Children != null)
            foreach (var c in e.Children)
                if (HasTag(c, tag, depth + 1)) return true;
        return false;
    }'''
assert s.count(o) == 1
s = s.replace(o, n)
p.write_text(s, encoding='utf-8')
print("cipher ok")
