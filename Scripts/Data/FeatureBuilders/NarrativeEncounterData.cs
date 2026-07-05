using System.Collections.Generic;

// ============================================================
// NarrativeEncounterData.cs
//
// Purpose:        Narrative encounter model — title, body text,
//                 terrain/region filter tags, list of player
//                 choices each with their own outcome deltas
//                 (gold, HP, steps) and Phase-3 gating fields.
// Layer:          Data
// Collaborators:  NarrativeEncounterLoader.cs (JSON parser),
//                 NarrativeEncounterPanel.cs (UI display),
//                 EncounterRouter.cs
// See:            README §4.3 (Adding a Narrative Encounter)
// ============================================================

/// <summary>One narrative encounter: title, body text, optional terrain/region filters, and the list of player choices. Loaded from Data/Encounters/*.json.</summary>
public class NarrativeEncounterData
{
    // ── Identity ────────────────────────────────────────────────────────
    public string Id = "";
    public string Title = "";
    public string Body = "";

    // ── Context filters ──────────────────────────────────────────────────
    // Empty list = matches any. Non-empty = only matches listed values.
    public List<string> TerrainTags = new();
    public List<string> RegionTags = new();

    // ── Choices ──────────────────────────────────────────────────────────
    public List<EncounterChoice> Choices = new();
}

/// <summary>
/// A single choice option within a narrative encounter.
/// </summary>
public class EncounterChoice
{
    public string Label = "";
    public string ResultText = "";

    // Outcomes (positive or negative)
    public int GoldDelta = 0;
    public int HPDelta = 0;
    public int StepDelta = 0;

    // Phase 3+ tracking
    public List<string> SetFlags = new();

    // Phase 3+ gating
    public string RequiredFlag = "";
    public string RequiredSchool = "";
    public int RequiredGold = 0;
}