using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;

using AOCCH.Data;
using AOCCH.Automation;
using AOCCH.IPC;
using AOCCH.Logging;
using AOCCH.Movement;
using AOCCH.Scanning;
using AOCCH.Shopping;
using Dalamud.Game.Command;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using AOCCH.Windows;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;

namespace AOCCH;

public sealed class Plugin : IDalamudPlugin
{
    private static readonly HashSet<uint> KnownTreasureCofferBaseIds = [2014741u, 2014742u, 2014743u];
    private const float PotCofferDebugRadius = 60f;
    private const float ManualPotInteractFallbackRadius = 30f;
    private const int PotCofferDebugEntryLimit = 30;
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
    internal static Plugin? Current { get; private set; }

    private const string CommandName = "/aocch";

    public Configuration Configuration { get; init; }
    public AocchLogger Logger { get; init; }
    public OccultCrescentDataCatalog OccultCrescentData { get; init; }
    public CofferPositionOverrideStore CofferPositionOverrideStore { get; init; }
    public VisibleCofferPositionOverrideStore VisibleCofferPositionOverrideStore { get; init; }
    public OccultCrescentNameResolver OccultCrescentNameResolver { get; init; }
    public CofferNameResolver CofferNameResolver { get; init; }
    public OccultCrescentScanner Scanner { get; init; }
    public VNavmeshIpc VNavmesh { get; init; }
    public LifestreamIpc Lifestream { get; init; }
    public BossModIpc BossMod { get; init; }
    public NormalAutomationDependencyChecker DependencyChecker { get; init; }
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
    public AutomaticTreasureCofferDebugController AutomaticTreasureCofferDebugController { get; init; }
    public ShopInspectorController ShopInspectorController { get; init; }
    public ShopPurchaseController ShopPurchaseController { get; init; }
    public CurrentCurrencyShopPageMatcher CurrentCurrencyShopPageMatcher { get; init; }
    public ManualCurrencyShoppingController ManualCurrencyShoppingController { get; init; }

    public readonly WindowSystem WindowSystem = new("AOCCH");
    private ConfigWindow ConfigWindow { get; init; }
    private LogWindow LogWindow { get; init; }
    private MainWindow MainWindow { get; init; }
    private DebugWindow DebugWindow { get; init; }
    private DependencyWindow DependencyWindow { get; init; }
    private bool isDisposing;
    private string lastTerritoryKey = string.Empty;

    public Plugin()
    {
        Current = this;
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Logger = new AocchLogger(Log);
        Configuration.SetLogger(Logger);
        OccultCrescentData = OccultCrescentDataLoader.Load(PluginInterface, Logger);
        CofferPositionOverrideStore = new CofferPositionOverrideStore(PluginInterface, Logger);
        VisibleCofferPositionOverrideStore = new VisibleCofferPositionOverrideStore(PluginInterface, Logger);
        if (Configuration.Migrate(OccultCrescentData))
        {
            Configuration.Save();
            Logger.Info("[Plugin] op=config-migrated");
        }

        OccultCrescentNameResolver = new OccultCrescentNameResolver(DataManager, OccultCrescentData, Logger);
        CofferNameResolver = new CofferNameResolver(DataManager, [2014741u, 2014742u, 2014743u], Logger);
        Scanner = new OccultCrescentScanner(ClientState, FateTable, Framework, ObjectTable, OccultCrescentData, Configuration, CofferNameResolver, Logger);
        VNavmesh = new VNavmeshIpc(Logger);
        Lifestream = new LifestreamIpc(Logger);
        BossMod = new BossModIpc(Logger);
        DependencyChecker = new NormalAutomationDependencyChecker(VNavmesh, Lifestream, BossMod);
        RoutePlanner = new RoutePlanner(Configuration, Logger);
        GameActionController = new GameActionController(CommandManager, Condition, ObjectTable, PlayerState, TargetManager, Logger);
        MovementController = new MovementController(Framework, Condition, ObjectTable, GameGui, Scanner, VNavmesh, Lifestream, RoutePlanner, GameActionController, Configuration, Logger);
        DangerousTreasureTravelController = new DangerousTreasureTravelController(Framework, Condition, ObjectTable, Scanner, MovementController, GameActionController, Configuration, Logger);
        AutorotationController = new AutorotationController(BossMod, Configuration, GameActionController, Logger);
        BuffRotationController = new BuffRotationController(Framework, Condition, ObjectTable, Scanner, MovementController, GameActionController, Configuration, Logger);
        CriticalEngagementAutomationController = new CriticalEngagementAutomationController(Framework, Condition, ObjectTable, Scanner, MovementController, AutorotationController, Configuration, Logger);
        FateAutomationController = new FateAutomationController(Framework, Condition, ObjectTable, Scanner, MovementController, AutorotationController, Configuration, Logger);
        DeathRecoveryController = new DeathRecoveryController(Framework, ObjectTable, GameGui, MovementController, AutorotationController, BuffRotationController, CriticalEngagementAutomationController, FateAutomationController, Logger);
        InstancedContentController = new InstancedContentController(Logger);
        PotCycleTracker = new PotCycleTracker(Framework, Scanner, Logger);
        TreasureHintTracker = new TreasureHintTracker(Framework, ChatGui, Scanner, Logger);
        TreasureSearchController = new TreasureSearchController(Framework, Scanner, MovementController, GameActionController, TreasureHintTracker, DangerousTreasureTravelController, CofferNameResolver, CofferPositionOverrideStore, Configuration, Logger);
        CofferInteractionController = new CofferInteractionController(Framework, Condition, ObjectTable, Scanner, MovementController, GameActionController, CofferPositionOverrideStore, Logger);
        TreasureCofferFarmController = new TreasureCofferFarmController(Framework, Condition, ObjectTable, Scanner, MovementController, GameActionController, DeathRecoveryController, DangerousTreasureTravelController, CofferInteractionController, VisibleCofferPositionOverrideStore, Configuration, Logger);
        PotFallbackWindowEvaluator = new PotFallbackWindowEvaluator(Configuration, Logger);
        PotInstanceTimeEvaluator = new PotInstanceTimeEvaluator(Configuration, Logger);
        PotFarmController = new PotFarmController(Framework, Scanner, MovementController, GameActionController, FateAutomationController, DeathRecoveryController, InstancedContentController, PotCycleTracker, TreasureHintTracker, TreasureSearchController, CofferInteractionController, DangerousTreasureTravelController, PotInstanceTimeEvaluator, Configuration, Logger);
        AutomaticTreasureCofferDebugController = new AutomaticTreasureCofferDebugController(Framework, Scanner, GameActionController, DeathRecoveryController, TreasureHintTracker, Configuration, Logger);
        ShopInspectorController = new ShopInspectorController(Framework, GameGui, DataManager, Logger);
        ShopPurchaseController = new ShopPurchaseController(Framework, ChatGui, GameGui, Logger);
        CurrentCurrencyShopPageMatcher = new CurrentCurrencyShopPageMatcher();
        ManualCurrencyShoppingController = new ManualCurrencyShoppingController(Framework, GameGui, Condition, Scanner, Configuration, GameActionController, MovementController, ShopInspectorController, ShopPurchaseController, CurrentCurrencyShopPageMatcher, CriticalEngagementAutomationController, FateAutomationController, BuffRotationController, PotFarmController, TreasureCofferFarmController, Logger);
        FarmSessionController = new FarmSessionController(Framework, Scanner, VNavmesh, Lifestream, MovementController, GameActionController, AutorotationController, BuffRotationController, CriticalEngagementAutomationController, FateAutomationController, DeathRecoveryController, DangerousTreasureTravelController, PotCycleTracker, PotFallbackWindowEvaluator, PotFarmController, TreasureHintTracker, TreasureCofferFarmController, ManualCurrencyShoppingController, Configuration, Logger);

        ConfigWindow = new ConfigWindow(this, Configuration, OccultCrescentNameResolver, Logger);
        LogWindow = new LogWindow(this);
        MainWindow = new MainWindow(this, Configuration, Scanner, MovementController, BuffRotationController, CriticalEngagementAutomationController, FateAutomationController, FarmSessionController, TreasureCofferFarmController);
        DebugWindow = new DebugWindow(this, Configuration, Scanner, MovementController, GameActionController, AutorotationController, BuffRotationController, CriticalEngagementAutomationController, FateAutomationController, DeathRecoveryController, PotFarmController, DangerousTreasureTravelController, FarmSessionController, TreasureCofferFarmController);
        DependencyWindow = new DependencyWindow(this);

        WindowSystem.AddWindow(ConfigWindow);
        WindowSystem.AddWindow(LogWindow);
        WindowSystem.AddWindow(MainWindow);
        WindowSystem.AddWindow(DebugWindow);
        WindowSystem.AddWindow(DependencyWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open AOCCH. Args: main, config, log, shopping, start, stop, coffer-start [index], coffer-stop, panic, help."
        });

        // Tell the UI system that we want our windows to be drawn through the window system
        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;

        // This adds a button to the plugin installer entry of this plugin which allows
        // toggling the display status of the configuration ui
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;

        // Adds another button doing the same but for the main ui of the plugin
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
        lastTerritoryKey = OccultCrescentData.GetTerritoryOrNull(ClientState.TerritoryType)?.Key ?? string.Empty;
        ClientState.TerritoryChanged += OnTerritoryChanged;

        Logger.Info($"[Plugin] op=loaded name=\"{PluginInterface.Manifest.Name}\"");
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
        Logger.Info("[Plugin] op=cleanup-start");

        ConfigWindow.IsOpen = false;
        LogWindow.IsOpen = false;
        MainWindow.IsOpen = false;
        DebugWindow.IsOpen = false;
        DependencyWindow.IsOpen = false;

        WindowSystem.RemoveAllWindows();

        ConfigWindow.Dispose();
        LogWindow.Dispose();
        MainWindow.Dispose();
        DebugWindow.Dispose();
        DependencyWindow.Dispose();
        AutomaticTreasureCofferDebugController.Dispose();
        ManualCurrencyShoppingController.Dispose();
        ShopInspectorController.Dispose();
        ShopPurchaseController.Dispose();
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
        Logger.Info("[Plugin] op=cleanup-finish");
        Current = null;
    }

    public AutomationDependencyReport GetNormalAutomationDependencyReport()
        => DependencyChecker.Evaluate();

    public bool TryOpenDependencyWindow()
    {
        var report = GetNormalAutomationDependencyReport();
        if (report.IsReady)
        {
            return false;
        }

        DependencyWindow.IsOpen = true;
        return true;
    }

    public void OpenDependencyUi()
        => DependencyWindow.IsOpen = true;

    private bool TryBlockNormalStart(string entryPoint)
    {
        var report = GetNormalAutomationDependencyReport();
        if (report.IsReady)
        {
            return false;
        }

        Logger.Warning($"[Plugin] op=start-blocked entry={entryPoint} dependencies={string.Join(',', report.Statuses.Where(status => !status.IsUsable).Select(status => status.Key))}");
        ChatGui.Print($"Automation cannot start: {report.FailureSummary}");
        DependencyWindow.IsOpen = true;
        return true;
    }

    private void OnCommand(string command, string args)
    {
        if (isDisposing)
        {
            return;
        }

        var tokens = args.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var subcommand = tokens.Length == 0 ? string.Empty : tokens[0].ToLowerInvariant();
        Logger.Info($"[Plugin] op=slash-command command=\"{command}\" args=\"{args.Trim()}\"");

        switch (subcommand)
        {
            case "":
            case "main":
                Logger.Info("[Plugin] op=slash-command-action action=toggle-main-window");
                MainWindow.Toggle();
                break;
            case "config":
                Logger.Info("[Plugin] op=slash-command-action action=toggle-config-window");
                ConfigWindow.Toggle();
                break;
            case "debug":
                Logger.Info("[Plugin] op=slash-command-action action=toggle-debug-window");
                DebugWindow.Toggle();
                break;
            case "log":
                Logger.Info("[Plugin] op=slash-command-action action=toggle-log-window");
                LogWindow.Toggle();
                break;
            case "shopping":
                Logger.Info("[Plugin] op=slash-command-action action=open-shopping-window");
                OpenShoppingUi();
                break;
            case "help":
                Logger.Info("[Plugin] op=slash-command-action action=show-help");
                PrintCommandHelp();
                break;
            case "start":
                Logger.Info("[Plugin] op=slash-command-action action=start-farm-session");
                if (TryBlockNormalStart("slash-farm"))
                {
                    break;
                }

                FarmSessionController.Start();
                break;
            case "stop":
                Logger.Info("[Plugin] op=slash-command-action action=stop-farm-session");
                FarmSessionController.Stop("Slash command stop requested.");
                break;
            case "coffer-start":
            {
                var territory = Scanner.ActiveTerritoryData;
                if (territory == null)
                {
                    ChatGui.Print("Overworld coffer route requires a supported Occult Crescent territory.");
                    break;
                }

                if (!Scanner.Snapshot.CanRunVisibleCofferRoute)
                {
                    ChatGui.Print($"Overworld coffer route data is unavailable in {territory.DisplayName}.");
                    break;
                }

                var routeCount = territory.VisibleCofferFarmRoute.Count;
                int? startRouteIndex = null;
                var oneBasedRouteIndex = 0;
                if (tokens.Length > 2 || (tokens.Length == 2 && (!int.TryParse(tokens[1], out oneBasedRouteIndex) || oneBasedRouteIndex < 1 || oneBasedRouteIndex > routeCount)))
                {
                    ChatGui.Print($"Coffer route index must be between 1 and {routeCount}.");
                    break;
                }

                if (tokens.Length == 2)
                {
                    startRouteIndex = oneBasedRouteIndex - 1;
                }

                if (FarmSessionController.IsRunning)
                {
                    Logger.Warning("[Plugin] op=slash-command-action-blocked action=start-visible-coffer-farm reason=farm-session-running");
                    ChatGui.Print("Overworld coffer route start is blocked while the farm session is running.");
                    break;
                }

                Logger.Info("[Plugin] op=slash-command-action action=start-visible-coffer-farm");
                if (TryBlockNormalStart("slash-coffer-farm"))
                {
                    break;
                }

                if (!TreasureCofferFarmController.Start(startRouteIndex: startRouteIndex))
                {
                    ChatGui.Print(TreasureCofferFarmController.LastError.Length == 0
                        ? TreasureCofferFarmController.LastTransition
                        : TreasureCofferFarmController.LastError);
                }
                break;
            }
            case "coffer-stop":
                Logger.Info("[Plugin] op=slash-command-action action=stop-visible-coffer-farm");
                TreasureCofferFarmController.Stop("Slash command overworld coffer stop requested.");
                break;
            case "panic":
                Logger.Warning("[Plugin] op=slash-command-action action=panic-stop");
                PanicStopAll();
                break;
            default:
                Logger.Warning($"[Plugin] op=slash-command-unknown args=\"{args}\"");
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
        ChatGui.Print("/aocch shopping - Open shopping configuration");
        ChatGui.Print("/aocch start - Start unified CE/FATE farm session");
        ChatGui.Print("/aocch stop - Stop unified CE/FATE farm session");
        ChatGui.Print("/aocch coffer-start [index] - Start overworld coffer route at an optional one-based route index");
        ChatGui.Print("/aocch coffer-stop - Stop overworld coffer route");
        ChatGui.Print("/aocch panic - Panic stop all farm activity");
        ChatGui.Print("/aocch help - Show this help");
    }

    internal void RunDebugPotInteraction()
    {
        if (CofferInteractionController.IsRunning)
        {
            Logger.Warning("[Plugin] op=debug-window-action-blocked action=debug-potinteract reason=coffer-interaction-running");
            ChatGui.Print("Pot coffer interaction debug is blocked because coffer interaction is already running.");
            return;
        }

        if (!TryPrepareDebugPotInteractionMatch(out var match, out var reason) || match == null)
        {
            Logger.Warning($"[Plugin] op=debug-window-action-blocked action=debug-potinteract reason=active-pot-match-unavailable detail=\"{reason}\"");
            ChatGui.Print(reason);
            return;
        }

        Logger.Info(
            $"[Plugin] op=debug-window-action action=debug-potinteract candidate={match.CandidateKey.Label} flow={match.Flow} trustworthy={match.IsTrustworthy} baseId={match.Coffer.DataId} objectId={match.Coffer.GameObjectId:X} playerDistance={match.Coffer.DistanceToPlayer:0.0}y reason=\"{reason}\" attribution=\"{match.AttributionReason}\"");

        if (!CofferInteractionController.Start(match))
        {
            var failureDetail = CofferInteractionController.LastError.Length == 0
                ? CofferInteractionController.LastTransition
                : CofferInteractionController.LastError;
            Logger.Warning($"[Plugin] op=debug-window-action-blocked action=debug-potinteract reason=coffer-start-failed detail=\"{failureDetail}\"");
            ChatGui.Print(failureDetail);
            return;
        }

        ChatGui.Print($"Started pot coffer interaction debug for {match.CandidateKey.Label}.");
    }

    private bool TryPrepareDebugPotInteractionMatch(out VisibleCofferMatch? match, out string reason)
    {
        match = null;

        if (TreasureSearchController.TryPrepareActivePotRevealInteractionMatch(out match, out reason) && match != null)
        {
            return true;
        }

        var productionReason = reason;
        if (!Scanner.Snapshot.IsInSupportedTerritory || !Scanner.Snapshot.CanRunPotTreasure)
        {
            reason = productionReason;
            return false;
        }

        if (!TryBuildManualPotRevealInteractionMatch(out match, out var manualReason) || match == null)
        {
            reason = $"{productionReason} Manual fallback also failed: {manualReason}";
            return false;
        }

        reason = $"{productionReason} Falling back to manual nearby coffer match.";
        return true;
    }

    private bool TryBuildManualPotRevealInteractionMatch(out VisibleCofferMatch? match, out string reason)
    {
        match = null;

        var snapshot = Scanner.Snapshot;
        if (!TreasureSearchController.TryFindNearbyPotRevealCofferForDebug(out var coffer, out var recognitionSource, out var objectKind))
        {
            reason = $"No recognized reveal coffer was found within {ManualPotInteractFallbackRadius:0.0}y of the player for manual fallback.";
            return false;
        }

        if (coffer.DistanceToPlayer > ManualPotInteractFallbackRadius)
        {
            reason = $"Nearest recognized reveal coffer {coffer.Name} ({coffer.GameObjectId:X}) is {coffer.DistanceToPlayer:0.0}y away, outside the {ManualPotInteractFallbackRadius:0.0}y manual fallback radius.";
            return false;
        }

        if (!coffer.IsTargetable)
        {
            reason = $"Recognized reveal coffer {coffer.Name} ({coffer.GameObjectId:X}) is within {ManualPotInteractFallbackRadius:0.0}y, but it is not targetable yet. recognition={recognitionSource} objectKind={objectKind}.";
            return false;
        }

        match = new VisibleCofferMatch
        {
            Flow = CofferInteractionFlow.PotReveal,
            CandidateKey = new TreasureCandidateKey
            {
                Label = "debug-manual",
                CandidateKey = "debug-manual",
            },
            Coffer = coffer,
            MatchDistance = 0f,
            IsTrustworthy = false,
            AttributionReason = $"Manual debug fallback selected a recognized reveal coffer within {ManualPotInteractFallbackRadius:0.0}y while no active treasure-search candidate context was available. recognition={recognitionSource} objectKind={objectKind} playerDistance={coffer.DistanceToPlayer:0.0}y treasureBuff={snapshot.HasTreasureBuff}.",
        };
        Logger.Info($"[Plugin] op=debug-potinteract-manual-match objectId={coffer.GameObjectId:X} baseId={coffer.DataId} name='{coffer.Name}' playerDistance={coffer.DistanceToPlayer:0.0}y recognition={recognitionSource} objectKind={objectKind} treasureBuff={snapshot.HasTreasureBuff}");
        reason = $"Using manual nearby coffer fallback for {coffer.Name} ({coffer.GameObjectId:X}).";
        return true;
    }

    internal void RunDebugAutomaticCofferSurvey()
    {
        if (AutomaticTreasureCofferDebugController.IsRunning)
        {
            Logger.Warning("[Plugin] op=debug-window-action-blocked action=debug-autocoffer reason=already-running");
            ChatGui.Print("Automatic coffer debug survey is already running.");
            return;
        }

        if (FarmSessionController.IsRunning || TreasureCofferFarmController.IsRunning || BuffRotationController.IsRunning || CriticalEngagementAutomationController.IsRunning || FateAutomationController.IsRunning || PotFarmController.IsRunning)
        {
            Logger.Warning("[Plugin] op=debug-window-action-blocked action=debug-autocoffer reason=conflicting-automation");
            ChatGui.Print("Automatic coffer debug survey requires the farm session, overworld coffer routing, CE/FATE automation, pot control, and buff rotation to be stopped.");
            return;
        }

        if (!AutomaticTreasureCofferDebugController.Start())
        {
            Logger.Warning($"[Plugin] op=debug-window-action-blocked action=debug-autocoffer reason=controller-start-failed detail=\"{AutomaticTreasureCofferDebugController.LastTransition}\"");
            ChatGui.Print(AutomaticTreasureCofferDebugController.LastTransition);
            return;
        }

        Logger.Info("[Plugin] op=debug-window-action action=debug-autocoffer");
    }

    internal void RunMagicalElixirDebugTest(bool waitForReady)
    {
        TreasureHintTracker.ClearDebugLogMessageCapture();

        Logger.Info($"[Plugin] op=debug-window-action action=test-magical-elixir mode=inventory wait={waitForReady}");
        LogMagicalElixirDebugSnapshot("preflight");

        if (waitForReady && !WaitForMagicalElixirReady())
        {
            Logger.Warning("[Plugin] op=magical-elixir-ready-wait result=timeout action=continue");
            LogMagicalElixirDebugSnapshot("post-wait-timeout");
        }
        else if (waitForReady)
        {
            Logger.Info("[Plugin] op=magical-elixir-ready-wait result=ready");
            LogMagicalElixirDebugSnapshot("post-wait-ready");
        }

        RunMagicalElixirDebugAttempt();
    }

    private void RunMagicalElixirDebugAttempt()
    {
        const string description = "manual test inventory attempt";
        var attemptId = TreasureHintTracker.ArmDebugLogMessageCapture(description, TimeSpan.FromSeconds(5));
        Logger.Info($"[Plugin] op=magical-elixir-attempt-start attempt={attemptId} method=inventory description=\"{description}\"");
        LogMagicalElixirDebugSnapshot("before-inventory");
        var success = GameActionController.TryUseMagicalElixirViaInventory(description);
        Logger.Info($"[Plugin] op=magical-elixir-attempt-finish attempt={attemptId} method=inventory success={success}");
        LogMagicalElixirDebugSnapshot("after-inventory");
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
        var player = ObjectTable.LocalPlayer;
        var position = player?.Position;
        var positionText = position == null
            ? "unavailable"
            : $"<{position.Value.X:0.0}, {position.Value.Y:0.0}, {position.Value.Z:0.0}>";

        Logger.Info($"[Plugin] op=magical-elixir-debug label={label} time={DateTimeOffset.UtcNow:O} territory={ClientState.TerritoryType} territoryKey={snapshot.TerritoryKey} supported={snapshot.IsInSupportedTerritory} playerPos={positionText} hp={player?.CurrentHp ?? 0}");
        Logger.Info($"[Plugin] op=magical-elixir-debug-conditions label={label} inCombat={Condition[ConditionFlag.InCombat]} casting={Condition[ConditionFlag.Casting]} betweenAreas={Condition[ConditionFlag.BetweenAreas]} occupiedInQuestEvent={Condition[ConditionFlag.OccupiedInQuestEvent]} mounted={Condition[ConditionFlag.Mounted]} occupied={Condition[ConditionFlag.Occupied]}");
        Logger.Info($"[Plugin] op=magical-elixir-debug-treasure label={label} buff={snapshot.HasTreasureBuff} remaining={snapshot.TreasureBuffRemainingSeconds:0.0}s");
        Logger.Info($"[Plugin] op=magical-elixir-debug-logmessages label={label} capture={TreasureHintTracker.GetDebugLogMessageCaptureSummary()}");
        Logger.Info($"[Plugin] op=magical-elixir-debug-inventory label={label} state={GameActionController.DescribeMagicalElixirState()}");
    }

    internal void LogPotCofferDebugSnapshot()
    {
        var snapshot = Scanner.Snapshot;
        var treasureSnapshot = TreasureHintTracker.Snapshot;
        var player = ObjectTable.LocalPlayer;
        var playerPosition = player?.Position;
        var activeCandidate = TreasureSearchController.ActiveCandidateKey;
        var activeCandidatePosition = TreasureSearchController.ActiveCandidateResolvedPosition;
        var activeCandidatePositionKnown = activeCandidatePosition != Vector3.Zero;
        var positionText = playerPosition == null
            ? "unavailable"
            : $"<{playerPosition.Value.X:0.0}, {playerPosition.Value.Y:0.0}, {playerPosition.Value.Z:0.0}>";
        var candidatePositionText = activeCandidatePositionKnown
            ? $"<{activeCandidatePosition.X:0.0}, {activeCandidatePosition.Y:0.0}, {activeCandidatePosition.Z:0.0}>"
            : "unavailable";

        Logger.Info($"[Plugin] op=pot-coffer-debug time={DateTimeOffset.UtcNow:O} territory={ClientState.TerritoryType} territoryKey={snapshot.TerritoryKey} supported={snapshot.IsInSupportedTerritory} playerPos={positionText} activeCandidate={activeCandidate?.Label ?? "none"} candidatePos={candidatePositionText}");
        Logger.Info($"[Plugin] op=pot-coffer-debug-treasure buff={snapshot.HasTreasureBuff} remaining={snapshot.TreasureBuffRemainingSeconds:0.0}s treasureSessionState={treasureSnapshot.SessionState} sessionId={treasureSnapshot.SessionId} revision={treasureSnapshot.Revision} searchState={TreasureSearchController.State} searchTransition=\"{TreasureSearchController.LastTransition}\" visibleCoffers={snapshot.VisibleCoffers.Count}");

        foreach (var visibleCoffer in snapshot.VisibleCoffers)
        {
            var candidateDistance = activeCandidatePositionKnown
                ? CalculateFlatDistance(visibleCoffer.Position, activeCandidatePosition)
                : float.NaN;
            Logger.Info($"[Plugin] op=pot-coffer-debug-visible name='{visibleCoffer.Name}' baseId={visibleCoffer.DataId} objectId={visibleCoffer.GameObjectId:X} pos=<{visibleCoffer.Position.X:0.0}, {visibleCoffer.Position.Y:0.0}, {visibleCoffer.Position.Z:0.0}> playerDistance={visibleCoffer.DistanceToPlayer:0.0}y candidateDistance={(float.IsNaN(candidateDistance) ? "n/a" : $"{candidateDistance:0.0}y")}");
        }

        if (playerPosition == null)
        {
            Logger.Warning("[Plugin] op=pot-coffer-debug-skip reason=player-position-unavailable");
            return;
        }

        var visibleObjectIds = snapshot.VisibleCoffers.Select(coffer => coffer.GameObjectId).ToHashSet();
        var entries = new List<(float Distance, string Message)>();
        foreach (var gameObject in ObjectTable)
        {
            if (gameObject is not Dalamud.Game.ClientState.Objects.Types.IGameObject objectEntry)
            {
                continue;
            }

            var playerDistance = CalculateFlatDistance(playerPosition.Value, objectEntry.Position);
            if (playerDistance > PotCofferDebugRadius)
            {
                continue;
            }

            var candidateDistance = activeCandidatePositionKnown
                ? CalculateFlatDistance(objectEntry.Position, activeCandidatePosition)
                : float.NaN;
            var objectKind = objectEntry.ObjectKind.ToString();
            var recognizedByBaseId = KnownTreasureCofferBaseIds.Contains(objectEntry.BaseId);
            var recognizedByLocalizedName = CofferNameResolver.IsKnownLocalizedName(objectEntry.Name.ToString());
            var recognizedByTreasureKind = objectKind.StartsWith("Treasure", StringComparison.OrdinalIgnoreCase);
            var includedInVisibleScan = visibleObjectIds.Contains(objectEntry.GameObjectId);
            entries.Add((
                playerDistance,
                $"[Plugin] op=pot-coffer-debug-object name='{objectEntry.Name}' baseId={objectEntry.BaseId} objectId={objectEntry.GameObjectId:X} kind={objectKind} pos=<{objectEntry.Position.X:0.0}, {objectEntry.Position.Y:0.0}, {objectEntry.Position.Z:0.0}> playerDistance={playerDistance:0.0}y candidateDistance={(float.IsNaN(candidateDistance) ? "n/a" : $"{candidateDistance:0.0}y")} targetable={objectEntry.IsTargetable} valid={objectEntry.IsValid()} recognizedByBaseId={recognizedByBaseId} recognizedByLocalizedName={recognizedByLocalizedName} recognizedByTreasureKind={recognizedByTreasureKind} includedInVisibleScan={includedInVisibleScan}"));
        }

        if (entries.Count == 0)
        {
            Logger.Info($"[Plugin] op=pot-coffer-debug-object-summary radius={PotCofferDebugRadius:0.0} entries=0");
            return;
        }

        Logger.Info($"[Plugin] op=pot-coffer-debug-object-summary radius={PotCofferDebugRadius:0.0} entries={entries.Count} logged={Math.Min(entries.Count, PotCofferDebugEntryLimit)}");
        foreach (var entry in entries.OrderBy(entry => entry.Distance).Take(PotCofferDebugEntryLimit))
        {
            Logger.Info(entry.Message);
        }
    }

    internal unsafe void RunProbeForay()
    {
        var snapshot = Scanner.Snapshot;
        var player = ObjectTable.LocalPlayer;
        var playerPosition = player?.Position;
        var playerPositionText = playerPosition == null
            ? "unavailable"
            : $"<{playerPosition.Value.X:0.0}, {playerPosition.Value.Y:0.0}, {playerPosition.Value.Z:0.0}>";

        var state = PublicContentOccultCrescent.GetState();
        var stateAvailable = state != null;
        var currentKnowledge = stateAvailable ? state->CurrentKnowledge : 0u;
        var neededKnowledge = stateAvailable ? state->NeededKnowledge : 0u;
        var knowledgeLevelSync = stateAvailable ? state->KnowledgeLevelSync : (byte)0;
        var currentSupportJob = stateAvailable ? state->CurrentSupportJob : (byte)0;
        var supportJobLevel = 0;
        if (stateAvailable)
        {
            var supportJobLevels = state->SupportJobLevels.ToArray();
            if (currentSupportJob < supportJobLevels.Length)
            {
                supportJobLevel = supportJobLevels[currentSupportJob];
            }
        }

        Logger.Info($"[Plugin] op=probe-foray-player territory={ClientState.TerritoryType} territoryKey={snapshot.TerritoryKey} supported={snapshot.IsInSupportedTerritory} playerPos={playerPositionText} hp={player?.CurrentHp ?? 0} forayLevel={(snapshot.PlayerForayLevel?.ToString() ?? "unavailable")} ocState={stateAvailable} knowledge={currentKnowledge} neededKnowledge={neededKnowledge} knowledgeSync={knowledgeLevelSync} supportJob={currentSupportJob} supportJobLevel={supportJobLevel}");

        var target = TargetManager.Target;
        if (target == null)
        {
            Logger.Info("[Plugin] op=probe-foray-target available=false reason=no-target");
            ChatGui.Print($"Probe: no current target; player knowledge={currentKnowledge} sync={knowledgeLevelSync} ocState={stateAvailable}.");
            return;
        }

        var targetPositionText = $"<{target.Position.X:0.0}, {target.Position.Y:0.0}, {target.Position.Z:0.0}>";
        var targetValid = target.IsValid();
        var targetable = target.IsTargetable;
        var normalLevel = -1;
        var forayInfoAvailable = false;
        var forayLevel = -1;
        var forayElement = -1;
        var targetIsBattleNpc = target is IBattleNpc;
        var characterPointer = (Character*)target.Address;
        var targetIsCharacter = target is ICharacter && characterPointer != null && characterPointer->VirtualTable != null;
        var battleCharaPointer = targetIsBattleNpc && targetIsCharacter ? (BattleChara*)characterPointer : null;
        var fateId = targetIsCharacter ? characterPointer->FateId : (ushort)0;

        if (targetIsCharacter)
        {
            normalLevel = characterPointer->Level;
            var forayInfo = characterPointer->GetForayInfo();
            if (forayInfo != null)
            {
                forayInfoAvailable = true;
                forayLevel = forayInfo->Level;
                forayElement = forayInfo->Element;
            }
            else if (battleCharaPointer != null)
            {
                forayInfoAvailable = true;
                forayLevel = battleCharaPointer->ForayInfo.Level;
                forayElement = battleCharaPointer->ForayInfo.Element;
            }
        }

        Logger.Info($"[Plugin] op=probe-foray-target available=true name='{target.Name}' kind={target.ObjectKind} objectId={target.GameObjectId:X} baseId={target.BaseId} objectIndex={target.ObjectIndex} fateId={fateId} pos={targetPositionText} valid={targetValid} targetable={targetable} isCharacter={targetIsCharacter} isBattleNpc={targetIsBattleNpc} normalLevel={normalLevel} forayInfo={forayInfoAvailable} forayLevel={forayLevel} forayElement={forayElement}");
        ChatGui.Print($"Probe: target=\"{target.Name}\" lvl={normalLevel} forayLvl={forayLevel} elem={forayElement} knowledge={currentKnowledge} sync={knowledgeLevelSync}");
    }

    private static float CalculateFlatDistance(Vector3 left, Vector3 right)
    {
        var deltaX = left.X - right.X;
        var deltaZ = left.Z - right.Z;
        return MathF.Sqrt((deltaX * deltaX) + (deltaZ * deltaZ));
    }
    
    public void ToggleConfigUi()
    {
        if (isDisposing)
        {
            return;
        }

        Logger.Info("[Plugin] op=ui-action action=toggle-config-window");
        ConfigWindow.Toggle();
    }

    public void OpenShoppingUi()
    {
        if (isDisposing)
        {
            return;
        }

        Logger.Info("[Plugin] op=ui-action action=open-shopping-window");
        ConfigWindow.OpenShoppingTab();
    }

    public void ToggleLogUi()
    {
        if (isDisposing)
        {
            return;
        }

        Logger.Info("[Plugin] op=ui-action action=toggle-log-window");
        LogWindow.Toggle();
    }

    public void ToggleMainUi()
    {
        if (isDisposing)
        {
            return;
        }

        Logger.Info("[Plugin] op=ui-action action=toggle-main-window");
        MainWindow.Toggle();
    }

    private void OnTerritoryChanged(uint territoryType)
    {
        if (isDisposing)
        {
            return;
        }

        var territory = OccultCrescentData.GetTerritoryOrNull(territoryType);
        var newTerritoryKey = territory?.Key ?? string.Empty;
        var territoryChanged = !string.Equals(lastTerritoryKey, newTerritoryKey, StringComparison.OrdinalIgnoreCase);

        if (territory != null)
        {
            if (!MainWindow.IsOpen)
            {
                MainWindow.IsOpen = true;
                Logger.Info($"[Plugin] op=main-window-auto-open state=open reason=territory-entry territoryKey={territory.Key} territoryId={territory.TerritoryTypeId}");
            }
        }
        else if (MainWindow.IsOpen)
        {
            MainWindow.IsOpen = false;
            Logger.Info($"[Plugin] op=main-window-auto-open state=closed territory={territoryType}");
        }

        if (territoryChanged && (!string.IsNullOrWhiteSpace(lastTerritoryKey) || territory != null))
        {
            ResetInstanceStateForTerritoryChange(lastTerritoryKey, newTerritoryKey, territoryType);
        }

        lastTerritoryKey = newTerritoryKey;
    }

    private void ResetInstanceStateForTerritoryChange(string previousTerritoryKey, string newTerritoryKey, uint territoryType)
    {
        var previousKey = string.IsNullOrWhiteSpace(previousTerritoryKey) ? "unsupported" : previousTerritoryKey;
        var nextKey = string.IsNullOrWhiteSpace(newTerritoryKey) ? "unsupported" : newTerritoryKey;
        var reason = $"Changed supported territory from {previousKey} to {nextKey} (territory {territoryType}); resetting instance state.";

        if (Configuration.StartingPotFate != StartingPotFateMode.Auto)
        {
            Logger.Info($"[Plugin] op=setting-change key=StartingPotFate old={Configuration.StartingPotFate} new={StartingPotFateMode.Auto} reason=territory-change territory={territoryType} previous={previousKey} next={nextKey}");
            Configuration.StartingPotFate = StartingPotFateMode.Auto;
            Configuration.Save();
        }

        ResetAutomationState(reason);
    }

    private void ResetAutomationState(string reason)
    {
        if (ManualCurrencyShoppingController.IsRunning)
        {
            ManualCurrencyShoppingController.Stop(reason);
        }

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
        AutorotationController.DeleteManagedPreset(reason);

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

        Logger.Info($"[Plugin] op=reset-automation reason={reason}");
    }

    public void PanicStopAll()
    {
        const string reason = "Global panic stop requested.";
        Logger.Warning($"[Plugin] op=panic-stop-request reason={reason}");

        if (FarmSessionController.IsRunning)
        {
            Logger.Info("[Plugin] op=panic-stop component=FarmSession action=stop");
            FarmSessionController.PanicStop();
        }
        else
        {
            Logger.Info("[Plugin] op=panic-stop component=FarmSession action=skip reason=not-running");
        }

        if (CriticalEngagementAutomationController.IsRunning)
        {
            Logger.Info("[Plugin] op=panic-stop component=CE action=stop");
            CriticalEngagementAutomationController.Stop(reason);
        }
        else
        {
            Logger.Info("[Plugin] op=panic-stop component=CE action=skip reason=not-running");
        }

        if (FateAutomationController.IsRunning)
        {
            Logger.Info("[Plugin] op=panic-stop component=FATE action=stop");
            FateAutomationController.Stop(reason);
        }
        else
        {
            Logger.Info("[Plugin] op=panic-stop component=FATE action=skip reason=not-running");
        }

        if (PotFarmController.IsRunning)
        {
            Logger.Info("[Plugin] op=panic-stop component=Pot action=stop");
            PotFarmController.Stop(reason);
        }
        else
        {
            Logger.Info("[Plugin] op=panic-stop component=Pot action=skip reason=not-running");
        }

        if (TreasureCofferFarmController.IsRunning)
        {
            Logger.Info("[Plugin] op=panic-stop component=CofferFarm action=stop");
            TreasureCofferFarmController.Stop(reason);
        }

        if (ManualCurrencyShoppingController.IsRunning)
        {
            Logger.Info("[Plugin] op=panic-stop component=CurrencyShopping action=stop");
            ManualCurrencyShoppingController.Stop(reason);
        }

        if (BuffRotationController.IsRunning)
        {
            Logger.Info("[Plugin] op=panic-stop component=BuffRotation action=stop");
            BuffRotationController.Stop(reason);
        }
        else
        {
            Logger.Info("[Plugin] op=panic-stop component=BuffRotation action=skip reason=not-running");
        }

        TreasureHintTracker.CompleteCurrentTreasureSession(reason, TreasureSessionState.Abandoned);
        if (CofferInteractionController.IsRunning)
        {
            Logger.Info("[Plugin] op=panic-stop component=CofferInteraction action=stop");
            CofferInteractionController.Stop(reason);
        }

        Logger.Info("[Plugin] op=panic-stop component=Movement action=stop");
        MovementController.Stop(reason);
        Logger.Info("[Plugin] op=panic-stop component=Autorotation action=release");
        AutorotationController.ReleaseOwnership(reason);
        ResetAutomationState(reason);
        Logger.Info("[Plugin] op=panic-stop-complete");
    }
}
