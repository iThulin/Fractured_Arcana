import sys, os, re, importlib.util
root = sys.argv[1]
def load(path):
    spec = importlib.util.spec_from_file_location("m", path)
    src = open(path).read()
    # capture edit() pairs by executing with a recording edit()
    pairs = []
    g = {'__name__': 'x', 'sys': sys, 'os': os}
    def rec(rel, ps): pairs.append((rel, ps))
    src = src.replace('root = sys.argv[1]', 'root = "."')
    code = src.split('def edit(')[0]
    body = 'def edit' + src.split('def edit',1)[1]
    body = re.sub(r'def edit\(rel, pairs\):.*?print\("ok", rel\)\n', '', body, flags=re.S)
    g['edit'] = rec
    exec(body, g)
    return pairs
for script in sys.argv[2:]:
    for rel, ps in load(script):
        if rel.endswith('RegisterManager.cs'):
            continue   # rewritten whole; handled below
        p = os.path.join(root, rel); s = open(p, encoding='utf-8').read()
        for old, new in ps:
            assert s.count(new) == 1, (rel, new[:80])
            s = s.replace(new, old)
        open(p, 'w', encoding='utf-8').write(s); print("reverted", rel)
p = os.path.join(root, 'Scripts/Systems/Register/RegisterManager.cs'); s = open(p, encoding='utf-8').read()
for line in ['        HangWatchdog.Start();   // dev: freeze diagnostics (Scripts/Dev/HangWatchdog.cs)\n',
             '        HangWatchdog.Beat();\n']:
    assert s.count(line) == 1, line; s = s.replace(line, '')
s = s.replace('//                 (NoteCombatTurn), ReactionChart.cs, HangWatchdog.cs\n', '//                 (NoteCombatTurn), ReactionChart.cs\n')
open(p, 'w', encoding='utf-8').write(s); print("cleaned RegisterManager.cs")
