using System;
using System.Linq;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.ClientState.Conditions;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Dalamud.Plugin.Services;
using AOCCH.Automation;
using AOCCH.Movement;
using AOCCH.Logging;
using System.Runtime.InteropServices;

namespace AOCCH.Shopping;

public sealed class ManualCurrencyShoppingController : IDisposable
{
    private const uint ExpeditionAntiquarianDataId = 1053614;
    private const uint EnlightenmentSilverPieceItemId = 45043;
    private const uint EnlightenmentGoldPieceItemId = 45044;
    private const float VendorInteractionRange = 3.25f;
    private static readonly TimeSpan NavigationRetryDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan NavigationSettleDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan NavigationLogThrottle = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan AutoStartCooldown = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan VendorDismountRetryDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan VendorDismountTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan VendorMenuOpenTimeout = TimeSpan.FromSeconds(3);
    private const int MaxVendorMenuOpenAttempts = 3;

    private readonly IFramework framework;
    private readonly IGameGui gameGui;
    private readonly ICondition condition;
    private readonly Configuration configuration;
    private readonly GameActionController gameActionController;
    private readonly MovementController movementController;
    private readonly ShopInspectorController shopInspectorController;
    private readonly ShopPurchaseController shopPurchaseController;
    private readonly CurrentCurrencyShopPageMatcher pageMatcher;
    private readonly CriticalEngagementAutomationController criticalEngagementAutomationController;
    private readonly FateAutomationController fateAutomationController;
    private readonly BuffRotationController buffRotationController;
    private readonly PotFarmController potFarmController;
    private readonly TreasureCofferFarmController treasureCofferFarmController;
    private readonly AocchLogger logger;
    private readonly object gate = new();

    private bool isRunning;
    private string status = "Idle";
    private CurrencyShopTarget? activeTarget;
    private int? activeTargetIndex;
    private int activeTargetOriginalAmount;
    private string activeTargetItemName = string.Empty;
    private ShoppingPurchaseIntent activePurchaseIntent;
    private bool purchaseWasInProgress;
    private bool completedAnyPurchases;
    private int completedGroupCount;
    private int completedPurchaseCount;
    private CurrencyShopGroup? desiredGroup;
    private DateTimeOffset nextNavigationAttemptAt = DateTimeOffset.MinValue;
    private DateTimeOffset matchedDesiredGroupAt = DateTimeOffset.MinValue;
    private bool desiredGroupStableLogged;
    private NavigationVerificationSnapshot? lastNavigationVerificationSnapshot;
    private DateTimeOffset lastNavigationVerificationLogAt = DateTimeOffset.MinValue;
    private DateTimeOffset autoStartBlockedUntil = DateTimeOffset.MinValue;
    private string triggerStatus = "Automatic shopping disabled.";
    private ShoppingStopKind lastStopKind;
    private bool vendorDismountPending;
    private DateTimeOffset vendorDismountStartedAt = DateTimeOffset.MinValue;
    private bool vendorMenuOpenPending;
    private DateTimeOffset vendorMenuOpenStartedAt = DateTimeOffset.MinValue;
    private int vendorMenuOpenAttemptCount;
    private bool vendorTravelPending;
    private bool vendorRecoveryAttempted;
    private bool vendorRecoveryPending;

    public ManualCurrencyShoppingController(
        IFramework framework,
        IGameGui gameGui,
        ICondition condition,
        Configuration configuration,
        GameActionController gameActionController,
        MovementController movementController,
        ShopInspectorController shopInspectorController,
        ShopPurchaseController shopPurchaseController,
        CurrentCurrencyShopPageMatcher pageMatcher,
        CriticalEngagementAutomationController criticalEngagementAutomationController,
        FateAutomationController fateAutomationController,
        BuffRotationController buffRotationController,
        PotFarmController potFarmController,
        TreasureCofferFarmController treasureCofferFarmController,
        AocchLogger logger)
    {
        this.framework = framework;
        this.gameGui = gameGui;
        this.condition = condition;
        this.configuration = configuration;
        this.gameActionController = gameActionController;
        this.movementController = movementController;
        this.shopInspectorController = shopInspectorController;
        this.shopPurchaseController = shopPurchaseController;
        this.pageMatcher = pageMatcher;
        this.criticalEngagementAutomationController = criticalEngagementAutomationController;
        this.fateAutomationController = fateAutomationController;
        this.buffRotationController = buffRotationController;
        this.potFarmController = potFarmController;
        this.treasureCofferFarmController = treasureCofferFarmController;
        this.logger = logger;

        framework.Update += OnFrameworkUpdate;
    }

    public bool IsRunning
    {
        get
        {
            lock (gate)
            {
                return isRunning;
            }
        }
    }

    public string Status
    {
        get
        {
            lock (gate)
            {
                return status;
            }
        }
    }

    public int CompletedGroupCount
    {
        get
        {
            lock (gate)
            {
                return completedGroupCount;
            }
        }
    }

    public int CompletedPurchaseCount
    {
        get
        {
            lock (gate)
            {
                return completedPurchaseCount;
            }
        }
    }

    public string CurrentGroupSummary
    {
        get
        {
            lock (gate)
            {
                return desiredGroup == null
                    ? "None"
                    : $"{desiredGroup.Value.MenuLabel} / {desiredGroup.Value.TabLabel}";
            }
        }
    }

    public string CurrentStatusSummary
    {
        get
        {
            lock (gate)
            {
                if (!isRunning)
                {
                    return triggerStatus;
                }

                if (activeTarget == null || activeTargetIndex == null || string.IsNullOrEmpty(activeTargetItemName))
                {
                    return status;
                }

                return activePurchaseIntent switch
                {
                    ShoppingPurchaseIntent.Buy => $"{status} | Buying {activeTargetItemName} {Math.Max(0, activeTargetOriginalAmount - activeTarget.BuyAmount)}/{activeTargetOriginalAmount}",
                    ShoppingPurchaseIntent.Keep => $"{status} | Keeping {activeTargetItemName} {GetItemCount(activeTarget.ItemId)}/{activeTarget.KeepAmount}",
                    ShoppingPurchaseIntent.KeepBuying => $"{status} | Keep Buying {activeTargetItemName}",
                    _ => status,
                };
            }
        }
    }

    public string TriggerStatus
    {
        get
        {
            lock (gate)
            {
                return triggerStatus;
            }
        }
    }

    public ShoppingStopKind LastStopKind
    {
        get
        {
            lock (gate)
            {
                return lastStopKind;
            }
        }
    }

    public bool NeedsControlNow(DateTimeOffset now, bool allowDuringFarmSession, out string reason)
    {
        if (IsRunning)
        {
            reason = Status;
            return true;
        }

        var evaluation = EvaluateAutoStart(allowDuringFarmSession);
        reason = evaluation.Reason;
        return evaluation.ShouldRun;
    }

    public void Dispose()
    {
        framework.Update -= OnFrameworkUpdate;
    }

    public bool Start()
    {
        lock (gate)
        {
            if (isRunning)
            {
                status = "Manual currency shopping is already running.";
                return false;
            }

            isRunning = true;
            status = "Starting manual current-page shopping.";
            activeTarget = null;
            activeTargetIndex = null;
            activeTargetOriginalAmount = 0;
            activeTargetItemName = string.Empty;
            activePurchaseIntent = ShoppingPurchaseIntent.None;
            purchaseWasInProgress = false;
            completedAnyPurchases = false;
            completedGroupCount = 0;
            completedPurchaseCount = 0;
            lastStopKind = ShoppingStopKind.None;
            desiredGroup = null;
            nextNavigationAttemptAt = DateTimeOffset.MinValue;
            matchedDesiredGroupAt = DateTimeOffset.MinValue;
            desiredGroupStableLogged = false;
            lastNavigationVerificationSnapshot = null;
            lastNavigationVerificationLogAt = DateTimeOffset.MinValue;
            vendorDismountPending = false;
            vendorDismountStartedAt = DateTimeOffset.MinValue;
            vendorTravelPending = false;
            vendorMenuOpenPending = false;
            vendorMenuOpenStartedAt = DateTimeOffset.MinValue;
            vendorMenuOpenAttemptCount = 0;
            vendorRecoveryAttempted = false;
            vendorRecoveryPending = false;
        }

        logger.Info("[ManualCurrencyShopping] op=start");
        return true;
    }

    public void Stop(string reason)
        => Stop(ShoppingStopKind.Failed, reason);

    public void StopCompleted(string reason)
        => Stop(ShoppingStopKind.Completed, reason);

    public void StopSkipped(string reason)
        => Stop(ShoppingStopKind.Skipped, reason);

    public void StopFailed(string reason)
        => Stop(ShoppingStopKind.Failed, reason);

    private void Stop(ShoppingStopKind stopKind, string reason)
    {
        lock (gate)
        {
            isRunning = false;
            status = reason;
            activeTarget = null;
            activeTargetIndex = null;
            activeTargetOriginalAmount = 0;
            activeTargetItemName = string.Empty;
            activePurchaseIntent = ShoppingPurchaseIntent.None;
            purchaseWasInProgress = false;
            completedAnyPurchases = false;
            completedGroupCount = 0;
            completedPurchaseCount = 0;
            lastStopKind = stopKind;
            desiredGroup = null;
            nextNavigationAttemptAt = DateTimeOffset.MinValue;
            matchedDesiredGroupAt = DateTimeOffset.MinValue;
            desiredGroupStableLogged = false;
            lastNavigationVerificationSnapshot = null;
            lastNavigationVerificationLogAt = DateTimeOffset.MinValue;
            vendorDismountPending = false;
            vendorDismountStartedAt = DateTimeOffset.MinValue;
            vendorTravelPending = false;
            vendorMenuOpenPending = false;
            vendorMenuOpenStartedAt = DateTimeOffset.MinValue;
            vendorMenuOpenAttemptCount = 0;
            vendorRecoveryAttempted = false;
            vendorRecoveryPending = false;
        }

        autoStartBlockedUntil = DateTimeOffset.UtcNow + AutoStartCooldown;

        logger.Info($"[ManualCurrencyShopping] op=stop reason=\"{reason.Replace("\"", "'")}\"");
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        if (!IsRunning)
        {
            RefreshTriggerStatus(allowDuringFarmSession: false);
            return;
        }

        var snapshot = shopInspectorController.Snapshot;

        CurrencyShopGroup? nextGroup = null;
        if (desiredGroup == null && !TrySelectNextActionableGroup(out nextGroup, out var nextGroupReason))
        {
            StopCompleted(nextGroupReason);
            return;
        }

        desiredGroup ??= nextGroup;

        if (!EnsureDesiredShopContext(snapshot))
        {
            return;
        }

        if (!pageMatcher.TryMatch(snapshot, out var match, out var matchReason) || match == null)
        {
            StopFailed($"Failed: {matchReason}");
            return;
        }

        var matchedPage = match.Page;
        var matchedTab = match.Tab;

        if (desiredGroup != null && (desiredGroup.Value.MenuIndex != matchedPage.MenuIndex || desiredGroup.Value.TabId != matchedTab.TabId))
        {
            if (purchaseWasInProgress || shopPurchaseController.IsBusy)
            {
                StopFailed($"Shopping stopped because the page/tab changed away from the active target group {desiredGroup.Value.MenuLabel} / {desiredGroup.Value.TabLabel}.");
                return;
            }

            if (!EnsureDesiredShopContext(snapshot))
            {
                return;
            }

            return;
        }

        if (purchaseWasInProgress && !shopPurchaseController.IsBusy)
        {
            purchaseWasInProgress = false;
            if (shopPurchaseController.LastStatus.StartsWith("Success:", StringComparison.Ordinal))
            {
                completedAnyPurchases = true;
                OnPurchaseSuccess();
            }
            else if (shopPurchaseController.LastStatus.StartsWith("Failed:", StringComparison.Ordinal))
            {
                StopFailed($"Purchase failed: {shopPurchaseController.LastStatus}");
                return;
            }
        }

        if (shopPurchaseController.IsBusy)
        {
            purchaseWasInProgress = true;
            UpdateStatus($"Running automatic shopping | Waiting for purchase completion on {matchedPage.MenuLabel} / {matchedTab.TabLabel} | groups={completedGroupCount} purchases={completedPurchaseCount}");
            return;
        }

        var reserve = configuration.CurrencyShopReserves.FirstOrDefault(entry => entry.CurrencyItemId == matchedPage.CurrencyItemId)?.ReserveAmount ?? 0;
        var currentCurrency = GetItemCount(matchedPage.CurrencyItemId);
        var availableCurrency = Math.Max(0, (int)currentCurrency - reserve);

        var targetSelection = SelectNextTarget(matchedPage, matchedTab, availableCurrency);
        if (targetSelection == null)
        {
            completedAnyPurchases = completedAnyPurchases || purchaseWasInProgress;
            if (TrySelectNextActionableGroup(out var nextActionableGroup, out var ignoredReason)
                && nextActionableGroup != null
                && (nextActionableGroup.Value.MenuIndex != matchedPage.MenuIndex || nextActionableGroup.Value.TabId != matchedTab.TabId))
            {
                desiredGroup = nextActionableGroup;
                nextNavigationAttemptAt = DateTimeOffset.MinValue;
                matchedDesiredGroupAt = DateTimeOffset.MinValue;
                desiredGroupStableLogged = false;
                lastNavigationVerificationSnapshot = null;
                lastNavigationVerificationLogAt = DateTimeOffset.MinValue;
                completedGroupCount++;
                UpdateStatus($"Running automatic shopping | Navigating to {nextActionableGroup.Value.MenuLabel} / {nextActionableGroup.Value.TabLabel} | groups={completedGroupCount} purchases={completedPurchaseCount}");
                return;
            }

            if (availableCurrency <= 0)
            {
                StopCompleted(completedAnyPurchases
                    ? $"Completed automatic shopping run. groups={completedGroupCount + 1} purchases={completedPurchaseCount}. Remaining targets are blocked by reserve settings."
                    : $"Currency reserve prevents further purchases on {matchedPage.MenuLabel} / {matchedTab.TabLabel}.");
            }
            else
            {
                StopCompleted(completedAnyPurchases
                    ? $"Completed automatic shopping run. groups={completedGroupCount + 1} purchases={completedPurchaseCount}."
                    : $"No shopping targets remain for {matchedPage.MenuLabel} / {matchedTab.TabLabel}.");
            }
            return;
        }

        var (target, targetIndex, itemDefinition, purchaseIntent) = targetSelection.Value;
        if (!shopPurchaseController.TryBuyCurrencyEntry(new LiveShopEntry
        {
            ItemId = itemDefinition.ItemId,
            ItemName = ShoppingItemNameResolver.ResolveItemName(itemDefinition.ItemId, itemDefinition.Name),
            CurrencyItemId = matchedPage.CurrencyItemId,
            CurrencyName = matchedPage.CurrencyName,
            Cost = itemDefinition.Cost,
            RowIndex = itemDefinition.RowIndex,
        }, 1))
        {
            StopFailed($"Failed to start purchase for {itemDefinition.Name}.");
            return;
        }

        activeTarget = target;
        activeTargetIndex = targetIndex;
        activeTargetOriginalAmount = purchaseIntent == ShoppingPurchaseIntent.Buy ? target.BuyAmount : purchaseIntent == ShoppingPurchaseIntent.Keep ? target.KeepAmount : 0;
        activeTargetItemName = ShoppingItemNameResolver.ResolveItemName(itemDefinition.ItemId, itemDefinition.Name);
        activePurchaseIntent = purchaseIntent;
        purchaseWasInProgress = true;
        UpdateStatus("Running automatic shopping");
    }

    private void RefreshTriggerStatus(bool allowDuringFarmSession)
    {
        var evaluation = EvaluateAutoStart(allowDuringFarmSession);
        lock (gate)
        {
            triggerStatus = evaluation.Reason;
        }
    }

    private AutoStartEvaluation EvaluateAutoStart(bool allowDuringFarmSession)
    {
        if (!configuration.EnableManualCurrencyShopping)
        {
            return new(false, "Shopping disabled.");
        }

        if (DateTimeOffset.UtcNow < autoStartBlockedUntil)
        {
            return new(false, "Automatic shopping cooldown active.");
        }

        if (IsRunning)
        {
            return new(false, "Automatic shopping already running.");
        }

        if (configuration.ScannerOnlyMode)
        {
            return new(false, "Blocked: scanner-only mode is enabled.");
        }

        if (true)
        {
            if (condition[ConditionFlag.InCombat])
            {
                return new(false, "Blocked: player is in combat.");
            }

            if (condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.OccupiedInQuestEvent] || condition[ConditionFlag.Casting])
            {
                return new(false, "Blocked: player is busy.");
            }

            if (movementController.IsPathBusy
                || criticalEngagementAutomationController.IsRunning
                || fateAutomationController.IsRunning
                || buffRotationController.IsRunning
                || potFarmController.IsRunning
                || treasureCofferFarmController.IsRunning)
            {
                return new(false, "Blocked: conflicting automation is active.");
            }

            if (!allowDuringFarmSession)
            {
                return new(false, "Waiting for a farm session idle window to start shopping.");
            }
        }

        if (!HasActionableTargetsAboveThreshold())
        {
            return new(false, "Blocked: no actionable targets meet currency thresholds.");
        }

        return new(true, "Automatic shopping trigger conditions satisfied.");
    }

    private void OnPurchaseSuccess()
    {
        if (activeTarget == null || activeTargetIndex == null)
        {
            return;
        }

        if (activePurchaseIntent == ShoppingPurchaseIntent.Buy && activeTarget.BuyAmount > 0)
        {
            var target = configuration.CurrencyShopTargets[activeTargetIndex.Value];
            target.BuyAmount = Math.Max(0, target.BuyAmount - 1);
            configuration.Save();
        }

        completedPurchaseCount++;

        activeTarget = null;
        activeTargetIndex = null;
        activeTargetOriginalAmount = 0;
        activeTargetItemName = string.Empty;
        activePurchaseIntent = ShoppingPurchaseIntent.None;
    }

    private bool EnsureDesiredShopContext(LiveShopSnapshot snapshot)
    {
        if (desiredGroup == null)
        {
            return true;
        }

        if (DateTimeOffset.UtcNow < nextNavigationAttemptAt)
        {
            return false;
        }

        if (snapshot.IsSelectIconStringOpen)
        {
            vendorMenuOpenPending = false;
            vendorMenuOpenStartedAt = DateTimeOffset.MinValue;
            vendorMenuOpenAttemptCount = 0;
            if (TrySelectMenuEntry(desiredGroup.Value.MenuIndex))
            {
                nextNavigationAttemptAt = DateTimeOffset.UtcNow + NavigationRetryDelay;
                matchedDesiredGroupAt = DateTimeOffset.MinValue;
                desiredGroupStableLogged = false;
                lastNavigationVerificationSnapshot = null;
                lastNavigationVerificationLogAt = DateTimeOffset.MinValue;
                UpdateStatus($"Running automatic shopping | Opening {desiredGroup.Value.MenuLabel} | groups={completedGroupCount} purchases={completedPurchaseCount}");
            }

            return false;
        }

        if (snapshot.IsShopExchangeCurrencyOpen)
        {
            vendorDismountPending = false;
            vendorDismountStartedAt = DateTimeOffset.MinValue;
            vendorTravelPending = false;
            vendorRecoveryPending = false;
            vendorMenuOpenPending = false;
            vendorMenuOpenStartedAt = DateTimeOffset.MinValue;
            vendorMenuOpenAttemptCount = 0;
            if (!pageMatcher.TryMatch(snapshot, out var match, out var matchReason)
                || match == null)
            {
                MaybeLogNavigationVerification(new NavigationVerificationSnapshot(
                    desiredGroup.Value.MenuIndex,
                    desiredGroup.Value.TabId,
                    snapshot.SelectedTabId,
                    null,
                    null,
                    matchReason));
                matchedDesiredGroupAt = DateTimeOffset.MinValue;
                desiredGroupStableLogged = false;
                return false;
            }

            MaybeLogNavigationVerification(new NavigationVerificationSnapshot(
                desiredGroup.Value.MenuIndex,
                desiredGroup.Value.TabId,
                snapshot.SelectedTabId,
                match.Page.MenuIndex,
                match.Tab.TabId,
                string.Empty));

            if (match.Page.MenuIndex != desiredGroup.Value.MenuIndex)
            {
                matchedDesiredGroupAt = DateTimeOffset.MinValue;
                desiredGroupStableLogged = false;
                if (TryCloseCurrencyShop())
                {
                    nextNavigationAttemptAt = DateTimeOffset.UtcNow + NavigationRetryDelay;
                    lastNavigationVerificationSnapshot = null;
                    lastNavigationVerificationLogAt = DateTimeOffset.MinValue;
                    UpdateStatus($"Running automatic shopping | Returning to vendor menu for {desiredGroup.Value.MenuLabel} | groups={completedGroupCount} purchases={completedPurchaseCount}");
                }

                return false;
            }

            if (match.Tab.TabId != desiredGroup.Value.TabId)
            {
                matchedDesiredGroupAt = DateTimeOffset.MinValue;
                desiredGroupStableLogged = false;
                if (TrySelectShopTab(desiredGroup.Value.TabId))
                {
                    nextNavigationAttemptAt = DateTimeOffset.UtcNow + NavigationRetryDelay;
                    lastNavigationVerificationSnapshot = null;
                    lastNavigationVerificationLogAt = DateTimeOffset.MinValue;
                    UpdateStatus($"Running automatic shopping | Switching to {desiredGroup.Value.TabLabel} tab | groups={completedGroupCount} purchases={completedPurchaseCount}");
                }

                return false;
            }

            if (matchedDesiredGroupAt == DateTimeOffset.MinValue)
            {
                matchedDesiredGroupAt = DateTimeOffset.UtcNow;
                desiredGroupStableLogged = false;
                UpdateStatus($"Running automatic shopping | Matched {desiredGroup.Value.MenuLabel} / {desiredGroup.Value.TabLabel}; waiting for settle | groups={completedGroupCount} purchases={completedPurchaseCount}");
                return false;
            }

            if (DateTimeOffset.UtcNow - matchedDesiredGroupAt < NavigationSettleDelay)
            {
                return false;
            }

            if (!desiredGroupStableLogged)
            {
                logger.Info($"[ManualCurrencyShopping] op=navigation-stable menuIndex={desiredGroup.Value.MenuIndex} tabId={desiredGroup.Value.TabId} reportedTabId={snapshot.SelectedTabId}");
                desiredGroupStableLogged = true;
            }

            return true;
        }

        if (vendorRecoveryPending)
        {
            if (movementController.IsPathBusy
                || movementController.State is MovementState.UsingReturn or MovementState.Pathfinding or MovementState.UsingAethernet or MovementState.WaitingForArrival)
            {
                UpdateStatus("Running automatic shopping | Returning to base camp to locate vendor.");
                return false;
            }

            if (movementController.State is MovementState.Failed or MovementState.TimedOut)
            {
                StopFailed($"Failed to recover to base camp for shopping: {movementController.LastError}");
                return false;
            }

            if (movementController.State is MovementState.Arrived or MovementState.Stopped or MovementState.Idle)
            {
                vendorRecoveryPending = false;
                nextNavigationAttemptAt = DateTimeOffset.UtcNow + NavigationRetryDelay;
                UpdateStatus("Running automatic shopping | Base camp recovery complete; locating vendor.");
                return false;
            }
        }

        if (!TryFindVendor(out var vendor) || vendor == null)
        {
            if (!vendorRecoveryAttempted)
            {
                if (!movementController.RecoverToBaseCamp())
                {
                    StopFailed($"Failed to recover to base camp for shopping: {movementController.LastError}");
                    return false;
                }

                vendorRecoveryAttempted = true;
                vendorRecoveryPending = true;
                nextNavigationAttemptAt = DateTimeOffset.UtcNow + NavigationRetryDelay;
                UpdateStatus("Running automatic shopping | Returning to base camp to locate vendor.");
                logger.Info("[ManualCurrencyShopping] op=vendor-recovery-start result=true");
                return false;
            }

            StopSkipped("Skipped shopping: vendor unavailable after base camp recovery.");
            return false;
        }

        var localPlayer = Plugin.ObjectTable.LocalPlayer;
        if (localPlayer == null)
        {
            StopFailed("Failed: player position is unavailable.");
            return false;
        }

        var vendorDistance = Vector3.Distance(vendor.Position, localPlayer.Position);
        if (vendorDistance > VendorInteractionRange)
        {
            if (vendorTravelPending)
            {
                if (movementController.IsPathBusy)
                {
                    UpdateStatus($"Running automatic shopping | Traveling to vendor ({vendorDistance:0.0}y remaining).");
                    return false;
                }

                if (movementController.State is MovementState.Failed or MovementState.TimedOut)
                {
                    StopFailed($"Failed to move to Expedition Antiquarian: {movementController.LastError}");
                    return false;
                }
            }

            if (!movementController.StartDirectMove("Approach Expedition Antiquarian", vendor.Position, VendorInteractionRange, shouldMountBeforeStep: true))
            {
                StopFailed($"Failed to move to Expedition Antiquarian: {movementController.LastError}");
                return false;
            }

            vendorTravelPending = true;
            nextNavigationAttemptAt = DateTimeOffset.UtcNow + NavigationRetryDelay;
            UpdateStatus($"Running automatic shopping | Traveling to Expedition Antiquarian ({vendorDistance:0.0}y).");
            logger.Info($"[ManualCurrencyShopping] op=vendor-travel-start distance={vendorDistance:0.0}");
            return false;
        }

        if (vendorTravelPending)
        {
            vendorTravelPending = false;
            if (movementController.IsPathBusy)
            {
                movementController.Stop("Reached currency shop vendor interaction range.");
            }

            nextNavigationAttemptAt = DateTimeOffset.UtcNow + NavigationRetryDelay;
            UpdateStatus("Running automatic shopping | Vendor interaction range reached.");
            logger.Info($"[ManualCurrencyShopping] op=vendor-travel-arrived distance={vendorDistance:0.0}");
            return false;
        }

        if (condition[ConditionFlag.Mounted])
        {
            if (!vendorDismountPending)
            {
                if (!gameActionController.TryExecuteGeneralAction(GameActionController.DismountActionId, "currency shop vendor interaction"))
                {
                    StopFailed("Failed: could not dismount before vendor interaction.");
                    return false;
                }

                vendorDismountPending = true;
                vendorDismountStartedAt = DateTimeOffset.UtcNow;
                nextNavigationAttemptAt = DateTimeOffset.UtcNow + VendorDismountRetryDelay;
                UpdateStatus("Running automatic shopping | Dismounting before vendor interaction.");
                logger.Info("[ManualCurrencyShopping] op=vendor-dismount-attempt result=true");
                return false;
            }

            if (DateTimeOffset.UtcNow - vendorDismountStartedAt >= VendorDismountTimeout)
            {
                StopFailed("Failed: could not dismount before vendor interaction.");
                return false;
            }

            return false;
        }

        if (vendorDismountPending)
        {
            vendorDismountPending = false;
            vendorDismountStartedAt = DateTimeOffset.MinValue;
            nextNavigationAttemptAt = DateTimeOffset.UtcNow + NavigationRetryDelay;
            UpdateStatus("Running automatic shopping | Dismount complete; opening vendor menu.");
            logger.Info("[ManualCurrencyShopping] op=vendor-dismount-complete");
            return false;
        }

        if (vendorMenuOpenPending)
        {
            if (DateTimeOffset.UtcNow - vendorMenuOpenStartedAt < VendorMenuOpenTimeout)
            {
                UpdateStatus($"Waiting for vendor menu to open (attempt {vendorMenuOpenAttemptCount}/{MaxVendorMenuOpenAttempts}).");
                return false;
            }

            logger.Warning($"[ManualCurrencyShopping] op=vendor-menu-timeout attempt={vendorMenuOpenAttemptCount}");
            vendorMenuOpenPending = false;
            vendorMenuOpenStartedAt = DateTimeOffset.MinValue;
            nextNavigationAttemptAt = DateTimeOffset.UtcNow + NavigationRetryDelay;

            if (vendorMenuOpenAttemptCount < MaxVendorMenuOpenAttempts)
            {
                UpdateStatus($"Retrying vendor menu open (attempt {vendorMenuOpenAttemptCount + 1}/{MaxVendorMenuOpenAttempts}).");
                logger.Info($"[ManualCurrencyShopping] op=vendor-menu-retry nextAttempt={vendorMenuOpenAttemptCount + 1}");
                return false;
            }

            StopFailed($"Failed: vendor menu did not open after {MaxVendorMenuOpenAttempts} attempt(s).");
            return false;
        }

        if (TryOpenVendorMenu())
        {
            vendorMenuOpenPending = true;
            vendorMenuOpenStartedAt = DateTimeOffset.UtcNow;
            vendorMenuOpenAttemptCount++;
            nextNavigationAttemptAt = DateTimeOffset.UtcNow + NavigationRetryDelay;
            UpdateStatus($"Opening vendor menu (attempt {vendorMenuOpenAttemptCount}/{MaxVendorMenuOpenAttempts}).");
            logger.Info($"[ManualCurrencyShopping] op=vendor-menu-wait-start attempt={vendorMenuOpenAttemptCount}");
            return false;
        }

        StopFailed("Failed: neither the vendor menu nor the currency shop is open, and the vendor menu could not be reopened.");
        return false;
    }

    private (CurrencyShopTarget Target, int TargetIndex, ShopCurrencyItemDefinition ItemDefinition, ShoppingPurchaseIntent PurchaseIntent)? SelectNextTarget(ShopCurrencyPageDefinition matchedPage, ShopCurrencyTabDefinition matchedTab, int availableCurrency)
    {
        var candidateTargets = configuration.CurrencyShopTargets
            .Select((target, index) => (Target: target, Index: index))
            .Where(entry => entry.Target.MenuIndex == matchedPage.MenuIndex && entry.Target.TabId == matchedTab.TabId)
            .OrderBy(entry => entry.Target.Priority)
            .ToList();

        foreach (var candidate in candidateTargets)
        {
            var itemDefinition = matchedTab.Items.FirstOrDefault(item => item.ItemId == candidate.Target.ItemId);
            if (itemDefinition == null || itemDefinition.Cost > (uint)availableCurrency)
            {
                continue;
            }

            var currentCount = (int)GetItemCount(candidate.Target.ItemId);
            // Evaluate each configured item deterministically: Keep first, then Buy, then Keep Buying.
            if (candidate.Target.KeepAmount > 0 && currentCount < candidate.Target.KeepAmount)
            {
                return (candidate.Target, candidate.Index, itemDefinition, ShoppingPurchaseIntent.Keep);
            }

            if (candidate.Target.BuyAmount > 0)
            {
                return (candidate.Target, candidate.Index, itemDefinition, ShoppingPurchaseIntent.Buy);
            }

            if (candidate.Target.KeepBuying)
            {
                return (candidate.Target, candidate.Index, itemDefinition, ShoppingPurchaseIntent.KeepBuying);
            }
        }

        return null;
    }

    private bool TrySelectNextActionableGroup(out CurrencyShopGroup? group, out string reason)
    {
        foreach (var page in ShopCurrencyCatalog.Pages.OrderBy(page => page.CurrencyItemId).ThenBy(page => page.MenuIndex))
        {
            var reserve = configuration.CurrencyShopReserves.FirstOrDefault(entry => entry.CurrencyItemId == page.CurrencyItemId)?.ReserveAmount ?? 0;
            var currentCurrency = GetItemCount(page.CurrencyItemId);
            var availableCurrency = Math.Max(0, (int)currentCurrency - reserve);

            foreach (var tab in page.Tabs.OrderBy(tab => tab.TabId))
            {
                var targetSelection = SelectNextTarget(page, tab, availableCurrency);
                if (targetSelection != null)
                {
                    group = new CurrencyShopGroup(page.MenuIndex, page.MenuLabel, tab.TabId, tab.TabLabel);
                    reason = string.Empty;
                    return true;
                }
            }
        }

        group = null;
        reason = "No actionable currency shopping targets remain.";
        return false;
    }

    private bool HasActionableTargetsAboveThreshold()
    {
        foreach (var page in ShopCurrencyCatalog.Pages)
        {
            var reserve = configuration.CurrencyShopReserves.FirstOrDefault(entry => entry.CurrencyItemId == page.CurrencyItemId)?.ReserveAmount ?? 0;
            var currentCurrency = GetItemCount(page.CurrencyItemId);
            var availableCurrency = Math.Max(0, (int)currentCurrency - reserve);
            var threshold = page.CurrencyItemId == EnlightenmentGoldPieceItemId
                ? configuration.GoldStartThreshold
                : configuration.SilverStartThreshold;
            if (currentCurrency < threshold)
            {
                continue;
            }

            foreach (var tab in page.Tabs)
            {
                if (SelectNextTarget(page, tab, availableCurrency) != null)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private bool TryFindVendor(out IGameObject? vendor)
    {
        var localPlayer = Plugin.ObjectTable.LocalPlayer;
        if (localPlayer == null)
        {
            vendor = null;
            return false;
        }

        vendor = Plugin.ObjectTable
            .Where(gameObject => gameObject != null
                && gameObject.IsValid()
                && gameObject.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventNpc
                && gameObject.BaseId == ExpeditionAntiquarianDataId
                && gameObject.IsTargetable)
            .OrderBy(gameObject => Vector3.Distance(gameObject.Position, localPlayer.Position))
            .FirstOrDefault();

        return vendor != null;
    }

    private unsafe bool TrySelectMenuEntry(int menuIndex)
    {
        var addon = (AddonSelectIconString*)gameGui.GetAddonByName("SelectIconString", 1).Address;
        if (addon == null || !addon->AtkUnitBase.IsReady)
        {
            return false;
        }

        var selected = addon->AtkUnitBase.FireCallbackInt(menuIndex);
        logger.Info($"[ManualCurrencyShopping] op=menu-select menuIndex={menuIndex} result={selected}");
        return selected;
    }

    private unsafe bool TrySelectShopTab(int tabId)
    {
        var addon = (AtkUnitBase*)gameGui.GetAddonByName("ShopExchangeCurrency", 1).Address;
        if (addon == null || !addon->IsReady)
        {
            return false;
        }

        var values = (AtkValue*)Marshal.AllocHGlobal(4 * sizeof(AtkValue));
        if (values == null)
        {
            return false;
        }

        try
        {
            values[0] = default;
            values[1] = default;
            values[2] = default;
            values[3] = default;
            values[0].Type = AtkValueType.Int;
            values[0].Int = 4;
            values[1].Type = AtkValueType.Int;
            values[1].Int = -1;
            values[2].Type = AtkValueType.Int;
            values[2].Int = 1;
            values[3].Type = AtkValueType.Int;
            values[3].Int = tabId;
            var selected = addon->FireCallback(4, values, true);
            logger.Info($"[ManualCurrencyShopping] op=tab-select tabId={tabId} result={selected}");
            return selected;
        }
        finally
        {
            Marshal.FreeHGlobal((nint)values);
        }
    }

    private bool TryOpenVendorMenu()
    {
        if (!TryFindVendor(out var vendor) || vendor == null)
        {
            logger.Warning($"[ManualCurrencyShopping] op=vendor-open-failed dataId={ExpeditionAntiquarianDataId} reason=vendor-not-found");
            return false;
        }

        if (!gameActionController.TrySetTarget(vendor, "currency shop vendor"))
        {
            logger.Warning($"[ManualCurrencyShopping] op=vendor-open-failed dataId={ExpeditionAntiquarianDataId} reason=set-target-failed");
            return false;
        }

        var interacted = gameActionController.TryInteractWithObject(vendor, "currency shop vendor");
        logger.Info($"[ManualCurrencyShopping] op=vendor-open dataId={ExpeditionAntiquarianDataId} name=\"{vendor.Name.TextValue}\" result={interacted}");
        return interacted;
    }

    private unsafe bool TryCloseCurrencyShop()
    {
        var addon = (AtkUnitBase*)gameGui.GetAddonByName("ShopExchangeCurrency", 1).Address;
        if (addon == null || !addon->IsReady)
        {
            return false;
        }

        var closed = addon->FireCallbackInt(-1);
        logger.Info($"[ManualCurrencyShopping] op=shop-close result={closed}");
        return closed;
    }

    private void UpdateStatus(string nextStatus)
    {
        lock (gate)
        {
            if (isRunning)
            {
                status = nextStatus;
            }
        }
    }

    private static unsafe uint GetItemCount(uint itemId)
    {
        if (itemId == 0)
        {
            return 0;
        }

        var inventoryManager = InventoryManager.Instance();
        return inventoryManager == null ? 0 : (uint)inventoryManager->GetInventoryItemCount(itemId, false);
    }

    private void MaybeLogNavigationVerification(NavigationVerificationSnapshot snapshot)
    {
        if (shopPurchaseController.IsBusy)
        {
            return;
        }

        if (desiredGroupStableLogged
            && snapshot.Reason.Length == 0
            && snapshot.MatchedMenuIndex == snapshot.DesiredMenuIndex
            && snapshot.MatchedTabId == snapshot.DesiredTabId)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (lastNavigationVerificationSnapshot.HasValue
            && lastNavigationVerificationSnapshot.Value.Equals(snapshot)
            && now - lastNavigationVerificationLogAt < NavigationLogThrottle)
        {
            return;
        }

        lastNavigationVerificationSnapshot = snapshot;
        lastNavigationVerificationLogAt = now;

        if (snapshot.MatchedMenuIndex == null || snapshot.MatchedTabId == null)
        {
            logger.Info($"[ManualCurrencyShopping] op=navigation-verify desiredMenuIndex={snapshot.DesiredMenuIndex} desiredTabId={snapshot.DesiredTabId} reportedTabId={snapshot.ReportedTabId} matched=none reason=\"{snapshot.Reason.Replace("\"", "'")}\"");
            return;
        }

        logger.Info($"[ManualCurrencyShopping] op=navigation-verify desiredMenuIndex={snapshot.DesiredMenuIndex} desiredTabId={snapshot.DesiredTabId} reportedTabId={snapshot.ReportedTabId} matchedMenuIndex={snapshot.MatchedMenuIndex} matchedTabId={snapshot.MatchedTabId}");
    }

    public enum ShoppingStopKind
    {
        None,
        Completed,
        Skipped,
        Failed,
    }

    private readonly record struct CurrencyShopGroup(int MenuIndex, string MenuLabel, int TabId, string TabLabel);
    private readonly record struct NavigationVerificationSnapshot(int DesiredMenuIndex, int DesiredTabId, int ReportedTabId, int? MatchedMenuIndex, int? MatchedTabId, string Reason);
    private readonly record struct AutoStartEvaluation(bool ShouldRun, string Reason);
    private enum ShoppingPurchaseIntent
    {
        None,
        Keep,
        Buy,
        KeepBuying,
    }
}
