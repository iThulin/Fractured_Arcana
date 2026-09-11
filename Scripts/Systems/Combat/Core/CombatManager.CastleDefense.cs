using Godot;
using System;
using System.Collections.Generic;

// ============================================================
// CombatManager.CastleDefense.cs  (partial of CombatManager)
//
// Purpose:        Runtime for "Defend the Castle" (castle_defense_v1,
//                 mobile fortress F6). Three things on top of the siege
//                 and protect machinery that already exist:
//                   1. The Castle Heart (the protect ward) spawns at the
//                      compiled heart tile, not at the spawn side.
//                   2. Stations: rampart tiles carrying an installed
//                      castle module. A unit standing on one at the
//                      turn boundaries gets the module's effect. No new
//                      verb: standing there is the action.
//                   3. The wizard arrives late: fielded normally (deck
//                      and all), then translocated off the board until
//                      the arrival round, when it steps out beside the
//                      Heart with translocation shock.
// Collaborators:  CastleDefenseCompiler / SiegeSpec (heart, stations),
//                 CastleModules (station specs), CombatManager.Objectives
//                 (protect ward), CombatManager.SiegeDoors (gate doors)
// See:            docs/castle_defense_v1.md
// ============================================================

public partial class CombatManager
{
    /// <summary>Set by the encounter router / debug launcher when this fight is a
    /// castle defence. Read by the spawn path and the turn boundaries.</summary>
    public static bool NextCombatIsCastleDefense;
    private bool _castleDefense;
    private Unit _castleWizard;
    private int _wizardArrivalRound = 0;    // 0 = present from the start
    private bool _wizardArrived = true;
    private int _wizardShockRound = -1;     // the round the wizard stepped out: AP 0 that turn
    private CastleAnimator _castleAnimator; // castle_defense_v2 motion: breathing, smoke, beats

    private const int BaseAmbushWizardDelay = 2;   // spec §13: arrives round 3

    private bool IsCastleDefense => _castleDefense && grid?.ActiveSiege?.Heart != null;

    /// <summary>Station spec on a tile, or null.</summary>
    private CastleStationSpec StationAt(Vector2I coord)
    {
        var siege = grid?.ActiveSiege;
        if (siege == null)
            return null;
        foreach (var (at, module) in siege.Stations)
            if (at == coord)
                return CastleModules.Get(module)?.Station;
        return null;
    }

    // ── Heart placement ──────────────────────────────────────────────────────

    /// <summary>Called from SpawnObjectiveWard when the recipe carries a heart tile:
    /// the ward stands THERE, not on a spawn slot. Returns the ward or null.</summary>
    private Unit SpawnWardAtHeart(UnitDefinition def)
    {
        var heartCoord = grid?.ActiveSiege?.Heart;
        if (heartCoord == null)
            return null;
        var tile = grid.GetTile(heartCoord.Value);
        if (tile == null || !tile.CanEnter(null))
        {
            // Nearest open courtyard tile.
            TileData best = null; int bestD = int.MaxValue;
            foreach (var kv in grid.Tiles)
                if (kv.Value.CanEnter(null) && grid.Distance(heartCoord.Value, kv.Key) < bestD)
                { bestD = grid.Distance(heartCoord.Value, kv.Key); best = kv.Value; }
            tile = best;
        }
        if (tile == null)
            return null;

        var ward = SpawnRegistryUnit(_objective.WardUnitId, tile, teamId: 0, isMidFightSummon: false);
        if (ward == null)
            return null;
        ward.IsStructure = false;   // the Heart is the ward, not a door: its death is the defeat
        ward.IsMartial = false;
        ward.MaxActionPoints = 0;
        ward.CurrentActionPoints = 0;
        ward.MoveRange = 0;
        ward.Name = "CastleHeart";
        return ward;
    }

    // ── Wizard arrival ───────────────────────────────────────────────────────

    /// <summary>After the party is fielded: pull the wizard off the board until
    /// the arrival round. The unit keeps its deck, hand, and roster slot; it has
    /// no tile, is hidden, and is unselectable until it steps out at the Heart.</summary>
    private void TranslocateWizardOut()
    {
        _castleDefense = NextCombatIsCastleDefense;
        NextCombatIsCastleDefense = false;
        _heartPulsed = false;
        _siegeRoleAssigned.Clear();
        _castleWizard = null;
        _wizardShockRound = -1;
        _castleAnimator = null;
        if (!IsCastleDefense)
            return;

        SpawnStationMarkers();
        AssignSiegeRoles();
        StartCastleAnimator();

        int delay = BaseAmbushWizardDelay + CastleModules.AmbushDelayModifier()
                    + AmbushDelayFromCrew();
        delay = Math.Max(0, delay);
        _wizardArrivalRound = delay > 0 ? 1 + delay : 0;
        _wizardArrived = delay == 0;

        foreach (var u in playerUnits)
            if (u != null && u.CompanionId == "wizard")
            { _castleWizard = u; break; }

        if (_castleWizard == null || _wizardArrived)
            return;

        // Nobody to hold the walls (a debug launch with no crew, or a party of one):
        // the delay would leave the player with no unit to command. The wizard
        // stays, and the log says why.
        bool crewFielded = false;
        foreach (var u in playerUnits)
            if (u != null && u != _castleWizard && IsInstanceValid(u) && u.Stats.IsAlive
                && !u.IsStructure && !u.IsObjectiveWard && u.IsPlayerControlled)
            { crewFielded = true; break; }
        if (!crewFielded)
        {
            _wizardArrived = true;
            _wizardArrivalRound = 0;
            combatUI?.AppendActionLog("── No crew to hold the walls: the wizard is on the field from the start. ──");
            return;
        }

        _castleWizard.LiftFromBoard();
        _castleWizard.IsAwaitingArrival = true;
        combatUI?.AppendActionLog($"── The crew holds the walls alone. {_castleWizard.DisplayName} steps through the waystone on round {_wizardArrivalRound}. ──");
    }

    /// <summary>After StartPlayerTurn has reset AP: the arrival turn is a hand and a
    /// position, not a full turn. Runs once, on the arrival round only.</summary>
    private void ApplyArrivalShock()
    {
        if (!IsCastleDefense || _castleWizard == null || !IsInstanceValid(_castleWizard))
            return;
        if (_wizardShockRound != roundNumber)
            return;
        _castleWizard.CurrentActionPoints = 0;
        _wizardShockRound = -1;
        combatUI?.AppendActionLog($"{_castleWizard.DisplayName} is still reeling from the waystone: no actions this turn, the hand is live.");
    }

    /// <summary>Wardroom crew station (CrewStations, F4): rounds off the delay.
    /// Read defensively; the expedition side owns it.</summary>
    private static int AmbushDelayFromCrew()
        => -Math.Max(0, PlayerSession.AmbushWizardDelayReduction);

    /// <summary>Round boundary: the wizard steps out at (or beside) the Heart with a
    /// one-round translocation shock: AP zeroed on arrival, so the arrival turn is a
    /// hand and a position, not a full turn. Called before the player turn starts.</summary>
    private void TryArriveWizard()
    {
        if (!IsCastleDefense || _wizardArrived || _castleWizard == null || !IsInstanceValid(_castleWizard))
            return;
        if (roundNumber < _wizardArrivalRound)
            return;

        var heart = grid.ActiveSiege.Heart.Value;
        TileData landing = null; int bestD = int.MaxValue;
        foreach (var kv in grid.Tiles)
            if (kv.Value.CanEnter(_castleWizard) && grid.Distance(heart, kv.Key) < bestD)
            { bestD = grid.Distance(heart, kv.Key); landing = kv.Value; }
        if (landing == null)
            return;   // try again next round

        _castleWizard.IsAwaitingArrival = false;
        _castleWizard.Visible = true;
        _castleWizard.PlaceOnTile(landing, MovementKind.Teleport);
        _castleWizard.Stats.HasActed = true;
        _castleWizard.CurrentActionPoints = 0;   // translocation shock
        _wizardShockRound = roundNumber;         // StartPlayerTurn resets AP; ApplyArrivalShock zeroes it again
        _wizardArrived = true;
        combatUI?.AppendActionLog($"── {_castleWizard.DisplayName} steps through the waystone beside the Heart, still reeling. ──");
        // The hand appears with the wizard: if nothing else is selected, select it so
        // the deck that just became playable is the one on screen.
        if (selectedUnit == null || !IsInstanceValid(selectedUnit) || !selectedUnit.Stats.IsAlive)
            SelectUnit(_castleWizard);
        RefreshPlayerUnitBar();
        RefreshThreatTiles();
    }

    // ── Motion ───────────────────────────────────────────────────────────────

    /// <summary>The castle's idle life. Bound one frame late so the deferred
    /// station props and markers exist when the animator gathers its parts.</summary>
    private void StartCastleAnimator()
    {
        if (grid == null || _castleAnimator != null)
            return;
        _castleAnimator = new CastleAnimator { Name = "CastleAnimator" };
        grid.AddChild(_castleAnimator);
        var heart = _wardUnit;
        Callable.From(() =>
        {
            if (_castleAnimator == null || !IsInstanceValid(_castleAnimator))
                return;
            _castleAnimator.Bind(grid, heart,
                at => grid.GetTile(at)?.Occupant,
                at => NearestLivingEnemyTo(at));
        }).CallDeferred();
    }

    private Unit NearestLivingEnemyTo(Vector2I at)
    {
        Unit best = null; int bestD = int.MaxValue;
        foreach (var e in enemyUnits)
        {
            if (e == null || !IsInstanceValid(e) || !e.Stats.IsAlive || e.CurrentTile == null || e.IsMapObject)
                continue;
            int d = grid.Distance(at, e.CurrentTile.Axial);
            if (d < bestD) { bestD = d; best = e; }
        }
        return best;
    }

    /// <summary>Called by ExecuteMapEvent after an event fires: the body plays the
    /// beat the compiler named. Unknown ids are ignored.</summary>
    private void OnCastleBeat(MapEventDef ev)
    {
        if (_castleAnimator == null || !IsInstanceValid(_castleAnimator) || ev == null)
            return;
        switch (ev.Id)
        {
            case "stomp_left": _castleAnimator.StompLeg(left: true); break;
            case "stomp_right": _castleAnimator.StompLeg(left: false); break;
            case "furnace_vent": _castleAnimator.Vent(); break;
            case "hull_lurch": _castleAnimator.Lurch(); break;
        }
    }

    // ── The Heart's pulse ────────────────────────────────────────────────────

    private bool _heartPulsed;
    private const int HeartPulseShield = 4;

    /// <summary>Once per fight, at the round boundary after the Heart first drops
    /// below half: it flares, and every player unit on the deck (the objective
    /// zone) gains shield. The castle is alive and it fights back for its crew.</summary>
    private void HeartPulse()
    {
        if (!IsCastleDefense || _heartPulsed)
            return;
        var heart = _wardUnit;
        if (heart == null || !IsInstanceValid(heart) || !heart.Stats.IsAlive)
            return;
        if (heart.Stats.Health * 2 > heart.Stats.MaxHealth)
            return;
        _heartPulsed = true;
        var deck = new HashSet<Vector2I>(grid.ActiveSiege.ObjectiveZone);
        int n = 0;
        foreach (var u in playerUnits)
        {
            if (u == null || !IsInstanceValid(u) || !u.Stats.IsAlive || u.CurrentTile == null)
                continue;
            if (u == heart || !deck.Contains(u.CurrentTile.Axial))
                continue;
            u.Stats.Shield += HeartPulseShield;
            u.RefreshHealthBar();
            n++;
        }
        combatUI?.AppendActionLog($"── The Heart flares. The deck answers: {n} unit(s) shielded {HeartPulseShield}. ──");
        _castleAnimator?.FlareHeart();
    }

    // ── Siege pressure ───────────────────────────────────────────────────────

    private readonly HashSet<Unit> _siegeRoleAssigned = new();

    /// <summary>Every second melee attacker is a sapper: it runs the hunt_ward
    /// routine and goes for the Heart through everything, so the fight has the
    /// pressure a siege needs. The rest keep their authored routine and fight
    /// the crew on the walls. Idempotent per unit, so it is safe to call again
    /// after reinforcement waves (round boundary).</summary>
    private void AssignSiegeRoles()
    {
        if (!IsCastleDefense)
            return;
        int meleeSeen = _siegeRoleAssigned.Count;
        int sappers = 0;
        foreach (var e in enemyUnits)
        {
            if (e == null || !IsInstanceValid(e) || !e.Stats.IsAlive || e.IsMapObject)
                continue;
            if (!_siegeRoleAssigned.Add(e))
                continue;
            bool melee = e.AttackRange <= 1
                && (e.BehaviorKey == "melee_advance" || e.BehaviorKey == "melee_target_highest_hp"
                    || e.BehaviorKey == "hold_until_near");
            if (!melee)
                continue;
            meleeSeen++;
            if (meleeSeen % 2 == 0)
            {
                e.BehaviorKey = "hunt_ward";
                sappers++;
            }
        }
        if (sappers > 0)
            combatUI?.AppendActionLog($"── {sappers} of the attackers make for the Heart. ──");
    }

    // ── Station markers ──────────────────────────────────────────────────────

    /// <summary>A floating label per station tile so the crew can see where to
    /// stand. Label3D via the glyph-label pattern (billboard, no depth test,
    /// deferred add_child). Text rather than a glyph: the module name is the
    /// information, and every font renders it.</summary>
    private void SpawnStationMarkers()
    {
        var siege = grid?.ActiveSiege;
        if (siege == null)
            return;
        foreach (var (at, module) in siege.Stations)
        {
            var st = CastleModules.Get(module)?.Station;
            var view = grid.GetTileView(at);
            if (st == null || view == null)
                continue;
            if (view.GetNodeOrNull<Label3D>("StationMarker") != null)
                continue;
            var label = new Label3D
            {
                Name = "StationMarker",
                Text = st.Label.ToUpperInvariant(),
                FontSize = 26,
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                NoDepthTest = true,
                Position = new Vector3(0f, 0.95f, 0f),
                Modulate = new Color(1f, 0.78f, 0.30f, 0.9f),
                OutlineSize = UITheme.Label3DOutlineSize,
            };
            view.CallDeferred("add_child", label);
            SpawnStationProp(view, st.Kind);
        }
    }

    /// <summary>Placeholder prop per station kind until the module meshes exist:
    /// a ballista (post and a bar), a lantern (pole and a lit ball), a winch (a
    /// drum), a brazier rack (a low tray). All sit to one side of the tile so the
    /// keeper stands beside them. Tagged generated_obstacle so map cleanup frees
    /// them with the rest.</summary>
    private static void SpawnStationProp(HexTile view, string kind)
    {
        var root = new Node3D { Name = "StationProp" };
        var iron = new StandardMaterial3D { AlbedoColor = new Color(0.32f, 0.29f, 0.27f), Roughness = 0.8f, Metallic = 0.2f };
        var wood = new StandardMaterial3D { AlbedoColor = new Color(0.45f, 0.33f, 0.22f), Roughness = 1f };
        void Add(PrimitiveMesh mesh, Vector3 at, Vector3 rot, Material mat)
        {
            mesh.Material = mat;
            root.AddChild(new MeshInstance3D { Mesh = mesh, Position = at, RotationDegrees = rot });
        }
        float side = 0.42f;   // off centre, leaves the tile's middle for the keeper
        switch (kind)
        {
            case "ballista":
                Add(new BoxMesh { Size = new Vector3(0.18f, 0.9f, 0.18f) }, new Vector3(side, 0.45f, 0f), Vector3.Zero, wood);
                Add(new BoxMesh { Size = new Vector3(0.12f, 0.12f, 1.1f) }, new Vector3(side, 0.92f, 0f), Vector3.Zero, wood);
                Add(new BoxMesh { Size = new Vector3(0.9f, 0.08f, 0.08f) }, new Vector3(side, 0.92f, 0.35f), Vector3.Zero, iron);
                break;
            case "ward_lantern":
                Add(new CylinderMesh { TopRadius = 0.05f, BottomRadius = 0.07f, Height = 1.3f }, new Vector3(side, 0.65f, 0f), Vector3.Zero, iron);
                var glow = new StandardMaterial3D
                {
                    AlbedoColor = new Color(0.95f, 0.80f, 0.35f),
                    EmissionEnabled = true, Emission = new Color(1f, 0.75f, 0.30f), EmissionEnergyMultiplier = 1.6f,
                };
                Add(new SphereMesh { Radius = 0.17f, Height = 0.34f }, new Vector3(side, 1.4f, 0f), Vector3.Zero, glow);
                break;
            case "repair_winch":
                Add(new CylinderMesh { TopRadius = 0.28f, BottomRadius = 0.28f, Height = 0.5f }, new Vector3(side, 0.35f, 0f), new Vector3(0f, 0f, 90f), wood);
                Add(new BoxMesh { Size = new Vector3(0.1f, 0.6f, 0.1f) }, new Vector3(side + 0.3f, 0.3f, 0f), Vector3.Zero, iron);
                Add(new BoxMesh { Size = new Vector3(0.1f, 0.6f, 0.1f) }, new Vector3(side - 0.3f, 0.3f, 0f), Vector3.Zero, iron);
                break;
            case "brazier_rack":
                Add(new BoxMesh { Size = new Vector3(0.8f, 0.1f, 0.4f) }, new Vector3(side, 0.55f, 0f), Vector3.Zero, iron);
                Add(new BoxMesh { Size = new Vector3(0.08f, 0.55f, 0.08f) }, new Vector3(side + 0.34f, 0.27f, 0.15f), Vector3.Zero, iron);
                Add(new BoxMesh { Size = new Vector3(0.08f, 0.55f, 0.08f) }, new Vector3(side - 0.34f, 0.27f, -0.15f), Vector3.Zero, iron);
                var ember = new StandardMaterial3D
                {
                    AlbedoColor = new Color(0.9f, 0.35f, 0.1f),
                    EmissionEnabled = true, Emission = new Color(1f, 0.4f, 0.1f), EmissionEnergyMultiplier = 1.2f,
                };
                Add(new SphereMesh { Radius = 0.12f, Height = 0.24f }, new Vector3(side, 0.7f, 0f), Vector3.Zero, ember);
                break;
            default:
                Add(new BoxMesh { Size = new Vector3(0.4f, 0.6f, 0.4f) }, new Vector3(side, 0.3f, 0f), Vector3.Zero, iron);
                break;
        }
        root.Position = new Vector3(0f, view.TileTopY, 0f);
        root.AddToGroup("generated_obstacle");
        root.AddToGroup(CastleAnimator.StationGroup);
        root.SetMeta("station_kind", kind);
        view.CallDeferred("add_child", root);
    }

    /// <summary>Hover suffix for the move cost label: the station under the cursor.</summary>
    private string StationHoverSuffix(Vector2I dest)
    {
        var st = IsCastleDefense ? StationAt(dest) : null;
        return st == null ? "" : $"man the {st.Label}";
    }

    // ── Stations ─────────────────────────────────────────────────────────────

    /// <summary>Player turn start: apply standing bonuses to whoever mans a station.
    /// Ballista: attack range and damage while standing (cleared when the unit
    /// leaves, see ClearStationBonuses). Ward Lantern: shield to the keeper, cover
    /// armour to everyone in its glow.</summary>
    private void ApplyStationBonuses()
    {
        if (!IsCastleDefense)
            return;
        foreach (var u in playerUnits)
        {
            if (u == null || !IsInstanceValid(u) || !u.Stats.IsAlive || u.CurrentTile == null)
                continue;
            ApplyStationBonusFor(u, log: false);
            var st = StationAt(u.CurrentTile.Axial);
            if (st == null)
                continue;
            switch (st.Kind)
            {
                case "ward_lantern":
                    if (st.Shield > 0)
                        u.Stats.Shield += st.Shield;
                    foreach (var ally in playerUnits)
                    {
                        if (ally == null || !IsInstanceValid(ally) || !ally.Stats.IsAlive || ally.CurrentTile == null)
                            continue;
                        if (grid.Distance(u.CurrentTile.Axial, ally.CurrentTile.Axial) <= st.Radius)
                            ally.Stats.CoverArmor += st.CoverBonus;
                    }
                    combatUI?.AppendActionLog($"{u.DisplayName} keeps the {st.Label}: the walls hold firmer within {st.Radius}.");
                    break;
            }
        }
    }

    private static void ClearStationBonuses(Unit u)
    {
        u.StationRangeBonus = 0;
        u.StationDamageBonus = 0;
        u.StationLabel = "";
        u.StationWeapon = null;
        u.StationShotsLeft = 0;
    }

    /// <summary>The ballista is live the moment a martial unit steps onto its tile:
    /// the unit's ordinary attack gains the station's range and damage until it
    /// steps off. No new verb, no menu: walk on, then attack as usual. Called on
    /// every player move (HandleUnitMoved) and at turn start.</summary>
    private void ApplyStationBonusFor(Unit u, bool log)
    {
        if (!IsCastleDefense || u == null || !IsInstanceValid(u))
            return;
        bool was = !string.IsNullOrEmpty(u.StationLabel);
        int shotsLeft = u.StationShotsLeft;
        ClearStationBonuses(u);
        var st = u.CurrentTile != null ? StationAt(u.CurrentTile.Axial) : null;
        if (st != null && st.Kind == "ballista" && u.IsPlayerControlled && !u.IsObjectiveWard)
        {
            u.StationWeapon = st;
            // Shots reset at turn start (log false); stepping on mid-turn loads it fresh,
            // stepping off and back on does not reload it.
            u.StationShotsLeft = !log ? st.Shots : (was ? shotsLeft : st.Shots);
            u.StationLabel = $"{st.Label.ToUpperInvariant()} rng {st.Range} dmg {st.Damage} ({st.Ap} AP, Alt+click)";
            if (log && !was)
                combatUI?.AppendActionLog($"{u.DisplayName} mans the {st.Label}: Alt+click an enemy to fire (range {st.Range}, {st.Damage} damage, {st.Ap} AP, {st.Shots} shot(s) a round).");
        }
        else if (log && was)
        {
            combatUI?.AppendActionLog($"{u.DisplayName} leaves the station.");
        }
        if (log && (was || !string.IsNullOrEmpty(u.StationLabel)) && u == selectedUnit)
            RefreshSelectedUnitUI();
    }

    /// <summary>Fire the crew weapon the unit is manning (Alt+click). A heavy bolt:
    /// the station's own range and damage, Bolt delivery so cover soaks it and full
    /// cover stops it, and the target is thrown straight back <c>push</c> tiles
    /// through the resolver (collision floor half the bolt). Costs the station's
    /// AP from the crew member; one shot a round per station by default.</summary>
    private void TryFireStation(Unit crew, Unit target)
    {
        var st = crew?.StationWeapon;
        if (st == null || target == null || crew.CurrentTile == null || target.CurrentTile == null)
            return;
        if (!crew.CanAct())
        {
            combatUI?.AppendActionLog($"{crew.DisplayName} is frozen!");
            return;
        }
        if (crew.StationShotsLeft <= 0)
        {
            combatUI?.AppendActionLog($"The {st.Label} is spent this round: it reloads at your next turn.");
            return;
        }
        var a = crew.CurrentTile.Axial;
        var b = target.CurrentTile.Axial;
        int reach = st.Range + (crew.CurrentTile.Height > target.CurrentTile.Height ? 1 : 0);
        int dist = grid.Distance(a, b);
        if (dist > reach)
        {
            combatUI?.AppendActionLog($"{st.Label}: {target.DisplayName} is out of range ({dist} > {reach}).");
            return;
        }
        if (!grid.HasLineOfSight(a, b))
        {
            combatUI?.AppendActionLog($"{st.Label}: no line of sight to {target.DisplayName}.");
            return;
        }
        if (dist > 1 && grid.CoverBetween(b, a) == CoverKind.High)
        {
            combatUI?.AppendActionLog($"{st.Label}: {target.DisplayName} is behind full cover from here.");
            return;
        }
        if (!crew.TrySpendAP(st.Ap))
        {
            combatUI?.AppendActionLog($"{crew.DisplayName} needs {st.Ap} AP to fire the {st.Label} (has {crew.CurrentActionPoints}).");
            return;
        }

        crew.Stats.HasActed = true;
        crew.StationShotsLeft--;
        combatUI?.AppendActionLog($"{crew.DisplayName} fires the {st.Label} at {target.DisplayName}: {st.Damage} damage.");
        CombatPresenter.EmitStrike(crew, target, Delivery.Bolt);   // spell_vfx_pipeline_v1 §5 phase 2
        target.ApplyDamage(st.Damage, crew, Delivery.Bolt);
        if (st.Push > 0 && target.Stats.IsAlive && target.CurrentTile != null && !(target.IsMapObject && !target.Pushable))
        {
            var dir = ForcedMove.StepAwayFrom(grid, a, b);
            ForcedMove.Push(grid, target, dir, st.Push, Math.Max(2, st.Damage / 2), null,
                            m => combatUI?.AppendActionLog(m));
        }
        _castleAnimator?.Recoil(a);

        RefreshSelectedUnitUI();
        RefreshEnemyRoster();
        RefreshPlayerUnitBar();
        RefreshThreatTiles();
        ClearMoveTiles();
        if (selectedUnit != null)
            ShowMoveTilesWithCost(selectedUnit);
    }

    /// <summary>Why the manned station cannot hit <paramref name="target"/>, or null.</summary>
    private string StationBlockReason(Unit crew, Unit target)
    {
        var st = crew?.StationWeapon;
        if (st == null || crew.CurrentTile == null || target?.CurrentTile == null)
            return "no station";
        var a = crew.CurrentTile.Axial; var b = target.CurrentTile.Axial;
        int reach = st.Range + (crew.CurrentTile.Height > target.CurrentTile.Height ? 1 : 0);
        if (grid.Distance(a, b) > reach) return "out of range";
        if (!grid.HasLineOfSight(a, b)) return "no sight";
        if (grid.Distance(a, b) > 1 && grid.CoverBetween(b, a) == CoverKind.High) return "full cover";
        if (crew.StationShotsLeft <= 0) return "spent";
        return null;
    }

    /// <summary>Round boundary (after the enemy phase): stations that act on the
    /// castle rather than the keeper. Repair Winch mends the most damaged door or
    /// the Heart. Brazier Rack sets the three tiles before the gate alight.</summary>
    private void RunStationRoundEffects()
    {
        if (!IsCastleDefense)
            return;
        var siege = grid.ActiveSiege;
        foreach (var u in playerUnits)
        {
            if (u == null || !IsInstanceValid(u) || !u.Stats.IsAlive || u.CurrentTile == null)
                continue;
            var st = StationAt(u.CurrentTile.Axial);
            if (st == null)
                continue;
            switch (st.Kind)
            {
                case "repair_winch":
                {
                    Unit target = null; int worst = 0;
                    foreach (var p in playerUnits)
                    {
                        if (p == null || !IsInstanceValid(p) || !p.Stats.IsAlive) continue;
                        if (!p.IsStructure && !p.IsObjectiveWard) continue;
                        int missing = p.Stats.MaxHealth - p.Stats.Health;
                        if (missing > worst) { worst = missing; target = p; }
                    }
                    if (target != null)
                    {
                        target.Stats.Health = Math.Min(target.Stats.MaxHealth, target.Stats.Health + st.Repair);
                        target.RefreshHealthBar();
                        combatUI?.AppendActionLog($"{u.DisplayName} works the {st.Label}: {target.DisplayName} mended {st.Repair}.");
                    }
                    break;
                }
                case "brazier_rack":
                {
                    int lit = 0;
                    foreach (var g in siege.GateGap)
                    {
                        var dir = ForcedMove.StepAwayFrom(grid, siege.Heart.Value, g);
                        var outside = grid.GetTile(g + dir);
                        if (outside != null && outside.IsWalkable && !outside.IsBlocked && outside.TerrainType != TileTerrainType.Water)
                        {
                            TileEntryReactions.ImbueTile(outside, TileElementType.Fire);
                            lit++;
                        }
                    }
                    if (lit > 0)
                        combatUI?.AppendActionLog($"{u.DisplayName} tips the {st.Label}: fire before the gate ({lit} tile(s)).");
                    break;
                }
            }
        }
    }
}
