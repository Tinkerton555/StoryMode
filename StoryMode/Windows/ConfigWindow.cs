using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace StoryMode.Windows;

public sealed class ConfigWindow : Window
{
    private readonly Configuration config;

    public ConfigWindow(Plugin plugin) : base("Story Mode###StoryModeConfig")
    {
        config = plugin.Configuration;
        Flags = ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(360, 0) };
    }

    public override void Draw()
    {
        Checkbox("Enable Story Mode", config.Enabled, v => config.Enabled = v);
        ImGui.TextDisabled("Other players see no change. This only affects your screen.");

        ImGui.Spacing();
        Section("Who to change");

        Radio("All other players", TargetMode.AllOtherPlayers);
        Radio("All except my party", TargetMode.ExcludeParty);
        Radio("All except my party and alliance", TargetMode.ExcludePartyAndAlliance);
        ImGui.Spacing();
        Checkbox("Don't change friends", config.ExcludeFriends, v => config.ExcludeFriends = v);
        Checkbox("Don't change my free company", config.ExcludeFreeCompany, v => config.ExcludeFreeCompany = v);
        Checkbox("Change me too", config.ApplyToSelf, v => config.ApplyToSelf = v);

        ImGui.Spacing();
        Section("When");

        Checkbox("Only during cutscenes", config.OnlyInCutscenes, v => config.OnlyInCutscenes = v);
        Checkbox("Only in duties", config.OnlyInDuties, v => config.OnlyInDuties = v);
        ImGui.TextDisabled(config.OnlyInCutscenes || config.OnlyInDuties
            ? "Active when any ticked condition applies."
            : "Active everywhere.");

        ImGui.Spacing();
        Section("What to replace");

        Checkbox("Weapons", config.ReplaceWeapons, v => config.ReplaceWeapons = v);
        Checkbox("Remove accessories", config.RemoveAccessories, v => config.RemoveAccessories = v);
        Checkbox("Hide facewear", config.HideFacewear, v => config.HideFacewear = v);

        ImGui.Spacing();
        ImGui.TextDisabled("Job stone equipped: artifact gear for their level.");
        ImGui.TextDisabled("No job stone: a starter outfit for their class.");
    }

    private static void Section(string title)
    {
        ImGui.TextUnformatted(title);
        ImGui.Separator();
    }

    private void Checkbox(string label, bool value, Action<bool> set)
    {
        if (ImGui.Checkbox(label, ref value))
        {
            set(value);
            config.Save();
        }
    }

    private void Radio(string label, TargetMode mode)
    {
        if (ImGui.RadioButton(label, config.TargetMode == mode))
        {
            config.TargetMode = mode;
            config.Save();
        }
    }
}
