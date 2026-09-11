using Godot;
using System.Collections.Generic;
using System.Threading.Tasks;

// ============================================================
// CombatPresenter.cs
//
// Purpose:        The presentation seam between the synchronous rules engine
//                 and everything the player SEES happen. The resolver, the
//                 damage path and the death path emit VisualEvents into this
//                 node's queue and return immediately; the presenter replays
//                 the queue in order on its own clock, spawning projectiles,
//                 bursts and damage numbers, and applying the DISPLAYED state
//                 (health-bar refresh, hide-on-death) only when the matching
//                 event lands. The rules never wait on it and never know it
//                 exists beyond three static entry points.
//
//                 Rulings baked in:
//                 - Never in the simulation (CombatSim.Active): the R22 drag
//                   preview runs the real resolver and must spawn nothing.
//                 - Always self-draining: the queue plays whenever it is
//                   non-empty, so enemy turns, DoT ticks and triggers get the
//                   same treatment as a player cast with no explicit flush.
//                 - Damage numbers are the mitigated numbers: emitted from
//                   Unit.ApplyDamage after ComputeMitigation, so what pops is
//                   what landed.
//                 - No input lock in phase 1: the sim is already resolved, so
//                   overlapping visuals are cosmetic. IsPlaying is exposed for
//                   a later lock.
// Layer:          Systems / Combat / Presentation
// Collaborators:  VisualEvent, VfxLibrary, ProjectileVfx, BurstVfx,
//                 DamageNumber; producers: Resolver.ResolveTop (cast),
//                 Unit.ApplyDamage (damage), Unit.Die (death);
//                 CombatManager._Ready (Ensure)
// See:            docs/spell_vfx_pipeline_v1.md
// ============================================================

public partial class CombatPresenter : Node
{
    /// <summary>The live presenter for the current combat, or null (headless,
    /// tests, campus). Every static entry point no-ops when this is null.</summary>
    public static CombatPresenter Current { get; private set; }

    /// <summary>Playback speed multiplier. Auto-raised when a backlog builds so a
    /// Spell Storm does not take ten seconds to show.</summary>
    public float SpeedScale = 1f;

    /// <summary>Backlog length above which playback fast-forwards.</summary>
    public int BacklogFastForwardThreshold = 6;
    public float BacklogSpeed = 2.5f;

    /// <summary>Height above a unit's origin at which projectiles are aimed and
    /// launched (roughly chest height on the 1.75 m stand-in after scale).</summary>
    public float ChestHeight = 1.0f;

    /// <summary>Height above a unit's origin where damage numbers spawn.</summary>
    public float NumberHeight = 1.7f;

    /// <summary>Stagger between successive damage numbers so a burst on five
    /// units does not print five glyphs on the same frame.</summary>
    public float DamageStagger = 0.05f;

    /// <summary>Delay between a lethal hit landing and the body vanishing. Long
    /// enough for the number to be read on the body; short enough not to block
    /// the tile it already freed for the rules.</summary>
    public float DeathHold = 0.35f;

    public bool IsPlaying => _playing;

    /// <summary>Seconds per tile for a walked step / a forced push. Teleports snap.</summary>
    public float WalkStepDuration = 0.14f;
    public float ForcedStepDuration = 0.20f;

    private readonly Queue<VisualEvent> _queue = new();
    private bool _playing;
    private Node3D _fxRoot;

    /// <summary>Units whose Death event has not played yet. PruneDeadUnits must not
    /// free these; FreeAfterVisuals parks them here instead.</summary>
    private readonly HashSet<Unit> _pendingDeath = new();
    private readonly HashSet<Unit> _freeAfterDeath = new();

    // ── Install ──────────────────────────────────────────────────────────────

    /// <summary>Create (or reuse) the presenter under <paramref name="host"/>. Called
    /// once from CombatManager._Ready. Uses CallDeferred for the add, per the
    /// standing Mac build rule.</summary>
    public static CombatPresenter Ensure(Node host)
    {
        if (Current != null && GodotObject.IsInstanceValid(Current))
            return Current;
        var p = new CombatPresenter { Name = "CombatPresenter" };
        Current = p;
        host.CallDeferred(Node.MethodName.AddChild, p);
        return p;
    }

    public override void _Ready()
    {
        _fxRoot = new Node3D { Name = "FxRoot" };
        AddChild(_fxRoot);
        SetProcess(true);
    }

    public override void _ExitTree()
    {
        if (Current == this)
            Current = null;
        _queue.Clear();
        foreach (var u in _freeAfterDeath)
            if (u != null && GodotObject.IsInstanceValid(u))
                u.QueueFree();
        _freeAfterDeath.Clear();
        _pendingDeath.Clear();
        base._ExitTree();
    }

    // ── Producers (static; safe from anywhere) ───────────────────────────────

    private static bool Live
        => Current != null && GodotObject.IsInstanceValid(Current)
           && Current.IsInsideTree() && !CombatSim.Active;

    /// <summary>Resolver.ResolveTop: a stack item is about to run its effects.</summary>
    public static void EmitCast(StackItem item)
    {
        if (!Live || item == null)
            return;
        Current._queue.Enqueue(VisualEvent.ForCast(item));
    }

    /// <summary>Unit.ApplyDamage: pools just changed. Returns true when the
    /// presenter took ownership of the visual (the caller must then NOT refresh
    /// its health bar); false when the caller should refresh immediately as
    /// before (no presenter, or in simulation).</summary>
    public static bool TryDeferDamageVisual(Unit victim, int hpLoss, int shieldLoss, int armorLoss)
    {
        if (!Live || victim == null)
            return false;
        Current._queue.Enqueue(VisualEvent.ForDamage(victim, hpLoss, shieldLoss, armorLoss));
        return true;
    }

    /// <summary>Unit.Die: the unit is rules-dead. Returns true when the presenter
    /// will hide it on its own schedule; false when the caller must hide it now.</summary>
    public static bool TryDeferDeathVisual(Unit victim)
    {
        if (!Live || victim == null)
            return false;
        Current._pendingDeath.Add(victim);
        Current._queue.Enqueue(VisualEvent.ForDeath(victim));
        return true;
    }

    /// <summary>Unit.PlaceOnTile: the rules position changed. Returns true when the
    /// presenter will move the body itself (tween, in queue order); false when the
    /// caller must snap now (no presenter, sim, or a fresh spawn with no prior tile).</summary>
    public static bool TryDeferMoveVisual(Unit mover, Vector3 to, MovementKind kind)
    {
        if (!Live || mover == null || !GodotObject.IsInstanceValid(mover) || !mover.IsInsideTree())
            return false;
        Current._queue.Enqueue(VisualEvent.ForMove(mover, to, kind));
        return true;
    }

    /// <summary>CombatManager.PruneDeadUnits: the rules are done with this corpse.
    /// Frees it now unless its Death visual is still queued, in which case the free
    /// happens right after that visual plays. Never loses a node: _ExitTree flushes.</summary>
    public static void FreeAfterVisuals(Unit u)
    {
        if (u == null || !GodotObject.IsInstanceValid(u))
            return;
        if (Current != null && GodotObject.IsInstanceValid(Current) && Current._pendingDeath.Contains(u))
        {
            Current._freeAfterDeath.Add(u);
            return;
        }
        u.QueueFree();
    }

    /// <summary>Martial strike paths (player attacks, enemy ResolveStrike, construct and
    /// siege-station shots, zone-of-control hits) call Unit.ApplyDamage directly and
    /// never pass through the resolver, so they announce themselves here. School is
    /// unknown by construction: steel and arrows use the neutral palette. Call it
    /// BEFORE the ApplyDamage so the travel precedes the number.</summary>
    public static void EmitStrike(Unit attacker, Unit victim, Delivery delivery)
    {
        if (!Live || victim == null || !GodotObject.IsInstanceValid(victim) || !victim.IsInsideTree())
            return;
        var ev = new VisualEvent
        {
            Kind = VisualEventKind.Cast,
            Caster = attacker,
            Delivery = delivery,
            HasUnitTargets = true
        };
        ev.TargetPoints.Add(VisualEvent.RulesPositionOf(victim));
        Current._queue.Enqueue(ev);
    }

    /// <summary>Fast-forward everything queued (hold-to-skip hook for later UI).</summary>
    public void Skip() => SpeedScale = 6f;

    // ── Playback ─────────────────────────────────────────────────────────────

    public override void _Process(double delta)
    {
        if (!_playing && _queue.Count > 0)
            _ = PlayQueueAsync();
    }

    private async Task PlayQueueAsync()
    {
        _playing = true;
        try
        {
            while (_queue.Count > 0 && GodotObject.IsInstanceValid(this) && IsInsideTree())
            {
                var ev = _queue.Dequeue();
                float speed = SpeedScale;
                if (_queue.Count > BacklogFastForwardThreshold)
                    speed = Mathf.Max(speed, BacklogSpeed);

                switch (ev.Kind)
                {
                    case VisualEventKind.Cast:
                        await PlayCast(ev, speed);
                        break;
                    case VisualEventKind.Damage:
                        await PlayDamage(ev, speed);
                        break;
                    case VisualEventKind.Death:
                        await PlayDeath(ev, speed);
                        break;
                    case VisualEventKind.Move:
                        await PlayMove(ev, speed);
                        break;
                }
            }
        }
        finally
        {
            _playing = false;
            if (_queue.Count == 0)
                SpeedScale = 1f;   // a Skip() lasts until the backlog is gone
        }
    }

    private async Task PlayCast(VisualEvent ev, float speed)
    {
        string archetype = ResolveArchetype(ev);

        Vector3 origin;
        bool casterValid = ev.Caster != null && GodotObject.IsInstanceValid(ev.Caster)
                           && ev.Caster.IsInsideTree();
        if (casterValid)
            origin = ev.Caster.GlobalPosition + new Vector3(0f, ChestHeight, 0f);
        else if (ev.TargetPoints.Count > 0)
            origin = ev.TargetPoints[0] + new Vector3(0f, ChestHeight + 2.5f, 0f);
        else
            return;   // nothing to show and nowhere to show it

        // Caster release flash. Not awaited in full: the projectile leaves while
        // the flash is still fading, which reads as "thrown from the flash".
        if (casterValid && archetype != null && archetype != VfxLibrary.ArchImpact)
        {
            var release = VfxLibrary.MakeBurst(VfxLibrary.ArchCast, ev, 0.25f);
            _fxRoot.AddChild(release);
            release.GlobalPosition = origin;
            _ = release.Play(speed);
            await Wait(0.08f / speed);
        }

        switch (archetype)
        {
            case VfxLibrary.ArchBolt:
            case VfxLibrary.ArchArc:
            {
                var flights = new List<Task>();
                foreach (var point in ev.TargetPoints)
                {
                    var proj = VfxLibrary.MakeProjectile(archetype, ev);
                    _fxRoot.AddChild(proj);
                    Vector3 to = point + new Vector3(0f, ChestHeight, 0f);
                    flights.Add(proj.Launch(origin, to, speed));
                }
                if (flights.Count > 0)
                    await Task.WhenAll(flights);
                foreach (var point in ev.TargetPoints)
                {
                    var impact = VfxLibrary.MakeBurst(VfxLibrary.ArchImpact, ev, 0.35f);
                    _fxRoot.AddChild(impact);
                    impact.GlobalPosition = point + new Vector3(0f, ChestHeight, 0f);
                    _ = impact.Play(speed);
                }
                break;
            }
            case VfxLibrary.ArchBurst:
            {
                if (ev.TargetPoints.Count == 0)
                    break;
                Vector3 centroid = Vector3.Zero;
                foreach (var p in ev.TargetPoints)
                    centroid += p;
                centroid /= ev.TargetPoints.Count;
                float spread = 0f;
                foreach (var p in ev.TargetPoints)
                    spread = Mathf.Max(spread, new Vector2(p.X - centroid.X, p.Z - centroid.Z).Length());
                float radius = Mathf.Clamp(spread + 0.9f, 0.9f, 3.5f);

                var burst = VfxLibrary.MakeBurst(VfxLibrary.ArchBurst, ev, radius);
                _fxRoot.AddChild(burst);
                burst.GlobalPosition = centroid + new Vector3(0f, 0.15f, 0f);
                await burst.Play(speed);
                break;
            }
            case VfxLibrary.ArchImpact:
            {
                foreach (var point in ev.TargetPoints)
                {
                    var impact = VfxLibrary.MakeBurst(VfxLibrary.ArchImpact, ev, 0.3f);
                    _fxRoot.AddChild(impact);
                    impact.GlobalPosition = point + new Vector3(0f, ChestHeight, 0f);
                    _ = impact.Play(speed);
                }
                await Wait(0.12f / speed);
                break;
            }
            default:
                break;
        }
    }

    /// <summary>Delivery → visual archetype. Untyped is the legacy default on most
    /// selectors, so it is read by target shape: units get the lobbed "willed"
    /// flight (the Arc rule: magic goes around cover), bare tiles get a burst,
    /// nothing gets nothing.</summary>
    private static string ResolveArchetype(VisualEvent ev)
    {
        // Card-authored override wins outright ("vfx": { "archetype": ... }).
        if (!string.IsNullOrEmpty(ev.VfxArchetype))
        {
            switch (ev.VfxArchetype.ToLowerInvariant())
            {
                case VfxLibrary.ArchBolt: return VfxLibrary.ArchBolt;
                case VfxLibrary.ArchArc: return VfxLibrary.ArchArc;
                case VfxLibrary.ArchBurst: return VfxLibrary.ArchBurst;
                case VfxLibrary.ArchImpact: return VfxLibrary.ArchImpact;
                case VfxLibrary.ArchNone: return null;
                default: break;   // unknown word: fall through to Delivery
            }
        }

        switch (ev.Delivery)
        {
            case Delivery.Bolt: return VfxLibrary.ArchBolt;
            case Delivery.Arc: return VfxLibrary.ArchArc;
            case Delivery.Burst: return VfxLibrary.ArchBurst;
            case Delivery.Ground: return VfxLibrary.ArchBurst;
            case Delivery.Melee: return VfxLibrary.ArchImpact;
            default:
                if (ev.HasUnitTargets) return VfxLibrary.ArchArc;
                if (ev.TargetPoints.Count > 0) return VfxLibrary.ArchBurst;
                return null;
        }
    }

    private async Task PlayDamage(VisualEvent ev, float speed)
    {
        var u = ev.Victim;
        bool alive = u != null && GodotObject.IsInstanceValid(u);

        // The displayed consequence: the bar catches up to the rules NOW.
        if (alive)
            u.RefreshHealthBar();

        Vector3 at;
        bool atKnown = false;
        if (alive && u.IsInsideTree())
        { at = u.GlobalPosition; atKnown = true; }
        else if (ev.VictimPointKnown)
        { at = ev.VictimPoint; atKnown = true; }
        else
            at = Vector3.Zero;

        if (atKnown)
        {
            var number = VfxLibrary.MakeDamageNumber(ev.HpLoss, ev.ShieldLoss, ev.ArmorLoss);
            _fxRoot.AddChild(number);
            number.GlobalPosition = at + new Vector3(0f, NumberHeight, 0f);
            number.Play(speed);
        }

        await Wait(DamageStagger / speed);
    }

    private async Task PlayDeath(VisualEvent ev, float speed)
    {
        var u = ev.Victim;
        await Wait(DeathHold / speed);
        if (u != null && GodotObject.IsInstanceValid(u))
            u.Visible = false;
        _pendingDeath.Remove(u);
        if (_freeAfterDeath.Remove(u) && u != null && GodotObject.IsInstanceValid(u))
            u.QueueFree();
    }

    private async Task PlayMove(VisualEvent ev, float speed)
    {
        var u = ev.Mover;
        if (u == null || !GodotObject.IsInstanceValid(u) || !u.IsInsideTree())
            return;

        float dur = ev.MoveKind switch
        {
            MovementKind.Walked => WalkStepDuration,
            MovementKind.Forced => ForcedStepDuration,
            _ => 0f
        } / speed;

        if (dur <= 0.01f || u.GlobalPosition.DistanceTo(ev.MoveTo) < 0.001f)
        {
            u.GlobalPosition = ev.MoveTo;
            return;
        }

        var tw = u.CreateTween();
        tw.SetEase(ev.MoveKind == MovementKind.Forced ? Tween.EaseType.Out : Tween.EaseType.InOut)
          .SetTrans(ev.MoveKind == MovementKind.Forced ? Tween.TransitionType.Quad : Tween.TransitionType.Sine);
        tw.TweenProperty(u, "global_position", ev.MoveTo, dur);
        await ToSignal(tw, Tween.SignalName.Finished);
        if (GodotObject.IsInstanceValid(u))
            u.GlobalPosition = ev.MoveTo;   // land exactly, whatever the tween did
    }

    private async Task Wait(float seconds)
    {
        if (seconds <= 0f)
            return;
        var timer = GetTree().CreateTimer(seconds);
        await ToSignal(timer, SceneTreeTimer.SignalName.Timeout);
    }
}
