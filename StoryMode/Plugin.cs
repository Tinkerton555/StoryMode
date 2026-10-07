using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using StoryMode.Gear;
using StoryMode.Windows;

namespace StoryMode;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider GameInterop { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private const string CommandName = "/storymode";

    public Configuration Configuration { get; }
    public GearTable GearTable { get; }
    internal OutfitApplier Applier { get; }

    private readonly WindowSystem windowSystem = new("StoryMode");
    private readonly ConfigWindow configWindow;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        GearTable = GearTable.Load(DataManager);
        Applier = new OutfitApplier(Configuration, new OutfitResolver(GearTable, Configuration));

        configWindow = new ConfigWindow(this);
        windowSystem.AddWindow(configWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Turn Story Mode on or off. Use \"/storymode config\" to open settings.",
        });

        PluginInterface.UiBuilder.Draw += windowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += configWindow.Toggle;
        PluginInterface.UiBuilder.OpenMainUi += configWindow.Toggle;
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= configWindow.Toggle;
        PluginInterface.UiBuilder.OpenMainUi -= configWindow.Toggle;
        windowSystem.RemoveAllWindows();
        CommandManager.RemoveHandler(CommandName);

        // Puts everyone back in their real gear before the hooks go away.
        Applier.Dispose();
    }

    private void OnCommand(string command, string args)
    {
        if (args.Trim().Equals("config", System.StringComparison.OrdinalIgnoreCase))
        {
            configWindow.Toggle();
            return;
        }

        Configuration.Enabled = !Configuration.Enabled;
        Configuration.Save();
        ChatGui.Print($"Story Mode {(Configuration.Enabled ? "on" : "off")}.");
    }
}
