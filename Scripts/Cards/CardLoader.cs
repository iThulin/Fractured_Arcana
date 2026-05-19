using Godot;
using System.Collections.Generic;

// ============================================================
// CardLoader.cs
//
// Purpose:        Startup glue between JsonCardLoader (which
//                 parses card JSON) and CardDatabase (which
//                 holds the runtime registry). Ensures the
//                 script registry is initialized exactly once
//                 and gates "wip" cards behind a DevMode flag.
// Layer:          Loader
// Collaborators:  JsonCardLoader.cs (the actual JSON parser),
//                 CardScriptRegistry (effects/predicates/
//                 targeters factories — registered on first
//                 call), CardDatabase.cs (target of every
//                 successful load), GameBootstrap.cs (caller)
// See:            README §3 (Architecture — card pipeline),
//                 README §4.1 (Adding a Card)
// ============================================================

/// <summary>Process-wide loader that wires the JSON parser into the database. Idempotent: first call registers the script factories and loads cards; subsequent calls no-op unless cleared via <see cref="Reload"/>.</summary>
public static class CardLoaderV2
{
    private static bool _registered = false;

    // ── Dev mode ────────────────────────────────────────────────────────

    /// <summary>When true, cards with status "wip" load alongside "ready" cards. Defaults to true in debug builds, false in release. Stubs are always skipped regardless. Set false for any build given to playtesters.</summary>
#if DEBUG
    public static bool DevMode = true;
#else
            public static bool DevMode = false;
#endif

    /// <summary>Loads every card JSON in the directory into <see cref="CardDatabase"/>. No-op when the database is already populated. Registers the script-registry built-ins on first call.</summary>
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

        // TEMP — delete after one run
        foreach (var bp in CardDatabase.Blueprints)
            GD.Print($"[BlueprintId] school={bp.School} id=\"{bp.Id}\"");
    }
}
