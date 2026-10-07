using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.Game.Character;

namespace StoryMode.Gear;

/// <summary>Decides which outfit (if any) a character should be shown in.</summary>
public sealed unsafe class OutfitResolver(GearTable table, Configuration config)
{
    // ClassJob IDs of the base classes that become jobs with a job stone.
    // Gladiator..Thaumaturge (1-7), Arcanist (26), Rogue (29).
    private static bool IsBaseClass(uint classJob) => classJob is >= 1 and <= 7 or 26 or 29;

    // Carpenter..Fisher (8-18). These are jobs from level 1 but only get job-specific gear at 50.
    private static bool IsCrafterOrGatherer(uint classJob) => classJob is >= 8 and <= 18;

    private const int CrafterGathererGearLevel = 50;

    public Outfit? Resolve(Character* character)
    {
        var classJob = (uint)character->CharacterData.ClassJob;
        var level = (int)character->CharacterData.Level;

        if (IsBaseClass(classJob) || (IsCrafterOrGatherer(classJob) && level < CrafterGathererGearLevel))
            return table.ClassOutfits.GetValueOrDefault(classJob);

        if (!table.JobTiers.TryGetValue(classJob, out var tiers) || tiers.Count == 0)
            return null;

        // The highest set at or below their level. If they're below every set (for example a
        // level 35 Dark Knight, whose first set is level 60), use their earliest set.
        var chosen = tiers[0];
        foreach (var tier in tiers)
        {
            if (tier.MinLevel <= level)
                chosen = tier;
        }

        return chosen.Outfit;
    }

    /// <summary>Whether this character should be changed under the current settings.</summary>
    public bool ShouldAffect(Character* character, Character* localPlayer)
    {
        if (character == localPlayer)
            return config.ApplyToSelf;

        if (config.TargetMode != TargetMode.AllOtherPlayers && character->IsPartyMember)
            return false;
        if (config.TargetMode == TargetMode.ExcludePartyAndAlliance && character->IsAllianceMember)
            return false;
        if (config.ExcludeFriends && character->IsFriend)
            return false;
        if (config.ExcludeFreeCompany && localPlayer != null && SameFreeCompany(character, localPlayer))
            return false;

        return true;
    }

    private static bool SameFreeCompany(Character* a, Character* b)
    {
        var tagA = a->FreeCompanyTagString;
        return !string.IsNullOrEmpty(tagA) && tagA == b->FreeCompanyTagString;
    }
}
