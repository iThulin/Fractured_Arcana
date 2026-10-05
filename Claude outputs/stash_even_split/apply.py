import pathlib
def edit(rel, pairs):
    p = pathlib.Path(rel); s = p.read_text()
    for o, n in pairs:
        assert s.count(o) == 1, (rel, o[:70], s.count(o))
        s = s.replace(o, n)
    p.write_text(s)

edit('Scripts/UI/DeckEditorUi.cs', [
 ('''        hbox.AddChild(div1);''',
  '''        div1.Visible = !compact;          // no class label to divide from in a stash row
        hbox.AddChild(div1);'''),
 ('''        topBlock.SizeFlagsHorizontal = compact ? SizeFlags.ExpandFill : SizeFlags.ShrinkBegin;''',
  '''        topBlock.SizeFlagsHorizontal = compact ? SizeFlags.ExpandFill : SizeFlags.ShrinkBegin;
        topBlock.SizeFlagsStretchRatio = 1f;   // even split with the bottom half in stash rows'''),
 ('''            botBlock.SizeFlagsHorizontal = compact ? SizeFlags.ExpandFill : SizeFlags.ShrinkBegin;''',
  '''            botBlock.SizeFlagsHorizontal = compact ? SizeFlags.ExpandFill : SizeFlags.ShrinkBegin;
            botBlock.SizeFlagsStretchRatio = 1f;'''),
 ('''        spacer.SizeFlagsHorizontal = SizeFlags.ExpandFill;''',
  '''        // Stash rows: the two halves take all the free width, split evenly, so the
        // buttons sit flush right and the names get the room. The spacer would
        // otherwise claim a third of it.
        spacer.SizeFlagsHorizontal = compact ? SizeFlags.Fill : SizeFlags.ExpandFill;
        spacer.Visible = !compact;'''),
])
print("ok")
