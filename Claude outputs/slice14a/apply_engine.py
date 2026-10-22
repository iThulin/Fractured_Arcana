import pathlib
def edit(rel, pairs):
    p = pathlib.Path(rel); s = p.read_text(encoding='utf-8')
    for o, n in pairs:
        assert s.count(o) == 1, (rel, o[:80], s.count(o))
        s = s.replace(o, n)
    p.write_text(s, encoding='utf-8')

edit('Scripts/Cards/UnitDeckData.cs', [
('''	public List<Card> ExilePile = new();
''',
'''	public List<Card> ExilePile = new();

	// ── The Register's leaf (Arcanist, class_identity_arcanist_v1 §2) ─────────
	/// <summary>Open pages on the leaf, oldest first. Cast either half of a penned card for
	/// its mana plus 1 Charge; it then goes to the discard pile.</summary>
	public List<Card> Pages = new();
	public const int MaxPages = 3;
	/// <summary>Infinity Loop's fourth page: permanent for the fight, recites itself.</summary>
	public Card Memorized;
	/// <summary>Overcharge's bottom: penned cards whose TOP half costs no Charge.</summary>
	public HashSet<Card> FreeTopPages = new();
	/// <summary>Grimoire Dive's bottom: leaf casts that cost no Charge.</summary>
	public int FreeLeafCasts;

	/// <summary>True for a card on the leaf (an open page or the memorized one).</summary>
	public bool IsLeafCard(Card c) => c != null && (Pages.Contains(c) || c == Memorized);

	/// <summary>Moves a card from the hand or the draw pile onto an open page.
	/// False when the leaf is full or the card is not in either.</summary>
	public bool Pen(Card card)
	{
		if (card == null || Pages.Count >= MaxPages || Pages.Contains(card))
			return false;
		if (!Hand.Remove(card) && !DrawPile.Remove(card))
			return false;
		Pages.Add(card);
		return true;
	}
'''),
('''	public void Discard(Card card)
	{
		if (Hand.Remove(card))
			DiscardPile.Add(card);
	}''',
'''	public void Discard(Card card)
	{
		if (Hand.Remove(card))
			DiscardPile.Add(card);
		else if (Pages.Remove(card))
		{
			FreeTopPages.Remove(card);
			DiscardPile.Add(card);   // a page cast goes to the discard pile like any card
		}
	}'''),
])

edit('Scripts/Cards/DeckManager.cs', [
('''    public List<Card> DiscardPile => _activeDeck?.DiscardPile ?? new();
''',
'''    public List<Card> DiscardPile => _activeDeck?.DiscardPile ?? new();
    /// <summary>The Register's leaf: open pages, oldest first (Arcanist).</summary>
    public List<Card> Pages => _activeDeck?.Pages ?? new();
    public Card Memorized => _activeDeck?.Memorized;
    public bool IsLeafCard(Card c) => _activeDeck != null && _activeDeck.IsLeafCard(c);
'''),
('''        var cardUis = new List<CardUi>();
        foreach (Node child in handUIContainer.GetChildren())
            if (child is CardUi cui) cardUis.Add(cui);
''',
'''        var cardUis = new List<CardUi>();
        foreach (Node child in handUIContainer.GetChildren())
            if (child is CardUi cui && !_activeDeck.IsLeafCard(cui.CardInstance)) cardUis.Add(cui);

        // The leaf's oldest page glows while the leaf is full: it babbles at end of turn.
        bool full = _activeDeck.Pages.Count >= UnitDeckData.MaxPages;
        foreach (Node child in handUIContainer.GetChildren())
            if (child is CardUi pc && _activeDeck.Pages.Contains(pc.CardInstance))
                pc.SetDiscardFlagged(full && pc.CardInstance == _activeDeck.Pages[0]);
'''),
])

edit('Scripts/Systems/Combat/Attunement/ArcaneAttunement.cs', [
('''    public void OnTurnStart()
    {
        SpellsCastThisTurn = 0;''',
'''    /// <summary>Overcharge (class_identity_arcanist_v1 §4): turn starts left before Charge
    /// can increase again. 2 = locked for the rest of this turn and all of the next.</summary>
    public int LockTurns { get; private set; }

    /// <summary>Locks Charge gains for <paramref name="turnStarts"/> turn starts (keeps the longer lock).</summary>
    public void LockCharge(int turnStarts) => LockTurns = Math.Max(LockTurns, turnStarts);

    public void OnTurnStart()
    {
        if (LockTurns > 0)
            LockTurns--;
        SpellsCastThisTurn = 0;'''),
('''        if (amount <= 0)
            return 0;
        int before = Charge;
        int target = before + amount;''',
'''        if (amount <= 0)
            return 0;
        if (LockTurns > 0)
        {
            GD.Print($"[ArcaneAttunement] Add({amount}) refused: Charge is locked by Overcharge.");
            return 0;
        }
        int before = Charge;
        int target = before + amount;'''),
('''    public void OnCombatStart()
    {''',
'''    public void OnCombatStart()
    {
        LockTurns = 0;'''),
])

edit('Scripts/Systems/GameStateManager.cs', [
('''    public Action<Unit> OnReplanIntent;''',
'''    public Action<Unit> OnReplanIntent;
    /// <summary>Rapid Notation's bottom (Arcanist): the unit's leaf recites its oldest page now.</summary>
    public Action<Unit> OnReciteOldest;'''),
])
print("engine ok")
