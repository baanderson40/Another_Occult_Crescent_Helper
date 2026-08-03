using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Threading;
using ECommons;

using AOCCH.Data;
using AOCCH.Automation;
using AOCCH.IPC;
using AOCCH.Logging;
using AOCCH.Movement;
using AOCCH.Rendering;
using AOCCH.Scanning;
using AOCCH.Shopping;
using AOCCH.Telemetry;
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
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer;

namespace AOCCH;

public sealed class Plugin : IDalamudPlugin
{
    private const float PotCofferDebugRadius = 60f;
    private const float ManualPotInteractFallbackRadius = 30f;
    private const int PotCofferDebugEntryLimit = 30;
    private const uint NorthHornTerritoryId = 1346;
    private const uint NorthHornLgbTreasureLayer = 43366;
    private const float NorthHornLgbCaptureForayScanRadius = 120f;
    private const float NorthHornRevealCaptureForayScanRadius = 120f;
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
    public OccultCrescentScanner Scanner { get; init; }
    public VNavmeshIpc VNavmesh { get; init; }
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
    public TreasureGuideRenderer TreasureGuideRenderer { get; init; }
    public CofferObservationSubmissionService CofferObservationSubmissionService { get; init; }

    public readonly WindowSystem WindowSystem = new("AOCCH");
    private ConfigWindow ConfigWindow { get; init; }
    private LogWindow LogWindow { get; init; }
    private MainWindow MainWindow { get; init; }
    private DebugWindow DebugWindow { get; init; }
    private DependencyWindow DependencyWindow { get; init; }
    private NorthHornStatusWindow NorthHornStatusWindow { get; init; }
    private bool isDisposing;
    private string lastTerritoryKey = string.Empty;
    private string lastDependencyState = string.Empty;

    public Plugin()
    {
        Current = this;
        ECommonsMain.Init(PluginInterface, this);
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
        Scanner = new OccultCrescentScanner(ClientState, FateTable, Framework, ObjectTable, OccultCrescentData, Configuration, Logger);
        VNavmesh = new VNavmeshIpc(Logger);
        BossMod = new BossModIpc(Logger);
        DependencyChecker = new NormalAutomationDependencyChecker(VNavmesh, BossMod);
        RoutePlanner = new RoutePlanner(Configuration, Logger);
        GameActionController = new GameActionController(CommandManager, Condition, ObjectTable, PlayerState, TargetManager, Logger);
        MovementController = new MovementController(Framework, Condition, ObjectTable, GameGui, DataManager, Scanner, VNavmesh, RoutePlanner, GameActionController, Configuration, Logger);
        DangerousTreasureTravelController = new DangerousTreasureTravelController(Framework, Condition, ObjectTable, Scanner, MovementController, GameActionController, Configuration, Logger);
        AutorotationController = new AutorotationController(BossMod, Configuration, GameActionController, Logger);
        BuffRotationController = new BuffRotationController(Framework, Condition, ObjectTable, Scanner, MovementController, GameActionController, Configuration, Logger);
        CriticalEngagementAutomationController = new CriticalEngagementAutomationController(Framework, Condition, ObjectTable, Scanner, MovementController, AutorotationController, Configuration, Logger);
        FateAutomationController = new FateAutomationController(Framework, Condition, ObjectTable, Scanner, MovementController, AutorotationController, Configuration, Logger);
        DeathRecoveryController = new DeathRecoveryController(Framework, ObjectTable, GameGui, MovementController, AutorotationController, BuffRotationController, CriticalEngagementAutomationController, FateAutomationController, Logger);
        InstancedContentController = new InstancedContentController(GameGui, Logger);
        PotCycleTracker = new PotCycleTracker(Framework, Scanner, Logger);
        TreasureHintTracker = new TreasureHintTracker(Framework, ChatGui, ObjectTable, Scanner, Logger);
        TreasureSearchController = new TreasureSearchController(Framework, Scanner, MovementController, GameActionController, TreasureHintTracker, DangerousTreasureTravelController, CofferPositionOverrideStore, Configuration, Logger);
        CofferInteractionController = new CofferInteractionController(Framework, Condition, ObjectTable, Scanner, MovementController, GameActionController, CofferPositionOverrideStore, Logger);
        CofferObservationSubmissionService = new CofferObservationSubmissionService(PluginInterface.ConfigDirectory.FullName, Configuration, Logger, typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "unknown");
        CofferInteractionController.CofferOpened += OnCofferOpened;
        Scanner.CofferOpened += OnScannerCofferOpened;
        TreasureCofferFarmController = new TreasureCofferFarmController(Framework, Condition, ObjectTable, Scanner, MovementController, GameActionController, DeathRecoveryController, DangerousTreasureTravelController, CofferInteractionController, VisibleCofferPositionOverrideStore, Configuration, Logger);
        PotFallbackWindowEvaluator = new PotFallbackWindowEvaluator(Configuration, Logger);
        PotInstanceTimeEvaluator = new PotInstanceTimeEvaluator(Configuration, Logger);
        PotFarmController = new PotFarmController(Framework, Scanner, MovementController, GameActionController, FateAutomationController, DeathRecoveryController, InstancedContentController, PotCycleTracker, TreasureHintTracker, TreasureSearchController, CofferInteractionController, DangerousTreasureTravelController, PotInstanceTimeEvaluator, Configuration, Logger);
        AutomaticTreasureCofferDebugController = new AutomaticTreasureCofferDebugController(Framework, Scanner, GameActionController, DeathRecoveryController, TreasureHintTracker, Configuration, Logger);
        TreasureGuideRenderer = new TreasureGuideRenderer(PluginInterface, Configuration, Scanner, Condition, ObjectTable, Logger);
        ShopInspectorController = new ShopInspectorController(Framework, GameGui, DataManager, Logger);
        ShopPurchaseController = new ShopPurchaseController(Framework, ChatGui, GameGui, Logger);
        CurrentCurrencyShopPageMatcher = new CurrentCurrencyShopPageMatcher();
        ManualCurrencyShoppingController = new ManualCurrencyShoppingController(Framework, GameGui, Condition, Scanner, Configuration, GameActionController, MovementController, ShopInspectorController, ShopPurchaseController, CurrentCurrencyShopPageMatcher, CriticalEngagementAutomationController, FateAutomationController, BuffRotationController, PotFarmController, TreasureCofferFarmController, Logger);
        FarmSessionController = new FarmSessionController(Framework, Scanner, VNavmesh, MovementController, GameActionController, AutorotationController, BuffRotationController, CriticalEngagementAutomationController, FateAutomationController, DeathRecoveryController, DangerousTreasureTravelController, PotCycleTracker, PotFallbackWindowEvaluator, PotFarmController, TreasureHintTracker, TreasureCofferFarmController, ManualCurrencyShoppingController, Configuration, Logger);

        ConfigWindow = new ConfigWindow(this, Configuration, OccultCrescentNameResolver, Logger);
        LogWindow = new LogWindow(this);
        MainWindow = new MainWindow(this, Configuration, Scanner, MovementController, BuffRotationController, CriticalEngagementAutomationController, FateAutomationController, FarmSessionController, TreasureCofferFarmController);
        DebugWindow = new DebugWindow(this, Configuration, Scanner, MovementController, GameActionController, AutorotationController, BuffRotationController, CriticalEngagementAutomationController, FateAutomationController, DeathRecoveryController, PotFarmController, DangerousTreasureTravelController, FarmSessionController, TreasureCofferFarmController);
        DependencyWindow = new DependencyWindow(this);
        NorthHornStatusWindow = new NorthHornStatusWindow(this, Configuration);

        WindowSystem.AddWindow(ConfigWindow);
        WindowSystem.AddWindow(LogWindow);
        WindowSystem.AddWindow(MainWindow);
        WindowSystem.AddWindow(DebugWindow);
        WindowSystem.AddWindow(DependencyWindow);
        WindowSystem.AddWindow(NorthHornStatusWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open AOCCH. Args: main, config, log, shopping, start, stop, coffer-start [index], coffer-stop, panic, help."
        });

        // Tell the UI system that we want our windows to be drawn through the window system
        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.Draw += TreasureGuideRenderer.Draw;

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
        PluginInterface.UiBuilder.Draw -= TreasureGuideRenderer.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
        ClientState.TerritoryChanged -= OnTerritoryChanged;
        Logger.Info("[Plugin] op=cleanup-start");

        ConfigWindow.IsOpen = false;
        LogWindow.IsOpen = false;
        MainWindow.IsOpen = false;
        DebugWindow.IsOpen = false;
        DependencyWindow.IsOpen = false;
        NorthHornStatusWindow.Close();

        WindowSystem.RemoveAllWindows();

        ConfigWindow.Dispose();
        LogWindow.Dispose();
        MainWindow.Dispose();
        DebugWindow.Dispose();
        DependencyWindow.Dispose();
        NorthHornStatusWindow.Dispose();
        AutomaticTreasureCofferDebugController.Dispose();
        TreasureGuideRenderer.Dispose();
        ManualCurrencyShoppingController.Dispose();
        ShopInspectorController.Dispose();
        ShopPurchaseController.Dispose();
        FarmSessionController.Dispose();
        PotFarmController.Dispose();
        TreasureCofferFarmController.Dispose();
        CofferInteractionController.CofferOpened -= OnCofferOpened;
        Scanner.CofferOpened -= OnScannerCofferOpened;
        CofferObservationSubmissionService.Dispose();
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
        ECommonsMain.Dispose();
        Logger.Info("[Plugin] op=cleanup-finish");
        Current = null;
    }

    public AutomationDependencyReport GetNormalAutomationDependencyReport()
    {
        var report = DependencyChecker.Evaluate();
        var state = string.Join(
            ";",
            report.Statuses.Select(status => $"{status.Key}:installed={status.Installed}:available={status.Available}"));
        if (!string.Equals(lastDependencyState, state, StringComparison.Ordinal))
        {
            lastDependencyState = state;
            var details = string.Join(
                " | ",
                report.Statuses.Select(status => $"{status.Key} installed={status.Installed} available={status.Available} usable={status.IsUsable}"));
            if (report.IsReady)
            {
                Logger.Info($"[Dependencies] op=state-changed ready=true entries={details}");
            }
            else
            {
                Logger.Warning($"[Dependencies] op=state-changed ready=false entries={details}");
            }
        }

        return report;
    }

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

    public void OpenNorthHornStatusPreview()
    {
        if (isDisposing)
        {
            return;
        }

        NorthHornStatusWindow.Open(debugPreview: true);
        Logger.Info("[Plugin] op=ui-action action=open-north-horn-status-preview");
    }

    private void OnCofferOpened(VisibleCofferMatch match)
    {
        if (!Scanner.Snapshot.IsInSupportedTerritory)
        {
            return;
        }

        var source = match.Flow == CofferInteractionFlow.PotReveal
            ? "pot-reveal"
            : "interaction-overworld";
        CofferObservationSubmissionService.Enqueue(Scanner.Snapshot, match.Coffer, source);
    }

    private void OnScannerCofferOpened(VisibleCoffer coffer)
    {
        if (!Scanner.Snapshot.IsInSupportedTerritory)
        {
            return;
        }

        CofferObservationSubmissionService.Enqueue(Scanner.Snapshot, coffer, "scanner-overworld");
    }

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
                    Logger.Warning($"[Plugin] op=slash-command-action-blocked action=start-visible-coffer-farm reason=unsupported-territory territoryId={ClientState.TerritoryType}");
                    ChatGui.Print("Overworld coffer route requires a supported Occult Crescent territory.");
                    break;
                }

                if (!Scanner.Snapshot.CanRunVisibleCofferRoute)
                {
                    Logger.Warning($"[Plugin] op=slash-command-action-blocked action=start-visible-coffer-farm reason=route-unavailable territoryKey={territory.Key} territoryId={territory.TerritoryTypeId}");
                    ChatGui.Print($"Overworld coffer route data is unavailable in {territory.DisplayName}.");
                    break;
                }

                var routeCount = territory.VisibleCofferFarmRoute.Count;
                int? startRouteIndex = null;
                var oneBasedRouteIndex = 0;
                if (tokens.Length > 2 || (tokens.Length == 2 && (!int.TryParse(tokens[1], out oneBasedRouteIndex) || oneBasedRouteIndex < 1 || oneBasedRouteIndex > routeCount)))
                {
                    Logger.Warning($"[Plugin] op=slash-command-action-blocked action=start-visible-coffer-farm reason=invalid-route-index requested=\"{args.Trim()}\" routeCount={routeCount}");
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
                    var failureDetail = TreasureCofferFarmController.LastError.Length == 0
                        ? TreasureCofferFarmController.LastTransition
                        : TreasureCofferFarmController.LastError;
                    Logger.Warning($"[Plugin] op=slash-command-action-blocked action=start-visible-coffer-farm reason=controller-start-failed requestedIndex={(startRouteIndex.HasValue ? (startRouteIndex.Value + 1).ToString() : "default")} detail=\"{failureDetail}\"");
                    ChatGui.Print(failureDetail);
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
        ChatGui.Print("/aocch debug - Toggle debug window");
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
        var attemptId = TreasureHintTracker.ArmDebugLogMessageCapture(description, TimeSpan.FromSeconds(5), captureAllMessageIds: true);
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
            var territory = Scanner.ActiveTerritoryData;
            var recognitionSource = string.Empty;
            var recognized = territory != null && CofferRecognition.TryRecognize(territory.VisibleCoffers, objectEntry, out recognitionSource);
            var potRevealRecognitionSource = string.Empty;
            var recognizedAsPotReveal = territory != null && CofferRecognition.TryRecognizePotReveal(territory.VisibleCoffers, objectEntry, out potRevealRecognitionSource);
            var recognizedByTreasureKind = objectKind.StartsWith("Treasure", StringComparison.OrdinalIgnoreCase);
            var includedInVisibleScan = visibleObjectIds.Contains(objectEntry.GameObjectId);
            entries.Add((
                playerDistance,
                $"[Plugin] op=pot-coffer-debug-object name='{objectEntry.Name}' baseId={objectEntry.BaseId} objectId={objectEntry.GameObjectId:X} kind={objectKind} pos=<{objectEntry.Position.X:0.0}, {objectEntry.Position.Y:0.0}, {objectEntry.Position.Z:0.0}> playerDistance={playerDistance:0.0}y candidateDistance={(float.IsNaN(candidateDistance) ? "n/a" : $"{candidateDistance:0.0}y")} targetable={objectEntry.IsTargetable} valid={objectEntry.IsValid()} recognized={recognized} recognition={recognitionSource} recognizedAsPotReveal={recognizedAsPotReveal} potRevealRecognition={potRevealRecognitionSource} recognizedByTreasureKind={recognizedByTreasureKind} includedInVisibleScan={includedInVisibleScan}"));
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

    internal void LogTargetedRevealCofferDebug()
    {
        var snapshot = Scanner.Snapshot;
        var territory = Scanner.ActiveTerritoryData;
        var target = TargetManager.Target;
        Logger.Info($"[Plugin] op=debug-targeted-reveal-coffer territory={ClientState.TerritoryType} territoryKey={snapshot.TerritoryKey} supported={snapshot.IsInSupportedTerritory} targetAvailable={target != null}");

        if (target == null)
        {
            Logger.Info("[Plugin] op=debug-targeted-reveal-coffer-result available=false reason=no-target");
            ChatGui.Print("Targeted reveal coffer dump: no current target.");
            return;
        }

        var visibleRecognitionSource = string.Empty;
        var potRecognitionSource = string.Empty;
        var visibleRecognition = territory != null
            && CofferRecognition.TryRecognize(territory.VisibleCoffers, target, out visibleRecognitionSource);
        var potRecognition = territory != null
            && CofferRecognition.TryRecognizePotReveal(territory.VisibleCoffers, target, out potRecognitionSource);
        var playerPosition = ObjectTable.LocalPlayer?.Position;
        var distance = playerPosition.HasValue ? CalculateFlatDistance(playerPosition.Value, target.Position) : float.NaN;
        var vendorMatch = territory?.Shopping.Vendors.FirstOrDefault(vendor => vendor.DataId == target.BaseId);

        Logger.Info(
            $"[Plugin] op=debug-targeted-reveal-coffer-result available=true name='{target.Name}' kind={target.ObjectKind} baseId={target.BaseId} objectId={target.GameObjectId:X} pos=<{target.Position.X:0.0},{target.Position.Y:0.0},{target.Position.Z:0.0}> playerDistance={(float.IsNaN(distance) ? "n/a" : $"{distance:0.0}y")} valid={target.IsValid()} targetable={target.IsTargetable} visibleRecognition={visibleRecognition} visibleRecognitionSource={visibleRecognitionSource} potRecognition={potRecognition} potRecognitionSource={potRecognitionSource} vendorBaseIdMatch={(vendorMatch != null)} activeCandidate={TreasureSearchController.ActiveCandidateKey?.Label ?? "none"}");
        ChatGui.Print($"Targeted reveal coffer dump logged for {target.Name} ({target.BaseId}).");
    }

    internal void LogVisibleCoffersDebug()
    {
        var snapshot = Scanner.Snapshot;
        var territory = Scanner.ActiveTerritoryData;
        Logger.Info($"[Plugin] op=debug-visible-coffers territory={ClientState.TerritoryType} territoryKey={snapshot.TerritoryKey} supported={snapshot.IsInSupportedTerritory} scannerCount={snapshot.VisibleCoffers.Count}");

        foreach (var coffer in snapshot.VisibleCoffers)
        {
            Logger.Info($"[Plugin] op=debug-visible-coffer source=scanner name='{coffer.Name}' kind={coffer.ObjectKind} baseId={coffer.DataId} objectId={coffer.GameObjectId:X} pos=<{coffer.Position.X:0.0},{coffer.Position.Y:0.0},{coffer.Position.Z:0.0}> playerDistance={coffer.DistanceToPlayer:0.0}y recognition={coffer.RecognitionSource} targetable={coffer.IsTargetable}");
        }

        if (territory == null)
        {
            Logger.Info("[Plugin] op=debug-visible-coffers-raw skipped=true reason=unsupported-territory");
            ChatGui.Print("Visible coffer dump logged; current territory has no catalog data.");
            return;
        }

        var playerPosition = ObjectTable.LocalPlayer?.Position;
        var rawCount = 0;
        foreach (var objectEntry in ObjectTable)
        {
            if (objectEntry == null || !objectEntry.IsValid())
            {
                continue;
            }

            var visibleRecognitionSource = string.Empty;
            var potRecognitionSource = string.Empty;
            var recognizedVisible = CofferRecognition.TryRecognize(territory.VisibleCoffers, objectEntry, out visibleRecognitionSource);
            var recognizedPot = CofferRecognition.TryRecognizePotReveal(territory.VisibleCoffers, objectEntry, out potRecognitionSource);
            if (!recognizedVisible && !recognizedPot)
            {
                continue;
            }

            rawCount++;
            var distance = playerPosition.HasValue ? CalculateFlatDistance(playerPosition.Value, objectEntry.Position) : float.NaN;
            Logger.Info($"[Plugin] op=debug-visible-coffer-raw name='{objectEntry.Name}' kind={objectEntry.ObjectKind} baseId={objectEntry.BaseId} objectId={objectEntry.GameObjectId:X} pos=<{objectEntry.Position.X:0.0},{objectEntry.Position.Y:0.0},{objectEntry.Position.Z:0.0}> playerDistance={(float.IsNaN(distance) ? "n/a" : $"{distance:0.0}y")} valid={objectEntry.IsValid()} targetable={objectEntry.IsTargetable} visibleRecognition={recognizedVisible} visibleRecognitionSource={visibleRecognitionSource} potRecognition={recognizedPot} potRecognitionSource={potRecognitionSource}");
        }

        Logger.Info($"[Plugin] op=debug-visible-coffers-raw-summary entries={rawCount}");
        ChatGui.Print($"Visible coffer dump logged: scanner={snapshot.VisibleCoffers.Count}, rawRecognized={rawCount}.");
    }

    internal void LogTargetedShopNpcDebug()
    {
        var snapshot = Scanner.Snapshot;
        var territory = Scanner.ActiveTerritoryData;
        var target = TargetManager.Target;
        Logger.Info($"[Plugin] op=debug-targeted-shop-npc territory={ClientState.TerritoryType} territoryKey={snapshot.TerritoryKey} supported={snapshot.IsInSupportedTerritory} targetAvailable={target != null}");

        if (target == null)
        {
            Logger.Info("[Plugin] op=debug-targeted-shop-npc-result available=false reason=no-target");
            ChatGui.Print("Targeted shop NPC dump: no current target.");
            return;
        }

        var playerPosition = ObjectTable.LocalPlayer?.Position;
        var distance = playerPosition.HasValue ? CalculateFlatDistance(playerPosition.Value, target.Position) : float.NaN;
        var vendor = territory?.Shopping.Vendors.FirstOrDefault(definition => definition.DataId == target.BaseId);
        var isEventNpc = string.Equals(target.ObjectKind.ToString(), "EventNpc", StringComparison.OrdinalIgnoreCase);

        Logger.Info(
            $"[Plugin] op=debug-targeted-shop-npc-result available=true name='{target.Name}' kind={target.ObjectKind} isEventNpc={isEventNpc} baseId={target.BaseId} objectId={target.GameObjectId:X} pos=<{target.Position.X:0.0},{target.Position.Y:0.0},{target.Position.Z:0.0}> playerDistance={(float.IsNaN(distance) ? "n/a" : $"{distance:0.0}y")} valid={target.IsValid()} targetable={target.IsTargetable} vendorMatch={(vendor != null)} configuredVendorName={vendor?.Name ?? "none"} configuredVendorDataId={vendor?.DataId.ToString() ?? "none"} preferredAethernet={vendor?.PreferredAethernet ?? "none"}");
        ChatGui.Print($"Targeted shop NPC dump logged for {target.Name} ({target.BaseId}).");
    }

    internal void LogConfiguredEventTablesDebug()
    {
        Logger.Info($"[Plugin] op=debug-configured-event-tables territories={OccultCrescentData.Territories.Count}");
        foreach (var territory in OccultCrescentData.Territories)
        {
            Logger.Info($"[Plugin] op=debug-configured-event-territory key={territory.Key} territoryId={territory.TerritoryTypeId} fates={territory.Fates.Count} criticalEncounters={territory.CriticalEncounters.Count}");
            foreach (var fate in territory.Fates)
            {
                var enabled = Configuration.IsFateEnabled(territory.Key, fate.Id);
                var isPotFate = territory.PotFates.Any(potFate => potFate.FateId == fate.Id);
                Logger.Info($"[Plugin] op=debug-configured-fate territoryKey={territory.Key} id={fate.Id} name='{fate.Name}' enabled={enabled} isPotFate={isPotFate} demiatma={fate.Demiatma ?? "none"} aethernet={fate.Aethernet ?? "none"} startPos=<{fate.StartPosition.X:0.0},{fate.StartPosition.Y:0.0},{fate.StartPosition.Z:0.0}>");
            }

            foreach (var encounter in territory.CriticalEncounters)
            {
                var enabled = Configuration.IsCriticalEncounterEnabled(territory.Key, encounter.Id);
                Logger.Info($"[Plugin] op=debug-configured-ce territoryKey={territory.Key} id={encounter.Id} name='{encounter.Name}' enabled={enabled} priority={encounter.Priority} engageRadius={encounter.EngageRadius:0.0} aethernet={encounter.PreferredAethernet} stagingPos=<{encounter.StagingPoint.X:0.0},{encounter.StagingPoint.Y:0.0},{encounter.StagingPoint.Z:0.0}>");
            }
        }

        ChatGui.Print($"Configured FATE/CE tables logged for {OccultCrescentData.Territories.Count} territories.");
    }

    internal unsafe void LogLoadedLgbTreasuresDebug()
    {
        Logger.Info($"[Plugin] op=debug-lgb-treasures territory={ClientState.TerritoryType} scan-start");

        var layoutWorld = LayoutWorld.Instance();
        if (layoutWorld == null)
        {
            Logger.Warning("[Plugin] op=debug-lgb-treasures-result available=false reason=layout-world-unavailable");
            return;
        }

        var activeLayout = layoutWorld->ActiveLayout;
        if (activeLayout == null)
        {
            Logger.Warning("[Plugin] op=debug-lgb-treasures-result available=false reason=active-layout-unavailable");
            return;
        }

        var treasureCount = 0;
        foreach (var layer in activeLayout->Layers.Values)
        {
            if (layer.IsNull)
            {
                continue;
            }

            foreach (var pair in layer.Value->Instances)
            {
                var instance = pair.Item2.Value;
                if (instance == null || instance->Id.Type != InstanceType.Treasure)
                {
                    continue;
                }

                var position = instance->GetTransformImpl()->Translation;
                treasureCount++;
                Logger.Info(
                    $"[Plugin] op=debug-lgb-treasure instanceKey={pair.Item1} layerKey={instance->Id.LayerKey} " +
                    $"position=<{position.X:0.000},{position.Y:0.000},{position.Z:0.000}>");
            }
        }

        Logger.Info($"[Plugin] op=debug-lgb-treasures-result available=true count={treasureCount}");
        ChatGui.Print($"Loaded LGB treasure scan complete: {treasureCount} entries logged.");
    }

    internal unsafe void CaptureNearestNorthHornLgbDebug(string area)
    {
        if (ClientState.TerritoryType != NorthHornTerritoryId)
        {
            Logger.Warning($"[North Horn LGB Capture] capture-failed reason=wrong-territory territory={ClientState.TerritoryType} expected={NorthHornTerritoryId}");
            return;
        }

        var player = ObjectTable.LocalPlayer;
        if (player == null)
        {
            Logger.Warning("[North Horn LGB Capture] capture-failed reason=player-unavailable");
            return;
        }

        var layoutWorld = LayoutWorld.Instance();
        var activeLayout = layoutWorld == null ? null : layoutWorld->ActiveLayout;
        if (activeLayout == null)
        {
            Logger.Warning("[North Horn LGB Capture] capture-failed reason=active-layout-unavailable");
            return;
        }

        var playerPosition = player.Position;
        var nearestInstanceKey = string.Empty;
        var nearestPosition = Vector3.Zero;
        var nearestDistance = float.MaxValue;

        foreach (var layer in activeLayout->Layers.Values)
        {
            if (layer.IsNull)
            {
                continue;
            }

            foreach (var pair in layer.Value->Instances)
            {
                var instance = pair.Item2.Value;
                if (instance == null
                    || instance->Id.Type != InstanceType.Treasure
                    || instance->Id.LayerKey != NorthHornLgbTreasureLayer)
                {
                    continue;
                }

                var position = instance->GetTransformImpl()->Translation;
                var distance = CalculateFlatDistance(playerPosition, position);
                if (distance >= nearestDistance)
                {
                    continue;
                }

                nearestInstanceKey = pair.Item1.ToString();
                nearestPosition = position;
                nearestDistance = distance;
            }
        }

        if (nearestDistance == float.MaxValue)
        {
            Logger.Warning($"[North Horn LGB Capture] capture-failed reason=no-treasure-layer layer={NorthHornLgbTreasureLayer}");
            return;
        }

        var hasThreat = Scanner.TryGetNearestForayThreat(playerPosition, NorthHornLgbCaptureForayScanRadius, out var threat);
        var escapedArea = (area ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
        Logger.Info(
            $"[North Horn LGB Capture] capture {{ area=\"{escapedArea}\", layer={NorthHornLgbTreasureLayer}, instance={nearestInstanceKey}, " +
            $"cofferPosition={{x={playerPosition.X:0.000},y={playerPosition.Y:0.000},z={playerPosition.Z:0.000}}}, " +
            $"lgbPosition={{x={nearestPosition.X:0.000},y={nearestPosition.Y:0.000},z={nearestPosition.Z:0.000}}}, " +
            $"offset={CalculateDistance(playerPosition, nearestPosition):0.000}, aggroLevel={(hasThreat ? threat.KnowledgeLevel : 0)}, " +
            $"nearestEntity=\"{(hasThreat ? EscapeLogValue(threat.Name) : string.Empty)}\", " +
            $"nearestEntityDistance={(hasThreat ? threat.DistanceToPlayer : -1f):0.000}, " +
            $"nearestEntityDataId={(hasThreat ? threat.BaseId : 0)}, " +
            $"nearestEntityGameObjectId={(hasThreat ? threat.ObjectId : 0)} }},");
    }

    internal void CaptureNorthHornRevealCandidateDebug(string region, string label)
    {
        if (ClientState.TerritoryType != NorthHornTerritoryId)
        {
            Logger.Warning($"[North Horn Reveal Capture] capture-failed reason=wrong-territory territory={ClientState.TerritoryType} expected={NorthHornTerritoryId}");
            ChatGui.Print("North Horn reveal capture requires North Horn.");
            return;
        }

        var player = ObjectTable.LocalPlayer;
        if (player == null)
        {
            Logger.Warning("[North Horn Reveal Capture] capture-failed reason=player-unavailable");
            ChatGui.Print("North Horn reveal capture failed: player unavailable.");
            return;
        }

        var trimmedRegion = (region ?? string.Empty).Trim();
        var trimmedLabel = (label ?? string.Empty).Trim();
        if (trimmedRegion.Length == 0 || trimmedLabel.Length == 0)
        {
            Logger.Warning($"[North Horn Reveal Capture] capture-failed reason=missing-label-or-region region=\"{EscapeLogValue(trimmedRegion)}\" label=\"{EscapeLogValue(trimmedLabel)}\"");
            ChatGui.Print("North Horn reveal capture requires both a region and label.");
            return;
        }

        var playerPosition = player.Position;
        var hasThreat = Scanner.TryGetNearestForayThreat(playerPosition, NorthHornRevealCaptureForayScanRadius, out var threat);
        var escapedRegion = EscapeLogValue(trimmedRegion);
        var escapedLabel = EscapeLogValue(trimmedLabel);
        var playerPositionText = $"{{x={playerPosition.X:0.000},y={playerPosition.Y:0.000},z={playerPosition.Z:0.000}}}";

        Logger.Info(
            $"[North Horn Reveal Capture] capture {{ label=\"{escapedLabel}\", region=\"{escapedRegion}\", " +
            $"territory={ClientState.TerritoryType}, playerPosition={playerPositionText}, " +
            $"aggroLevel={(hasThreat ? threat.KnowledgeLevel.ToString() : "unknown")}, " +
            $"nearestEntity=\"{(hasThreat ? EscapeLogValue(threat.Name) : string.Empty)}\", " +
            $"nearestEntityDistance={(hasThreat ? threat.DistanceToPlayer : -1f):0.000}, " +
            $"nearestEntityDataId={(hasThreat ? threat.BaseId : 0)}, " +
            $"nearestEntityGameObjectId={(hasThreat ? threat.ObjectId : 0)}, " +
            $"timestamp=\"{DateTimeOffset.UtcNow:O}\" }},");
        ChatGui.Print($"North Horn reveal candidate captured: {trimmedLabel} ({trimmedRegion}).");
    }

    private static float CalculateDistance(Vector3 left, Vector3 right)
    {
        var delta = left - right;
        return delta.Length();
    }

    private static string EscapeLogValue(string value)
        => (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");

    internal unsafe void LogLoadedLgbRevealCoffersDebug()
    {
        var territory = Scanner.ActiveTerritoryData;
        var revealBaseIds = territory?.VisibleCoffers.BaseIds.ToHashSet() ?? [];
        Logger.Info(
            $"[Plugin] op=debug-lgb-reveal-coffers territory={ClientState.TerritoryType} " +
            $"baseIds=[{string.Join(",", revealBaseIds.OrderBy(id => id))}] scan-start");

        if (revealBaseIds.Count == 0)
        {
            Logger.Warning("[Plugin] op=debug-lgb-reveal-coffers-result available=false reason=no-configured-base-ids");
            ChatGui.Print("Loaded LGB reveal-coffer scan skipped: no configured BaseIds.");
            return;
        }

        var layoutWorld = LayoutWorld.Instance();
        if (layoutWorld == null)
        {
            Logger.Warning("[Plugin] op=debug-lgb-reveal-coffers-result available=false reason=layout-world-unavailable");
            return;
        }

        var activeLayout = layoutWorld->ActiveLayout;
        if (activeLayout == null)
        {
            Logger.Warning("[Plugin] op=debug-lgb-reveal-coffers-result available=false reason=active-layout-unavailable");
            return;
        }

        var revealCofferCount = 0;
        foreach (var layer in activeLayout->Layers.Values)
        {
            if (layer.IsNull)
            {
                continue;
            }

            foreach (var pair in layer.Value->Instances)
            {
                var instance = pair.Item2.Value;
                if (instance == null || instance->Id.Type != InstanceType.EventObject)
                {
                    continue;
                }

                var gameObjectInstance = (GameObjectLayoutInstance*)instance;
                if (!revealBaseIds.Contains(gameObjectInstance->BaseId))
                {
                    continue;
                }

                var position = instance->GetTransformImpl()->Translation;
                revealCofferCount++;
                Logger.Info(
                    $"[Plugin] op=debug-lgb-reveal-coffer instanceKey={pair.Item1} " +
                    $"layerKey={instance->Id.LayerKey} baseId={gameObjectInstance->BaseId} " +
                    $"position=<{position.X:0.000},{position.Y:0.000},{position.Z:0.000}>");
            }
        }

        Logger.Info($"[Plugin] op=debug-lgb-reveal-coffers-result available=true count={revealCofferCount}");
        ChatGui.Print($"Loaded LGB reveal-coffer scan complete: {revealCofferCount} entries logged.");
    }

    internal unsafe void LogLoadedLgbEventRangesDebug()
    {
        Logger.Info($"[Plugin] op=debug-lgb-event-ranges territory={ClientState.TerritoryType} scan-start");

        var layoutWorld = LayoutWorld.Instance();
        if (layoutWorld == null)
        {
            Logger.Warning("[Plugin] op=debug-lgb-event-ranges-result available=false reason=layout-world-unavailable");
            return;
        }

        var activeLayout = layoutWorld->ActiveLayout;
        if (activeLayout == null)
        {
            Logger.Warning("[Plugin] op=debug-lgb-event-ranges-result available=false reason=active-layout-unavailable");
            return;
        }

        var eventRangeCount = 0;
        foreach (var layer in activeLayout->Layers.Values)
        {
            if (layer.IsNull)
            {
                continue;
            }

            foreach (var pair in layer.Value->Instances)
            {
                var instance = pair.Item2.Value;
                if (instance == null || instance->Id.Type != InstanceType.EventRange)
                {
                    continue;
                }

                var position = instance->GetTransformImpl()->Translation;
                eventRangeCount++;
                Logger.Info(
                    $"[Plugin] op=debug-lgb-event-range instanceKey={pair.Item1} " +
                    $"layerKey={instance->Id.LayerKey} " +
                    $"position=<{position.X:0.000},{position.Y:0.000},{position.Z:0.000}>");
            }
        }

        Logger.Info($"[Plugin] op=debug-lgb-event-ranges-result available=true count={eventRangeCount}");
        ChatGui.Print($"Loaded LGB EventRange scan complete: {eventRangeCount} entries logged.");
    }

    internal unsafe void LogLoadedLgbEventObjectsDebug()
    {
        Logger.Info($"[Plugin] op=debug-lgb-event-objects territory={ClientState.TerritoryType} scan-start");

        var layoutWorld = LayoutWorld.Instance();
        if (layoutWorld == null)
        {
            Logger.Warning("[Plugin] op=debug-lgb-event-objects-result available=false reason=layout-world-unavailable");
            return;
        }

        var activeLayout = layoutWorld->ActiveLayout;
        if (activeLayout == null)
        {
            Logger.Warning("[Plugin] op=debug-lgb-event-objects-result available=false reason=active-layout-unavailable");
            return;
        }

        var eventObjectCount = 0;
        foreach (var layer in activeLayout->Layers.Values)
        {
            if (layer.IsNull)
            {
                continue;
            }

            foreach (var pair in layer.Value->Instances)
            {
                var instance = pair.Item2.Value;
                if (instance == null || instance->Id.Type != InstanceType.EventObject)
                {
                    continue;
                }

                var gameObjectInstance = (GameObjectLayoutInstance*)instance;
                var position = instance->GetTransformImpl()->Translation;
                eventObjectCount++;
                Logger.Info(
                    $"[Plugin] op=debug-lgb-event-object instanceKey={pair.Item1} " +
                    $"layerKey={instance->Id.LayerKey} baseId={gameObjectInstance->BaseId} " +
                    $"position=<{position.X:0.000},{position.Y:0.000},{position.Z:0.000}>");
            }
        }

        Logger.Info($"[Plugin] op=debug-lgb-event-objects-result available=true count={eventObjectCount}");
        ChatGui.Print($"Loaded LGB EventObject scan complete: {eventObjectCount} entries logged.");
    }

    internal void GenerateEventDataDebug()
    {
        var snapshot = Scanner.Snapshot;
        var territory = Scanner.ActiveTerritoryData;
        var playerPosition = ObjectTable.LocalPlayer?.Position;
        var fates = snapshot.Fates
            .Select(fate =>
            {
                var metadata = territory?.Fates.FirstOrDefault(knownFate => knownFate.Id == fate.Id);
                return new
                {
                    id = fate.Id,
                    name = fate.Name,
                    demiatma = metadata?.Demiatma,
                    note = metadata?.Note,
                    aethernet = metadata?.Aethernet,
                    startPosition = CreatePosition(fate.Position),
                };
            })
            .ToArray();
        var criticalEncounters = snapshot.CriticalEncounters
            .Concat(snapshot.UnknownCriticalEncounters)
            .Select(encounter =>
            {
                var metadata = territory?.CriticalEncounters.FirstOrDefault(knownEncounter => knownEncounter.Id == encounter.Id);
                return new
                {
                    id = encounter.Id,
                    name = encounter.Name,
                    preferredAethernet = metadata?.PreferredAethernet ?? string.Empty,
                    priority = metadata?.Priority > 0 ? metadata.Priority : 100,
                    engageRadius = metadata?.EngageRadius > 0 ? metadata.EngageRadius : 20f,
                    stagingPoint = playerPosition.HasValue
                        ? CreatePosition(playerPosition.Value)
                        : null,
                };
            })
            .ToArray();
        var json = JsonSerializer.Serialize(new
        {
            fates,
            criticalEncounters,
        }, new JsonSerializerOptions { WriteIndented = true });

        Logger.Info($"[Plugin] op=debug-event-data territory={ClientState.TerritoryType} territoryKey={snapshot.TerritoryKey} fates={fates.Length} potFates={snapshot.PotFates.Count} ces={criticalEncounters.Length} playerPosition={(playerPosition.HasValue ? FormatVector3(playerPosition.Value) : "unavailable")}");
        Logger.Info("[Plugin] op=debug-event-data-json-begin");
        foreach (var line in json.Split('\n'))
        {
            Logger.Info($"[Plugin] op=debug-event-data-json {line.TrimEnd('\r')}");
        }
        Logger.Info("[Plugin] op=debug-event-data-json-end");
        ChatGui.Print($"Event data generated: fates={fates.Length}, CEs={criticalEncounters.Length}. See the log for paste-ready JSON.");
    }

    private static object CreatePosition(Vector3 position)
        => new { x = position.X, y = position.Y, z = position.Z };

    private static string FormatVector3(Vector3 position)
        => $"<{position.X:0.000}, {position.Y:0.000}, {position.Z:0.000}>";

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

        if (territoryChanged && string.Equals(newTerritoryKey, "northHorn", StringComparison.OrdinalIgnoreCase))
        {
            if (NorthHornStatusWindow.ShouldOpenAutomatically)
            {
                NorthHornStatusWindow.Open(debugPreview: false);
                Logger.Info($"[Plugin] op=north-horn-status-auto-open state=open territoryId={territoryType} revision={NorthHornStatusWindow.CurrentStatusRevision}");
            }
        }
        else if (!string.Equals(newTerritoryKey, "northHorn", StringComparison.OrdinalIgnoreCase)
                 && NorthHornStatusWindow.IsOpen
                 && !NorthHornStatusWindow.IsDebugPreview)
        {
            NorthHornStatusWindow.Close();
            Logger.Info($"[Plugin] op=north-horn-status-auto-open state=closed territory={territoryType}");
        }

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

        if (!string.IsNullOrWhiteSpace(previousTerritoryKey))
        {
            var previousStartingPotFateId = Configuration.GetStartingPotFateId(previousTerritoryKey);
            if (previousStartingPotFateId != 0 && Configuration.SetStartingPotFateId(previousTerritoryKey, 0))
            {
                Logger.Info($"[Plugin] op=setting-change key=StartingPotFate territoryKey={previousTerritoryKey} old={previousStartingPotFateId} new=0 reason=territory-change territory={territoryType} previous={previousKey} next={nextKey}");
                Configuration.Save();
            }
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
