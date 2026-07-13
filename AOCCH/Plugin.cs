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
    private static readonly HashSet<uint> KnownTreasureCofferBaseIds = [2014741u, 2014742u, 2014743u];
    private const float PotCofferDebugRadius = 60f;
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
    public AutomaticTreasureCofferDebugController AutomaticTreasureCofferDebugController { get; init; }

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
            Logger.Info("[Plugin] op=config-migrated");
        }

        OccultCrescentNameResolver = new OccultCrescentNameResolver(DataManager, OccultCrescentData, Logger);
        CofferNameResolver = new CofferNameResolver(DataManager, [2014741u, 2014742u, 2014743u], Logger);
        Scanner = new OccultCrescentScanner(ClientState, FateTable, Framework, ObjectTable, OccultCrescentData, Configuration, CofferNameResolver, Logger);
        VNavmesh = new VNavmeshIpc(Logger);
        Lifestream = new LifestreamIpc(Logger);
        BossMod = new BossModIpc(Logger);
        RoutePlanner = new RoutePlanner(OccultCrescentData, Configuration, Logger);
        GameActionController = new GameActionController(CommandManager, Condition, ObjectTable, PlayerState, TargetManager, Logger);
        MovementController = new MovementController(Framework, Condition, ObjectTable, GameGui, Scanner, VNavmesh, Lifestream, RoutePlanner, GameActionController, Configuration, OccultCrescentData, Logger);
        DangerousTreasureTravelController = new DangerousTreasureTravelController(Framework, Condition, ObjectTable, MovementController, GameActionController, Configuration, Logger);
        AutorotationController = new AutorotationController(BossMod, Configuration, Logger);
        BuffRotationController = new BuffRotationController(Framework, Condition, ObjectTable, Scanner, MovementController, GameActionController, Configuration, Logger);
        CriticalEngagementAutomationController = new CriticalEngagementAutomationController(Framework, Condition, ObjectTable, Scanner, MovementController, AutorotationController, Configuration, Logger);
        FateAutomationController = new FateAutomationController(Framework, Condition, ObjectTable, Scanner, MovementController, AutorotationController, Configuration, Logger);
        DeathRecoveryController = new DeathRecoveryController(Framework, ObjectTable, GameGui, MovementController, AutorotationController, BuffRotationController, CriticalEngagementAutomationController, FateAutomationController, Logger);
        InstancedContentController = new InstancedContentController(Logger);
        PotCycleTracker = new PotCycleTracker(Framework, Scanner, OccultCrescentData, Logger);
        TreasureHintTracker = new TreasureHintTracker(Framework, ChatGui, Scanner, Logger);
        TreasureSearchController = new TreasureSearchController(Framework, Scanner, MovementController, GameActionController, TreasureHintTracker, DangerousTreasureTravelController, CofferNameResolver, OccultCrescentData, CofferPositionOverrideStore, Configuration, Logger);
        CofferInteractionController = new CofferInteractionController(Framework, ObjectTable, Scanner, MovementController, GameActionController, CofferPositionOverrideStore, Logger);
        TreasureCofferFarmController = new TreasureCofferFarmController(Framework, ObjectTable, Scanner, MovementController, DeathRecoveryController, DangerousTreasureTravelController, CofferInteractionController, OccultCrescentData, VisibleCofferPositionOverrideStore, Configuration, Logger);
        PotFallbackWindowEvaluator = new PotFallbackWindowEvaluator(Configuration, Logger);
        PotInstanceTimeEvaluator = new PotInstanceTimeEvaluator(Configuration, Logger);
        PotFarmController = new PotFarmController(Framework, Scanner, MovementController, GameActionController, FateAutomationController, DeathRecoveryController, InstancedContentController, PotCycleTracker, TreasureHintTracker, TreasureSearchController, CofferInteractionController, DangerousTreasureTravelController, PotInstanceTimeEvaluator, OccultCrescentData, Configuration, Logger);
        FarmSessionController = new FarmSessionController(Framework, Scanner, VNavmesh, Lifestream, MovementController, GameActionController, AutorotationController, BuffRotationController, CriticalEngagementAutomationController, FateAutomationController, DeathRecoveryController, PotCycleTracker, PotFallbackWindowEvaluator, PotFarmController, TreasureHintTracker, TreasureCofferFarmController, Configuration, Logger);
        AutomaticTreasureCofferDebugController = new AutomaticTreasureCofferDebugController(Framework, Scanner, GameActionController, DeathRecoveryController, TreasureHintTracker, Configuration, Logger);

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
            HelpMessage = "Open AOCCH. Args: main, debug, config, log, start, stop, coffer-start, coffer-stop, panic, testkeyitem, debug-potcoffer, debug-autocoffer, help."
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

        WindowSystem.RemoveAllWindows();

        ConfigWindow.Dispose();
        LogWindow.Dispose();
        MainWindow.Dispose();
        DebugWindow.Dispose();
        AutomaticTreasureCofferDebugController.Dispose();
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
    }

    private void OnCommand(string command, string args)
    {
        if (isDisposing)
        {
            return;
        }

        var normalizedArgs = args.Trim().ToLowerInvariant();
        Logger.Info($"[Plugin] op=slash-command command=\"{command}\" args=\"{args.Trim()}\"");

        switch (normalizedArgs)
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
            case "help":
                Logger.Info("[Plugin] op=slash-command-action action=show-help");
                PrintCommandHelp();
                break;
            case "start":
                Logger.Info("[Plugin] op=slash-command-action action=start-farm-session");
                FarmSessionController.Start();
                break;
            case "stop":
                Logger.Info("[Plugin] op=slash-command-action action=stop-farm-session");
                FarmSessionController.Stop("Slash command stop requested.");
                break;
            case "coffer-start":
                if (FarmSessionController.IsRunning)
                {
                    Logger.Warning("[Plugin] op=slash-command-action-blocked action=start-visible-coffer-farm reason=farm-session-running");
                    ChatGui.Print("Visible coffer route start is blocked while the farm session is running.");
                    break;
                }

                Logger.Info("[Plugin] op=slash-command-action action=start-visible-coffer-farm");
                TreasureCofferFarmController.Start();
                break;
            case "coffer-stop":
                Logger.Info("[Plugin] op=slash-command-action action=stop-visible-coffer-farm");
                TreasureCofferFarmController.Stop("Slash command visible coffer stop requested.");
                break;
            case "panic":
                Logger.Warning("[Plugin] op=slash-command-action action=panic-stop");
                PanicStopAll();
                break;
            case "debug-potcoffer":
                Logger.Info("[Plugin] op=slash-command-action action=debug-potcoffer");
                LogPotCofferDebugSnapshot();
                break;
            case "debug-autocoffer":
                HandleDebugAutomaticCofferCommand();
                break;
            default:
                if (normalizedArgs.StartsWith("testkeyitem", StringComparison.Ordinal))
                {
                    HandleTestKeyItemCommand(args);
                    break;
                }

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
        ChatGui.Print("/aocch debug - Toggle debug window");
        ChatGui.Print("/aocch config - Toggle config window");
        ChatGui.Print("/aocch log - Toggle log window");
        ChatGui.Print("/aocch start - Start unified CE/FATE farm session");
        ChatGui.Print("/aocch stop - Stop unified CE/FATE farm session");
        ChatGui.Print("/aocch coffer-start - Start visible coffer farm route");
        ChatGui.Print("/aocch coffer-stop - Stop visible coffer farm route");
        ChatGui.Print("/aocch panic - Panic stop all farm activity");
        ChatGui.Print("/aocch testkeyitem [wait] - Use Magical Elixir via the production inventory path with detailed treasure logs");
        ChatGui.Print("/aocch debug-potcoffer - Log nearby raw objects for pot treasure reveal debugging");
        ChatGui.Print("/aocch debug-autocoffer - Run the automatic coffer survey flow without starting the coffer route");
        ChatGui.Print("/aocch help - Show this help");
    }

    private void HandleDebugAutomaticCofferCommand()
    {
        if (AutomaticTreasureCofferDebugController.IsRunning)
        {
            Logger.Warning("[Plugin] op=slash-command-action-blocked action=debug-autocoffer reason=already-running");
            ChatGui.Print("Automatic coffer debug survey is already running.");
            return;
        }

        if (FarmSessionController.IsRunning || TreasureCofferFarmController.IsRunning || BuffRotationController.IsRunning || CriticalEngagementAutomationController.IsRunning || FateAutomationController.IsRunning || PotFarmController.IsRunning)
        {
            Logger.Warning("[Plugin] op=slash-command-action-blocked action=debug-autocoffer reason=conflicting-automation");
            ChatGui.Print("Automatic coffer debug survey requires the farm session, visible coffer routing, CE/FATE automation, pot control, and buff rotation to be stopped.");
            return;
        }

        if (!AutomaticTreasureCofferDebugController.Start())
        {
            Logger.Warning($"[Plugin] op=slash-command-action-blocked action=debug-autocoffer reason=controller-start-failed detail=\"{AutomaticTreasureCofferDebugController.LastTransition}\"");
            ChatGui.Print(AutomaticTreasureCofferDebugController.LastTransition);
            return;
        }

        Logger.Info("[Plugin] op=slash-command-action action=debug-autocoffer");
    }

    private void HandleTestKeyItemCommand(string rawArgs)
    {
        var tokens = rawArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var waitForReady = false;

        TreasureHintTracker.ClearDebugLogMessageCapture();

        for (var i = 1; i < tokens.Length; i++)
        {
            switch (tokens[i].ToLowerInvariant())
            {
                case "wait":
                    waitForReady = true;
                    break;
                default:
                    Logger.Warning($"[Plugin] op=testkeyitem-option-ignored value=\"{tokens[i]}\" reason=inventory-path-only");
                    break;
            }
        }

        Logger.Info($"[Plugin] op=slash-command-action action=test-magical-elixir mode=inventory wait={waitForReady}");
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

        Logger.Info($"[Plugin] op=magical-elixir-debug label={label} time={DateTimeOffset.UtcNow:O} territory={ClientState.TerritoryType} southHorn={snapshot.IsInSouthHorn} playerPos={positionText} hp={player?.CurrentHp ?? 0}");
        Logger.Info($"[Plugin] op=magical-elixir-debug-conditions label={label} inCombat={Condition[ConditionFlag.InCombat]} casting={Condition[ConditionFlag.Casting]} betweenAreas={Condition[ConditionFlag.BetweenAreas]} occupiedInQuestEvent={Condition[ConditionFlag.OccupiedInQuestEvent]} mounted={Condition[ConditionFlag.Mounted]} occupied={Condition[ConditionFlag.Occupied]}");
        Logger.Info($"[Plugin] op=magical-elixir-debug-treasure label={label} buff={snapshot.HasTreasureBuff} remaining={snapshot.TreasureBuffRemainingSeconds:0.0}s");
        Logger.Info($"[Plugin] op=magical-elixir-debug-logmessages label={label} capture={TreasureHintTracker.GetDebugLogMessageCaptureSummary()}");
        Logger.Info($"[Plugin] op=magical-elixir-debug-inventory label={label} state={GameActionController.DescribeMagicalElixirState()}");
    }

    private void LogPotCofferDebugSnapshot()
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

        Logger.Info($"[Plugin] op=pot-coffer-debug time={DateTimeOffset.UtcNow:O} territory={ClientState.TerritoryType} southHorn={snapshot.IsInSouthHorn} playerPos={positionText} activeCandidate={activeCandidate?.Label ?? "none"} candidatePos={candidatePositionText}");
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

        var leavingSouthHorn = wasInSouthHorn && territoryType != SouthHornTerritoryTypeId;
        wasInSouthHorn = territoryType == SouthHornTerritoryTypeId;

        if (territoryType == SouthHornTerritoryTypeId)
        {
            if (!MainWindow.IsOpen)
            {
                MainWindow.IsOpen = true;
                Logger.Info("[Plugin] op=main-window-auto-open state=open reason=south-horn-entry");
            }

            return;
        }

        if (MainWindow.IsOpen)
        {
            MainWindow.IsOpen = false;
            Logger.Info($"[Plugin] op=main-window-auto-open state=closed territory={territoryType}");
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
