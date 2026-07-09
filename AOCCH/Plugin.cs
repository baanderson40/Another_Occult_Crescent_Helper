using AOCCH.Data;
using AOCCH.Automation;
using AOCCH.IPC;
using AOCCH.Logging;
using AOCCH.Movement;
using AOCCH.Scanning;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using AOCCH.Windows;

namespace AOCCH;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IFateTable FateTable { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;

    private const string CommandName = "/aocch";

    public Configuration Configuration { get; init; }
    public AocchLogger Logger { get; init; }
    public OccultCrescentData OccultCrescentData { get; init; }
    public OccultCrescentScanner Scanner { get; init; }
    public VNavmeshIpc VNavmesh { get; init; }
    public LifestreamIpc Lifestream { get; init; }
    public BossModIpc BossMod { get; init; }
    public RoutePlanner RoutePlanner { get; init; }
    public MovementController MovementController { get; init; }
    public AutorotationController AutorotationController { get; init; }
    public BuffRotationController BuffRotationController { get; init; }
    public CriticalEngagementAutomationController CriticalEngagementAutomationController { get; init; }
    public FateAutomationController FateAutomationController { get; init; }
    public DeathRecoveryController DeathRecoveryController { get; init; }

    public readonly WindowSystem WindowSystem = new("AOCCH");
    private ConfigWindow ConfigWindow { get; init; }
    private LogWindow LogWindow { get; init; }
    private MainWindow MainWindow { get; init; }

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Logger = new AocchLogger(Log);
        OccultCrescentData = OccultCrescentDataLoader.Load(PluginInterface, Logger);
        Scanner = new OccultCrescentScanner(ClientState, FateTable, Framework, ObjectTable, OccultCrescentData, Configuration, Logger);
        VNavmesh = new VNavmeshIpc(Logger);
        Lifestream = new LifestreamIpc(Logger);
        BossMod = new BossModIpc(Logger);
        RoutePlanner = new RoutePlanner(OccultCrescentData, Logger);
        MovementController = new MovementController(Framework, ObjectTable, Scanner, VNavmesh, Lifestream, RoutePlanner, OccultCrescentData, Logger);
        AutorotationController = new AutorotationController(BossMod, Configuration, Logger);
        BuffRotationController = new BuffRotationController(Framework, Condition, ObjectTable, Scanner, MovementController, Configuration, Logger);
        CriticalEngagementAutomationController = new CriticalEngagementAutomationController(Framework, Condition, ObjectTable, Scanner, MovementController, AutorotationController, Configuration, Logger);
        FateAutomationController = new FateAutomationController(Framework, Condition, ObjectTable, Scanner, MovementController, AutorotationController, Configuration, Logger);
        DeathRecoveryController = new DeathRecoveryController(Framework, ObjectTable, GameGui, MovementController, AutorotationController, BuffRotationController, CriticalEngagementAutomationController, FateAutomationController, Logger);

        ConfigWindow = new ConfigWindow(Configuration);
        LogWindow = new LogWindow(this);
        MainWindow = new MainWindow(Configuration, Scanner, MovementController, AutorotationController, BuffRotationController, CriticalEngagementAutomationController, FateAutomationController, DeathRecoveryController);

        WindowSystem.AddWindow(ConfigWindow);
        WindowSystem.AddWindow(LogWindow);
        WindowSystem.AddWindow(MainWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open AOCCH. Args: main, config, log, help."
        });

        // Tell the UI system that we want our windows to be drawn through the window system
        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;

        // This adds a button to the plugin installer entry of this plugin which allows
        // toggling the display status of the configuration ui
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;

        // Adds another button doing the same but for the main ui of the plugin
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;

        Logger.Info($"{PluginInterface.Manifest.Name} loaded.");
    }

    public void Dispose()
    {
        // Unregister all actions to not leak anything during disposal of plugin
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
        Logger.Info("AOCCH cleanup starting.");
        
        WindowSystem.RemoveAllWindows();

        ConfigWindow.Dispose();
        LogWindow.Dispose();
        MainWindow.Dispose();
        DeathRecoveryController.Dispose();
        FateAutomationController.Dispose();
        CriticalEngagementAutomationController.Dispose();
        BuffRotationController.Dispose();
        AutorotationController.Dispose();
        MovementController.Dispose();
        Scanner.Dispose();

        CommandManager.RemoveHandler(CommandName);
        Logger.Info("AOCCH cleanup finished.");
    }

    private void OnCommand(string command, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "":
            case "main":
                MainWindow.Toggle();
                break;
            case "config":
                ConfigWindow.Toggle();
                break;
            case "log":
                LogWindow.Toggle();
                break;
            case "help":
                PrintCommandHelp();
                break;
            default:
                Logger.Warning($"Unknown command argument: {args}");
                PrintCommandHelp();
                break;
        }
    }

    private static void PrintCommandHelp()
    {
        ChatGui.Print("AOCCH commands:");
        ChatGui.Print("/aocch - Toggle main window");
        ChatGui.Print("/aocch main - Toggle main window");
        ChatGui.Print("/aocch config - Toggle config window");
        ChatGui.Print("/aocch log - Toggle log window");
        ChatGui.Print("/aocch help - Show this help");
    }
    
    public void ToggleConfigUi() => ConfigWindow.Toggle();
    public void ToggleLogUi() => LogWindow.Toggle();
    public void ToggleMainUi() => MainWindow.Toggle();
}
