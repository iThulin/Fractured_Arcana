using Godot;
using System.Linq;

// ============================================================
// RecruitmentSources.cs
//
// Purpose:        K5: the non-hall recruitment sources from
//                 companion_item_systems v2.1 §5a: the Unite
//                 adept (a united school seconds one of its own)
//                 and the favor retainer (an Arcane Major favor
//                 called in for a courtier's person). Both draw
//                 from the K3 candidate matrix with forced
//                 class/school: one people-generation surface,
//                 no parallel schema. Displacement refugees live
//                 in HiringHallService.RollStock (they are hall
//                 stock, just discounted).
// Layer:          Data (FeatureBuilders)
// Collaborators:  CandidateGenerator (the matrix),
//                 ExpeditionManager / CampusScreen (Unite sites),
//                 ExpeditionManager.ExecuteCallIn (retainer),
//                 ArchmageRegistry (school lookup).
// Notes:          SCOPE RULING (logged): the spec offers a
//                 retainer for ANY Major favor; here it is the
//                 Arcane Major call-in specifically, the one
//                 favor type with no field effect, so the slot
//                 was empty and no existing effect is overloaded.
// ============================================================

/// <summary>The §5a recruitment sources that hand the guild a person
/// directly (no hall, no gold). Both recruit in place: the new companion
/// lands in the roster immediately, party-addable, hall-invisible (already
/// IsRecruited, so the hall prune ignores them by construction).</summary>
public static class RecruitmentSources
{
    /// <summary>Unite resolution: the united school seconds one adept,
    /// "the people-and-knowledge path where Overthrow is the power-and-shard
    /// path." Rolls an Arcane candidate of the archmage's school at seat
    /// quality and recruits them free. Idempotent per archmage (re-resolution
    /// cannot double-grant). Returns the toast line, or null on no-op.</summary>
    public static string OnArchmageUnited(string archmageId)
    {
        var save = SaveManager.ActiveSave;
        if (save == null || string.IsNullOrEmpty(archmageId)) return null;

        // Q4.2: stamp the unite lunation for the relic anniversary. BEFORE
        // the adept idempotence check, but never overwritten (the first
        // swearing is the anniversary that counts).
        var campaign = save.Cycle?.Campaign;
        if (campaign != null && !campaign.UniteLunations.ContainsKey(archmageId))
        {
            campaign.UniteLunations[archmageId] = save.Cycle.Calendar.CurrentLunation;
            SaveManager.MarkDirty();
        }

        string id = $"hire_unite_{archmageId}";
        if (save.Companions.Any(c => c.Id == id)) return null; // already seconded

        var def = ArchmageRegistry.Get(archmageId);
        var rng = new RandomNumberGenerator();
        rng.Randomize();

        var adept = CandidateGenerator.Generate(rng, quality: 1,
            cityId: $"unite_{archmageId}", lunation: 0, index: 0,
            forceClass: "Arcane", forceSchool: def?.School);
        adept.Id = id;
        adept.IsRecruited = true;
        adept.IsAvailable = true;
        adept.RecruitmentCost = 0; // seconded, not sold
        adept.Backstory = $"Seconded to the guild by {def?.DisplayName ?? "a united seat"}. " +
                          "The alliance's first gift is a person.";

        save.Companions.Add(adept);
        SaveManager.MarkDirty();
        GD.Print($"[Recruit] Unite adept: {adept.Name} ({adept.School}) joins from {archmageId}.");
        return $"{adept.Name}, an adept of the united school, is seconded to the guild.";
    }

    /// <summary>Arcane Major favor call-in: the Court Wizard sends a retainer
    /// of the court's own; school follows the court's archmage where one is
    /// resolvable. Arrives recruited, free (the favor was the price). Returns
    /// the call-in message, or null if the save is missing (caller refuses
    /// without consuming).</summary>
    public static string RedeemRetainer(Favor favor)
    {
        var save = SaveManager.ActiveSave;
        var cycle = save?.Cycle;
        if (cycle == null || favor == null) return null;

        // The court's school, when its kingdom has a resolvable archmage.
        string school = null;
        if (cycle.Kingdoms.TryGetValue(favor.KingdomId, out var ks) &&
            !string.IsNullOrEmpty(ks.TemplateRegionId))
        {
            string amId = cycle.Campaign?.GetArchmageForRegion(ks.TemplateRegionId);
            school = string.IsNullOrEmpty(amId) ? null : ArchmageRegistry.Get(amId)?.School;
        }

        var rng = new RandomNumberGenerator();
        rng.Randomize();

        var retainer = CandidateGenerator.Generate(rng, quality: 1,
            cityId: $"retainer_{favor.KingdomId}", lunation: cycle.Calendar.CurrentLunation,
            index: (int)(rng.Randi() % 1000),
            forceClass: "Arcane", forceSchool: school);
        retainer.IsRecruited = true;
        retainer.IsAvailable = true;
        retainer.RecruitmentCost = 0;
        retainer.Backstory = "A retainer of the court, sent to settle a debt of honor. " +
                             "Their patron will be watching how they are treated.";

        save.Companions.Add(retainer);
        SaveManager.MarkDirty();
        GD.Print($"[Recruit] Favor retainer: {retainer.Name} ({retainer.School}) " +
                 $"from {favor.KingdomId}.");
        return $"The Court Wizard's debt is paid in kind: {retainer.Name} enters the guild's service.";
    }
}
