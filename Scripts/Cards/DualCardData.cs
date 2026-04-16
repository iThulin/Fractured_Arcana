using Godot;
using System;
using System.Collections.Generic;

[GlobalClass]
public partial class DualCardData : Node2D
{
    [Export] public CardData topHalf;
    [Export] public CardData bottomHalf;
}