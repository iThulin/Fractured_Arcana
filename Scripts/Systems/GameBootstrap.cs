using Godot;

// ============================================================
// GameBootstrap.cs
//
// Purpose:        Autoload that runs once at game startup to
//                 prime any process-wide registries (currently:
//                 the card database). Cheap to add new
//                 initialization steps here — keeps that wiring
//                 out of individual scenes.
// Layer:          System
// Collaborators:  CardLoaderV2.cs (LoadCardsFromJson)
// See:            README §3 — startup sequence
// ============================================================

/// <summary>Singleton-style autoload that primes process-wide registries at game startup. Currently only loads the card database; add additional initialization steps here as the project grows.</summary>
public partial class GameBootstrap : Node
{
    public override void _Ready()
    {
        // Ensure card database is loaded before any gameplay scenes that rely on it.
        CardLoaderV2.LoadCardsFromJson("res://Data/Cards");
    }
}