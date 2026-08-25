using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// DualCardData.cs
//
// Purpose:        Legacy Node2D that wraps two CardData halves
//                 (top and bottom). Predates the runtime
//                 Card/CardHalf split in CardRuntime.cs.
// Layer:          Data
// Collaborators:  CardData.cs (the wrapped halves)
// See:            README §7 ("CardData.cs vs CardRuntime.cs")
// ============================================================

/// <summary>Legacy scene-graph wrapper holding two <see cref="CardData"/> halves. Predates the runtime <c>Card</c>/<c>CardHalf</c> split, so new code should use those instead. Retained for any scenes that still instantiate dual cards directly.</summary>
[GlobalClass]
public partial class DualCardData : Node2D
{
    /// <summary>Top half of the dual card.</summary>
    [Export] public CardData topHalf;

    /// <summary>Bottom half of the dual card.</summary>
    [Export] public CardData bottomHalf;
}