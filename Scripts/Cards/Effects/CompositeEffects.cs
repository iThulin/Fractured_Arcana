using System;
using System.Collections.Generic;
using Godot;

// ============================================================
// CompositeEffects.cs
//
// Purpose:        Core composite effects — Sequence, Conditional,
//                 ForEachTarget, Retarget, Push/PullDamage — plus
//                 shared element-tile utilities (ImbuePath,
//                 ImbueArea, ConsumeElementTile). School capstones
//                 (Cataclysm, Ragnarok, Terraform, etc.) now live
//                 in ElementalistEffects.cs. Together with the
//                 leaf primitives in Effect.cs, any card's
//                 behaviour can be expressed as a tree.
// Layer:          Effects
// Collaborators:  Effect.cs (EffectBase + leaf effects),
//                 PersistentEffect.cs (some composites spawn
//                 persistent zones, e.g. CreateMaelstromEffect →
//                 MaelstromEffect, AvatarTransformEffect →
//                 AvatarAuraEffect),
//                 JsonCardLoader.cs (RegisterBuiltins maps JSON
//                 type strings to these classes),
//                 GameState.cs, ElementalAttunement.cs
// See:            README §5.4 — Composite Effects
// ============================================================

/// <summary>Resolves a list of child effects in order, threading the resulting <see cref="EffectResult"/> from each step into the next via <c>PredicateContext.LastResult</c>. The result of the final step is returned to the parent.</summary>
public sealed class SequenceEffect : EffectBase
{
    public IEffect[] Steps;

    public SequenceEffect(params IEffect[] steps) { Steps = steps; }

    public override IEnumerable<IEffect> Children => Steps;

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        var ctx = new PredicateContext { Game = s, Caster = caster, Targets = targets, Snapshot = snap };
        ResolveWithResult(ctx);
    }

    public override EffectResult ResolveWithResult(PredicateContext ctx)
    {
        EffectResult last = new();
        for (int i = 0; i < Steps.Length; i++)
        {
            var step = Steps[i];
            if (step is EffectBase eb)
            {
                last = eb.ResolveWithResult(ctx);
                ctx.LastResult = last;
            }
            else
            {
                // Fallback for any IEffect that doesn't use EffectBase
                step.Resolve(ctx.Game, ctx.Caster, ctx.Targets, ctx.Snapshot);
                last = new EffectResult();
            }

            // Post-cast player choice (2026-07-28): that step asked the player
            // something and returned without finishing. Everything AFTER it must
            // happen after the answer, so fold the remaining steps into the
            // request's continuation and stop here.
            //
            // Without this, `[scry, draw]` would draw before the player had chosen
            // what to keep — and five of the nine authored scry sequences have steps
            // after the scry. The alternative was a rule that a choice must be the
            // last step of its sequence, enforced in the loader; that is a constraint
            // on the CONTENT to work around a limitation of the CODE, and it would be
            // violated by the first person who forgot.
            var pending = ctx.Game?.PendingChoice;
            if (pending != null && i < Steps.Length - 1)
            {
                var rest = new IEffect[Steps.Length - i - 1];
                Array.Copy(Steps, i + 1, rest, 0, rest.Length);
                var tail = new SequenceEffect(rest);
                var capturedCtx = ctx;
                pending.Then(_ => tail.ResolveWithResult(capturedCtx));
                return last;
            }
        }
        return last;
    }
}

/// <summary>
/// Choose One — the cast-time modal (2026-07-29). Holds N option effects; exactly one
/// resolves, selected by <see cref="EffectSnapshot.ChosenOption"/>, which CombatManager
/// sets from a mode picker BEFORE the cast is paid.
///
/// This is deliberately NOT a post-cast continuation. Both options are printed on the
/// card, so the information the player needs exists at cast time — by the bucket test
/// (post_cast_choice_v1 §1) that makes it an input-layer choice: the pick is public
/// when the spell goes on the stack (a Reaction can respond to the chosen mode), the
/// preview can model the chosen mode, and cancelling is free because nothing has been
/// paid. The index rides the snapshot so a Reaction cast while this waits on the
/// stack cannot clobber it.
///
/// With no pick recorded (AI cast, headless test), option 0 — the FIRST authored
/// option, by convention the safest — resolves, and the log says so.
/// JSON: { "type": "choose_one", "options": [
///          { "label": "...", "description": "...", "effect": {...} }, ... ] }
/// </summary>
public sealed class ChooseOneEffect : EffectBase
{
    public IEffect[] Options = Array.Empty<IEffect>();
    public string[] Labels = Array.Empty<string>();
    public string[] Descriptions = Array.Empty<string>();

    public override IEnumerable<IEffect> Children => Options;

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        if (Options.Length == 0)
        { s?.Log("[ChooseOne] no options authored — no-op."); return; }

        int idx = snap?.ChosenOption ?? -1;
        if (idx < 0 || idx >= Options.Length)
        {
            if (idx != -1)
                s?.Log($"[ChooseOne] option {idx} out of range — using option 0.");
            else
                s?.Log($"[ChooseOne] no mode was picked (AI/headless) — using option 0" +
                       (Labels.Length > 0 ? $" ({Labels[0]})." : "."));
            idx = 0;
        }
        else if (Labels.Length > idx)
            s?.Log($"[ChooseOne] resolving '{Labels[idx]}'.");

        Options[idx].Resolve(s, caster, targets, snap);
    }
}

/// <summary>Branches on a predicate. <see cref="Then"/> is required; <see cref="Else"/> is optional (no-ops when null). Reads <see cref="PredicateContext.LastResult"/> if the predicate needs it (e.g. <c>was_lethal</c>).</summary>
public sealed class ConditionalEffect : EffectBase
{
    public IPredicate If;
    public IEffect Then;
    public IEffect Else; // may be null

    public ConditionalEffect(IPredicate pred, IEffect thenEff, IEffect elseEff = null)
    {
        If = pred;
        Then = thenEff;
        Else = elseEff;
    }

    public override IEnumerable<IEffect> Children
    {
        get
        {
            yield return Then;
            if (Else != null)
                yield return Else;
        }
    }

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        var ctx = new PredicateContext { Game = s, Caster = caster, Targets = targets, Snapshot = snap };
        ResolveWithResult(ctx);
    }

    public override EffectResult ResolveWithResult(PredicateContext ctx)
    {
        bool branch = If.Evaluate(ctx);
        s_LogBranch(ctx, branch);

        var chosen = branch ? Then : Else;
        if (chosen == null)
            return new EffectResult();

        if (chosen is EffectBase eb)
            return eb.ResolveWithResult(ctx);
        chosen.Resolve(ctx.Game, ctx.Caster, ctx.Targets, ctx.Snapshot);
        return new EffectResult();
    }

    private static void s_LogBranch(PredicateContext ctx, bool taken)
    {
        ctx.Game?.Log($"[Conditional] predicate={taken} -> {(taken ? "THEN" : "ELSE")}");
    }
}

/// <summary>Runs the child effect once per target in the current target set, wrapping each target in a single-element <see cref="TargetSet"/> so the child sees exactly one target at a time. Order follows <c>TargetSet.Items</c>.</summary>
public sealed class ForEachTargetEffect : EffectBase
{
    public IEffect PerTarget;

    public ForEachTargetEffect(IEffect per) { PerTarget = per; }

    public override IEnumerable<IEffect> Children { get { yield return PerTarget; } }

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        var ctx = new PredicateContext { Game = s, Caster = caster, Snapshot = snap };
        foreach (var item in targets.Items)
        {
            // Wrap single target so nested effects see exactly one target.
            var single = new TargetSet();
            single.Items.Add(item);
            ctx.Targets = single;
            ctx.Caster = caster;

            if (PerTarget is EffectBase eb)
                eb.ResolveWithResult(ctx);
            else
                PerTarget.Resolve(s, caster, single, snap);
        }
    }
}

/// <summary>Pushes each target away from the caster up to <see cref="PushTiles"/> tiles, then deals <c>pushed × DamagePerTile</c> damage (proportional to actual distance moved, not the requested amount).</summary>
public sealed class PushDamageEffect : EffectBase
{
    public int PushTiles;
    public int DamagePerTile;

    /// <summary>Aimed mode (2026-07-29): with a `unit_then_direction` targeter, the
    /// TargetSet arrives as [victim, aim-tile] and the shove walks the CHOSEN axis
    /// instead of away-from-caster — Gore hurls the boar's victim where the player
    /// points it, which is the entire reason to cast Gore next to a wall. Falls back
    /// to the derived direction when no aim tile is present, so the same effect class
    /// serves both authorings.</summary>
    public bool Aimed;

    public PushDamageEffect(int pushTiles, int damagePerTile, bool aimed = false)
    {
        PushTiles = pushTiles;
        DamagePerTile = damagePerTile;
        Aimed = aimed;
    }

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        var casterUnit = FindCasterUnit(s, caster);
        if (casterUnit?.CurrentTile == null || s?.Grid == null)
            return;
        if (targets == null)
            return;

        // Aimed: [victim, tile] via the shared TwoStep reader; the aim tile is the
        // direction, not the landing spot — same convention as PushAimedEffect.
        Vector2I? aimedDir = null;
        if (Aimed && TwoStep.Read(s, targets, "PushDamage", out var aimedVictim, out var aimTile))
        {
            var d = aimTile.Axial - aimedVictim.CurrentTile.Axial;
            if (d != Vector2I.Zero)
                aimedDir = d;
        }

        var casterPos = casterUnit.CurrentTile.Axial;

        foreach (var obj in targets.Items)
        {
            var victim = ResolveTargetUnit(s, obj);
            if (victim == null || victim.CurrentTile == null)
                continue;
            if (Aimed && obj is TileData)
                continue;   // the aim tile's occupant is a bystander, not a target

            // Forced movement so tile-entry verbs (Fire Sears, Frost Slides,
            // Stone Anchors, falling) fire per step (tile_interaction_spec §2.1).
            var ctx = new MoveContext(s.Grid);
            int pushed = 0;
            for (int i = 0; i < PushTiles; i++)
            {
                if (ctx.HaltForced || ctx.ForcedTilesRemaining <= 0)
                    break;

                var current = victim.CurrentTile.Axial;
                TileData bestTile = null;

                if (aimedDir.HasValue)
                {
                    var td = s.Grid.GetTile(current + aimedDir.Value);
                    if (td != null && td.CanEnter(victim))
                        bestTile = td;
                }
                else
                {
                    int bestDist = -1;
                    foreach (var neighbor in s.Grid.GetNeighbors(current))
                    {
                        var td = s.Grid.GetTile(neighbor);
                        if (td == null || !td.CanEnter(victim))
                            continue;

                        int distFromCaster = s.Grid.Distance(casterPos, neighbor);
                        if (distFromCaster > bestDist)
                        {
                            bestDist = distFromCaster;
                            bestTile = td;
                        }
                    }
                }

                if (bestTile != null)
                {
                    ctx.ForcedTilesRemaining--;
                    victim.PlaceOnTile(bestTile, MovementKind.Forced, ctx);
                    pushed++;
                    if (ctx.HaltForced) // Stone Anchors caught it, or the cap hit
                        break;
                }
                else
                {
                    s.Log($"[PushDamage] {victim.Name} hit obstacle after {pushed} tile(s).");
                    break;
                }
            }

            int totalDmg = pushed * DamagePerTile;
            if (totalDmg > 0)
            {
                victim.ApplyDamage(totalDmg);
                s.Log($"[PushDamage] {victim.Name} pushed {pushed} tile(s), takes {totalDmg} damage ({DamagePerTile}/tile).");
            }
            else
            {
                s.Log($"[PushDamage] {victim.Name} couldn't be pushed.");
            }
        }
    }
}

// ── Pull Damage Effect ─────────────────────────────────────────────────────────

/// <summary>
/// Pulls each target toward the caster up to <see cref="PullTiles"/> tiles,
/// then deals <c>pulled × DamagePerTile</c> damage proportional to actual
/// distance moved. Mirrors PushDamageEffect exactly but in reverse direction.
/// JSON keys: "tiles", "damage_per_tile".
/// </summary>
public sealed class PullDamageEffect : EffectBase
{
    public int PullTiles;
    public int DamagePerTile;

    public PullDamageEffect(int pullTiles, int damagePerTile)
    {
        PullTiles = pullTiles;
        DamagePerTile = damagePerTile;
    }

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        var casterUnit = FindCasterUnit(s, caster);
        if (casterUnit?.CurrentTile == null || s?.Grid == null || targets == null)
            return;

        var casterPos = casterUnit.CurrentTile.Axial;

        foreach (var obj in targets.Items)
        {
            var victim = ResolveTargetUnit(s, obj);
            if (victim == null || victim.CurrentTile == null)
                continue;
            if (victim == casterUnit)
                continue;

            // Forced movement (entry verbs fire per step); pull suppresses falling.
            var ctx = new MoveContext(s.Grid) { SuppressFalling = true };
            int pulled = 0;

            for (int i = 0; i < PullTiles; i++)
            {
                if (ctx.HaltForced || ctx.ForcedTilesRemaining <= 0)
                    break;

                var current = victim.CurrentTile.Axial;

                if (s.Grid.Distance(casterPos, current) <= 1)
                    break;

                TileData bestTile = null;
                int bestDist = int.MaxValue;

                foreach (var neighbor in s.Grid.GetNeighbors(current))
                {
                    var td = s.Grid.GetTile(neighbor);
                    if (td == null || !td.CanEnter(victim))
                        continue;

                    int distFromCaster = s.Grid.Distance(casterPos, neighbor);
                    if (distFromCaster < bestDist)
                    {
                        bestDist = distFromCaster;
                        bestTile = td;
                    }
                }

                if (bestTile != null)
                {
                    ctx.ForcedTilesRemaining--;
                    victim.PlaceOnTile(bestTile, MovementKind.Forced, ctx);
                    pulled++;
                    if (ctx.HaltForced) // Stone Anchors caught it, or the cap hit
                        break;
                }
                else
                {
                    s.Log($"[PullDamage] {victim.Name} blocked after {pulled} tile(s).");
                    break;
                }
            }

            int totalDmg = pulled * DamagePerTile;
            if (totalDmg > 0)
            {
                victim.ApplyDamage(totalDmg);
                s.Log($"[PullDamage] {victim.Name} pulled {pulled} tile(s), takes {totalDmg} damage ({DamagePerTile}/tile).");
            }
            else
            {
                s.Log($"[PullDamage] {victim.Name} couldn't be pulled.");
            }
        }
    }
}

/// <summary>
/// Replaces the current target set with a freshly computed one from a new
/// <see cref="ITargetSelector"/>, runs the child effect against it, then restores the
/// original targets. Enables chaining patterns like "damage initial target, then retarget
/// nearest enemy and damage again". Stashes the prior targets on
/// <c>GameState.RetargetOrigin</c> so chain-targeters can compute distances from the
/// previous hits.
/// </summary>
public sealed class RetargetEffect : EffectBase
{
    public ITargetSelector Targeter;
    public IEffect Child;

    public RetargetEffect(ITargetSelector targeter, IEffect child)
    {
        Targeter = targeter;
        Child = child;
    }

    public override IEnumerable<IEffect> Children
    {
        get { yield return Child; }
    }

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        var ctx = new PredicateContext
        {
            Game = s,
            Caster = caster,
            Targets = targets,
            Snapshot = snap
        };
        ResolveWithResult(ctx);
    }

    public override EffectResult ResolveWithResult(PredicateContext ctx)
    {
        var originalTargets = ctx.Targets;

        // Store previous targets so chain targeters can use them
        // as the origin point ("nearest to whoever was just hit")
        ctx.Game.RetargetOrigin = originalTargets;

        TargetSet newTargets;
        if (Targeter != null && Targeter.Select(ctx.Game, ctx.Caster, out newTargets))
        {
            ctx.Game?.Log($"[Retarget] Switched to {newTargets.Items.Count} new target(s).");
            ctx.Targets = newTargets;
        }
        else
        {
            ctx.Game?.Log("[Retarget] No valid targets found. Skipping.");
            ctx.Game.RetargetOrigin = null;
            return new EffectResult();
        }

        // Execute child effect with new targets
        EffectResult result;
        if (Child is EffectBase eb)
            result = eb.ResolveWithResult(ctx);
        else
        {
            Child.Resolve(ctx.Game, ctx.Caster, ctx.Targets, ctx.Snapshot);
            result = new EffectResult();
        }

        // Store for siblings before restoring
        ctx.LastRetargetedTargets = ctx.Targets;

        // Restore
        ctx.Targets = originalTargets;
        ctx.Game.RetargetOrigin = null;

        return result;
    }
}

/// <summary>Grants the caster <see cref="MoveTiles"/> extra movement; subscribes a callback that imbues each tile the caster vacates with the chosen element. At end of turn, grants <c>armor_per_tile × tilesImbued</c> armor and unsubscribes.</summary>
public sealed class ImbuePathEffect : EffectBase
{
    public string Element;
    public int MoveTiles;
    public int ArmorPerTile;

    public ImbuePathEffect(string element, int moveTiles, int armorPerTile = 0)
    {
        Element = element;
        MoveTiles = moveTiles;
        ArmorPerTile = armorPerTile;
    }

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        var casterUnit = FindCasterUnit(s, caster);
        if (casterUnit == null || s?.Grid == null)
            return;

        TileElementType elementType = Element.ToLowerInvariant() switch
        {
            "fire" => TileElementType.Fire,
            "ice" => TileElementType.Frost,
            "frost" => TileElementType.Frost,
            "storm" => TileElementType.Lightning,
            "stone" => TileElementType.Earth,
            _ => TileElementType.None
        };

        int tilesImbued = 0;

        // Subscribe: imbue every tile the unit leaves
        Action<TileData> onLeave = null;
        onLeave = (leftTile) =>
        {
            if (leftTile == null)
                return;
            leftTile.ElementType = elementType;
            leftTile.ElementStrength = 1.0f;
            if (Element.ToLowerInvariant() == "fire")
                leftTile.IsHazardous = true;
            leftTile.TileView?.SetElement(elementType);
            tilesImbued++;
            s.Log($"[ImbuePath] {leftTile.Axial} imbued with {Element}.");
        };

        casterUnit.OnTileLeft += onLeave;

        // Grant the movement (movespeed currency — read by EffectiveMovement)
        casterUnit.Stats.BonusMoveRange += MoveTiles;
        s.Log($"[ImbuePath] {casterUnit.Name} gains +{MoveTiles} move range this turn. Tiles left behind will be imbued with {Element}.");

        // Also imbue the starting tile
        if (casterUnit.CurrentTile != null)
        {
            casterUnit.CurrentTile.ElementType = elementType;
            casterUnit.CurrentTile.ElementStrength = 1.0f;
            casterUnit.CurrentTile.TileView?.SetElement(elementType);
            tilesImbued++;
        }

        // The callback stays active until turn ends.
        // We need to clean it up. Store a cleanup action on GameState.
        s.OnTurnEndCleanups ??= new List<Action>();
        s.OnTurnEndCleanups.Add(() =>
        {
            casterUnit.OnTileLeft -= onLeave;

            // Grant armor based on tiles imbued
            if (ArmorPerTile > 0 && tilesImbued > 0)
            {
                int totalArmor = tilesImbued * ArmorPerTile;
                casterUnit.Stats.Armor += totalArmor;
                casterUnit.RefreshHealthBar();
                s.Log($"[ImbuePath] {casterUnit.Name} gains {totalArmor} armor ({tilesImbued} tiles x {ArmorPerTile}).");
            }
        });
    }
}

// ── Element-tile utilities (core; school capstones live in ElementalistEffects.cs) ──

/// <summary>Imbues all tiles within radius around the caster with a single named element. Fire imbuements set <c>IsHazardous</c>.</summary>
public sealed class ImbueAreaEffect : EffectBase
{
    public string Element;
    public int Radius;

    public ImbueAreaEffect(string element, int radius)
    {
        Element = element;
        Radius = radius;
    }

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        if (s?.Grid == null)
            return;
        var casterUnit = FindCasterUnit(s, caster);
        if (casterUnit?.CurrentTile == null)
            return;

        var center = casterUnit.CurrentTile.Axial;

        TileElementType elementType = Element.ToLowerInvariant() switch
        {
            "fire" => TileElementType.Fire,
            "ice" => TileElementType.Frost,
            "storm" => TileElementType.Lightning,
            "stone" => TileElementType.Earth,
            _ => TileElementType.None
        };

        int imbued = 0;
        foreach (var kvp in s.Grid.Tiles)
        {
            if (s.Grid.Distance(center, kvp.Key) > Radius)
                continue;
            var tile = kvp.Value;
            if (tile == null)
                continue;

            tile.ElementType = elementType;
            tile.ElementStrength = 1.0f;
            if (elementType == TileElementType.Fire)
                tile.IsHazardous = true;
            tile.TileView?.SetElement(elementType);
            imbued++;
        }

        s.Log($"[ImbueArea] Imbued {imbued} tiles within {Radius} with {Element}.");
    }
}

/// <summary>Consumes a single target tile of the matching element, then deals <see cref="Damage"/> to every enemy within radius of it. No-op when the target tile is the wrong element or missing.</summary>
public sealed class ConsumeElementTileEffect : EffectBase
{
    public string Element;
    public int Radius;
    public int Damage;

    public ConsumeElementTileEffect(string element, int radius, int damage)
    {
        Element = element;
        Radius = radius;
        Damage = damage;
    }

    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap)
    {
        if (s?.Grid == null || targets == null)
            return;
        var casterUnit = FindCasterUnit(s, caster);

        TileElementType needed = Element.ToLowerInvariant() switch
        {
            "fire" => TileElementType.Fire,
            "ice" => TileElementType.Frost,
            "storm" => TileElementType.Lightning,
            "stone" => TileElementType.Earth,
            _ => TileElementType.None
        };

        // Find the target tile
        TileData targetTile = null;
        foreach (var obj in targets.Items)
        {
            if (obj is TileData td)
            { targetTile = td; break; }
            else if (obj is HexTile tv)
            { targetTile = s.Grid.GetTile(tv.Axial); break; }
            else if (obj is Unit u && u.CurrentTile != null)
            { targetTile = u.CurrentTile; break; }
        }

        if (targetTile == null)
        {
            s.Log($"[ConsumeTile] No target tile found.");
            return;
        }

        if (targetTile.ElementType != needed)
        {
            s.Log($"[ConsumeTile] Target tile is not {Element}. Cannot consume.");
            return;
        }

        // Consume the tile
        var center = targetTile.Axial;
        targetTile.ElementType = TileElementType.None;
        targetTile.ElementStrength = 0f;
        targetTile.IsHazardous = false;
        targetTile.TileView?.SetElement(TileElementType.None);
        s.Log($"[ConsumeTile] Consumed {Element} tile at {center}.");

        // Deal damage to enemies within radius
        foreach (var unit in s.UnitsInPlay)
        {
            if (unit == null || !unit.Stats.IsAlive || unit.CurrentTile == null)
                continue;
            if (casterUnit != null && unit.TeamId == casterUnit.TeamId)
                continue;
            if (s.Grid.Distance(center, unit.CurrentTile.Axial) > Radius)
                continue;

            unit.ApplyDamage(Damage);
            s.Log($"[ConsumeTile] {unit.Name} takes {Damage} damage from {Element} explosion.");
        }
    }
}

// ── Worldshaper Effect ─────────────────────────────────────────────────────────

// ── Open Gate Leaf ──────────────────────────────────────────────────────

// ── Ossuary Aura Leaf ────────────────────────────────────────────────────

// ── Memorial Seat Aura Leaf ──────────────────────────────────────────────

// ── Hallowed Double Rise Leaf ────────────────────────────────────────────

// ── Elder Aura Leaf ──────────────────────────────────────────────────────

/// <summary>Does nothing. Used by the registry's `empty` factory as a placeholder while a card is being sketched, and as the fallback for unknown effect types so unknown JSON never crashes the loader.</summary>
public sealed class EmptyEffect : EffectBase
{
    public override void Resolve(GameState s, Entity caster, TargetSet targets, EffectSnapshot snap) { }
}
