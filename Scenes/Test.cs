using Godot;
using System;

public partial class SignalTest : Control
{
    [Signal]
    public delegate void HoveredEventHandler();

    public override void _Ready()
    {
        GD.Print("SignalTest ready.");
    }
}