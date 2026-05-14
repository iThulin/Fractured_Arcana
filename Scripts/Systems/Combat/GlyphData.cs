using System;

public sealed class GlyphData
{
    public string OwnerId;
    public int OwnerTeam;
    public GameState GameState;  // stored at placement time
    public Action<Unit, GameState> OnTrigger;
    public bool Consumed;
}