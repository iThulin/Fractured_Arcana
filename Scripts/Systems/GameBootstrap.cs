using Godot;

// ============================================================
// GameBootstrap.cs
//
// Purpose:        Autoload that runs once at game startup to
//                 prime any process-wide registries (currently:
//                 the card database). Cheap to add new
//                 initialization steps here — keeps that wiring
//                 out of individual scenes. Also hosts the dev
//                 entry points for CardVerifier (F9 in debug
//                 builds, or `--verify-cards` headless).
// Layer:          System
// Collaborators:  CardLoaderV2.cs (LoadCardsFromJson),
//                 CardVerifier.cs (dev verification pass)
// See:            README §3 — startup sequence
// ============================================================

/// <summary>Singleton-style autoload that primes process-wide registries at game startup. Currently only loads the card database; add additional initialization steps here as the project grows.</summary>
public partial class GameBootstrap : Node
{
    public override void _Ready()
    {
        // Ensure card database is loaded before any gameplay scenes that rely on it.
        CardLoaderV2.LoadCardsFromJson("res://Data/Cards");

        // Headless verification: `godot --headless -- --verify-cards`
        // Exit code 1 on any card error, so a script can gate on it.
        foreach (var arg in OS.GetCmdlineUserArgs())
        {
            if (arg == "--verify-cards")
            {
                bool ok = CardVerifier.RunAndReport();
                GetTree().Quit(ok ? 0 : 1);
                return;
            }

            // `godot --headless -- --verify-cipher`
            // Glyph cipher goldens + invariants. Runs after the card database is
            // loaded so it can check the live Enchanter corpus, not just the
            // baked-in goldens.
            if (arg == "--verify-cipher")
            {
                bool ok = GlyphCipherSelfTest.RunAndReport();
                GetTree().Quit(ok ? 0 : 1);
                return;
            }
        }
    }

    public override void _UnhandledKeyInput(InputEvent e)
    {
        // F9 in a debug build: run the card verification pass on demand.
        if (OS.IsDebugBuild()
            && e is InputEventKey k && k.Pressed && !k.Echo && k.Keycode == Key.F9)
        {
            CardVerifier.RunAndReport();
        }

        // F10: the glyph cipher's equivalent pass.
        if (OS.IsDebugBuild()
            && e is InputEventKey k2 && k2.Pressed && !k2.Echo && k2.Keycode == Key.F10)
        {
            GlyphCipherSelfTest.RunAndReport();
        }

        // F11: the glyph gallery — every Enchanter half drawn through the shipping
        // renderer, for comparison against docs/glyph_cipher_sheet.png.
        if (OS.IsDebugBuild()
            && e is InputEventKey k3 && k3.Pressed && !k3.Echo && k3.Keycode == Key.F11)
        {
            GlyphCipherGallery.Toggle(GetTree().Root);
        }
    }
}
