using System;
using System.Threading;

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
using Dalamud.Game.ClientState.Conditions;

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
    [PluginService] internal static ITargetManager TargetManager { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;

    private const string CommandName = "/aocch";

    public Configuration Configuration { get; init; }
    public AocchLogger Logger { get; init; }
    public OccultCrescentData OccultCrescentData { get; init; }
    public CofferPositionOverrideStore CofferPositionOverrideStore { get; init; }
    public VisibleCofferPositionOverrideStore VisibleCofferPositionOverrideStore { get; init; }
    public OccultCrescentNameResolver OccultCrescentNameResolver { get; init; }
    public CofferNameResolver CofferNameResolver { get; init; }
    public OccultCrescentScanner Scanner { get; init; }
    public VNavmeshIpc VNavmesh { get; init; }
    public LifestreamIpc Lifestream { get; init; }
    public BossModIpc BossMod { get; init; }
    public RoutePlanner RoutePlanner { get; init; }
    public GameActionController GameActionController { get; init; }
    public MovementController MovementController { get; init; }
    public DangerousTreasureTravelController DangerousTreasureTravelController { get; init; }
    public AutorotationController AutorotationController { get; init; }
    public BuffRotationController BuffRotationController { get; init; }
    public CriticalEngagementAutomationController CriticalEngagementAutomationController { get; init; }
    public FateAutomationController FateAutomationController { get; init; }
    public DeathRecoveryController DeathRecoveryController { get; init; }
    public InstancedContentController InstancedContentController { get; init; }
    public PotCycleTracker PotCycleTracker { get; init; }
    public TreasureHintTracker TreasureHintTracker { get; init; }
    public TreasureSearchController TreasureSearchController { get; init; }
    public CofferInteractionController CofferInteractionController { get; init; }
    public TreasureCofferFarmController TreasureCofferFarmController { get; init; }
    public PotFallbackWindowEvaluator PotFallbackWindowEvaluator { get; init; }
    public PotInstanceTimeEvaluator PotInstanceTimeEvaluator { get; init; }
    public PotFarmController PotFarmController { get; init; }
    public FarmSessionController FarmSessionController { get; init; }

    public readonly WindowSystem WindowSystem = new("AOCCH");
    private ConfigWindow ConfigWindow { get; init; }
    private LogWindow LogWindow { get; init; }
    private MainWindow MainWindow { get; init; }
    private DebugWindow DebugWindow { get; init; }
    private bool isDisposing;
    private bool wasInSouthHorn;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Logger = new AocchLogger(Log);
        Configuration.SetLogger(Logger);
        OccultCrescentData = OccultCrescentDataLoader.Load(PluginInterface, Logger);
        CofferPositionOverrideStore = new CofferPositionOverrideStore(PluginInterface, Logger);
        VisibleCofferPositionOverrideStore = new VisibleCofferPositionOverrideStore(PluginInterface, Logger);
        if (Configuration.Migrate(OccultCrescentData))
        {
            Configuration.Save();
            Logger.Info("Migrated configuration settings.");
        }

        OccultCrescentNameResolver = new OccultCrescentNameResolver(DataManager, OccultCrescentData, Logger);
        CofferNameResolver = new CofferNameResolver(DataManager, [2014741u, 2014742u, 2014743u], Logger);
        Scanner = new OccultCrescentScanner(ClientState, FateTable, Framework, ObjectTable, OccultCrescentData, Configuration, CofferNameResolver, Logger);
        VNavmesh = new VNavmeshIpc(Logger);
        Lifestream = new LifestreamIpc(Logger);
        BossMod = new BossModIpc(Logger);
        RoutePlanner = new RoutePlanner(OccultCrescentData, Configuration, Logger);
        GameActionController = new GameActionController(CommandManager, Condition, PlayerState, TargetManager, Logger);
        MovementController = new MovementController(Framework, Condition, ObjectTable, GameGui, Scanner, VNavmesh, Lifestream, RoutePlanner, GameActionController, Configuration, OccultCrescentData, Logger);
        DangerousTreasureTravelController = new DangerousTreasureTravelController(Framework, Condition, ObjectTable, MovementController, GameActionController, Configuration, Logger);
        AutorotationController = new AutorotationController(BossMod, Configuration, Logger);
        BuffRotationController = new BuffRotationController(Framework, Condition, ObjectTable, Scanner, MovementController, Configuration, Logger);
        CriticalEngagementAutomationController = new CriticalEngagementAutomationController(Framework, Condition, ObjectTable, Scanner, MovementController, AutorotationController, Configuration, Logger);
        FateAutomationController = new FateAutomationController(Framework, Condition, ObjectTable, Scanner, MovementController, AutorotationController, Configuration, Logger);
        DeathRecoveryController = new DeathRecoveryController(Framework, ObjectTable, GameGui, MovementController, AutorotationController, BuffRotationController, CriticalEngagementAutomationController, FateAutomationController, Logger);
        InstancedContentController = new InstancedContentController(Logger);
        PotCycleTracker = new PotCycleTracker(Framework, Scanner, OccultCrescentData, Logger);
        TreasureHintTracker = new TreasureHintTracker(Framework, ChatGui, Scanner, Logger);
        TreasureSearchController = new TreasureSearchController(Framework, Scanner, MovementController, GameActionController, TreasureHintTracker, DangerousTreasureTravelController, OccultCrescentData, CofferPositionOverrideStore, Configuration, Logger);
        CofferInteractionController = new CofferInteractionController(Framework, ObjectTable, Scanner, MovementController, GameActionController, CofferPositionOverrideStore, Logger);
        TreasureCofferFarmController = new TreasureCofferFarmController(Framework, ObjectTable, Scanner, MovementController, DangerousTreasureTravelController, CofferInteractionController, OccultCrescentData, VisibleCofferPositionOverrideStore, Configuration, Logger);
        PotFallbackWindowEvaluator = new PotFallbackWindowEvaluator(Configuration, Logger);
        PotInstanceTimeEvaluator = new PotInstanceTimeEvaluator(Configuration, Logger);
        PotFarmController = new PotFarmController(Framework, Scanner, MovementController, GameActionController, FateAutomationController, DeathRecoveryController, InstancedContentController, PotCycleTracker, TreasureHintTracker, TreasureSearchController, CofferInteractionController, DangerousTreasureTravelController, PotInstanceTimeEvaluator, OccultCrescentData, Configuration, Logger);
        FarmSessionController = new FarmSessionController(Framework, Scanner, VNavmesh, Lifestream, MovementController, AutorotationController, BuffRotationController, CriticalEngagementAutomationController, FateAutomationController, DeathRecoveryController, PotCycleTracker, PotFallbackWindowEvaluator, PotFarmController, Configuration, Logger);

        ConfigWindow = new ConfigWindow(Configuration, OccultCrescentData, OccultCrescentNameResolver, Logger);
        LogWindow = new LogWindow(this);
        MainWindow = new MainWindow(this, Configuration, Scanner, MovementController, AutorotationController, BuffRotationController, CriticalEngagementAutomationController, FateAutomationController, FarmSessionController, TreasureCofferFarmController);
        DebugWindow = new DebugWindow(this, Configuration, Scanner, MovementController, GameActionController, AutorotationController, BuffRotationController, CriticalEngagementAutomationController, FateAutomationController, DeathRecoveryController, PotFarmController, DangerousTreasureTravelController, FarmSessionController, TreasureCofferFarmController);

        WindowSystem.AddWindow(ConfigWindow);
        WindowSystem.AddWindow(LogWindow);
        WindowSystem.AddWindow(MainWindow);
        WindowSystem.AddWindow(DebugWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open AOCCH. Args: main, debug, config, log, start, stop, coffer-start, coffer-stop, panic, testkeyitem, help."
        });

        // Tell the UI system that we want our windows to be drawn through the window system
        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;

        // This adds a button to the plugin installer entry of this plugin which allows
        // toggling the display status of the configuration ui
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;

        // Adds another button doing the same but for the main ui of the plugin
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
        wasInSouthHorn = ClientState.TerritoryType == SouthHornTerritoryTypeId;
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
        PotFarmController.Dispose();
        TreasureCofferFarmController.Dispose();
        CofferInteractionController.Dispose();
        TreasureSearchController.Dispose();
        DangerousTreasureTravelController.Dispose();
        TreasureHintTracker.Dispose();
        PotCycleTracker.Dispose();
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
            case "coffer-start":
                Logger.Info("Slash command action: start visible coffer farm.");
                TreasureCofferFarmController.Start();
                break;
            case "coffer-stop":
                Logger.Info("Slash command action: stop visible coffer farm.");
                TreasureCofferFarmController.Stop("Slash command visible coffer stop requested.");
                break;
            case "panic":
                Logger.Warning("Slash command action: panic stop.");
                PanicStopAll();
                break;
            default:
                if (normalizedArgs.StartsWith("testkeyitem", StringComparison.Ordinal))
                {
                    HandleTestKeyItemCommand(args);
                    break;
                }

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
        ChatGui.Print("/aocch coffer-start - Start visible coffer farm route");
        ChatGui.Print("/aocch coffer-stop - Stop visible coffer farm route");
        ChatGui.Print("/aocch panic - Panic stop all farm activity");
        ChatGui.Print("/aocch testkeyitem [slot|inventory|command|both] [wait] - Test Magical Elixir usage with detailed logs");
        ChatGui.Print("/aocch help - Show this help");
    }

    private void HandleTestKeyItemCommand(string rawArgs)
    {
        var tokens = rawArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var method = GameActionController.MagicalElixirUseMethod.Inventory;
        var runBoth = false;
        var waitForReady = false;

        for (var i = 1; i < tokens.Length; i++)
        {
            switch (tokens[i].ToLowerInvariant())
            {
                case "slot":
                    method = GameActionController.MagicalElixirUseMethod.Slot;
                    break;
                case "inventory":
                    method = GameActionController.MagicalElixirUseMethod.Inventory;
                    break;
                case "command":
                    method = GameActionController.MagicalElixirUseMethod.Command;
                    break;
                case "both":
                    runBoth = true;
                    break;
                case "wait":
                    waitForReady = true;
                    break;
                default:
                    Logger.Warning($"Unknown testkeyitem option: {tokens[i]}");
                    break;
            }
        }

        Logger.Info($"Slash command action: test Magical Elixir use. mode={(runBoth ? "both" : method.ToString().ToLowerInvariant())} wait={waitForReady}.");
        LogMagicalElixirDebugSnapshot("preflight");

        if (waitForReady && !WaitForMagicalElixirReady())
        {
            Logger.Warning("Manual Magical Elixir test readiness wait timed out; continuing with the requested method(s).");
            LogMagicalElixirDebugSnapshot("post-wait-timeout");
        }
        else if (waitForReady)
        {
            Logger.Info("Manual Magical Elixir test readiness conditions are satisfied.");
            LogMagicalElixirDebugSnapshot("post-wait-ready");
        }

        if (runBoth)
        {
            RunMagicalElixirDebugAttempt(GameActionController.MagicalElixirUseMethod.Slot, "manual test slot attempt");
            RunMagicalElixirDebugAttempt(GameActionController.MagicalElixirUseMethod.Inventory, "manual test inventory attempt");
            RunMagicalElixirDebugAttempt(GameActionController.MagicalElixirUseMethod.Command, "manual test command attempt");
            return;
        }

        RunMagicalElixirDebugAttempt(method, $"manual test {method.ToString().ToLowerInvariant()} attempt");
    }

    private void RunMagicalElixirDebugAttempt(GameActionController.MagicalElixirUseMethod method, string description)
    {
        Logger.Info($"Manual Magical Elixir test attempt starting. method={method.ToString().ToLowerInvariant()} description={description}.");
        LogMagicalElixirDebugSnapshot($"before-{method.ToString().ToLowerInvariant()}");
        var success = GameActionController.TryUseMagicalElixir(method, description);
        Logger.Info($"Manual Magical Elixir test attempt finished. method={method.ToString().ToLowerInvariant()} success={success}.");
        LogMagicalElixirDebugSnapshot($"after-{method.ToString().ToLowerInvariant()}");
    }

    private bool WaitForMagicalElixirReady()
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (!Condition[ConditionFlag.InCombat]
                && !Condition[ConditionFlag.Casting]
                && !Condition[ConditionFlag.BetweenAreas]
                && !Condition[ConditionFlag.OccupiedInQuestEvent])
            {
                return true;
            }

            Thread.Sleep(100);
        }

        return false;
    }

    private void LogMagicalElixirDebugSnapshot(string label)
    {
        var snapshot = Scanner.Snapshot;
        var treasureSnapshot = TreasureHintTracker.Snapshot;
        var player = ObjectTable.LocalPlayer;
        var position = player?.Position;
        var positionText = position == null
            ? "unavailable"
            : $"<{position.Value.X:0.0}, {position.Value.Y:0.0}, {position.Value.Z:0.0}>";

        Logger.Info($"Magical Elixir debug [{label}] time={DateTimeOffset.UtcNow:O} territory={ClientState.TerritoryType} southHorn={snapshot.IsInSouthHorn} playerPos={positionText} hp={player?.CurrentHp ?? 0}.");
        Logger.Info($"Magical Elixir debug [{label}] conditions: inCombat={Condition[ConditionFlag.InCombat]} casting={Condition[ConditionFlag.Casting]} betweenAreas={Condition[ConditionFlag.BetweenAreas]} occupiedInQuestEvent={Condition[ConditionFlag.OccupiedInQuestEvent]} mounted={Condition[ConditionFlag.Mounted]} occupied={Condition[ConditionFlag.Occupied]}.");
        Logger.Info($"Magical Elixir debug [{label}] controllers: movement={MovementController.State} fate={FateAutomationController.State} pot={PotFarmController.State} farm={FarmSessionController.State} treasureSearch={TreasureSearchController.State} cofferRunning={CofferInteractionController.IsRunning}.");
        Logger.Info($"Magical Elixir debug [{label}] treasure: buff={snapshot.HasTreasureBuff} remaining={snapshot.TreasureBuffRemainingSeconds:0.0}s activePot={(snapshot.ActivePotFate == null ? "none" : $"{snapshot.ActivePotFate.Name} ({snapshot.ActivePotFate.Id})")} sessionState={treasureSnapshot.SessionState} sessionId={treasureSnapshot.SessionId} revision={treasureSnapshot.Revision} hint={treasureSnapshot.GetHintSummary()}.");
        Logger.Info($"Magical Elixir debug [{label}] inventory: {GameActionController.DescribeMagicalElixirState()}");
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

        var leavingSouthHorn = wasInSouthHorn && territoryType != SouthHornTerritoryTypeId;
        wasInSouthHorn = territoryType == SouthHornTerritoryTypeId;

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

        if (leavingSouthHorn)
        {
            ResetInstanceStateForTerritoryExit(territoryType);
        }
    }

    private void ResetInstanceStateForTerritoryExit(uint territoryType)
    {
        var reason = $"Left South Horn for territory {territoryType}; resetting instance state.";

        ResetAutomationState(reason);
    }

    private void ResetAutomationState(string reason)
    {
        if (FarmSessionController.IsRunning)
        {
            FarmSessionController.Stop(reason);
        }

        if (PotFarmController.IsRunning)
        {
            PotFarmController.Stop(reason);
        }

        if (TreasureCofferFarmController.IsRunning)
        {
            TreasureCofferFarmController.Stop(reason);
        }

        if (CriticalEngagementAutomationController.IsRunning)
        {
            CriticalEngagementAutomationController.Stop(reason);
        }

        if (FateAutomationController.IsRunning)
        {
            FateAutomationController.Stop(reason);
        }

        if (BuffRotationController.IsRunning)
        {
            BuffRotationController.Stop(reason);
        }

        if (CofferInteractionController.IsRunning)
        {
            CofferInteractionController.Stop(reason);
        }

        if (TreasureSearchController.IsRunning)
        {
            TreasureSearchController.Stop(reason);
        }

        if (DangerousTreasureTravelController.IsRunning)
        {
            DangerousTreasureTravelController.Stop(reason);
        }

        if (MovementController.State is not MovementState.Idle and not MovementState.Stopped)
        {
            MovementController.Stop(reason);
        }

        AutorotationController.ReleaseOwnership(reason);

        FarmSessionController.ResetInstanceState(reason);
        PotFarmController.ResetInstanceState(reason);
        TreasureCofferFarmController.ResetInstanceState(reason);
        CofferInteractionController.ResetInstanceState(reason);
        TreasureSearchController.ResetInstanceState(reason);
        DangerousTreasureTravelController.ResetInstanceState(reason);
        TreasureHintTracker.ResetInstanceState(reason);
        PotCycleTracker.ResetInstanceState(reason);
        DeathRecoveryController.ResetInstanceState(reason);
        FateAutomationController.ResetInstanceState(reason);
        CriticalEngagementAutomationController.ResetInstanceState(reason);
        BuffRotationController.ResetInstanceState(reason);
        AutorotationController.ResetInstanceState(reason);
        MovementController.ResetInstanceState(reason);

        Logger.Info(reason);
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

        if (PotFarmController.IsRunning)
        {
            Logger.Info("Panic stop: stopping pot control.");
            PotFarmController.Stop(reason);
        }
        else
        {
            Logger.Info("Panic stop: pot control not running.");
        }

        if (TreasureCofferFarmController.IsRunning)
        {
            Logger.Info("Panic stop: stopping visible coffer farm.");
            TreasureCofferFarmController.Stop(reason);
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

        TreasureHintTracker.CompleteCurrentTreasureSession(reason, TreasureSessionState.Abandoned);
        if (CofferInteractionController.IsRunning)
        {
            Logger.Info("Panic stop: stopping coffer interaction.");
            CofferInteractionController.Stop(reason);
        }

        Logger.Info("Panic stop: stopping movement.");
        MovementController.Stop(reason);
        Logger.Info("Panic stop: releasing autorotation ownership.");
        AutorotationController.ReleaseOwnership(reason);
        ResetAutomationState(reason);
        Logger.Info("Global panic stop completed.");
    }
}
