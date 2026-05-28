using System.Collections.Generic;

// ============================================================
// DisenchantValues.cs
//
// Purpose:        Stateless helper that calculates the Arcane
//                 Splinter yield when a card is disenchanted.
//                 Base yield scales with rarity (looked up from
//                 CardDatabase); upgrade refund scales with
//                 PointsSpent; building bonus from PlayerSession.
// Layer:          System
// Collaborators:  OwnedCard (input), CardDatabase (rarity lookup),
//                 PlayerSession (bonus), DeckEditorUi (consumer)
// ============================================================

/// <summary>Calculates Arcane Splinter yield for disenchanting an owned card.</summary>
public static class DisenchantValues
{
	// Base yields by rarity — intentionally below full upgrade cost
	// so disenchanting is never strictly better than using the card
	private static int BaseYield(CardRarity rarity) => rarity switch
	{
		CardRarity.Common => 3,
		CardRarity.Uncommon => 5,
		CardRarity.Rare => 8,
		CardRarity.Legendary => 12,
		_ => 3,
	};

	private const int UpgradeRefundPerPoint = 4;

	public static int GetYield(OwnedCard card)
	{
		var bp = CardDatabase.Blueprints.Find(b =>
			string.Equals(b.Id, card.BlueprintId,
				System.StringComparison.OrdinalIgnoreCase));

		CardRarity rarity = bp?.Rarity ?? CardRarity.Common;
		int baseYield = BaseYield(rarity);
		int upgradeRefund = card.PointsSpent * UpgradeRefundPerPoint;
		return baseYield + upgradeRefund + PlayerSession.DisenchantSplinterBonus;
	}
}