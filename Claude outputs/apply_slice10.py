#!/usr/bin/env python3
# Slice 10: once-per-fight halves, self-cast drops anywhere, opening camera frames the
# enemies, Reflex-window highlights count the Time Bank, Register seen flags persist.
import sys, os, json
root = sys.argv[1]
def edit(rel, pairs):
    p = os.path.join(root, rel)
    s = open(p, encoding='utf-8').read()
    for old, new in pairs:
        n = s.count(old)
        assert n == 1, f"{rel}: expected 1 match, got {n} for:\n{old}"
        s = s.replace(old, new)
    open(p, 'w', encoding='utf-8').write(s)
    print("ok", rel)

# ── A. Once per fight ─────────────────────────────────────────────────
edit('Scripts/Cards/CardRuntime.cs', [
('''public sealed class CardHalf : Ability
{''',
'''public sealed class CardHalf : Ability
{
    /// <summary>A fight-long effect ("for the rest of the fight"): once it has been cast,
    /// this half (every copy of this card's half) cannot be cast again this fight,
    /// because a second cast would buy nothing. JSON: "once_per_fight": true on the half
    /// (or set by an upgrade rung). Tracked on GameState.OncePerFightSpent.</summary>
    public bool OncePerFight;
'''),
])
edit('Scripts/Cards/Loader/JsonCardLoader.cs', [
('''        if (halfNode.TryGetProperty("tags", out var tagsElement)
            && tagsElement.ValueKind == JsonValueKind.Array)''',
'''        half.OncePerFight = halfNode.TryGetProperty("once_per_fight", out var opf)
                            && opf.ValueKind == JsonValueKind.True;

        if (halfNode.TryGetProperty("tags", out var tagsElement)
            && tagsElement.ValueKind == JsonValueKind.Array)'''),
])
edit('Scripts/Systems/GameStateManager.cs', [
('''    public StackItem LastResolvedItem;
    public int SpellsCastThisTurn = 0;''',
'''    public StackItem LastResolvedItem;
    public int SpellsCastThisTurn = 0;

    /// <summary>Once-per-fight halves already cast this fight (CardHalf.OncePerFight),
    /// keyed by card and half so every copy of the card shares the lock.</summary>
    public HashSet<string> OncePerFightSpent = new();

    public static string OncePerFightKey(CardHalf h)
    {
        var card = h?.OwnerCard;
        string id = !string.IsNullOrEmpty(card?.BlueprintId) ? card.BlueprintId : card?.CardName ?? h?.Name ?? "";
        string side = card != null && ReferenceEquals(card.BottomHalf, h) ? "bottom" : "top";
        return id + ":" + side;
    }

    public bool IsOncePerFightSpent(CardHalf h) =>
        h != null && h.OncePerFight && OncePerFightSpent.Contains(OncePerFightKey(h));'''),
])
edit('Scripts/Systems/Combat/Core/RulesManager.cs', [
('''    public static bool CanCast(Ability a, GameState s, Entity caster)
    {''',
'''    public static bool CanCast(Ability a, GameState s, Entity caster)
    {
        if (a is CardHalf once && s.IsOncePerFightSpent(once))
        {
            s.Log($"{once.Name} lasts the whole fight and has already been cast.");
            return false;
        }
'''),
('''        foreach (var c in a.Costs)
            c.Pay(s, caster);
''',
'''        foreach (var c in a.Costs)
            c.Pay(s, caster);

        if (a is CardHalf spent && spent.OncePerFight)
            s.OncePerFightSpent.Add(GameState.OncePerFightKey(spent));
'''),
])
edit('Scripts/Cards/CardUi.cs', [
('''    private bool HalfReactionLocked(CardHalf half)
        => _reactionWindow && half?.Speed != PlaySpeed.Reflex;''',
'''    private bool HalfReactionLocked(CardHalf half)
        => (_reactionWindow && half?.Speed != PlaySpeed.Reflex)
           || (HalfSpentProvider != null && half != null && HalfSpentProvider(half));

    /// <summary>Set by CombatManager: true for a once-per-fight half already cast this
    /// fight. Such a half reads as locked, exactly like a Studied half in a Reflex window.</summary>
    public static Func<CardHalf, bool> HalfSpentProvider;'''),
])

# ── B. Self / untargeted spells can be dropped anywhere ───────────────
edit('Scripts/Cards/CardDropHandler.cs', [
('''        var cardUi = DragPayloadManager.DraggedCard;
        bool isTop = DragPayloadManager.IsTopHalf;
        var tile = CurrentHoveredTile;
''',
'''        var cardUi = DragPayloadManager.DraggedCard;
        bool isTop = DragPayloadManager.IsTopHalf;
        var tile = CurrentHoveredTile;

        // 2026-10-04: a spell that lands on its caster (self or untargeted) does not
        // care where it is dropped. Off the board, on the vista ring, or on a tile that
        // cannot take a cast, it goes to the caster's own tile. A drop back over the
        // hand still cancels (the resolver returns null there).
        if (cardUi != null && SelfCastRedirect != null)
        {
            var redirected = SelfCastRedirect(isTop ? cardUi.TopHalf : cardUi.BottomHalf,
                                              GetViewport().GetMousePosition());
            if (redirected != null)
                tile = redirected;
        }
'''),
('''    public void TryDropCardOnTile()''',
'''    /// <summary>Set by CombatManager: the caster's tile for a self/untargeted half dropped
    /// anywhere but the hand; null otherwise (the drop resolves as before).</summary>
    public static System.Func<CardHalf, Vector2, HexTile> SelfCastRedirect;

    public void TryDropCardOnTile()'''),
])
edit('Scripts/UI/DeckUiManager.cs', [
('''	private Func<int> _getMana;
''',
'''	private Func<int> _getMana;

	/// <summary>True when a screen point is over the hand (a drop there cancels).</summary>
	public bool IsOverHand(Vector2 screenPos)
		=> handUIContainer != null && IsInstanceValid(handUIContainer)
		   && handUIContainer.GetGlobalRect().HasPoint(screenPos);
'''),
])

# ── C/D. CombatManager wiring: Time Bank in highlights, providers, camera ─
edit('Scripts/Systems/Combat/Core/CombatManager.cs', [
('''            deckUiManager.SetManaProvider(() => selectedUnit?.Stats.Mana ?? 0);''',
'''            // 2026-10-04: in a Reflex window the rules let banked Foresight pay (1:1),
            // and a full bank makes the first Reflex free (Rules.CanCast). The hand only
            // counted mana, so the window opened over a hand with nothing lit. Mirror
            // ManaCost.CanPay here.
            deckUiManager.SetManaProvider(() =>
            {
                var u = selectedUnit;
                if (u == null)
                    return 0;
                int m = u.Stats.Mana;
                if (State?.EnemyPhaseContext == true && u.Attunement is FateAttunement bank)
                {
                    if (bank.HasFreeReaction)
                        return 99;
                    m += Math.Max(0, bank.Charges);
                }
                return m;
            });
            CardUi.HalfSpentProvider = h => State != null && State.IsOncePerFightSpent(h);
            CardDropHandler.SelfCastRedirect = (h, pos) =>
            {
                if (h?.Targeting is not (SelectSelfTarget or SelectGlobalTarget))
                    return null;
                if (deckUiManager != null && IsInstanceValid(deckUiManager) && deckUiManager.IsOverHand(pos))
                    return null;
                return selectedUnit != null && IsInstanceValid(selectedUnit) ? selectedUnit.CurrentTile?.TileView : null;
            };'''),
('''        UninstallIntentTime();
        UninstallAnchorHooks();
    }''',
'''        UninstallIntentTime();
        UninstallAnchorHooks();
        CardUi.HalfSpentProvider = null;
        CardDropHandler.SelfCastRedirect = null;
    }'''),
('''        if (!_openingIntentsPlanned)
        {
            _openingIntentsPlanned = true;''',
'''        if (!_openingIntentsPlanned)
        {
            _openingIntentsPlanned = true;
            // Opening view (2026-10-04): frame the party AND the enemies. Deferred a
            // beat so it lands after the turn-start selection glide.
            GetTree().CreateTimer(0.2).Timeout += FrameOpeningView;'''),
('''    private void SelectNextLivingAfterDeath()
    {''',
'''    /// <summary>Round 1: point the camera from the party toward the enemies and pull back
    /// far enough that both are on screen. Spawn-zone centroids (OrientCameraForCombat)
    /// were not enough: reactive spawns and deployment variants put enemies elsewhere.</summary>
    private void FrameOpeningView()
    {
        if (!IsInstanceValid(this) || CombatCamera == null || !IsInstanceValid(CombatCamera))
            return;
        var party = playerUnits.Where(u => u != null && IsInstanceValid(u) && u.Stats.IsAlive
                                           && u.CurrentTile != null && !u.IsAwaitingArrival && !u.IsStructure)
                               .Select(u => u.GlobalPosition).ToList();
        var foes = enemyUnits.Where(u => u != null && IsInstanceValid(u) && u.Stats.IsAlive && u.CurrentTile != null)
                             .Select(u => u.GlobalPosition).ToList();
        if (party.Count == 0 || foes.Count == 0)
            return;
        CombatCamera.FrameUnits(party, foes);
    }

    private void SelectNextLivingAfterDeath()
    {'''),
])

edit('Scripts/UI/CameraController.cs', [
('''    public void FaceToward(Vector3 fromCenter, Vector3 towardPoint)
    {''',
'''    /// <summary>Opening view: face from the party toward the enemies, centred between
    /// them, zoomed out until every point fits (within the map's zoom ceiling).</summary>
    public void FrameUnits(System.Collections.Generic.IList<Vector3> party,
                           System.Collections.Generic.IList<Vector3> enemies)
    {
        if (!EnsureCameraNodes() || party == null || enemies == null || party.Count == 0 || enemies.Count == 0)
            return;

        Vector3 pc = Vector3.Zero, ec = Vector3.Zero;
        foreach (var p in party) pc += p;
        foreach (var e in enemies) ec += e;
        pc /= party.Count;
        ec /= enemies.Count;
        Vector3 center = pc.Lerp(ec, 0.5f);

        float extent = 0f;
        foreach (var p in party) extent = Mathf.Max(extent, new Vector2(p.X - center.X, p.Z - center.Z).Length());
        foreach (var e in enemies) extent = Mathf.Max(extent, new Vector2(e.X - center.X, e.Z - center.Z).Length());

        FaceToward(center, center + (ec - pc));
        float zoom = Mathf.Clamp(Mathf.Max(extent * 1.9f + 3f, MinSafeZoom()), MinZoom, _maxZoomDynamic);
        _zoomTarget = zoom;
        _camera.Position = new Vector3(0f, 0f, zoom);
        GD.Print($"[Camera] Opening view: center {center}, extent {extent:0.0}, zoom {zoom:0.0}.");
    }

    public void FaceToward(Vector3 fromCenter, Vector3 towardPoint)
    {'''),
])

# ── E. Register seen flags written through immediately ────────────────
edit('Scripts/Systems/Register/RegisterManager.cs', [
('''        RegisterBarks.EnsureLoaded();
''',
'''        RegisterBarks.EnsureLoaded();
        LoadSeenFile();
'''),
('''    private void MarkSeen(string id)
    {
        _sessionSeen.Add(id);''',
'''    // 2026-10-04: once flags were only written to the save's ledger and marked dirty;
    // nothing flushes a dirty save during a fight (and debug fights never save), so
    // quitting before a campus save brought every tip back. They are now also written
    // straight to a small file of their own the moment they are marked.
    private const string SeenFilePath = "user://register_seen.txt";

    private void LoadSeenFile()
    {
        if (!FileAccess.FileExists(SeenFilePath))
            return;
        using var f = FileAccess.Open(SeenFilePath, FileAccess.ModeFlags.Read);
        if (f == null)
            return;
        while (!f.EofReached())
        {
            string line = f.GetLine().Trim();
            if (line.Length > 0)
                _sessionSeen.Add(line);
        }
    }

    private static void AppendSeenFile(string id)
    {
        using var f = FileAccess.FileExists(SeenFilePath)
            ? FileAccess.Open(SeenFilePath, FileAccess.ModeFlags.ReadWrite)
            : FileAccess.Open(SeenFilePath, FileAccess.ModeFlags.Write);
        if (f == null)
            return;
        f.SeekEnd();
        f.StoreLine(id);
    }

    private void MarkSeen(string id)
    {
        if (_sessionSeen.Add(id))
            AppendSeenFile(id);'''),
])

edit('Scripts/Systems/Combat/Core/CombatManager.Triggers.cs', [
('''                if (half == null || half.Speed == PlaySpeed.Studied)   // only Reflexes respond
                    continue;''',
'''                if (half == null || half.Speed == PlaySpeed.Studied)   // only Reflexes respond
                    continue;
                if (State.IsOncePerFightSpent(half))   // a spent fight-long half cannot answer
                    continue;'''),
])

# ── Card data: once_per_fight flags ───────────────────────────────────
base = [('chronomancer_ephemeris', 'top'), ('chronomancer_the_fixed_hour', 'top'),
        ('chronomancer_temporal_sovreign', 'top'), ('chronomancer_time_lord', 'top'),
        ('arcanist_omniscience', 'bottom'), ('enchanter_the_grand_design', 'top'),
        ('tinker_conduit_singularity', 'bottom')]
rungs = [('necromancer_hollow_mantle', 'bottom', 4), ('necromancer_march_and_remember', 'bottom', 4),
         ('tinker_assembly_line', 'top', 4)]
def rw(name, fn):
    p = os.path.join(root, 'Data/Cards', name + '.json')
    d = json.load(open(p, encoding='utf-8'))
    fn(d)
    open(p, 'w', encoding='utf-8').write(json.dumps(d, indent='\t', ensure_ascii=False) + '\n')
    print('ok', p)
for name, half in base:
    def f(d, half=half): d[half]['once_per_fight'] = True
    rw(name, f)
for name, half, tier in rungs:
    def f(d, half=half, tier=tier):
        u = next(u for u in d['upgrades'] if u['tier'] == tier and u['half'] in (half, 'both'))
        u['changes'] = [c for c in u['changes'] if c['field'] != 'once_per_fight']
        u['changes'].append({'half': half, 'field': 'once_per_fight', 'value': True})
    rw(name, f)
print("slice 10 applied")
