using Godot;
using System;
using System.Collections.Generic;
using System.Text.Json;

// ============================================================
// PartyLoadouts.cs: save a party's build, and put it back (2026-11-11)
//
// Purpose:        A debug tool for testing fights against a build a player
//                 would really have. "Snapshot" writes the current timeline's
//                 build to a named file: the deck and collection, the armory,
//                 the companions, the grimoire and the signature pool. "Apply"
//                 puts a saved build onto the current timeline. A build is
//                 taken from a real run, so it stays truthful as the game
//                 changes (ruled by Magos, 2026-11-11: snapshots, not invented
//                 decks).
//
//                 In the editor the files live in the project, under
//                 res://Data/Debug/Loadouts, so they are committed and both
//                 machines share them. In an exported debug build they live
//                 under user://loadouts.
//
//                 A build is applied only to a guild of the same school. The
//                 school decides the deck's rules and the class features, and
//                 a mixed state would test nothing real.
// Layer:          Dev (debug builds only)
// Collaborators:  DebugToolsPanel.cs (the two rows that call this),
//                 SaveManager (JsonOptions, MarkDirty), CycleState (the parts)
// ============================================================

/// <summary>One saved build: the parts of a timeline that make the party what it is.</summary>
public class PartyLoadout
{
    public string Name = "";
    public string School = "";
    public string SavedOn = "";
    public int CycleNumber;
    public int Lunation;
    public int MaxPartySize;

    public PlayerDeckSave PlayerDeck;
    public ArmoryData Armory;
    public List<Companion> Companions;
    public GrimoireState Grimoire;
    public SignaturePoolState SignaturePool;
}

/// <summary>Writes and reads <see cref="PartyLoadout"/> files.</summary>
public static class PartyLoadouts
{
    private const string EditorFolder = "res://Data/Debug/Loadouts";
    private const string ExportFolder = "user://loadouts";

    private static string Folder => OS.HasFeature("editor") ? EditorFolder : ExportFolder;

    /// <summary>The file name for a build's name: lower case, letters, digits and underscores.</summary>
    public static string Slug(string name)
    {
        var chars = new List<char>();
        foreach (char ch in (name ?? "").Trim().ToLowerInvariant())
        {
            chars.Add(char.IsLetterOrDigit(ch) ? ch : '_');
        }
        string slug = new string(chars.ToArray()).Trim('_');
        return slug.Length > 0 ? slug : "loadout";
    }

    private static string PathOf(string slug) => $"{Folder}/{slug}.json";

    /// <summary>The saved builds' file names without the extension, sorted.</summary>
    public static List<string> List()
    {
        var found = new List<string>();
        using var dir = DirAccess.Open(Folder);
        if (dir == null)
        {
            return found;
        }
        foreach (string file in dir.GetFiles())
        {
            if (file.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                found.Add(file.Substring(0, file.Length - ".json".Length));
            }
        }
        found.Sort(StringComparer.OrdinalIgnoreCase);
        return found;
    }

    /// <summary>Write the current timeline's build to a file named for it. Returns a line for
    /// the log saying what happened.</summary>
    public static string Snapshot(GuildSaveData save, string name)
    {
        var cycle = save?.Cycle;
        if (cycle == null)
        {
            return "No timeline is loaded, so there is nothing to snapshot.";
        }
        string slug = Slug(name);
        var loadout = new PartyLoadout
        {
            Name = string.IsNullOrWhiteSpace(name) ? slug : name.Trim(),
            School = cycle.SelectedSchool ?? "",
            SavedOn = Time.GetDatetimeStringFromSystem(),
            CycleNumber = cycle.CycleNumber,
            Lunation = cycle.Calendar?.CurrentLunation ?? 0,
            MaxPartySize = cycle.MaxPartySize,
            PlayerDeck = cycle.PlayerDeck,
            Armory = cycle.Armory,
            Companions = cycle.Companions,
            Grimoire = cycle.Grimoire,
            SignaturePool = cycle.SignaturePool,
        };

        string json;
        try
        {
            json = JsonSerializer.Serialize(loadout, SaveManager.JsonOptions);
        }
        catch (Exception e)
        {
            return $"The snapshot could not be written: {e.Message}";
        }

        DirAccess.MakeDirRecursiveAbsolute(ProjectSettings.GlobalizePath(Folder));
        using var file = FileAccess.Open(PathOf(slug), FileAccess.ModeFlags.Write);
        if (file == null)
        {
            return $"The snapshot could not be written to {PathOf(slug)}: {FileAccess.GetOpenError()}.";
        }
        file.StoreString(json);

        int deck = cycle.PlayerDeck?.RealActiveDeckInstanceIds?.Count ?? 0;
        int owned = cycle.PlayerDeck?.RealCards?.Count ?? 0;
        int companions = cycle.Companions?.Count ?? 0;
        return $"Saved '{loadout.Name}' to {PathOf(slug)}: {loadout.School}, moon {loadout.Lunation}, "
               + $"a deck of {deck} from {owned} card(s), {companions} companion(s).";
    }

    /// <summary>Put a saved build onto the current timeline. Returns a line for the log saying
    /// what happened. Nothing is changed unless the whole file reads cleanly.</summary>
    public static string Apply(GuildSaveData save, string slug)
    {
        var cycle = save?.Cycle;
        if (cycle == null)
        {
            return "No timeline is loaded, so there is nothing to apply a build to.";
        }
        if (string.IsNullOrEmpty(slug) || !FileAccess.FileExists(PathOf(slug)))
        {
            return $"There is no saved build at {PathOf(slug)}.";
        }

        PartyLoadout loadout;
        try
        {
            using var file = FileAccess.Open(PathOf(slug), FileAccess.ModeFlags.Read);
            loadout = JsonSerializer.Deserialize<PartyLoadout>(file.GetAsText(), SaveManager.JsonOptions);
        }
        catch (Exception e)
        {
            return $"The build at {PathOf(slug)} could not be read: {e.Message}";
        }
        if (loadout == null || loadout.PlayerDeck == null)
        {
            return $"The build at {PathOf(slug)} holds no deck, so it was not applied.";
        }
        if (!string.Equals(loadout.School, cycle.SelectedSchool, StringComparison.OrdinalIgnoreCase))
        {
            return $"'{loadout.Name}' is a build for the {loadout.School}. This guild's wizard is "
                   + $"the {cycle.SelectedSchool}. Found a guild of that school, then apply it.";
        }

        cycle.PlayerDeck = loadout.PlayerDeck;
        cycle.Armory = loadout.Armory ?? new ArmoryData();
        cycle.Companions = loadout.Companions ?? new List<Companion>();
        cycle.Grimoire = loadout.Grimoire ?? new GrimoireState();
        cycle.SignaturePool = loadout.SignaturePool ?? new SignaturePoolState();
        cycle.MaxPartySize = Math.Max(cycle.MaxPartySize, loadout.MaxPartySize);

        // A party posted in the field names its members. One who is not in the new company
        // is dropped from the posting, so no party points at someone who is not there.
        var present = new HashSet<string>();
        foreach (var companion in cycle.Companions)
        {
            if (companion != null && !string.IsNullOrEmpty(companion.Id))
            {
                present.Add(companion.Id);
            }
        }
        if (cycle.FieldParties != null)
        {
            foreach (var party in cycle.FieldParties)
            {
                party?.MemberCompanionIds?.RemoveAll(id => !present.Contains(id));
            }
        }

        SaveManager.MarkDirty();
        SaveManager.SaveIfDirty();

        int deck = cycle.PlayerDeck.RealActiveDeckInstanceIds?.Count ?? 0;
        return $"Applied '{loadout.Name}' (saved {loadout.SavedOn}, moon {loadout.Lunation}): a deck of {deck}, "
               + $"{cycle.Companions.Count} companion(s). Go back to the campus so every screen reads the new build.";
    }
}
