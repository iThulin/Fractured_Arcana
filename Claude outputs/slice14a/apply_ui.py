import pathlib
def edit(rel, pairs):
    p = pathlib.Path(rel); s = p.read_text(encoding='utf-8')
    for o, n in pairs:
        assert s.count(o) == 1, (rel, o[:80], s.count(o))
        s = s.replace(o, n)
    p.write_text(s, encoding='utf-8')

CU = 'Scripts/Cards/CardUi.cs'
edit(CU, [
('''    private bool _isDiscardFlagged = false;
    private Tween _amberPulseTween;
''',
'''    private bool _isDiscardFlagged = false;
    private Tween _amberPulseTween;

    /// <summary>Scale the card rests at (pages on the Register's leaf sit smaller than
    /// hand cards and grow to full size when hovered).</summary>
    public float RestScale = 1f;
    private Label _pagePlate;

    /// <summary>Shows a small plate above the card ("PAGE · +1 Charge"); null hides it.</summary>
    public void SetPagePlate(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            if (_pagePlate != null)
                _pagePlate.Visible = false;
            return;
        }
        if (_pagePlate == null)
        {
            _pagePlate = new Label
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                MouseFilter = MouseFilterEnum.Ignore,
                Position = new Vector2(0f, -44f),
                Size = new Vector2(UITheme.HandCardWidth, 40f),
            };
            _pagePlate.AddThemeFontSizeOverride("font_size", 26);
            _pagePlate.AddThemeColorOverride("font_color", UITheme.Warning);
            _pagePlate.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.85f));
            _pagePlate.AddThemeConstantOverride("outline_size", 6);
            AddChild(_pagePlate);
        }
        _pagePlate.Text = text;
        _pagePlate.Visible = true;
    }
'''),
('''        _cardTween.TweenProperty(this, "rotation", _restRotation, 0.12f);
        _cardTween.TweenProperty(this, "scale", Vector2.One, 0.12f);
        ZIndex = 0;''',
'''        _cardTween.TweenProperty(this, "rotation", _restRotation, 0.12f);
        _cardTween.TweenProperty(this, "scale", Vector2.One * RestScale, 0.12f);
        ZIndex = 0;'''),
('''            Modulate = new Color(1, 1, 1, 0f);
            Scale = Vector2.One;

            float delay = GetIndex() * 0.09f;''',
'''            Modulate = new Color(1, 1, 1, 0f);
            Scale = Vector2.One * RestScale;

            float delay = GetIndex() * 0.09f;'''),
])

DU = 'Scripts/UI/DeckUiManager.cs'
edit(DU, [
('''			// Snapshot hand RIGHT NOW for diffing
			var targetHand = new List<Card>(deckManager.Hand);''',
'''			// Snapshot hand RIGHT NOW for diffing (the leaf's pages show in the same container)
			var targetHand = DisplayCards();'''),
('''			var finalHand = new HashSet<Card>(deckManager.Hand);''',
'''			var finalHand = new HashSet<Card>(DisplayCards());'''),
('''			foreach (var card in deckManager.Hand)
			{
				if (!presentCards.Contains(card))''',
'''			foreach (var card in DisplayCards())
			{
				if (!presentCards.Contains(card))'''),
('''	public void SafeRefreshUI()
	{''',
'''	/// <summary>Everything the hand container shows: the hand, then the Register's leaf
	/// (open pages oldest first, then the memorized page).</summary>
	private List<Card> DisplayCards()
	{
		var list = new List<Card>(deckManager.Hand);
		list.AddRange(deckManager.Pages);
		if (deckManager.Memorized != null)
			list.Add(deckManager.Memorized);
		return list;
	}

	/// <summary>Pages sit in a small row above the left of the hand fan. They rest at
	/// <see cref="PageScale"/> and grow to full size when hovered.</summary>
	private const float PageScale = 0.55f;

	private void PositionPages(List<CardUi> pages, float boxLeft, float cardW, float cardH, float handTopY)
	{
		var deck = deckManager.GetActiveDeck();
		float w = cardW * PageScale, h = cardH * PageScale;
		float y = handTopY - h - 40f;
		for (int i = 0; i < pages.Count; i++)
		{
			var p = pages[i];
			p.RestScale = PageScale;
			bool memorized = deck != null && p.CardInstance == deck.Memorized;
			bool free = deck != null && (deck.FreeLeafCasts > 0 || deck.FreeTopPages.Contains(p.CardInstance));
			bool babbles = deck != null && deck.Pages.Count >= UnitDeckData.MaxPages && deck.Pages.Count > 0
			               && p.CardInstance == deck.Pages[0];
			p.SetPagePlate(memorized ? "MEMORIZED"
			               : babbles ? "BABBLES AT END OF TURN"
			               : free ? "PAGE · FREE"
			               : "PAGE · +1 CHARGE");
			var pos = new Vector2(boxLeft + i * (w + 14f), y);
			p.Position = pos;
			p.Rotation = 0f;
			if (!p.HasBeenPlaced || p.Scale.X > PageScale + 0.01f && p.ZIndex == 0)
				p.Scale = Vector2.One * PageScale;
			p.SetRestTransform(pos, 0f);
		}
	}

	public void SafeRefreshUI()
	{'''),
('''	private void PositionHandCards()
	{
		int count = handUIContainer.GetChildCount();
		if (count == 0)
			return;
''',
'''	private void PositionHandCards()
	{
		// The leaf's pages share this container; the fan lays out only the hand.
		var handNodes = new List<Control>();
		var pageNodes = new List<CardUi>();
		foreach (Node child in handUIContainer.GetChildren())
		{
			if (child is CardUi pc && deckManager.IsLeafCard(pc.CardInstance))
				pageNodes.Add(pc);
			else if (child is Control hc)
			{
				if (hc is CardUi hcu)
					hcu.RestScale = 1f;
				ClearPagePlate(hc);
				handNodes.Add(hc);
			}
		}
		int count = handNodes.Count;
		if (count == 0 && pageNodes.Count == 0)
			return;
'''),
('''		for (int i = 0; i < count; i++)
		{
			if (handUIContainer.GetChild(i) is not Control card)
				continue;

			float angle''',
'''		for (int i = 0; i < count; i++)
		{
			var card = handNodes[i];

			float angle'''),
('''		// Second half of the diagnostic: where the cards ACTUALLY landed, in''',
'''		if (pageNodes.Count > 0)
			PositionPages(pageNodes, boxLeft, cardW, cardH, cardBotY - cardH);

		// Second half of the diagnostic: where the cards ACTUALLY landed, in'''),
('''	private void UpdateCardCounts()
	{''',
'''	private static void ClearPagePlate(Control c)
	{
		if (c is CardUi cu)
			cu.SetPagePlate(null);
	}

	private void UpdateCardCounts()
	{'''),
])
print("ui ok")
