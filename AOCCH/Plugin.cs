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
    private const uint SouthHornTerritoryTypeId = 1252;
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
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;

    private const string CommandName = "/aocch";

    public Configuration Configuration { get; init; }
    public AocchLogger Logger { get; init; }
    public OccultCrescentData OccultCrescentData { get; init; }
    public OccultCrescentNameResolver OccultCrescentNameResolver { get; init; }
    public OccultCrescentScanner Scanner { get; init; }
    public VNavmeshIpc VNavmesh { get; init; }
    public LifestreamIpc Lifestream { get; init; }
    public BossModIpc BossMod { get; init; }
    public RoutePlanner RoutePlanner { get; init; }
    public GameActionController GameActionController { get; init; }
    public MovementController MovementController { get; init; }
    public AutorotationController AutorotationController { get; init; }
    public BuffRotationController BuffRotationController { get; init; }
    public CriticalEngagementAutomationController CriticalEngagementAutomationController { get; init; }
    public FateAutomationController FateAutomationController { get; init; }
    public DeathRecoveryController DeathRecoveryController { get; init; }
    public FarmSessionController FarmSessionController { get; init; }

    public readonly WindowSystem WindowSystem = new("AOCCH");
    private ConfigWindow ConfigWindow { get; init; }
    private LogWindow LogWindow { get; init; }
    private MainWindow MainWindow { get; init; }
    private DebugWindow DebugWindow { get; init; }
    private bool isDisposing;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Logger = new AocchLogger(Log);
        OccultCrescentData = OccultCrescentDataLoader.Load(PluginInterface, Logger);
        if (Configuration.Migrate(OccultCrescentData))
        {
            Configuration.Save();
            Logger.Info("Migrated configuration to CE/FATE checkbox settings.");
        }

        OccultCrescentNameResolver = new OccultCrescentNameResolver(DataManager, OccultCrescentData);
        Scanner = new OccultCrescentScanner(ClientState, FateTable, Framework, ObjectTable, OccultCrescentData, Configuration, Logger);
        VNavmesh = new VNavmeshIpc(Logger);
        Lifestream = new LifestreamIpc(Logger);
        BossMod = new BossModIpc(Logger);
        RoutePlanner = new RoutePlanner(OccultCrescentData, Configuration, Logger);
        GameActionController = new GameActionController(Logger);
        MovementController = new MovementController(Framework, Condition, ObjectTable, GameGui, Scanner, VNavmesh, Lifestream, RoutePlanner, GameActionController, Configuration, OccultCrescentData, Logger);
        AutorotationController = new AutorotationController(BossMod, Configuration, Logger);
        BuffRotationController = new BuffRotationController(Framework, Condition, ObjectTable, Scanner, MovementController, Configuration, Logger);
        CriticalEngagementAutomationController = new CriticalEngagementAutomationController(Framework, Condition, ObjectTable, Scanner, MovementController, AutorotationController, Configuration, Logger);
        FateAutomationController = new FateAutomationController(Framework, Condition, ObjectTable, Scanner, MovementController, AutorotationController, Configuration, Logger);
        DeathRecoveryController = new DeathRecoveryController(Framework, ObjectTable, GameGui, MovementController, AutorotationController, BuffRotationController, CriticalEngagementAutomationController, FateAutomationController, Logger);
        FarmSessionController = new FarmSessionController(Framework, Scanner, VNavmesh, Lifestream, MovementController, AutorotationController, BuffRotationController, CriticalEngagementAutomationController, FateAutomationController, DeathRecoveryController, Configuration, Logger);

        ConfigWindow = new ConfigWindow(Configuration, OccultCrescentData, OccultCrescentNameResolver, Logger);
        LogWindow = new LogWindow(this);
        MainWindow = new MainWindow(this, Configuration, Scanner, MovementController, AutorotationController, BuffRotationController, CriticalEngagementAutomationController, FateAutomationController, FarmSessionController);
        DebugWindow = new DebugWindow(this, Configuration, Scanner, MovementController, AutorotationController, BuffRotationController, CriticalEngagementAutomationController, FateAutomationController, DeathRecoveryController, FarmSessionController);

        WindowSystem.AddWindow(ConfigWindow);
        WindowSystem.AddWindow(LogWindow);
        WindowSystem.AddWindow(MainWindow);
        WindowSystem.AddWindow(DebugWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open AOCCH. Args: main, debug, config, log, start, stop, panic, help."
        });

        // Tell the UI system that we want our windows to be drawn through the window system
        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;

        // This adds a button to the plugin installer entry of this plugin which allows
        // toggling the display status of the configuration ui
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;

        // Adds another button doing the same but for the main ui of the plugin
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
        ClientState.TerritoryChanged += OnTerritoryChanged;

        Logger.Info($"{PluginInterface.Manifest.Name} loaded.");
    }

    public void Dispose()
    {
        if (isDisposing)
        {
            return;
        }

        isDisposing = true;

        // Unregister all actions to not leak anything during disposal of plugin
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
        ClientState.TerritoryChanged -= OnTerritoryChanged;
        Logger.Info("AOCCH cleanup starting.");

        ConfigWindow.IsOpen = false;
        LogWindow.IsOpen = false;
        MainWindow.IsOpen = false;
        DebugWindow.IsOpen = false;

        WindowSystem.RemoveAllWindows();

        ConfigWindow.Dispose();
        LogWindow.Dispose();
        MainWindow.Dispose();
        DebugWindow.Dispose();
        FarmSessionController.Dispose();
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
        if (isDisposing)
        {
            return;
        }

        var normalizedArgs = args.Trim().ToLowerInvariant();
        Logger.Info($"Slash command received: {command} {args}".TrimEnd());

        switch (normalizedArgs)
        {
            case "":
            case "main":
                Logger.Info("Slash command action: toggle main window.");
                MainWindow.Toggle();
                break;
            case "config":
                Logger.Info("Slash command action: toggle config window.");
                ConfigWindow.Toggle();
                break;
            case "debug":
                Logger.Info("Slash command action: toggle debug window.");
                DebugWindow.Toggle();
                break;
            case "log":
                Logger.Info("Slash command action: toggle log window.");
                LogWindow.Toggle();
                break;
            case "help":
                Logger.Info("Slash command action: show help.");
                PrintCommandHelp();
                break;
            case "start":
                Logger.Info("Slash command action: start farm session.");
                FarmSessionController.Start();
                break;
            case "stop":
                Logger.Info("Slash command action: stop farm session.");
                FarmSessionController.Stop("Slash command stop requested.");
                break;
            case "panic":
                Logger.Warning("Slash command action: panic stop.");
                PanicStopAll();
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
        ChatGui.Print("/aocch debug - Toggle debug window");
        ChatGui.Print("/aocch config - Toggle config window");
        ChatGui.Print("/aocch log - Toggle log window");
        ChatGui.Print("/aocch start - Start unified CE/FATE farm session");
        ChatGui.Print("/aocch stop - Stop unified CE/FATE farm session");
        ChatGui.Print("/aocch panic - Panic stop all farm activity");
        ChatGui.Print("/aocch help - Show this help");
    }
    
    public void ToggleConfigUi()
    {
        if (isDisposing)
        {
            return;
        }

        Logger.Info("UI action: toggle config window.");
        ConfigWindow.Toggle();
    }

    public void ToggleLogUi()
    {
        if (isDisposing)
        {
            return;
        }

        Logger.Info("UI action: toggle log window.");
        LogWindow.Toggle();
    }

    public void ToggleMainUi()
    {
        if (isDisposing)
        {
            return;
        }

        Logger.Info("UI action: toggle main window.");
        MainWindow.Toggle();
    }

    private void OnTerritoryChanged(uint territoryType)
    {
        if (isDisposing)
        {
            return;
        }

        if (territoryType == SouthHornTerritoryTypeId)
        {
            if (!MainWindow.IsOpen)
            {
                MainWindow.IsOpen = true;
                Logger.Info("Main window auto-opened on South Horn entry.");
            }

            return;
        }

        if (MainWindow.IsOpen)
        {
            MainWindow.IsOpen = false;
            Logger.Info($"Main window auto-closed on territory change to {territoryType}.");
        }
    }

    public void PanicStopAll()
    {
        const string reason = "Global panic stop requested.";
        Logger.Warning(reason);

        if (FarmSessionController.IsRunning)
        {
            Logger.Info("Panic stop: stopping farm session.");
            FarmSessionController.PanicStop();
        }
        else
        {
            Logger.Info("Panic stop: farm session not running.");
        }

        if (CriticalEngagementAutomationController.IsRunning)
        {
            Logger.Info("Panic stop: stopping CE automation.");
            CriticalEngagementAutomationController.Stop(reason);
        }
        else
        {
            Logger.Info("Panic stop: CE automation not running.");
        }

        if (FateAutomationController.IsRunning)
        {
            Logger.Info("Panic stop: stopping FATE automation.");
            FateAutomationController.Stop(reason);
        }
        else
        {
            Logger.Info("Panic stop: FATE automation not running.");
        }

        if (BuffRotationController.IsRunning)
        {
            Logger.Info("Panic stop: stopping buff rotation.");
            BuffRotationController.Stop(reason);
        }
        else
        {
            Logger.Info("Panic stop: buff rotation not running.");
        }

        Logger.Info("Panic stop: stopping movement.");
        MovementController.Stop(reason);
        Logger.Info("Panic stop: releasing autorotation ownership.");
        AutorotationController.ReleaseOwnership(reason);
        Logger.Info("Global panic stop completed.");
    }
}
