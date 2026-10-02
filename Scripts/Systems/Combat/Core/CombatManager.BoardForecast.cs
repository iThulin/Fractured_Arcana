using Godot;
using System.Collections.Generic;

// ============================================================
// CombatManager.BoardForecast.cs
//
// Purpose:        What the board will do next, shown before it
//                 happens. Two forecasts, both pure reads:
//                 • enemy intents that will cause an element
//                   reaction name it on their nameplate
//                   (class_identity_elementalist_v1 §2a item 7);
//                 • next growth tick: faint sprouts where growth
//                   will seed, a "+" where it will advance
//                   (class_identity_druid_v1 §2a, "Preview").
//                 Both refresh with RefreshThreatTiles (after any
//                 board-changing action) and, for growth, whenever
//                 a growth stage changes (coalesced to once a frame).
// Layer:          Combat
// Collaborators:  GrowthManager.PreviewNextTick, ElementReactions,
//                 HexTile.SetGrowthPreview, UpdateIntentDisplay
// See:            docs/class_identity_elementalist_v1.md §2a,
//                 docs/class_identity_druid_v1.md §2a
// ============================================================

public partial class CombatManager
{
    private readonly HashSet<TileData> _growthPreviewTiles = new();
    private readonly List<TileData> _forecastSprouts = new();
    private readonly List<TileData> _forecastAdvances = new();
    private bool _growthPreviewQueued;

    // ── Intent reactions ────────────────────────────────────────────────

    /// <summary>
    /// The reaction an imbue intent will form if it resolved on the board as it stands
    /// now: the first telegraphed tile, in telegraph order, whose current element reacts
    /// with the intent's element. None for every other intent. Pure: reads only.
    /// </summary>
    private ElementReaction PredictIntentReaction(EnemyIntent intent)
    {
        if (intent == null || intent.ImbueElement == TileElementType.None || intent.ThreatTiles == null || grid == null)
            return ElementReaction.None;

        foreach (var coord in intent.ThreatTiles)
        {
            if (!IsImbuableTile(coord))
                continue;
            var tile = grid.GetTile(coord);
            if (tile == null || tile.Reaction == ElementReaction.Glacier)
                continue;   // a standing glacier ignores imbues
            var reaction = ElementReactions.Resolve(tile.ElementType, intent.ImbueElement);
            if (reaction != ElementReaction.None)
                return reaction;
        }
        return ElementReaction.None;
    }

    /// <summary>Repaints the nameplate of every enemy holding an imbue intent, so the
    /// predicted reaction follows the board as the player changes it.</summary>
    private void RefreshIntentReactions()
    {
        foreach (var enemy in enemyUnits)
        {
            if (IsValidActor(enemy) && enemy.CurrentIntent != null && enemy.CurrentIntent.ImbueElement != TileElementType.None)
                UpdateIntentDisplay(enemy);
        }
    }

    // ── Growth forecast ─────────────────────────────────────────────────

    /// <summary>Asks for a growth forecast repaint at the end of this frame. Many stage
    /// changes in one tick collapse into a single repaint.</summary>
    private void QueueGrowthPreview()
    {
        if (_growthPreviewQueued)
            return;
        _growthPreviewQueued = true;
        CallDeferred(nameof(RefreshGrowthPreview));
    }

    /// <summary>Repaints the next-tick growth forecast: "○" where a tile will sprout,
    /// "+" where living ground will advance a stage.</summary>
    public void RefreshGrowthPreview()
    {
        _growthPreviewQueued = false;

        foreach (var t in _growthPreviewTiles)
            t.TileView?.SetGrowthPreview(0);
        _growthPreviewTiles.Clear();

        if (State?.Growth == null || grid == null)
            return;
        if (currentPhase == CombatPhase.Victory || currentPhase == CombatPhase.Defeat)
            return;

        State.Growth.PreviewNextTick(_forecastSprouts, _forecastAdvances);

        foreach (var t in _forecastAdvances)
        {
            t.TileView?.SetGrowthPreview(2);
            _growthPreviewTiles.Add(t);
        }
        foreach (var t in _forecastSprouts)
        {
            t.TileView?.SetGrowthPreview(1);
            _growthPreviewTiles.Add(t);
        }

        if (_forecastSprouts.Count > 0 || _forecastAdvances.Count > 0)
            RegisterManager.Fire("druid.growth_preview");
    }
}
