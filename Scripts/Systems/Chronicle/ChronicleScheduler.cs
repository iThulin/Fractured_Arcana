using Godot;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

// ============================================================
// ChronicleScheduler.cs
//
// Purpose:        The chronicle (wire item C1, docs/story_wiring_v1.md).
//                 Loads Data/Story, listens on the story hooks, writes
//                 flags, fills tokens, and hands panels to their hosts:
//                 an immediate panel goes back to the caller, anything
//                 else waits on CycleState.ChronicleQueue for a quiet
//                 moment. Also owns the pieces the seat arcs need (C5:
//                 the deed tally, the sway helper), the Ceremony Hall's
//                 raise (C2), the traitor argument (C3) and the director
//                 line lookup the finale will call (C6).
//
//                 The rules follow the doc's C1 section and the working
//                 reference, _build/c1_reference.py in every story
//                 package. Where this file differs from the reference it
//                 says so: a missing line leaves its token literal rather
//                 than failing, a panel already waiting is not queued
//                 twice, and hook arguments are tokens too.
//
//                 The prologue (C4): a quiet moment in the home city shows
//                 the prologue's world steps first (PrologueDirector:
//                 the Anchorhold, then the Door). A save from before the
//                 prologue reads the full skip's recap instead (the
//                 bridge, B-1). The recap's choice writes every prologue
//                 flag, which is what opens Part I.
// Layer:          System (static)
// Collaborators:  ChronicleData.cs (the shapes), CycleState
//                 (ChronicleQueue, ReadingDeeds), EternalLedger
//                 (CoConspiratorId, MetaNarrativeFlags), RegisterManager
//                 (barks, PrologueMode), StrategicView and CampusScreen
//                 (the quiet-moment hosts), and the raise sites the doc
//                 lists under "Hooks and raise sites"
// See:            docs/story_wiring_v1.md (C1, C2, C3, C5),
//                 docs/prologue_script_v1.md (the recap the bridge shows)
// ============================================================

/// <summary>Process-wide chronicle. Every method is safe to call with no save loaded and
/// before the story data exists: it does nothing and returns null.</summary>
public static class ChronicleScheduler
{
    private const string STORY_DIR = "res://Data/Story/";
    private const string ENCOUNTERS_DIR = "res://Data/Encounters/";

    // The prologue bridge (B-1): the recap panel, read by id from the prologue's panel file.
    private const string PrologueEncounterFile = "prologue";
    private const string PrologueRecapId = "prologue_skip_recap";

    // Mirrors NarrativeEncounterLoader, so the panel files read exactly as the pools do.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        IncludeFields = true,
    };

    /// <summary>A {token} or {arg}: lower case, digits and underscores, as the reference reads them.</summary>
    private static readonly Regex TokenRx = new(@"\{([a-z_][a-z0-9_]*)\}", RegexOptions.Compiled);

    // The three deeds (C5), and the order ties go in for weakest_1 / weakest_2.
    private static readonly string[] Deeds = { "field", "table", "road" };
    private static readonly string[] DeedTieOrder = { "table", "road", "field" };

    /// <summary>Tokens the scheduler computes itself (globals, derived values, and the finale's
    /// composed arguments). A beat may declare these without a line behind them.</summary>
    private static readonly HashSet<string> CodeTokenNames = new()
    {
        "wizard_name", "guild_name", "traitor_full", "traitor_short",
        "settlement_name", "companion_name", "archmage_full", "corrupted_faction", "faculty_name",
        "intended", "door_region", "traitor_redemption", "traitor_second", "anchor_name",
        "council_votes", "dissenter_names", "front_news", "companion_fates", "reading_recap",
    };

    // ── Loaded data ──────────────────────────────────────────────────────
    private static List<ChronicleFile> _files;   // in load order
    private static Dictionary<string, ChronicleFile> _fileByName;
    private static Dictionary<string, ChronicleBeat> _beatById;
    // Panel file (no extension), then panel id, then the cached panel. Never mutated.
    private static Dictionary<string, Dictionary<string, NarrativeEncounterData>> _panels;

    // on_combat_start's reading of the fight, kept for on_combat_end: the router clears its
    // weather when the fight finishes, before the expedition scene comes back.
    private static bool _combatBorrowed;
    private static string _combatWeather = "clear";

    // ════════════════════════════════════════════════════════════════════════
    //  The core: raise, drain, render
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>Raise <paramref name="hook"/> with its arguments (C1 rule 1). Returns the first
    /// immediate panel for the caller to show; every other panel this raise produces waits on
    /// the queue. Null when nothing is to be shown now.</summary>
    public static NarrativeEncounterData Raise(string hook, Dictionary<string, string> args = null)
        => RaiseCore(hook, args, queueImmediate: false);

    /// <summary>As <see cref="Raise"/>, for a site that cannot show a panel: an immediate panel
    /// waits on the queue like any other, so nothing is lost.</summary>
    public static void RaiseQueued(string hook, Dictionary<string, string> args = null)
        => RaiseCore(hook, args, queueImmediate: true);

    /// <summary>The quiet moment (on_scene_ready, C1 rule 6). A host calls this when nothing
    /// modal is up, and shows what comes back through its own narrative panel and applier.
    /// Order: the prologue bridge, then the queue, one panel at a time, then the new-reading
    /// beats (campus only) and the scene-ready beats. <paramref name="screen"/> is
    /// "strategic" or "campus" (C5).</summary>
    public static NarrativeEncounterData DrainIfQuiet(string screen)
    {
        var save = SaveManager.ActiveSave;
        if (!Live(save))
            return null;
        EnsureLoaded();

        // C4: the prologue's world steps come first; a save from before the prologue reads
        // the full skip's recap instead (B-1). A completed prologue lists its practical on
        // this computer, for the skips.
        PrologueDirector.NoteProfileCompletion(save);
        var prologue = PrologueDirector.WorldPanel(save, screen) ?? PrologueBridge(save);
        if (prologue != null)
            return prologue;

        var waiting = PopQueued(save);
        if (waiting != null)
            return waiting;

        if (screen == "campus")
        {
            // The new reading's minutes come before anything the scene itself raises.
            var reading = RaiseNewReading(save) ?? PopQueued(save);
            if (reading != null)
                return reading;
        }

        var ready = Raise("on_scene_ready", new Dictionary<string, string> { ["screen"] = screen ?? "" });
        if (ready != null)
            return ready;
        return PopQueued(save);
    }

    /// <summary>The finale's director lines (C6): the first line of <paramref name="key"/> in
    /// story file <paramref name="file"/> whose conditions match <paramref name="args"/>, its
    /// {tokens} filled from the arguments, then the globals. Null when nothing matches.</summary>
    public static string Line(string file, string key, Dictionary<string, string> args)
    {
        EnsureLoaded();
        if (string.IsNullOrEmpty(file) || string.IsNullOrEmpty(key)
            || !_fileByName.TryGetValue(file, out var f))
            return null;
        var a = args ?? new Dictionary<string, string>();
        string text = FindLine(f, key, a);
        if (text == null)
            return null;
        var save = SaveManager.ActiveSave;
        return TokenRx.Replace(text, m =>
        {
            string t = m.Groups[1].Value;
            if (a.TryGetValue(t, out var v) && v != null)
                return v;
            string g = save != null ? GlobalToken(save, t) : null;
            return g ?? m.Value;
        });
    }

    private static NarrativeEncounterData RaiseCore(string hook, Dictionary<string, string> args,
                                                    bool queueImmediate)
    {
        var save = SaveManager.ActiveSave;
        if (!Live(save) || string.IsNullOrEmpty(hook))
            return null;
        EnsureLoaded();
        if (_files.Count == 0)
            return null;

        // Context arguments first, then the hook's own, which win on a clash (rule 1).
        var all = Context(save);
        if (args != null)
            foreach (var kv in args)
                all[kv.Key] = kv.Value ?? "";

        NarrativeEncounterData shown = null;
        bool produced = false;   // any beat on this raise showed or queued a panel
        bool wrote = false;

        // Rule 3: every beat without fallback first; the fallback beats only when that pass
        // produced no panel at all.
        for (int pass = 0; pass < 2; pass++)
        {
            bool fallbackPass = pass == 1;
            if (fallbackPass && produced)
                break;
            foreach (var file in _files)
            {
                var firedGroups = new HashSet<string>();   // groups are file-local
                foreach (var beat in file.Beats)
                {
                    if (beat.Hook != hook || beat.Fallback != fallbackPass)
                        continue;
                    if (!string.IsNullOrEmpty(beat.Group) && firedGroups.Contains(beat.Group))
                        continue;
                    if (!Eligible(save, beat, all, out string once, out bool eternal))
                        continue;

                    // Rule 4: rememberFlag and flagArgs read the flags before this beat writes any.
                    var extra = new Dictionary<string, string>();
                    if (!string.IsNullOrEmpty(beat.RememberFlag))
                        extra["remembered"] = Bool(HasMeta(save, Sub(beat.RememberFlag, all)));
                    foreach (var fa in beat.FlagArgs)
                        extra[fa.Key] = Bool(Has(save, Sub(fa.Value, all)));

                    // Rule 5: write, grant, bark, then the panel.
                    if (once.Length > 0)
                    {
                        if (eternal) SetMeta(save, once);
                        else save.Cycle.SetFlag(once);
                    }
                    foreach (var flag in beat.AlsoSetMeta)
                        SetMeta(save, Sub(flag, all));
                    foreach (var flag in beat.AlsoSetTimeline)
                        save.Cycle.SetFlag(Sub(flag, all));
                    string lore = Sub(beat.Lore, all);
                    if (lore.Length > 0 && !save.UnlockedLoreEntries.Contains(lore))
                        save.UnlockedLoreEntries.Add(lore);
                    string bark = Sub(beat.Bark, all);
                    if (bark.Length > 0)
                        RegisterManager.Fire(bark);
                    wrote = true;

                    if (!string.IsNullOrEmpty(beat.Show))
                    {
                        produced = true;
                        var entry = Resolve(save, file, beat, all, extra);
                        if (entry != null)
                        {
                            bool immediate = string.Equals(beat.Delivery, "immediate",
                                                           StringComparison.OrdinalIgnoreCase);
                            if (immediate && !queueImmediate && shown == null)
                                shown = Render(entry);
                            else
                                Enqueue(save, entry);
                        }
                    }

                    if (!string.IsNullOrEmpty(beat.Group))
                        firedGroups.Add(beat.Group);
                }
            }
        }

        if (wrote)
            SaveManager.MarkDirty();
        return shown;
    }

    /// <summary>Rule 2: every when pair matches, every requires flag is set (either layer), no
    /// excludes flag is set, and the once flag is not set in the beat's layer.</summary>
    private static bool Eligible(GuildSaveData save, ChronicleBeat beat, Dictionary<string, string> all,
                                 out string once, out bool eternal)
    {
        once = "";
        eternal = string.Equals(beat.Layer, "Eternal", StringComparison.OrdinalIgnoreCase);
        if (!WhenMatches(beat.WhenNorm, all))
            return false;
        foreach (var flag in beat.Requires)
            if (!Has(save, Sub(flag, all)))
                return false;
        foreach (var flag in beat.Excludes)
            if (Has(save, Sub(flag, all)))
                return false;
        once = Sub(beat.Once, all);
        if (once.Length > 0)
        {
            bool set = eternal ? HasMeta(save, once) : save.Cycle.HasFlag(once);
            if (set)
                return false;
        }
        return true;
    }

    /// <summary>Resolve a fired beat's panel into a queue entry: the panel id and every token
    /// its text needs, resolved now (rule 7).</summary>
    private static ChronicleQueued Resolve(GuildSaveData save, ChronicleFile file, ChronicleBeat beat,
                                           Dictionary<string, string> all, Dictionary<string, string> extra)
    {
        string panelId = Sub(beat.Show, all);
        var panel = Panel(file.EncounterFile, panelId);
        if (panel == null)
        {
            GD.PushError($"ChronicleScheduler: beat {beat.Id} shows {panelId}, which is not in {file.EncounterFile}.");
            return null;
        }
        var resolver = new TokenResolver(save, file, beat, all, extra);
        resolver.Fill(panel.Title);
        resolver.Fill(panel.Body);
        if (panel.Choices != null)
            foreach (var c in panel.Choices)
            {
                if (c == null) continue;
                resolver.Fill(c.Label);
                resolver.Fill(c.ResultText);
            }
        return new ChronicleQueued
        {
            File = file.Name,
            BeatId = beat.Id,
            PanelId = panelId,
            Tokens = resolver.Values,
        };
    }

    /// <summary>Rule 8: a display clone of the queued panel with its text filled from the stored
    /// token values. The cached panel is never mutated. Null when the panel has gone.</summary>
    private static NarrativeEncounterData Render(ChronicleQueued entry)
    {
        if (entry == null)
            return null;
        ChronicleFile file = null;
        if (!string.IsNullOrEmpty(entry.File))
            _fileByName.TryGetValue(entry.File, out file);
        var panel = file != null ? Panel(file.EncounterFile, entry.PanelId) : null;
        if (panel == null)
        {
            GD.PushError($"ChronicleScheduler: queued panel {entry.PanelId} ({entry.File}, beat {entry.BeatId}) is missing; dropped.");
            return null;
        }
        var values = entry.Tokens ?? new Dictionary<string, string>();
        return BuildDisplay(panel, s => FillWith(s, values));
    }

    /// <summary>Copy a panel, passing every piece of text through <paramref name="fill"/>.
    /// Choices are copied with EncounterChoice.Clone, so every reward verb rides along.</summary>
    private static NarrativeEncounterData BuildDisplay(NarrativeEncounterData panel, Func<string, string> fill)
    {
        var clone = new NarrativeEncounterData
        {
            Id = panel.Id,
            Title = fill(panel.Title),
            Body = fill(panel.Body),
            TerrainTags = panel.TerrainTags != null ? new List<string>(panel.TerrainTags) : new List<string>(),
            RegionTags = panel.RegionTags != null ? new List<string>(panel.RegionTags) : new List<string>(),
            RequiredFlag = panel.RequiredFlag ?? "",
            ArchmageId = panel.ArchmageId ?? "",
        };
        if (panel.Choices != null)
            foreach (var c in panel.Choices)
            {
                if (c == null) continue;
                var copy = c.Clone();
                copy.Label = fill(c.Label);
                copy.ResultText = fill(c.ResultText);
                clone.Choices.Add(copy);
            }
        return clone;
    }

    /// <summary>Queue a resolved panel. One that is already waiting, with the same text, is not
    /// queued twice (a beat with no once flag, such as the Deal's envoy, can fire again before
    /// the player has read the first).</summary>
    private static void Enqueue(GuildSaveData save, ChronicleQueued entry)
    {
        var queue = save.Cycle.ChronicleQueue;
        foreach (var q in queue)
            if (q != null && q.File == entry.File && q.BeatId == entry.BeatId
                && q.PanelId == entry.PanelId && SameTokens(q.Tokens, entry.Tokens))
                return;
        queue.Add(entry);
        SaveManager.MarkDirty();
    }

    private static NarrativeEncounterData PopQueued(GuildSaveData save)
    {
        var queue = save.Cycle.ChronicleQueue;
        while (queue.Count > 0)
        {
            var entry = queue[0];
            queue.RemoveAt(0);
            SaveManager.MarkDirty();
            var panel = Render(entry);
            if (panel != null)
                return panel;
        }
        return null;
    }

    /// <summary>B-1: a save from before the prologue (no PrologueDirector.BegunFlag) reads the
    /// full skip's recap once. Null for every save founded since, which plays the prologue,
    /// and once the prologue is complete or its stated intention asked.</summary>
    private static NarrativeEncounterData PrologueBridge(GuildSaveData save)
    {
        if (save.HasFlag("prologue_complete") || save.HasFlag("stated_intention_set")
            || HasMeta(save, PrologueDirector.BegunFlag))
            return null;
        var recap = Panel(PrologueEncounterFile, PrologueRecapId);
        if (recap == null)
            return null;
        var resolver = new TokenResolver(save, null, null, Context(save), new Dictionary<string, string>());
        return BuildDisplay(recap, resolver.Fill);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Hooks the raise sites call (docs/story_wiring_v1.md, Hooks and raise sites)
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>on_settlement_enter from the atlas: an NPC city, or the guild home.</summary>
    public static void RaiseSettlementEnter(bool home, string kingdom, string settlementName)
    {
        RaiseQueued("on_settlement_enter", new Dictionary<string, string>
        {
            ["home"] = Bool(home),
            ["kingdom"] = kingdom ?? "",
            ["settlement_name"] = settlementName ?? "",
            ["audience"] = "false",
        });
    }

    /// <summary>on_settlement_enter from an audience (task 9: audience=true), with the seat's
    /// name, so a beat that waits for a real town can tell the two apart.</summary>
    public static void RaiseAudienceSettlement(string archmageId)
    {
        var save = SaveManager.ActiveSave;
        if (!Live(save) || string.IsNullOrEmpty(archmageId))
            return;
        var cycle = save.Cycle;
        string kingdom = "", name = "";
        if (cycle.Kingdoms != null)
            foreach (var kv in cycle.Kingdoms)
                if (kv.Value != null && kv.Value.ArchmageId == archmageId)
                {
                    kingdom = kv.Key;
                    name = kv.Value.DisplayName ?? "";
                    break;
                }
        if (kingdom.Length > 0 && cycle.World?.Settlements != null)
            foreach (var s in cycle.World.Settlements)
                if (s != null && s.IsSeat && s.KingdomId == kingdom && !string.IsNullOrEmpty(s.Name))
                {
                    name = s.Name;
                    break;
                }
        RaiseQueued("on_settlement_enter", new Dictionary<string, string>
        {
            ["home"] = "false",
            ["kingdom"] = kingdom,
            ["settlement_name"] = name,
            ["audience"] = "true",
        });
    }

    /// <summary>on_forces_met, on every crossing (the dossier stamp runs only on the first).
    /// The Wilds and the Astrologer are not seats, so they are ignored.</summary>
    public static void RaiseForcesMet(string archmageId)
    {
        var save = SaveManager.ActiveSave;
        if (!Live(save) || string.IsNullOrEmpty(archmageId) || archmageId == "wilds" || archmageId == "astrologer")
            return;
        var campaign = save.Cycle.Campaign;
        bool corrupted = campaign != null && campaign.GetDisposition(archmageId) == ArchmageDisposition.Corrupted;
        RaiseQueued("on_forces_met", new Dictionary<string, string>
        {
            ["archmage"] = archmageId,
            ["coconspirator"] = Bool(archmageId == Traitor(save)),
            ["corrupted"] = Bool(corrupted),
            ["remembered"] = Bool(HasMeta(save, $"chronicle_first_contact_{archmageId}")),
        });
    }

    /// <summary>on_combat_start, at the round 1 player turn. A fight is borrowed when any enemy
    /// is the Astrologer's, a fracture keeper, or belongs to a seat lost to corruption. The
    /// reading is kept for on_combat_end, with the weather the router launched the fight in.</summary>
    public static void RaiseCombatStart(IEnumerable<Unit> enemies)
    {
        var router = EncounterRouter.Instance;
        _combatWeather = router != null ? router.SavedWeather.ToString().ToLowerInvariant() : "clear";
        _combatBorrowed = false;
        var save = SaveManager.ActiveSave;
        var campaign = save?.Cycle?.Campaign;
        if (enemies != null)
            foreach (var u in enemies)
            {
                if (u == null || !GodotObject.IsInstanceValid(u))
                    continue;
                string faction = u.FactionId ?? "";
                bool corruptSeat = campaign != null && faction.Length > 0
                                   && campaign.GetDisposition(faction) == ArchmageDisposition.Corrupted;
                if (faction == "astrologer" || u.DefinitionId == "fracture_keeper" || corruptSeat)
                {
                    _combatBorrowed = true;
                    break;
                }
            }
        RaiseQueued("on_combat_start", new Dictionary<string, string>
        {
            ["borrowed"] = Bool(_combatBorrowed),
            ["prologue"] = Bool(!string.IsNullOrEmpty(RegisterManager.PrologueMode)),
        });
    }

    /// <summary>on_combat_end, won or lost, from EncounterRouter.OnCombatFinished, the one place
    /// every routed fight ends (the expedition's, the campus's and the strategic map's).
    /// <paramref name="archmageId"/> is the fight's attributed archmage ("" or "wilds" for none).</summary>
    public static void RaiseCombatEnd(bool won, string archmageId)
    {
        bool borrowed = _combatBorrowed;
        string weather = string.IsNullOrEmpty(_combatWeather) ? "clear" : _combatWeather;
        _combatBorrowed = false;
        _combatWeather = "clear";

        var save = SaveManager.ActiveSave;
        if (!Live(save))
            return;
        string arch = archmageId == "wilds" ? "" : (archmageId ?? "");
        var campaign = save.Cycle.Campaign;
        bool corruptedSeat = arch.Length > 0 && campaign != null
                             && campaign.GetDisposition(arch) == ArchmageDisposition.Corrupted;
        RaiseQueued("on_combat_end", new Dictionary<string, string>
        {
            ["won"] = Bool(won),
            ["borrowed"] = Bool(borrowed),
            ["prologue"] = Bool(!string.IsNullOrEmpty(RegisterManager.PrologueMode)),
            ["archmage"] = arch,
            ["corrupted_seat"] = Bool(corruptedSeat),
            ["weather"] = weather,
        });
    }

    /// <summary>on_declaration, right after DeclarationService.Declare succeeds. Returns the
    /// conferral panel (immediate) for the caller to show in place of its dialog. The callback's
    /// shape follows Part I's I-5: match, differs, differs_lost, adept, unstated, later or none.</summary>
    public static NarrativeEncounterData RaiseDeclaration(string school)
    {
        var save = SaveManager.ActiveSave;
        if (!Live(save) || string.IsNullOrEmpty(school))
            return null;
        string key = school.Trim().ToLowerInvariant();
        string intention = StatedIntention(save);
        if (intention.Length == 0)
            intention = "unstated";   // a save from before the prologue never stated one
        bool first = !HasMeta(save, "chronicle_first_declaration");
        string shape;
        if (first)
        {
            if (intention == "unstated") shape = "unstated";
            else if (intention == "adept") shape = "adept";
            else if (intention == key) shape = "match";
            else shape = IntendedFacultyLost(save, intention) ? "differs_lost" : "differs";
        }
        else
        {
            shape = intention == key ? "later" : "none";
        }
        return Raise("on_declaration", new Dictionary<string, string>
        {
            ["school"] = key,
            ["first"] = Bool(first),
            ["shape"] = shape,
            ["answered"] = Bool(shape == "match" || shape == "later"),
            ["faculty"] = FacultyId(save, key),
            ["intention"] = intention,
        });
    }

    /// <summary>on_shard_zone: how is "surveyed" (a field survey finished) or "entered" (the
    /// party stepped onto the zone's ground); fragment is the zone's key.</summary>
    public static void RaiseShardZone(string how, string fragment)
    {
        RaiseQueued("on_shard_zone", new Dictionary<string, string>
        {
            ["how"] = how ?? "",
            ["fragment"] = fragment ?? "",
        });
    }

    /// <summary>The Ceremony Hall was clicked (C2). Always returns a panel while the story data
    /// is in: the next angle when a fragment is waiting, otherwise a due report, otherwise the
    /// hall at rest (the fallback beats).</summary>
    public static NarrativeEncounterData RaiseHall()
    {
        var save = SaveManager.ActiveSave;
        if (!Live(save))
            return null;
        int placed = 0;
        string pending = "";
        foreach (var key in ShardZones.FragmentKeys)
        {
            if (save.HasFlag($"fragment_{key}_placed"))
                placed++;
            else if (pending.Length == 0 && save.HasFlag($"fragment_{key}_collected"))
                pending = key;
        }
        return Raise("on_hall", new Dictionary<string, string>
        {
            ["pending"] = Bool(pending.Length > 0),
            ["pending_fragment"] = pending,
            ["placed"] = Str(placed),
        });
    }

    /// <summary>on_companion_join: a companion recruited, found, or setting out on a sortie.
    /// Sets the timeline flag met_&lt;id&gt;, which the next reading remembers as known_&lt;id&gt;.</summary>
    public static void RaiseCompanionJoin(string companionId)
    {
        var save = SaveManager.ActiveSave;
        if (!Live(save) || string.IsNullOrEmpty(companionId))
            return;
        var arc = CompanionArcLoader.Load(companionId);
        string rememberPrefix = $"remember_{companionId}_";
        bool known = HasMeta(save, $"known_{companionId}")
                     || (save.Ledger.MetaNarrativeFlags?.Exists(
                            f => f != null && f.StartsWith(rememberPrefix, StringComparison.Ordinal)) ?? false);
        bool remembered = arc != null && HasMeta(save, arc.ArcCompleteMetaFlag);
        bool anchored = arc != null && HasMeta(save, arc.HallAnchorFlag);
        RaiseQueued("on_companion_join", new Dictionary<string, string>
        {
            ["companion"] = companionId,
            ["known"] = Bool(known),
            ["remembered"] = Bool(remembered),
            ["anchored"] = Bool(anchored),
            ["reading"] = Str(save.Cycle.CycleNumber),
        });
        if (save.Cycle.SetFlag($"met_{companionId}"))
            SaveManager.MarkDirty();
    }

    /// <summary>on_audience_open, before the audience is built (C5 item 5). Returns the arc's
    /// immediate panel, if any: show it first and open the audience when it completes.</summary>
    public static NarrativeEncounterData RaiseAudienceOpen(string archmageId)
    {
        var save = SaveManager.ActiveSave;
        var campaign = save?.Cycle?.Campaign;
        if (!Live(save) || campaign == null || string.IsNullOrEmpty(archmageId))
            return null;
        bool uniteOpen = campaign.ResolutionOptions(archmageId, save.HasFlag).canUnite;
        return Raise("on_audience_open", new Dictionary<string, string>
        {
            ["archmage"] = archmageId,
            ["unite_open"] = Bool(uniteOpen),
        });
    }

    /// <summary>A field party moved one tile on the world map (FieldMarch). The Wizard's Party
    /// walking onto a shard zone's ground is on_shard_zone with how=entered.</summary>
    public static void RaisePartyStepped(CycleState cycle, FieldParty party, int fromX, int fromY)
    {
        if (cycle?.World == null || !CarriesWizard(party))
            return;
        var zone = cycle.World.ShardZoneAt(party.X, party.Y);
        if (zone == null || zone == cycle.World.ShardZoneAt(fromX, fromY))
            return;
        RaiseShardZone("entered", zone.FragmentKey);
    }

    /// <summary>A field party landed (FieldMarch.Arrive or Teleport). The Wizard's Party on the
    /// Convergence's tile is the fallen seat (Parts II and III), raised on every arrival; its
    /// panel waits for the strategic view's next quiet moment.</summary>
    public static void RaisePartyArrived(CycleState cycle, FieldParty party)
    {
        if (cycle?.World == null || !CarriesWizard(party))
            return;
        if (party.X == cycle.World.ConvergenceX && party.Y == cycle.World.ConvergenceY)
            RaiseQueued("on_fallen_seat");
    }

    /// <summary>on_new_reading: every campus quiet moment while a reading has closed (the once
    /// flags keep it to one showing per reading).</summary>
    private static NarrativeEncounterData RaiseNewReading(GuildSaveData save)
    {
        var history = save.Ledger.LoopHistory;
        if (history == null || history.Count == 0)
            return null;
        var last = history[history.Count - 1];
        bool echoes = false;
        if (save.Cycle.WorldFlags != null)
            foreach (var f in save.Cycle.WorldFlags)
                if (f != null && f.StartsWith("echo_", StringComparison.Ordinal)
                    && f.EndsWith("_eligible", StringComparison.Ordinal))
                {
                    echoes = true;
                    break;
                }
        return Raise("on_new_reading", new Dictionary<string, string>
        {
            ["previous"] = Str(last?.CycleNumber ?? 0),
            ["outcome"] = last?.Outcome ?? "",
            ["echoes"] = Bool(echoes),
        });
    }

    // ── C5: deeds and sway ───────────────────────────────────────────────

    /// <summary>A deed of this reading (C5 item 2): adds one to the tally, then raises on_deed.
    /// Deed sites cannot show a panel, so every panel waits on the queue.</summary>
    public static void RaiseDeed(string deed, bool given = false)
    {
        var save = SaveManager.ActiveSave;
        if (!Live(save) || string.IsNullOrEmpty(deed))
            return;
        var tally = save.Cycle.ReadingDeeds;
        tally.TryGetValue(deed, out int count);
        tally[deed] = count + 1;
        SaveManager.MarkDirty();
        RaiseQueued("on_deed", new Dictionary<string, string>
        {
            ["deed"] = deed,
            ["given"] = Bool(given),
        });
    }

    /// <summary>A narrative encounter from a random pool was answered: a road deed. Audiences
    /// (an archmage id) and chronicle panels never count. given=true when the choice costs
    /// something and grants nothing (task 9).</summary>
    public static void RaiseRoadDeed(NarrativeEncounterData encounter, EncounterChoice choice)
    {
        if (encounter == null || choice == null || !string.IsNullOrEmpty(encounter.ArchmageId))
            return;
        if (IsChroniclePanel(encounter.Id))
            return;
        RaiseDeed("road", Costs(choice) && !Grants(choice));
    }

    /// <summary>C5 item 1: the one sway helper every narrative applier calls after its other
    /// effects. ShiftSentiment applies the archmage's resistance and ignores answered seats.</summary>
    public static void ApplySway(EncounterChoice choice)
    {
        if (choice == null || string.IsNullOrEmpty(choice.SwayArchmage) || choice.SwayAmount == 0)
            return;
        var campaign = SaveManager.ActiveSave?.Cycle?.Campaign;
        if (campaign == null)
            return;
        campaign.ShiftSentiment(choice.SwayArchmage, choice.SwayAmount);
        SaveManager.MarkDirty();
    }

    private static bool Costs(EncounterChoice c) =>
        c.GoldDelta < 0 || c.HPDelta < 0 || c.StepDelta < 0 || c.ReputationAmount < 0;

    private static bool Grants(EncounterChoice c) =>
        c.GoldDelta > 0 || c.HPDelta > 0 || c.StepDelta > 0 || c.ReputationAmount > 0
        || !string.IsNullOrEmpty(c.CardReward) || !string.IsNullOrEmpty(c.SpellReward)
        || !string.IsNullOrEmpty(c.ItemReward) || !string.IsNullOrEmpty(c.CompanionUnlock)
        || c.CardCodex || !string.IsNullOrEmpty(c.LoreId) || c.RevealPois > 0;

    // ── A reading closes ─────────────────────────────────────────────────

    /// <summary>Called by SaveManager.BeginNewCycle once the new CycleState exists. Every
    /// companion met in the ending reading is known to the next (Eternal known_&lt;id&gt;), and a
    /// queued panel whose beat is Eternal carries over: its once flag is already written, so
    /// dropping it with the timeline would lose it for good.</summary>
    public static void CloseReading(CycleState ending, CycleState next)
    {
        var save = SaveManager.ActiveSave;
        if (ending == null || next == null || save?.Ledger == null)
            return;
        if (ending.WorldFlags != null)
            foreach (var f in ending.WorldFlags)
                if (f != null && f.Length > 4 && f.StartsWith("met_", StringComparison.Ordinal))
                    SetMeta(save, "known_" + f.Substring(4));
        if (ending.ChronicleQueue == null || ending.ChronicleQueue.Count == 0)
            return;
        EnsureLoaded();
        next.ChronicleQueue ??= new List<ChronicleQueued>();
        foreach (var entry in ending.ChronicleQueue)
        {
            if (entry == null || string.IsNullOrEmpty(entry.BeatId))
                continue;
            if (_beatById.TryGetValue(entry.BeatId, out var beat)
                && string.Equals(beat.Layer, "Eternal", StringComparison.OrdinalIgnoreCase))
                next.ChronicleQueue.Add(entry);
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Context arguments and tokens
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>The context arguments, computed once per raise (doc: Context arguments).</summary>
    private static Dictionary<string, string> Context(GuildSaveData save)
    {
        var cycle = save.Cycle;
        var (weakest1, weakest2) = Weakest(cycle.ReadingDeeds);
        int answered = 0, corrupted = 0;
        var campaign = cycle.Campaign;
        if (campaign?.RegionArchmageMap != null)
        {
            var seen = new HashSet<string>();
            foreach (var id in campaign.RegionArchmageMap.Values)
            {
                if (string.IsNullOrEmpty(id) || !seen.Add(id))
                    continue;
                var d = campaign.GetDisposition(id);
                if (d == ArchmageDisposition.Allied || d == ArchmageDisposition.Coerced
                    || d == ArchmageDisposition.Overthrown || d == ArchmageDisposition.Corrupted)
                    answered++;
                if (d == ArchmageDisposition.Corrupted)
                    corrupted++;
            }
        }
        return new Dictionary<string, string>
        {
            ["reading"] = Str(cycle.CycleNumber),
            ["practical"] = Practical(save),
            ["weakest_1"] = weakest1,
            ["weakest_2"] = weakest2,
            ["traitor"] = Traitor(save),
            ["answered_seats"] = Str(answered),
            ["corrupted_seats"] = Str(corrupted),
            ["lunations_left"] = Str(cycle.Calendar?.LunationsRemaining ?? 0),
        };
    }

    /// <summary>C5 item 3: the two deeds with the lowest tally this reading, ties going table,
    /// then road, then field. An empty tally gives table and road.</summary>
    private static (string, string) Weakest(Dictionary<string, int> tally)
    {
        int Count(string deed) => tally != null && tally.TryGetValue(deed, out int n) ? n : 0;
        var order = Deeds.OrderBy(d => Count(d)).ThenBy(d => Array.IndexOf(DeedTieOrder, d)).ToArray();
        return (order[0], order[1]);
    }

    /// <summary>The school (lower case) of the save's prologue_practical_&lt;school&gt; flag, or "".</summary>
    private static string Practical(GuildSaveData save)
    {
        foreach (CardSchool s in Enum.GetValues(typeof(CardSchool)))
        {
            string key = s.ToString().ToLowerInvariant();
            if (HasMeta(save, $"prologue_practical_{key}"))
                return key;
        }
        return "";
    }

    /// <summary>The stated intention (R7b): a school (lower case), "unstated", or "" when the
    /// save was never asked.</summary>
    private static string StatedIntention(GuildSaveData save)
    {
        if (HasMeta(save, "stated_intention_unstated"))
            return "unstated";
        foreach (CardSchool s in Enum.GetValues(typeof(CardSchool)))
        {
            string key = s.ToString().ToLowerInvariant();
            if (HasMeta(save, $"stated_intention_{key}"))
                return key;
        }
        return "";
    }

    /// <summary>C3: this save's pinned co-conspirator, or "" before the pin.</summary>
    private static string Traitor(GuildSaveData save) => save?.Ledger?.CoConspiratorId ?? "";

    /// <summary>A token's value from code, or null: the globals, then a hook or context argument,
    /// then the derived values (doc: Tokens the scheduler computes). An argument that is present
    /// but empty is still a value, except where a token names its own fallback.</summary>
    private static string CodeToken(GuildSaveData save, string t, Dictionary<string, string> all)
    {
        string global = GlobalToken(save, t);
        if (global != null)
            return global;
        if (all.TryGetValue(t, out var arg) && !string.IsNullOrEmpty(arg))
            return arg;
        switch (t)
        {
            case "settlement_name":
                return "The town";
            case "companion_name":
                return NonEmpty(CompanionName(save, Arg(all, "companion")), "Your old companion");
            case "archmage_full":
                return NonEmpty(ArchmageRegistry.Get(Arg(all, "archmage"))?.FullName, "one of the archmagi");
            case "corrupted_faction":
                return NonEmpty(ArchmageRegistry.Get(Arg(all, "archmage"))?.CorruptedFactionName, "the Astrologer's");
            case "faculty_name":
                return NonEmpty(FacultyName(save, Arg(all, "faculty")), "someone who stayed");
            case "intended":
                return Intended(save);
            case "door_region":
                return NonEmpty(DoorRegion(save), "somewhere new");
            case "traitor_redemption":
                return NonEmpty(ArchmageRegistry.Get(Traitor(save))?.FinaleRedemptionLine,
                                "I know where I am standing this time. I chose it.");
            case "traitor_second":
                return NonEmpty(ArchmageRegistry.Get(Traitor(save))?.FinaleSecondBetrayalLine,
                                "He showed me the ending again. I am only choosing where to stand when it arrives.");
            case "anchor_name":
                return NonEmpty(CompanionName(save, Arg(all, "anchor")), "one of the guild");
        }
        return all.ContainsKey(t) ? "" : null;
    }

    /// <summary>A global token's value for the loaded save (wizard_name, guild_name,
    /// traitor_full, traitor_short), with its fallback; null for any other name or no save.
    /// The prologue director fills its panels' globals through this.</summary>
    public static string Token(string name)
    {
        var save = SaveManager.ActiveSave;
        return save != null && !string.IsNullOrEmpty(name) ? GlobalToken(save, name) : null;
    }

    /// <summary>The four globals, with their fallbacks; null for any other name.</summary>
    private static string GlobalToken(GuildSaveData save, string t)
    {
        switch (t)
        {
            case "wizard_name":
                return NonEmpty(save.Cycle?.WizardName, "the candidate");
            case "guild_name":
                return NonEmpty(save.Ledger?.GuildName, "the guild");
            case "traitor_full":
                return NonEmpty(ArchmageRegistry.Get(Traitor(save))?.FullName, "one of the archmagi");
            case "traitor_short":
                return NonEmpty(ArchmageRegistry.Get(Traitor(save))?.CallName, "the traitor");
        }
        return null;
    }

    private static string Intended(GuildSaveData save)
    {
        string key = StatedIntention(save);
        if (key.Length == 0 || key == "unstated")
            return "none given";
        foreach (CardSchool s in Enum.GetValues(typeof(CardSchool)))
            if (s.ToString().ToLowerInvariant() == key)
                return s.ToString();
        return key;
    }

    /// <summary>The region or kingdom name of this reading's guild home.</summary>
    private static string DoorRegion(GuildSaveData save)
    {
        var cycle = save.Cycle;
        WorldSettlement home = null;
        if (cycle.World?.Settlements != null)
            foreach (var s in cycle.World.Settlements)
                if (s != null && s.IsGuildHome)
                {
                    home = s;
                    break;
                }
        if (home == null)
            return "";
        if (cycle.Kingdoms != null && !string.IsNullOrEmpty(home.KingdomId)
            && cycle.Kingdoms.TryGetValue(home.KingdomId, out var ks)
            && !string.IsNullOrEmpty(ks?.DisplayName))
            return ks.DisplayName;
        return home.Name ?? "";
    }

    /// <summary>A faculty's clean name: an archmage's full name, or a companion's name, never
    /// the "(arc 2)" / "(allied)" suffix FindFacultySource adds for the UI.</summary>
    private static string FacultyName(GuildSaveData save, string id)
    {
        if (string.IsNullOrEmpty(id))
            return "";
        var def = ArchmageRegistry.Get(id);
        if (def != null)
            return NonEmpty(def.FullName, def.DisplayName);
        return CompanionName(save, id);
    }

    /// <summary>Who in this reading teaches <paramref name="school"/>: the id, by
    /// DeclarationService.FindFacultySource's rule (a recruited, living companion of that school
    /// at arc stage 2 or later, else an Allied archmage of that school), or "".</summary>
    private static string FacultyId(GuildSaveData save, string school)
    {
        if (save?.Cycle?.Companions != null)
            foreach (var c in save.Cycle.Companions)
                if (c != null && c.IsRecruited && !c.IsPermadead
                    && c.ArcStage >= DeclarationService.FacultyArcStage
                    && string.Equals(c.School, school, StringComparison.OrdinalIgnoreCase))
                    return c.Id;
        var dispositions = save?.Cycle?.Campaign?.Dispositions;
        if (dispositions != null)
            foreach (var kv in dispositions)
            {
                if (kv.Value != ArchmageDisposition.Allied)
                    continue;
                var def = ArchmageRegistry.Get(kv.Key);
                if (def != null && string.Equals(def.School, school, StringComparison.OrdinalIgnoreCase))
                    return kv.Key;
            }
        return "";
    }

    /// <summary>differs_lost (Part I, I-5): nobody can teach the intended school this reading,
    /// because its seat is Coerced, Overthrown or Corrupted, or its companion is dead.</summary>
    private static bool IntendedFacultyLost(GuildSaveData save, string intention)
    {
        if (FacultyId(save, intention).Length > 0)
            return false;
        var dispositions = save.Cycle.Campaign?.Dispositions;
        if (dispositions != null)
            foreach (var kv in dispositions)
            {
                var def = ArchmageRegistry.Get(kv.Key);
                if (def == null || !string.Equals(def.School, intention, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (kv.Value == ArchmageDisposition.Coerced || kv.Value == ArchmageDisposition.Overthrown
                    || kv.Value == ArchmageDisposition.Corrupted)
                    return true;
            }
        if (save.Cycle.Companions != null)
            foreach (var c in save.Cycle.Companions)
                if (c != null && c.IsPermadead && string.Equals(c.School, intention, StringComparison.OrdinalIgnoreCase))
                    return true;
        return false;
    }

    private static string CompanionName(GuildSaveData save, string id)
    {
        if (string.IsNullOrEmpty(id) || save?.Cycle?.Companions == null)
            return "";
        foreach (var c in save.Cycle.Companions)
            if (c != null && c.Id == id)
                return c.Name ?? "";
        return "";
    }

    /// <summary>The Wizard's Party (A1). Until A1's CarriesWizard flag exists, the primary field
    /// party: at baseline the only one, and a detachment never carries the wizard.</summary>
    private static bool CarriesWizard(FieldParty party) =>
        party != null && party.Id == ExpeditionAnchors.PrimaryFieldPartyId && string.IsNullOrEmpty(party.ParentId);

    /// <summary>Resolves a beat's tokens lazily, in the doc's order: globals, then arguments, then
    /// derived values, then the beat's declared line tokens. Records every value it resolves, so
    /// the queue can redraw the panel later exactly as it reads now.</summary>
    private sealed class TokenResolver
    {
        private readonly GuildSaveData _save;
        private readonly ChronicleFile _file;
        private readonly ChronicleBeat _beat;
        private readonly Dictionary<string, string> _all;
        private readonly Dictionary<string, string> _extra;
        private readonly HashSet<string> _declared;

        public readonly Dictionary<string, string> Values = new();

        public TokenResolver(GuildSaveData save, ChronicleFile file, ChronicleBeat beat,
                             Dictionary<string, string> all, Dictionary<string, string> extra)
        {
            _save = save;
            _file = file;
            _beat = beat;
            _all = all ?? new Dictionary<string, string>();
            _extra = extra ?? new Dictionary<string, string>();
            _declared = beat?.Tokens != null ? new HashSet<string>(beat.Tokens) : new HashSet<string>();
        }

        /// <summary>Fill the text, expand once more (a line may hold tokens), drop paragraphs that
        /// resolved to nothing, and trim the rest. A token with no value stays literal.</summary>
        public string Fill(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text ?? "";
            string s = text;
            for (int i = 0; i < 2; i++)
                s = TokenRx.Replace(s, m => Lookup(m.Groups[1].Value) ?? m.Value);
            return Paragraphs(s);
        }

        private string Lookup(string t)
        {
            if (Values.TryGetValue(t, out var cached))
                return cached;
            string v = CodeToken(_save, t, _all);
            if (v == null && _file != null && _beat != null && _declared.Contains(t)
                && _file.LineTokens.TryGetValue(t, out var key) && !string.IsNullOrEmpty(key))
            {
                var la = new Dictionary<string, string>(_all);
                foreach (var kv in _extra)
                    la[kv.Key] = kv.Value;
                if (_beat.LineArgs.TryGetValue(t, out var overrides) && overrides != null)
                    foreach (var kv in overrides)
                        la[kv.Key] = Sub(kv.Value, _all);
                v = FindLine(_file, key, la);
                if (v == null)
                    GD.PushWarning($"ChronicleScheduler: beat {_beat.Id} has no line for {t} ({key}).");
            }
            if (v != null)
                Values[t] = v;
            return v;
        }
    }

    /// <summary>The queue's redraw: the stored values, two passes, the same paragraph rule.</summary>
    private static string FillWith(string text, Dictionary<string, string> values)
    {
        if (string.IsNullOrEmpty(text))
            return text ?? "";
        string s = text;
        for (int i = 0; i < 2; i++)
            s = TokenRx.Replace(s, m => values.TryGetValue(m.Groups[1].Value, out var v) && v != null ? v : m.Value);
        return Paragraphs(s);
    }

    /// <summary>Drop any paragraph that resolves to nothing, so an empty {remembrance} leaves no
    /// gap, and trim the rest (rule 7).</summary>
    private static string Paragraphs(string s)
    {
        var kept = new List<string>();
        foreach (var part in s.Split("\n\n"))
        {
            string p = part.Trim();
            if (p.Length > 0)
                kept.Add(p);
        }
        return string.Join("\n\n", kept);
    }

    /// <summary>The first line of <paramref name="key"/> whose conditions all match, or null
    /// (the rule of RegisterBark.Matches).</summary>
    private static string FindLine(ChronicleFile file, string key, Dictionary<string, string> args)
    {
        foreach (var line in file.Lines)
            if (line.Key == key && WhenMatches(line.WhenNorm, args))
                return line.Text ?? "";
        return null;
    }

    private static bool WhenMatches(Dictionary<string, string[]> when, Dictionary<string, string> args)
    {
        if (when == null)
            return true;
        foreach (var kv in when)
        {
            args.TryGetValue(kv.Key, out var have);
            have ??= "";
            bool any = false;
            foreach (var want in kv.Value)
                if (string.Equals(have, want ?? "", StringComparison.OrdinalIgnoreCase))
                {
                    any = true;
                    break;
                }
            if (!any)
                return false;
        }
        return true;
    }

    /// <summary>{arg} substitution in flag names, panel ids and line arguments. An unknown name
    /// stays literal.</summary>
    private static string Sub(string s, Dictionary<string, string> args)
    {
        if (string.IsNullOrEmpty(s))
            return "";
        if (s.IndexOf('{') < 0)
            return s;
        return TokenRx.Replace(s, m => args.TryGetValue(m.Groups[1].Value, out var v) && v != null ? v : m.Value);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Loading
    // ════════════════════════════════════════════════════════════════════════

    private static void EnsureLoaded()
    {
        if (_files != null)
            return;
        _files = new List<ChronicleFile>();
        _fileByName = new Dictionary<string, ChronicleFile>();
        _beatById = new Dictionary<string, ChronicleBeat>();
        _panels = new Dictionary<string, Dictionary<string, NarrativeEncounterData>>();

        AssertRoundTripOnce();

        // DirAccess.Open returns null for a missing directory, in the editor and in an export.
        using var dir = DirAccess.Open(STORY_DIR);
        if (dir == null)
        {
            GD.Print($"ChronicleScheduler: no story directory at {STORY_DIR}");
            return;
        }
        var names = new List<string>();
        dir.ListDirBegin();
        for (string fn = dir.GetNext(); fn != ""; fn = dir.GetNext())
        {
            if (dir.CurrentIsDir() || !fn.EndsWith(".json"))
                continue;
            names.Add(fn);
        }
        dir.ListDirEnd();
        names.Sort(StringComparer.Ordinal);

        var loaded = new List<ChronicleFile>();
        foreach (var fn in names)
        {
            try
            {
                using var f = FileAccess.Open($"{STORY_DIR}{fn}", FileAccess.ModeFlags.Read);
                if (f == null)
                    continue;
                var file = JsonSerializer.Deserialize<ChronicleFile>(f.GetAsText(), JsonOptions);
                if (file == null)
                    continue;
                file.Name = fn.Substring(0, fn.Length - ".json".Length);
                Normalize(file);
                loaded.Add(file);
            }
            catch (Exception e)
            {
                GD.PrintErr($"ChronicleScheduler: error loading {fn}: {e.Message}");
            }
        }

        // Load order: part files by number, then every other file by name. Beats are evaluated
        // in this order, and in file order within a file.
        _files = loaded
            .OrderBy(f => f.Part > 0 ? 0 : 1)
            .ThenBy(f => f.Part)
            .ThenBy(f => f.Name, StringComparer.Ordinal)
            .ToList();

        foreach (var file in _files)
        {
            _fileByName[file.Name] = file;
            foreach (var beat in file.Beats)
            {
                if (string.IsNullOrEmpty(beat.Id))
                    continue;
                if (_beatById.ContainsKey(beat.Id))
                    GD.PushError($"ChronicleScheduler: beat id {beat.Id} repeats in {file.Name}; ids must be unique across Data/Story.");
                else
                    _beatById[beat.Id] = beat;
            }
        }
        CheckFiles();

        int beats = _files.Sum(f => f.Beats.Count);
        GD.Print($"ChronicleScheduler: loaded {_files.Count} story file(s), {beats} beat(s).");
    }

    /// <summary>Null-proof every collection, and normalize every when.</summary>
    private static void Normalize(ChronicleFile file)
    {
        file.Arc ??= "";
        file.EncounterFile ??= "";
        file.LineTokens ??= new Dictionary<string, string>();
        file.Beats ??= new List<ChronicleBeat>();
        file.Lines ??= new List<ChronicleLine>();
        file.Beats.RemoveAll(b => b == null);
        file.Lines.RemoveAll(l => l == null);
        foreach (var b in file.Beats)
        {
            b.Id ??= "";
            b.Hook ??= "";
            b.Layer ??= "";
            b.Once ??= "";
            b.RememberFlag ??= "";
            b.Group ??= "";
            b.Show ??= "";
            b.Bark ??= "";
            b.Lore ??= "";
            b.Delivery ??= "";
            b.Requires ??= new List<string>();
            b.Excludes ??= new List<string>();
            b.AlsoSetMeta ??= new List<string>();
            b.AlsoSetTimeline ??= new List<string>();
            b.Tokens ??= new List<string>();
            b.LineArgs ??= new Dictionary<string, Dictionary<string, string>>();
            b.FlagArgs ??= new Dictionary<string, string>();
            b.WhenNorm = NormalizeWhen(b.When);
        }
        foreach (var l in file.Lines)
        {
            l.Key ??= "";
            l.Text ??= "";
            l.WhenNorm = NormalizeWhen(l.When);
        }
    }

    private static Dictionary<string, string[]> NormalizeWhen(Dictionary<string, JsonElement> when)
    {
        var norm = new Dictionary<string, string[]>();
        if (when == null)
            return norm;
        foreach (var kv in when)
        {
            var el = kv.Value;
            norm[kv.Key] = el.ValueKind == JsonValueKind.Array
                ? el.EnumerateArray().Select(x => ElementText(x)).ToArray()
                : new[] { ElementText(el) };
        }
        return norm;
    }

    private static string ElementText(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString() ?? "",
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => "",
        JsonValueKind.Undefined => "",
        _ => el.GetRawText(),
    };

    /// <summary>The load-time checks (doc: Asserts at load). Errors, not exceptions: a broken
    /// file should be loud in the log and still let the rest of the chronicle run.</summary>
    private static void CheckFiles()
    {
        foreach (var file in _files)
        {
            if (string.IsNullOrEmpty(file.EncounterFile))
                GD.PushError($"ChronicleScheduler: {file.Name} names no encounterFile.");
            var lineKeys = new HashSet<string>();
            foreach (var line in file.Lines)
                lineKeys.Add(line.Key);
            foreach (var kv in file.LineTokens)
                if (string.IsNullOrEmpty(kv.Value) || !lineKeys.Contains(kv.Value))
                    GD.PushError($"ChronicleScheduler: {file.Name}: line token {kv.Key} reads key {kv.Value}, which has no lines.");
            foreach (var beat in file.Beats)
            {
                if (beat.Show.Length > 0 && beat.Show.IndexOf('{') < 0
                    && Panel(file.EncounterFile, beat.Show) == null)
                    GD.PushError($"ChronicleScheduler: {file.Name}: beat {beat.Id} shows {beat.Show}, which is not in {file.EncounterFile}.");
                foreach (var t in beat.Tokens)
                    if (!file.LineTokens.ContainsKey(t) && !CodeTokenNames.Contains(t))
                        GD.PushError($"ChronicleScheduler: {file.Name}: beat {beat.Id} declares token {t}, which neither a line nor the scheduler provides.");
            }
        }
    }

    /// <summary>A panel by id from a panel file (loaded once, by id only: never a random pool).</summary>
    private static NarrativeEncounterData Panel(string encounterFile, string id)
    {
        if (string.IsNullOrEmpty(encounterFile) || string.IsNullOrEmpty(id))
            return null;
        if (!_panels.TryGetValue(encounterFile, out var map))
        {
            map = LoadPanels(encounterFile);
            _panels[encounterFile] = map;
        }
        return map.TryGetValue(id, out var panel) ? panel : null;
    }

    private static Dictionary<string, NarrativeEncounterData> LoadPanels(string fileNoExt)
    {
        var map = new Dictionary<string, NarrativeEncounterData>();
        string path = $"{ENCOUNTERS_DIR}{fileNoExt}.json";
        if (!FileAccess.FileExists(path))
        {
            GD.PushError($"ChronicleScheduler: panel file {path} is missing.");
            return map;
        }
        try
        {
            using var f = FileAccess.Open(path, FileAccess.ModeFlags.Read);
            if (f == null)
                return map;
            var list = JsonSerializer.Deserialize<List<NarrativeEncounterData>>(f.GetAsText(), JsonOptions);
            if (list == null)
                return map;
            foreach (var p in list)
                if (p != null && !string.IsNullOrEmpty(p.Id) && !map.ContainsKey(p.Id))
                    map[p.Id] = p;
        }
        catch (Exception e)
        {
            GD.PrintErr($"ChronicleScheduler: error loading {path}: {e.Message}");
        }
        return map;
    }

    /// <summary>True when <paramref name="id"/> is a panel of a story file or of the prologue.</summary>
    private static bool IsChroniclePanel(string id)
    {
        if (string.IsNullOrEmpty(id))
            return false;
        EnsureLoaded();
        foreach (var file in _files)
            if (Panel(file.EncounterFile, id) != null)
                return true;
        return Panel(PrologueEncounterFile, id) != null;
    }

    /// <summary>Testing hook: drop the loaded data so edited JSON reloads.</summary>
    public static void ClearCache()
    {
        _files = null;
        _fileByName = null;
        _beatById = null;
        _panels = null;
    }

    // ── Save-shape assertion (house rule for new serialized fields) ───────

    private static bool _roundTripAsserted;

    /// <summary>One-shot round trip through the REAL save options for every field the chronicle
    /// added: CycleState.ChronicleQueue (the ChronicleQueued struct), CycleState.ReadingDeeds and
    /// EternalLedger.CoConspiratorId. Mirrors GrimoireState.AssertRoundTripOnce.</summary>
    public static void AssertRoundTripOnce()
    {
        if (_roundTripAsserted)
            return;
        _roundTripAsserted = true;
        try
        {
            var cycle = new CycleState();
            cycle.ChronicleQueue.Add(new ChronicleQueued
            {
                File = "probe_file",
                BeatId = "probe_beat",
                PanelId = "probe_panel",
                Tokens = new Dictionary<string, string> { ["probe_token"] = "probe value" },
            });
            cycle.ReadingDeeds["road"] = 3;
            var ledger = new EternalLedger { CoConspiratorId = "namer" };

            var cycleBack = JsonSerializer.Deserialize<CycleState>(
                JsonSerializer.Serialize(cycle, SaveManager.JsonOptions), SaveManager.JsonOptions);
            var ledgerBack = JsonSerializer.Deserialize<EternalLedger>(
                JsonSerializer.Serialize(ledger, SaveManager.JsonOptions), SaveManager.JsonOptions);

            var q = cycleBack?.ChronicleQueue;
            bool ok = q != null && q.Count == 1 && q[0] != null
                      && q[0].File == "probe_file" && q[0].BeatId == "probe_beat" && q[0].PanelId == "probe_panel"
                      && q[0].Tokens != null && q[0].Tokens.TryGetValue("probe_token", out var token)
                      && token == "probe value"
                      && cycleBack.ReadingDeeds != null && cycleBack.ReadingDeeds.TryGetValue("road", out int road)
                      && road == 3
                      && ledgerBack != null && ledgerBack.CoConspiratorId == "namer";
            if (ok)
                GD.Print("[Chronicle RoundTrip] ChronicleQueue, ReadingDeeds and CoConspiratorId round-trip.");
            else
                GD.PushError("[Chronicle RoundTrip] FAILED: the chronicle's save fields do not survive SaveManager.JsonOptions.");
        }
        catch (Exception e)
        {
            GD.PushError($"[Chronicle RoundTrip] threw: {e.Message}");
        }
    }

    // ── Small helpers ────────────────────────────────────────────────────

    /// <summary>A save the chronicle may touch: loaded, both tiers present, and not inside the
    /// prologue (C1 rule 9: no beat fires while RegisterManager.PrologueMode is set).</summary>
    private static bool Live(GuildSaveData save) =>
        save?.Cycle != null && save.Ledger != null && string.IsNullOrEmpty(RegisterManager.PrologueMode);

    private static bool Has(GuildSaveData save, string flag) =>
        !string.IsNullOrEmpty(flag) && save.HasFlag(flag);

    private static bool HasMeta(GuildSaveData save, string flag) =>
        !string.IsNullOrEmpty(flag) && (save.Ledger?.MetaNarrativeFlags?.Contains(flag) ?? false);

    private static void SetMeta(GuildSaveData save, string flag)
    {
        if (string.IsNullOrEmpty(flag) || save.Ledger == null)
            return;
        save.Ledger.MetaNarrativeFlags ??= new List<string>();
        if (!save.Ledger.MetaNarrativeFlags.Contains(flag))
            save.Ledger.MetaNarrativeFlags.Add(flag);
    }

    private static bool SameTokens(Dictionary<string, string> a, Dictionary<string, string> b)
    {
        int ca = a?.Count ?? 0, cb = b?.Count ?? 0;
        if (ca != cb)
            return false;
        if (ca == 0)
            return true;
        foreach (var kv in a)
            if (!b.TryGetValue(kv.Key, out var v) || v != kv.Value)
                return false;
        return true;
    }

    private static string Arg(Dictionary<string, string> all, string name) =>
        all.TryGetValue(name, out var v) ? v ?? "" : "";

    private static string NonEmpty(string value, string fallback) =>
        string.IsNullOrEmpty(value) ? fallback : value;

    private static string Bool(bool b) => b ? "true" : "false";

    private static string Str(int n) => n.ToString(CultureInfo.InvariantCulture);
}
