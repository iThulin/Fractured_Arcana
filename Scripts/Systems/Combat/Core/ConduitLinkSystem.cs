using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

// ============================================================
// ConduitLinkSystem.cs
//
// Purpose:        The Tinker Conduit Link layer. A static
//                 registry of active links between units, plus
//                 the three behaviors links produce:
//                   • damage redistribution (Split / Mirror),
//                     queried by Unit.ApplyDamage,
//                   • line-crossing damage when an enemy enters
//                     the hex line between two linked units,
//                     queried by Unit.PlaceOnTile,
//                   • convergence (Singularity).
//                 Links are cleaned up on unit death and cleared
//                 at the start of each combat.
// Layer:          System
// Collaborators:  Unit.cs (ApplyDamage redistribution preamble,
//                 PlaceOnTile entry hook), Unit.ConduitLink.cs
//                 (ApplyDamageSkippingLinks guard),
//                 TinkerLinkEffects.cs (create / arc / singularity),
//                 CombatManager.cs (Clear in _Ready, OnUnitDied
//                 in HandleUnitDeath)
//
// WIRING REQUIRED (see accompanying notes):
//   • Unit.ApplyDamage  → redistribution preamble (full method given)
//   • Unit.PlaceOnTile  → `ConduitLinkSystem.OnUnitEntered(this);` last line
//   • CombatManager._Ready        → `ConduitLinkSystem.Clear();`
//   • CombatManager.HandleUnitDeath → `ConduitLinkSystem.OnUnitDied(unit);`
// ============================================================

/// <summary>How a link redistributes damage. Split = damage halves between the two (defensive sponge). Mirror = the struck unit takes full damage and the partner takes a backlash share (offensive spread).</summary>
public enum LinkMode
{
    Split,
    Mirror
}

/// <summary>One active link between two units. Symmetric. Carries the redistribution mode, the owning team (whose enemies the line zaps), and the per-cross line damage (0 = no zone).</summary>
public sealed class ConduitLink
{
    public Unit A, B;
    public LinkMode Mode;
    public int OwnerTeam;
    public int LineDamage;

    public bool IsAlive =>
        A != null && B != null &&
        A.Stats.IsAlive && B.Stats.IsAlive &&
        !A.IsDeathQueued && !B.IsDeathQueued;

    public bool Contains(Unit u) => u == A || u == B;
    public Unit Other(Unit u) => u == A ? B : (u == B ? A : null);
}

public static class ConduitLinkSystem
{
    private static readonly List<ConduitLink> ActiveLinks = new();

    // ── Lifecycle ───────────────────────────────────────────────────

    /// <summary>Clears all links. Call once at the start of each combat.</summary>
    public static void Clear() => ActiveLinks.Clear();

    /// <summary>Removes every link involving the dead unit. Call from HandleUnitDeath.</summary>
    public static void OnUnitDied(Unit u)
    {
        if (u == null) return;
        ActiveLinks.RemoveAll(l => l.Contains(u));
    }

    /// <summary>Creates (or refreshes) a link between two distinct units.</summary>
    public static void CreatePair(Unit a, Unit b, LinkMode mode, int ownerTeam, int lineDamage)
    {
        if (a == null || b == null || a == b) return;

        foreach (var l in ActiveLinks)
        {
            if (l.Contains(a) && l.Contains(b))
            {
                l.Mode = mode;
                l.OwnerTeam = ownerTeam;
                l.LineDamage = Math.Max(l.LineDamage, lineDamage);
                return;
            }
        }

        ActiveLinks.Add(new ConduitLink
        {
            A = a, B = b, Mode = mode, OwnerTeam = ownerTeam, LineDamage = lineDamage
        });
    }

    // ── Queries ─────────────────────────────────────────────────────

    public static List<Unit> PartnersOf(Unit u) =>
        ActiveLinks.Where(l => l.Contains(u) && l.IsAlive)
                   .Select(l => l.Other(u))
                   .Where(x => x != null)
                   .ToList();

    public static int CountLinksForTeam(int team) =>
        ActiveLinks.Count(l => l.OwnerTeam == team && l.IsAlive);

    public static void ClearTeam(int team) =>
        ActiveLinks.RemoveAll(l => l.OwnerTeam == team);

    // ── Damage redistribution (called by Unit.ApplyDamage) ──────────

    /// <summary>
    /// Given incoming damage to <paramref name="self"/>, routes link shares to partners
    /// (via the guarded ApplyDamageSkippingLinks entry, so this never recurses) and
    /// returns the damage <paramref name="self"/> should actually take. One hop only.
    /// </summary>
    public static int RedistributeFor(Unit self, int amount)
    {
        if (self == null || amount <= 0)
            return amount;

        var links = ActiveLinks.Where(l => l.Contains(self) && l.IsAlive).ToList();
        if (links.Count == 0)
            return amount;

        int half = amount / 2;
        int selfShare = amount;
        bool splitApplied = false;

        foreach (var link in links)
        {
            var p = link.Other(self);
            if (p == null || !p.Stats.IsAlive || p.IsDeathQueued)
                continue;

            if (link.Mode == LinkMode.Split)
            {
                // Self pays the larger half once; each split partner soaks the floor half.
                if (!splitApplied) { selfShare = amount - half; splitApplied = true; }
                if (half > 0) p.ApplyDamageSkippingLinks(half);
            }
            else // Mirror: self takes full, partner takes backlash
            {
                p.ApplyDamageSkippingLinks(Math.Max(1, half));
            }
        }

        return selfShare;
    }

    // ── Line-crossing damage (called by Unit.PlaceOnTile) ───────────

    /// <summary>
    /// When a unit enters a tile, checks whether it stepped onto the interior of any
    /// link line it's an enemy of, and zaps it for that link's LineDamage.
    /// </summary>
    public static void OnUnitEntered(Unit mover)
    {
        if (mover == null || !mover.Stats.IsAlive || mover.CurrentTile == null)
            return;
        if (ActiveLinks.Count == 0)
            return;

        var pos = mover.CurrentTile.Axial;

        foreach (var link in ActiveLinks.ToList())
        {
            if (link.LineDamage <= 0 || !link.IsAlive)
                continue;
            if (mover.TeamId == link.OwnerTeam)
                continue;                       // the owner's side walks freely
            if (mover == link.A || mover == link.B)
                continue;                       // endpoints are the linked units themselves

            var a = link.A.CurrentTile?.Axial;
            var b = link.B.CurrentTile?.Axial;
            if (a == null || b == null)
                continue;

            if (IsOnInteriorLine(a.Value, b.Value, pos))
            {
                mover.ApplyDamageSkippingLinks(link.LineDamage);
                return;                         // one zap per move
            }
        }
    }

    // ── Hex geometry (cube coordinates; grid-independent) ───────────

    private static bool IsOnInteriorLine(Vector2I a, Vector2I b, Vector2I p)
    {
        var line = HexLine(a, b);
        for (int i = 1; i < line.Count - 1; i++)   // exclude both endpoints
            if (line[i] == p)
                return true;
        return false;
    }

    private static List<Vector2I> HexLine(Vector2I a, Vector2I b)
    {
        var result = new List<Vector2I>();
        int n = HexDistance(a, b);
        var (ax, ay, az) = Cube(a);
        var (bx, by, bz) = Cube(b);

        if (n == 0) { result.Add(a); return result; }

        for (int i = 0; i <= n; i++)
        {
            double t = (double)i / n;
            result.Add(RoundCube(ax + (bx - ax) * t,
                                 ay + (by - ay) * t,
                                 az + (bz - az) * t));
        }
        return result;
    }

    private static (double x, double y, double z) Cube(Vector2I a) => (a.X, -a.X - a.Y, a.Y);

    private static int HexDistance(Vector2I a, Vector2I b)
    {
        var (ax, ay, az) = Cube(a);
        var (bx, by, bz) = Cube(b);
        return (int)((Math.Abs(ax - bx) + Math.Abs(ay - by) + Math.Abs(az - bz)) / 2);
    }

    private static Vector2I RoundCube(double x, double y, double z)
    {
        int rx = (int)Math.Round(x), ry = (int)Math.Round(y), rz = (int)Math.Round(z);
        double dx = Math.Abs(rx - x), dy = Math.Abs(ry - y), dz = Math.Abs(rz - z);
        if (dx > dy && dx > dz) rx = -ry - rz;
        else if (dy > dz) ry = -rx - rz;
        else rz = -rx - ry;
        return new Vector2I(rx, rz);   // axial: q = x, r = z
    }
}
