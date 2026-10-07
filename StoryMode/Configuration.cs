using System;
using Dalamud.Configuration;

namespace StoryMode;

public enum TargetMode
{
    AllOtherPlayers,
    ExcludeParty,
    ExcludePartyAndAlliance,
}

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    public bool Enabled { get; set; } = true;

    // Who is affected
    public TargetMode TargetMode { get; set; } = TargetMode.ExcludeParty;
    public bool ExcludeFriends { get; set; } = false;
    public bool ExcludeFreeCompany { get; set; } = false;
    public bool ApplyToSelf { get; set; } = false;

    // When it is active. If both are off, it is active everywhere.
    public bool OnlyInCutscenes { get; set; } = false;
    public bool OnlyInDuties { get; set; } = false;

    // What is replaced
    public bool ReplaceWeapons { get; set; } = true;
    public bool RemoveAccessories { get; set; } = true;
    public bool HideFacewear { get; set; } = true;

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
