using Godot;
using System.Collections.Generic;

// ============================================================
// CardLoaderV2 — PHASE 3 UPDATE
//
// Added: DevMode flag. When true, cards with status "wip" are
// loaded in addition to "ready" cards. Stubs are always skipped.
//
// Toggle DevMode by setting the exported property in the editor,
// or flip the constant below for a build-wide default.
// ============================================================

public static class CardLoaderV2
{
    private static bool _registered = false;

    // ── Dev mode ────────────────────────────────────────────────────
    // Set to true locally while testing unfinished cards.
    // Should be false in any build you hand to a playtester.
#if DEBUG
    public static bool DevMode = true;
#else
            public static bool DevMode = false;
#endif

    public static void LoadCardsFromJson(string directoryPath)
    {
        if (CardDatabase.Blueprints.Count > 0)
            return;

        if (!_registered)
        {
            CardScriptRegistry.RegisterBuiltins();
            _registered = true;
        }

        var cards = JsonCardLoader.LoadAll(directoryPath, DevMode);

        int added = 0;
        foreach (var c in cards)
        {
            CardDatabase.RegisterPrebuiltCard(c);
            added++;
        }

        GD.Print($"[CardLoaderV2] Registered {added} cards (DevMode={DevMode}). " +
                 $"Total blueprints: {CardDatabase.Blueprints.Count}");
    }

    // Force-reload — use from dev tools only, not gameplay code.
    public static void Reload(string directoryPath)
    {
        CardDatabase.Blueprints.Clear();
        _registered = false;
        LoadCardsFromJson(directoryPath);
    }
}
