using Godot;
using System;
using System.Collections.Generic;
using static CampusUi;
// ============================================================
// DebugToolsPanel.cs: the developer tools, in Settings (2026-11-05)
//
// Purpose:        The Guild tab's debug panel, moved whole: Debug Mode and
//                 its eight sections (combat, prologue, diplomacy,
//                 expedition, strategic map and court, espionage,
//                 progression, verification). Settings shows it only in a
//                 debug build (Godot's "debug" feature tag: running from the
//                 editor or a debug export), so a release export never
//                 builds it and a player never sees it. Ruled 2026-11-05:
//                 "move the debugger into the settings menu and have it
//                 hidden behind the debug tag".
//
//                 The sections are unchanged. They read Ctx as they did in
//                 the Guild tab; here Ctx is a small stand-in with the same
//                 three members (the active save, the host node, a refresh
//                 after a change, which repaints the HUD). A tool that changes
//                 scenes from the in-game Menu closes the Menu as the scene
//                 changes (PauseManager), so the new scene is not left paused.
// Layer:          Dev (debug builds only)
// Collaborators:  SettingsMenu.Developer.cs (the host), PlayerSession (the
//                 debug flags), every launcher and assert the sections call
// See:            docs/guild_screen_retirement_v1.md
// ============================================================
/// <summary>Debug Mode and the debug tools, for Settings in a debug build.</summary>
public partial class DebugToolsPanel : VBoxContainer
{
    /// <summary>True when the developer tools may be shown: a debug build only.</summary>
    public static bool Available => OS.IsDebugBuild();

    /// <summary>What the moved sections read as Ctx in the Guild tab: the active save, the node
    /// they run from, and a refresh after a change. The campus panels that refresh used to
    /// repaint are not on screen here, so it repaints the HUD's readouts.</summary>
    private sealed class ToolsContext
    {
        public Node Host;
        public GuildSaveData Save => SaveManager.ActiveSave;
        public Action RequestRefreshAll;
    }

    private readonly ToolsContext Ctx = new();
    private OptionButton _forceEncounterDropdown;

    public override void _Ready()
    {
        Ctx.Host = this;
        Ctx.RequestRefreshAll = () => HudManager.Instance?.RefreshReadouts(true);
        AddThemeConstantOverride("separation", 10);

        // The tools act on the loaded guild. Opened from the title screen before Continue,
        // none is loaded yet, so the last one loads, as it did when the campus hosted them.
        if (SaveManager.ActiveSave == null)
            SaveManager.AutoLoadLast();
        var save = SaveManager.ActiveSave;
        var acting = new Label
        {
            Text = save == null
                ? "No guild is loaded, so the tools that change a save do nothing."
                : $"The tools act on {save.GuildName} (slot {SaveManager.ActiveSlot + 1}). Use a scratch slot.",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        acting.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        acting.AddThemeColorOverride("font_color", UITheme.CampusSubtleText);
        AddChild(acting);

        var debugCheckbox = new CheckBox
        {
            Text = "Debug Mode",
            ButtonPressed = PlayerSession.DebugMode,
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter,
        };
        AddChild(debugCheckbox);

        var debugPanel = BuildDebugPanel();
        debugPanel.Visible = PlayerSession.DebugMode;
        AddChild(debugPanel);

        debugCheckbox.Toggled += (on) =>
        {
            PlayerSession.DebugMode = on;
            debugPanel.Visible = on;
            if (!on)
            {
                PlayerSession.NoFog = false;
                PlayerSession.UnlimitedSteps = false;
                PlayerSession.GodModeHP = false;
                PlayerSession.StartWithGold = false;
                PlayerSession.StartWithSplinters = false;
                PlayerSession.SkipDeployment = false;
                PlayerSession.ForceNextEncounterType = -1;
                PlayerSession.DebugRevealStrategicMap = false;
                PlayerSession.DebugGrantStagingArmed = false;
                PlayerSession.DebugUnlockAllClasses = false;
                PlayerSession.DebugWalkArc = false;
            }
        };
    }

    private PanelContainer BuildDebugPanel()
    {
        var panel = new PanelContainer { SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter };
        var style = new StyleBoxFlat
        {
            BgColor = UITheme.DebugPanelBg,
            BorderColor = UITheme.DebugPanelBorder,
            BorderWidthTop = 1,
            BorderWidthBottom = 1,
            BorderWidthLeft = 1,
            BorderWidthRight = 1,
            ContentMarginLeft = UITheme.PaddingNormal + 4,
            ContentMarginRight = UITheme.PaddingNormal + 4,
            ContentMarginTop = UITheme.PaddingNormal,
            ContentMarginBottom = UITheme.PaddingNormal,
        };
        panel.AddThemeStyleboxOverride("panel", style);

        var column = MakeVBox(6);
        column.CustomMinimumSize = new Vector2(560, 0);
        panel.AddChild(column);

        // ── Helpers ──────────────────────────────────────────────────────
        // A section = gold header, one-line note, then a 3-column grid of controls.
        GridContainer Section(string header, string note = "")
        {
            if (column.GetChildCount() > 0)
                column.AddChild(new HSeparator());
            var h = new Label { Text = header };
            h.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
            h.AddThemeColorOverride("font_color", UITheme.Gold);
            column.AddChild(h);
            if (!string.IsNullOrEmpty(note))
            {
                var n = new Label { Text = note, AutowrapMode = TextServer.AutowrapMode.WordSmart };
                n.AddThemeFontSizeOverride("font_size", UITheme.CampusTinyFontSize);
                n.AddThemeColorOverride("font_color", UITheme.TextDim);
                column.AddChild(n);
            }
            var grid = new GridContainer { Columns = 3 };
            grid.AddThemeConstantOverride("h_separation", 14);
            grid.AddThemeConstantOverride("v_separation", 6);
            column.AddChild(grid);
            return grid;
        }

        CheckBox Check(GridContainer grid, string label, bool current, Action<bool> onChange)
        {
            var cb = new CheckBox { Text = label, ButtonPressed = current };
            cb.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
            cb.Toggled += (on) => onChange(on);
            grid.AddChild(cb);
            return cb;
        }

        Button Act(GridContainer grid, string label, Action onPress, string tooltip = "")
        {
            var b = new Button { Text = label, CustomMinimumSize = new Vector2(160, 28), TooltipText = tooltip };
            b.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
            b.Pressed += onPress;
            grid.AddChild(b);
            return b;
        }

        // A grant writes to the EternalLedger and saves immediately: PERMANENT
        // for that guild. Use a scratch slot.
        Button Grant(GridContainer grid, string text, Action<GuildSaveData> apply, string tooltip = "")
        {
            return Act(grid, text, () =>
            {
                var save = Ctx.Save;
                if (save?.Ledger == null) { GD.PrintErr("[Debug] No save loaded."); return; }
                apply(save);
                SaveManager.Save();
                Ctx.RequestRefreshAll?.Invoke();
            }, tooltip);
        }

        Label Caption(GridContainer grid, string text)
        {
            var l = new Label { Text = text, VerticalAlignment = VerticalAlignment.Center };
            l.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
            grid.AddChild(l);
            return l;
        }

        // ── LAUNCHERS (top) ──────────────────────────────────────────────
        var combat = Section("COMBAT DEBUG",
            "Launch a standalone battlefield: school, tier, terrain, enemies, allies, objectives, map-event injectors. Returns to the hub.");
        var combatBtn = Act(combat, "Open Combat Launcher", () => CombatDebugLauncher.Toggle(column));
        combatBtn.CustomMinimumSize = new Vector2(200, 34);
        UITheme.ApplyButtonStyle(combatBtn, isPrimary: true);
        Act(combat, "Assert Deck Split", () => CombatDebugLauncher.AssertDeckSplit(),
            "Round-trips the real/debug deck split and prints the result.");
        Act(combat, "Assert Units", () => UnitRegistry.AssertParityAndRoundTrip(),
            "Unit registry parity + save round-trip.");
        Act(combat, "Assert Prologue", () => PrologueDirector.AssertRoundTrip(),
            "The prologue's data, folios, profile file and Register modes. Shows and marks nothing.");
        Check(combat, "Skip Deployment", PlayerSession.SkipDeployment,
            on => PlayerSession.SkipDeployment = on);
        Check(combat, "God Mode HP", PlayerSession.GodModeHP,
            on => PlayerSession.GodModeHP = on);

        // ── PROLOGUE ─────────────────────────────────────────────────────
        var prologue = Section("PROLOGUE",
            "Unlock All Classes is for the next guild you found: it opens every school on the founding screen, the one you state as your intention is the practical you sit in the prologue, and its folio is the one you carry into the Sundering. The guild still begins as an Adept, and the trial is still the Adept duel. Walk the Arc is for the next guild you found too: chapter I in the Grand Hall instead of the founding, then out of the gate into the interval. Walk First Lunation Again is for this guild, and saves at once. Walk the Interval, Next Standing Order and End the Interval are for this guild too: each saves, then opens the map. Rehearse the Founding, Rehearse Chapter I and Rehearse the Sundering play those scenes on this guild and keep nothing. Bake Stage Previews (only while the game runs from the editor) saves the prologue's halls for the direction scenes in the editor.");
        Check(prologue, "Unlock All Classes", PlayerSession.DebugUnlockAllClasses,
            on => PlayerSession.DebugUnlockAllClasses = on);
        Check(prologue, "Walk the Arc", PlayerSession.DebugWalkArc,
            on => PlayerSession.DebugWalkArc = on).TooltipText =
            "Standing Orders O2, for the next guild you found: after the Anchorhold it meets chapter I in the "
            + "Grand Hall (the statutes, the hearth, the gate) instead of the founding, and goes out of the gate "
            + "into the interval. Until the later chapters exist, Next Standing Order walks the rest.";
        Grant(prologue, "Walk First Lunation Again", save => FirstLunation.DebugForget(save),
            "This guild's first-lunation steps go back to undone (S5). Steps its own state shows, such as a "
            + "sortie, a won fight or a new moon, tick again at once. The Register's notes stay seen on this computer.");
        // Standing Orders O1a: the interval's buttons move the castle and shut or open the
        // home, so after saving they open the map afresh from wherever Settings was opened
        // (the title screen, or the Menu over a game).
        void IntervalDebug(Action<GuildSaveData> apply)
        {
            var save = Ctx.Save;
            if (save?.Ledger == null) { GD.PrintErr("[Debug] No save loaded."); return; }
            apply(save);
            SaveManager.Save();
            CampusScreen.OverlayLeaveHandler = null;   // an overlay goes with the scene
            PlayerSession.CampusOverlayOpen = false;
            PlayerSession.ZoomFromHomeOnOpen = true;   // the map rises: from the castle while the door is shut
            Ctx.Host.GetTree().ChangeSceneToFile("res://Scenes/Overworld/StrategicScene.tscn");
        }
        // O1b: a walk starts the interval's quest over with it.
        void WalkInterval(GuildSaveData save)
        {
            StandingOrders.DebugForget(save);
            Interval.Begin(save);
        }
        Act(prologue, "Walk the Interval", () => IntervalDebug(WalkInterval),
            "Standing Orders O1, for testing: this reading enters the interval, and its quest starts over. The moon "
            + "is held, the home's door is shut, and the castle stands on a road with 8 fuel and the party beside "
            + "it. Saves, then opens the map on the castle.");
        Act(prologue, "Next Standing Order", () => IntervalDebug(save => StandingOrders.DebugAdvance(save)),
            "Standing Orders O1b, for testing: the quest's current step is done as its chapter will do it. The store "
            + "is taken back (O3: the furnace filled, the cash box and the order book taken), the quorum is ticked, "
            + "the door is set at home (the castle berths there, and the moon stays held), "
            + "and the orders are adopted. Saves, then opens the map. Walked to the end, the finish is noted on this "
            + "computer, which offers the skip once the gate exists.");
        Act(prologue, "End the Interval", () => IntervalDebug(save => Interval.End(save)),
            "The interval lets go: the door is set, the moon runs again, and the castle berths at home with a full "
            + "furnace. Saves, then opens the map. While the interval walks, Debug Mode lets the HUD's Return to "
            + "Campus open the campus panels over the map.");
        Act(prologue, "Rehearse the Founding", () => FoundingStage.Rehearse(Ctx.Host.GetTree()),
            "The founding in the Grand Hall from its first beat, on this guild, with its panels shown and nothing "
            + "applied, then back to the city. For watching a change to Scenes/Prologue/Direction/founding.tscn.");
        Act(prologue, "Rehearse Chapter I", () => FoundingStage.RehearseArc(Ctx.Host.GetTree()),
            "Standing Orders O2: chapter I on this guild (the Grand Hall's first visit, the statutes, the gate and "
            + "Legible), with its panels shown and nothing applied, then back to the city.");
        Act(prologue, "Rehearse the Sundering", () => PrologueDirector.RehearseSundering(Ctx.Host.GetTree()),
            "The Sundering from its opening to the walk out, on this guild, with its panels shown. Nothing is saved "
            + "while it runs, and at the end the guild is read back from its slot, so nothing it did is kept. For "
            + "watching a change to Scenes/Prologue/Direction/sundering.tscn.");
        if (StageBake.Available)
            Act(prologue, "Bake Stage Previews", () => StageBake.Begin(Ctx.Host.GetTree()),
                "Builds each of the prologue's halls as the game does and saves it under Scenes/Prologue/Sets, for the "
                + "direction scenes to show in the editor, then writes any direction scene that is missing. Only while "
                + "the game runs from the editor.");

        var diplomacy = Section("DIPLOMACY DEBUG",
            "Sit at any negotiation table directly: encounter, school, standing, goodwill / patience overrides, forced grievance, extra leverage, allies. Nothing is recorded. Returns to the hub.");
        var diplomacyBtn = Act(diplomacy, "Open Diplomacy Launcher", () => NegotiationDebugLauncher.Toggle(column));
        diplomacyBtn.CustomMinimumSize = new Vector2(200, 34);
        UITheme.ApplyButtonStyle(diplomacyBtn, isPrimary: true);
        Act(diplomacy, "Verify Tables", () =>
        {
            // The validator lives in tools/; from here we do the cheap in-engine
            // half: load every table through the real loader and report.
            int ok = 0, bad = 0;
            var dir = DirAccess.Open("res://Data/Negotiations/");
            if (dir == null) { GD.PrintErr("[Debug] Data/Negotiations not found."); return; }
            dir.ListDirBegin();
            for (string f = dir.GetNext(); !string.IsNullOrEmpty(f); f = dir.GetNext())
            {
                if (dir.CurrentIsDir() || !f.EndsWith(".json")) continue;
                var d = NegotiationEncounterLoader.Load(f[..^5]);
                if (d == null || d.Clauses.Count == 0) { bad++; GD.PrintErr($"[Debug] table '{f}' failed to load or has no clauses."); }
                else ok++;
            }
            dir.ListDirEnd();
            GD.Print($"[Debug] Negotiation tables: {ok} loaded, {bad} failed. Run tools/verify_negotiations.py for the full check.");
        }, "Loads every Data/Negotiations table through the real loader.");

        // ── EXPEDITION ───────────────────────────────────────────────────
        var expedition = Section("EXPEDITION", "Applies to the next sortie.");
        Check(expedition, "No Fog", PlayerSession.NoFog, on => PlayerSession.NoFog = on);
        Check(expedition, "Unlimited Fuel", PlayerSession.UnlimitedSteps, on => PlayerSession.UnlimitedSteps = on);
        Check(expedition, "No Enemy Ambushes", PlayerSession.DebugNoAmbush, on => PlayerSession.DebugNoAmbush = on);
        Check(expedition, "Classic Exploration (A/B baseline)", PlayerSession.DebugClassicExploration,
            on => PlayerSession.DebugClassicExploration = on);
        Check(expedition, "Grant Staging (press G)", PlayerSession.DebugGrantStagingArmed,
            on => PlayerSession.DebugGrantStagingArmed = on);
        Check(expedition, "Start With Gold", PlayerSession.StartWithGold, on => PlayerSession.StartWithGold = on);
        Check(expedition, "Start With Splinters", PlayerSession.StartWithSplinters, on => PlayerSession.StartWithSplinters = on);
        Caption(expedition, "Force next POI:");
        _forceEncounterDropdown = new OptionButton { CustomMinimumSize = new Vector2(160, 28) };
        _forceEncounterDropdown.AddThemeFontSizeOverride("font_size", UITheme.CampusSmallFontSize);
        _forceEncounterDropdown.AddItem("None (normal)", -1);
        _forceEncounterDropdown.AddItem("Combat", (int)OverworldHex.POIType.Combat);
        _forceEncounterDropdown.AddItem("Rest", (int)OverworldHex.POIType.Rest);
        _forceEncounterDropdown.AddItem("Narrative", (int)OverworldHex.POIType.Narrative);
        _forceEncounterDropdown.AddItem("Negotiation", (int)OverworldHex.POIType.Negotiation);
        _forceEncounterDropdown.Selected = 0;
        _forceEncounterDropdown.ItemSelected += (idx) =>
            PlayerSession.ForceNextEncounterType = _forceEncounterDropdown.GetItemId((int)idx);
        expedition.AddChild(_forceEncounterDropdown);
        // Castle-role review, test 2 (2026-11-03). Added after the dropdown so the
        // caption and the dropdown keep their row in the three-column grid.
        expedition.AddChild(new Control());

        // ── STRATEGIC MAP & COURT ────────────────────────────────────────
        var strategic = Section("STRATEGIC MAP & COURT", "Levers take effect on the next strategic-map load; dumps print to the log.");
        Check(strategic, "Reveal Strategic Map", PlayerSession.DebugRevealStrategicMap,
            on => PlayerSession.DebugRevealStrategicMap = on);
        Act(strategic, "Force Conjunction", StrategicDebug.ForceConjunction);
        Act(strategic, "Prime Warfront", StrategicDebug.PrimeWarfront);
        Act(strategic, "Push Front +40", () =>
        {
            if (!StrategicDebug.PushFront(40))
                return;
            // The map opens afresh, so the taking shows at once.
            CampusScreen.OverlayLeaveHandler = null;   // an overlay goes with the scene
            PlayerSession.CampusOverlayOpen = false;
            Ctx.Host.GetTree().ChangeSceneToFile("res://Scenes/Overworld/StrategicScene.tscn");
        }, "Provinces PV3a (Gate PV3a): the first open war moves 40 toward its aggressor and settles at once "
           + "by the lunation's rules: at 100 the province is taken and the front moves on at 60, or the realm "
           + "falls with its capital. Then the map opens afresh.");
        Act(strategic, "Push Annex +40", () =>
        {
            if (!StrategicDebug.PushAnnex(40))
                return;
            // The map opens afresh, so the annexation shows at once.
            CampusScreen.OverlayLeaveHandler = null;   // an overlay goes with the scene
            PlayerSession.CampusOverlayOpen = false;
            Ctx.Host.GetTree().ChangeSceneToFile("res://Scenes/Overworld/StrategicScene.tscn");
        }, "Provinces PV3b (Gate PV3b): of the land no one rules, the bid furthest along gains 40 and "
           + "settles at once: a full bar annexes the province for its realm. Then the map opens afresh.");
        Act(strategic, "Learn Maneuvers", () =>
        {
            if (!StrategicDebug.LearnManeuvers())
                return;
            // The map opens afresh, so the castle's Maneuvers button reads the new spells.
            CampusScreen.OverlayLeaveHandler = null;   // an overlay goes with the scene
            PlayerSession.CampusOverlayOpen = false;
            Ctx.Host.GetTree().ChangeSceneToFile("res://Scenes/Overworld/StrategicScene.tscn");
        }, "Provinces PV4 (the Bastion): the guild learns every maneuver's spell and the font fills, so "
           + "each can be cast from the castle's Maneuvers button. Then the map opens afresh.");
        Act(strategic, "Wake the Keeper", () =>
        {
            if (!StrategicDebug.WakeKeeper())
                return;
            // The map opens afresh, so the Keeper's mark shows at once.
            CampusScreen.OverlayLeaveHandler = null;   // an overlay goes with the scene
            PlayerSession.CampusOverlayOpen = false;
            Ctx.Host.GetTree().ChangeSceneToFile("res://Scenes/Overworld/StrategicScene.tscn");
        }, "Provinces PV6 (the Keeper): the Keeper wakes now, two hops from the castle, knowing where it "
           + "stands; each Owe +1 Lunation then plays its next card. Then the map opens afresh.");
        Act(strategic, "Muster the Army", () =>
        {
            if (!StrategicDebug.MusterArmy())
                return;
            // The map opens afresh, so the Army button reads the new lines.
            CampusScreen.OverlayLeaveHandler = null;   // an overlay goes with the scene
            PlayerSession.CampusOverlayOpen = false;
            Ctx.Host.GetTree().ChangeSceneToFile("res://Scenes/Overworld/StrategicScene.tscn");
        }, "Provinces PV7 (troops): one line of each kind joins the army at full strength, with 30 "
           + "supplies for their keep. Then Force Castle Assault: the lines stand garrison on the castle's "
           + "deck, and the Orders card comes to the commander's deck. Then the map opens afresh.");
        Act(strategic, "Owe +1 Lunation", () => StrategicDebug.OweLunations(1));
        Act(strategic, "Owe +3 Lunations", () => StrategicDebug.OweLunations(3));
        Act(strategic, "Resolve All Seats", StrategicDebug.ResolveAllSeats);
        Act(strategic, "Dump Expedition State", StrategicDebug.DumpExpeditionState);
        Act(strategic, "March Castle", StrategicDebug.MarchCastleToNearestAnchor);
        Act(strategic, "Rumor the Nearest Place", StrategicDebug.RumorNearestPlace,
            "Knowledge ladder L4a: a lead on the nearest undiscovered place in the wild to the castle, then saves. "
            + "Its ring shows on the next map load. March and camp within sight of the place, and the lead goes "
            + "with a note that it held true.");
        Act(strategic, "Hear the Nearest Talk", () => StrategicDebug.HearNearestTalk(false),
            "Knowledge ladder L4b1: the settlement nearest the castle talks now, ignoring the once-a-lunation rule, "
            + "true or false by its kingdom's reliability, then saves. Its ring shows on the next map load.");
        Act(strategic, "Hear a False Rumor", () => StrategicDebug.HearNearestTalk(true),
            "Knowledge ladder L4b1: a false rumor from the settlement nearest the castle, then saves. Stop on its "
            + "center in the field, or camp in sight of the whole ring, and it comes to nothing.");
        Act(strategic, "Force Castle Raid", () => StrategicDebug.ForceCastleThreat(CastleThreatKind.PatrolRaid));
        Act(strategic, "Force Castle Assault", () => StrategicDebug.ForceCastleThreat(CastleThreatKind.KingdomAssault));
        Act(strategic, "Force Portal Strike", () =>
        {
            // Needs a built, sited Teleport Sigil; offered on the next strategic-map load.
            var cycle = Ctx.Save?.Cycle;
            if (cycle?.Kingdoms == null || TeleportSigil.Tier(Ctx.Save) < 1)
            {
                GD.PrintErr("[Debug] No sited Teleport Sigil, so there is no rift to strike through.");
                return;
            }
            foreach (var kv in cycle.Kingdoms)
            {
                cycle.PendingPortalStrikeKingdomId = kv.Key;
                cycle.PortalStrikeLaunched = false;
                SaveManager.MarkDirty();
                SaveManager.SaveIfDirty();
                GD.Print($"[Debug] Portal strike owed by {kv.Key}. Open the strategic map.");
                break;
            }
        });
        Act(strategic, "Foul Campus Ground", () =>
        {
            // Campus corruption (section 11): one step onto a sited building whose ground can
            // still sink. Non-foundational buildings first (they go to Overrun), then
            // foundational ones (capped at Blighted), in id order, so repeated presses
            // walk one building through its levels before moving to the next. A new
            // guild owns only its five foundational buildings, so those are the targets
            // until something else is built and sited.
            var save = Ctx.Save;
            if (save?.Buildings == null)
                return;
            var ordinary = new System.Collections.Generic.List<string>();
            var foundational = new System.Collections.Generic.List<string>();
            foreach (var b in save.Buildings)
            {
                var t = BuildingDatabase.GetTemplate(b.Id);
                if (b.Tier <= 0 || !b.IsPlaced || t == null)
                    continue;
                if (t.IsFoundational)
                {
                    if (b.BlightLevel < CampusBlight.FoundationalCap)
                        foundational.Add(b.Id);
                }
                else if (b.BlightLevel < CampusBlight.MaxLevel)
                {
                    ordinary.Add(b.Id);
                }
            }
            ordinary.Sort(string.CompareOrdinal);
            foundational.Sort(string.CompareOrdinal);
            string pick = ordinary.Count > 0 ? ordinary[0] : foundational.Count > 0 ? foundational[0] : null;
            if (pick == null)
            {
                GD.PrintErr("[Debug] Every sited building's ground is already as foul as it can get.");
                return;
            }
            string line = CampusBlight.CorruptBuildingLand(save, pick, 1);
            GD.Print($"[Debug] Fouled '{pick}' (now {CampusBlight.LevelName(CampusBlight.Level(save, pick))}). {line}");
            SaveManager.SaveIfDirty();
        });
        Act(strategic, "Assert Field Save", () => FieldExpeditionSaveAssert.AssertAll());
        Act(strategic, "Dump Echoes", () => CouncilDebug.DumpEchoes());
        Act(strategic, "Dump Regard", () => CouncilDebug.DumpRegard());

        // ── ESPIONAGE ────────────────────────────────────────────────────
        var espionage = Section("ESPIONAGE (the Concord)", "E2 watchers \u2192 E3 market \u2192 E4 sabotage \u2192 E5 shadow war \u2192 E6 spine, left to right.");
        Act(espionage, "Plant Watcher", () => ConcordDebug.DebugPlantWatcher());
        Act(espionage, "Contact Concord", () => ConcordDebug.DebugContactConcord());
        Act(espionage, "Dump Shadow", () => { ConcordDebug.DumpNodes(); ConcordDebug.DumpShadow(); });
        Act(espionage, "+50 Favor", () => ConcordDebug.DebugGrantFavor());
        Act(espionage, "Sell Secret", () => ConcordDebug.DebugSellSecret());
        Act(espionage, "Buy: Plant", () => ConcordDebug.DebugCommissionPlant());
        Act(espionage, "Buy: Intel", () => ConcordDebug.DebugCommissionIntel());
        Act(espionage, "Buy: Theft", () => ConcordDebug.DebugCommissionTheft());
        Act(espionage, "Plant Saboteur", () => ConcordDebug.DebugPlantSaboteur());
        Act(espionage, "Saboteur Strike", () => ConcordDebug.DebugSaboteurStrike());
        Act(espionage, "Forge Echo", () => ConcordDebug.DebugForgeEcho());
        Act(espionage, "Buy: Sabotage Siege", () => ConcordDebug.DebugBuySabotageSiege());
        Act(espionage, "Buy: Sabotage Corr.", () => ConcordDebug.DebugBuySabotageCorruption());
        Act(espionage, "Marked \u2192 9", () => ConcordDebug.DebugForceMarked());
        Act(espionage, "Outbid", () => ConcordDebug.DebugOutbid());
        Act(espionage, "Imprison Envoy", () => ConcordDebug.DebugImprisonEnvoy());
        Act(espionage, "Buy: Extraction", () => ConcordDebug.DebugBuyExtraction());
        Act(espionage, "Undercroft +1", () => ConcordDebug.DebugUndercroftUp());
        Act(espionage, "Exfiltrate", () => ConcordDebug.DebugExfiltrate());
        Act(espionage, "Buy: Assassination", () => ConcordDebug.DebugBuyAssassination());

        // ── PROGRESSION ──────────────────────────────────────────────────
        var progression = Section("PROGRESSION",
            "Skips the slow gates. These write the EternalLedger and save at once: PERMANENT for this guild. Use a scratch slot.");
        Grant(progression, "Grant Elementalist Facets", save =>
        {
            int n = 0;
            foreach (var source in Facets.FragmentSources)
                if (Facets.Grant(save, "Elementalist", source)) n++;
            GD.Print($"[Debug] Granted {n} Elementalist facet(s); the seat reads {Facets.Count(save, "Elementalist")}.");
        });
        Grant(progression, "Declare All Schools", save =>
        {
            save.Ledger.MetaNarrativeFlags ??= new List<string>();
            int n = 0;
            foreach (CardSchool s in Enum.GetValues(typeof(CardSchool)))
            {
                string flag = DeclarationService.DeclaredFlag(s.ToString());
                if (save.Ledger.MetaNarrativeFlags.Contains(flag)) continue;
                save.Ledger.MetaNarrativeFlags.Add(flag);
                n++;
            }
            GD.Print($"[Debug] Declared {n} additional discipline(s). Every school is now " +
                     $"selectable at the next cycle.");
        });
        Grant(progression, "Unlock All Cards", save =>
        {
            save.Ledger.UnlockedCardBlueprintIds ??= new List<string>();
            var known = new HashSet<string>(save.Ledger.UnlockedCardBlueprintIds,
                                            StringComparer.OrdinalIgnoreCase);
            int n = 0;
            foreach (var bp in CardDatabase.Blueprints)
            {
                if (!known.Add(bp.Id)) continue;
                save.Ledger.UnlockedCardBlueprintIds.Add(bp.Id);
                n++;
            }
            // Legendaries are included on purpose: DraftablePool drops them from
            // the draft regardless, so this only makes the card library honest.
            GD.Print($"[Debug] Unlocked {n} blueprint(s). " +
                     $"{save.Ledger.UnlockedCardBlueprintIds.Count} known. " +
                     $"Legendaries remain undraftable (they are Regalia).");
        });
        Grant(progression, "Commission Random Rare", save =>
        {
            // Bypasses the Archives/gold/capacity gates and uses a 1-lunation timer
            // so a single moon-turn settles it.
            var id = CardCommissionService.DebugCommissionRandom(save, lunations: 1);
            if (id == null)
                GD.Print("[Debug] Nothing to commission. Every Rare is already known here.");
        });
        Grant(progression, "Learn All Spells", save =>
        {
            OverworldSpellRegistry.EnsureLoaded();
            save.Cycle ??= new CycleState();
            save.Cycle.Grimoire ??= new GrimoireState();
            save.Cycle.Grimoire.KnownSpellIds ??= new List<string>();

            var known = new HashSet<string>(save.Cycle.Grimoire.KnownSpellIds, StringComparer.Ordinal);
            int n = 0;
            foreach (var id in OverworldSpellRegistry.All.Keys)
            {
                if (!known.Add(id)) continue;
                save.Cycle.Grimoire.KnownSpellIds.Add(id);
                n++;
            }
            // Cycle-scoped by design, since the Grimoire dies with the timeline.
            GD.Print($"[Debug] Learned {n} overworld spell(s). " +
                     $"{save.Cycle.Grimoire.KnownSpellIds.Count} known this cycle.");
        });

        // ── VERIFICATION ─────────────────────────────────────────────────
        var verify = Section("VERIFICATION", "Save-adjacent round-trip assertions; results print to the log.");
        Act(verify, "Assert Round-Trips", () =>
        {
            CouncilSaveAssert.AssertAll();
            ProgressionSaveAssert.AssertAll();
            ArmorySaveAssert.AssertAll();   // Edge M6: belts, charges, bindings
            CharterSaveAssert.AssertAll();  // campus doctrines: charters, fills
            LeadsSaveAssert.AssertAll();    // knowledge ladder L1: leads, Known
            IntervalSaveAssert.AssertAll(); // Standing Orders O1: the interval
            ProvinceSaveAssert.AssertAll(); // provinces PV1 and PV2a: the partition, influence, their save
        });
        Act(verify, "Province Report", () =>
        {
            var provinceWorld = SaveManager.ActiveSave?.Cycle?.World;
            GD.Print(ProvinceBuilder.Report(provinceWorld));
        }, "Print the active world's provinces: sizes, seats, how often borders follow rivers and ridges, each kingdom, and the checks.");
        Act(verify, "Province Influence", () =>
        {
            GD.Print(ProvinceInfluence.Report(SaveManager.ActiveSave?.Cycle));
        }, "Provinces PV2a: print each kingdom's influence and each province's, its hold, and where the castle stands.");
        Act(verify, "Province Deed +20", () =>
        {
            var deedCycle = SaveManager.ActiveSave?.Cycle;
            GD.Print(ProvinceInfluence.DebugDeed(deedCycle, 20));
            SaveManager.MarkDirty();
            SaveManager.SaveIfDirty();
        }, "Provinces PV2a: +20 guild influence in the castle's province, as a deed gives, then saves. From 40, one press holds it.");

        return panel;
    }
}
