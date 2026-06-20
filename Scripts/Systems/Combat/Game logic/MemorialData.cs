// Scripts/Systems/Combat/MemorialData.cs
using System;

public enum MemorialStrength
{
    Faint,    // minor enemy, generic death
    Solid,    // named enemy, champion, significant death
    Strong    // ally death, boss, accumulated overlap
}

public enum MemorialState
{
    Fresh,       // created this turn — brighter visually
    Established, // 1+ turns old — settled
    Hallowed     // upgraded by a card effect
}

public class MemorialData
{
    // Who/what died here — drives the spirit identity flash on summon
    public string SourceName = "Unknown";
    public int SourceTeamId = -1;     // -1 = unknown
    public bool WasAlly = false;

    // Mechanical weight
    public MemorialStrength Strength = MemorialStrength.Faint;

    // Visual/age state
    public MemorialState State = MemorialState.Fresh;
    public int TurnsAlive = 0;

    // Owning necromancer (for multi-wizard scenarios later)
    public int OwnerTeamId = 0;

    // Flagged for removal at end of turn if a card consumes it
    public bool ConsumedThisTurn = false;

    public MemorialData(string sourceName, bool wasAlly, MemorialStrength strength, int ownerTeamId)
    {
        SourceName = sourceName;
        WasAlly = wasAlly;
        Strength = strength;
        OwnerTeamId = ownerTeamId;
    }

    public void Tick()
    {
        TurnsAlive++;
        if (State == MemorialState.Fresh)
            State = MemorialState.Established;
    }

    public void Hallow()
    {
        State = MemorialState.Hallowed;
        Strength = MemorialStrength.Strong;
    }

    // Strength integer for card math (Revenant HP scaling, etc.)
    public int StrengthValue => Strength switch
    {
        MemorialStrength.Faint => 1,
        MemorialStrength.Solid => 2,
        MemorialStrength.Strong => 3,
        _ => 1
    };
}