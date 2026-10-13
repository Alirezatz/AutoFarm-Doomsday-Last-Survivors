using System;
using System.Collections.Generic;
using System.Globalization;
using Il2CppIGG.Game.Data.Cache;
using Il2CppIGG.Game.Data.Cache.Hero;
using Il2CppIGG.Game.Data.Cache.Troop;
using Il2CppIGG.Game.Data.Cache.WorldMap.Entity.Comp;
using Il2CppIGG.Game.Data.Config;
using Il2CppIGG.Game.Data.Entity;
using Il2CppIGG.Framework.Panel;
using Il2CppIGG.Game.Module.March;
using Il2CppIGG.Game.Module.March.View;
using Il2CppIGG.Game.UI.Deploy;
using Il2CppFairyGUI;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Runtime;
using Il2CppProtomsg;
using MelonLoader;

namespace FesticalAutoFarm
{
    internal sealed class DeploymentPreviewLogger
    {
        private sealed class FarmMarchTracker
        {
            public int SlotIndex;
            public ulong TargetId;
            public HashSet<ulong> PriorMarchIds;
            public ulong MarchEntityId;
            public DateTime AcceptedAtUtc;
            public DateTime NextPollUtc;
            public DateTime MissingSinceUtc;
        }

        private static readonly TimeSpan PanelReadyTimeout = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan DispatchOutcomeTimeout = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan MarchRemovalConfirmDelay = TimeSpan.FromSeconds(2);
        private bool _testMarchAttempted;
        private bool _pendingGameOpen;
        private ulong _pendingTargetId;
        private DateTime _pendingSinceUtc;
        private string _lastPendingSnapshot;
        private bool _nativeAutoSelectionAttempted;
        private bool _awaitingDispatchOutcome;
        private int _marchCountBeforeDispatch;
        private DateTime _dispatchInvokedAtUtc;
        private DateTime _lastOutcomePollUtc;
        private DateTime _lastRouteWaitLogUtc;
        private DeployMainPanel _panelToRestore;
        private GLoader _maskToRestore;
        private bool _panelVisibilityCaptured;
        private bool _panelWasVisible;
        private bool _maskVisibilityCaptured;
        private bool _maskWasVisible;
        private string _lastBackdropDiagnostic;
        private uint _requestedCollectCapacity;
        private bool _farmRunning;
        private FarmSettings _farmSettings;
        private int _farmSlotIndex = -1;
        private readonly List<FarmMarchTracker> _activeFarmMarches = new List<FarmMarchTracker>();
        private HashSet<ulong> _marchIdsBeforeDispatch;
        private DateTime _nextFarmAttemptUtc;
        private string _farmStatus = "Stopped";
        private bool _pendingFarmManaged;

        public bool FarmRunning
        {
            get { return _farmRunning; }
        }

        public string FarmStatus
        {
            get { return _farmStatus; }
        }

        public string LogNearestFoodPreview(WorldMapScanResult mapScan)
        {
            MelonLogger.Msg("Food preview diagnostic button clicked.");

            if (mapScan == null
                || !mapScan.HasScanned
                || !mapScan.HasUsableNodeSnapshot)
            {
                return Skip("No current successful world-map scan.");
            }

            NearestResourceNodeInfo nearestFood = FindNearestFreeFood(mapScan);
            if (nearestFood == null)
                return Skip("No free loaded Food node was found in the latest scan.");

            PanelMgr panelManager = PanelMgr.Inst;
            if (panelManager == null)
                return Skip("PanelMgr.Inst is null.");

            MelonLogger.Msg("Food preview panel lookup: class=DeployMainPanel"
                + " fairyUrl=" + (string.IsNullOrEmpty(BaseDeployMainPanel.URL)
                    ? "<empty>"
                    : BaseDeployMainPanel.URL));
            DeployMainPanel deployPanel = FindOpenDeployPanel(panelManager);
            if (deployPanel == null)
                return Skip("PanelMgr did not return an open DeployMainPanel for any known panel key.");

            ClientMarchCommandRequestData requestData = deployPanel.m_marchCommandRequestData;
            MarchRequestData marchData = requestData.commandData;
            MarchCommand command = marchData.Command;
            ulong targetId = command == null ? 0UL : command.TargetId;
            NearestResourceNodeInfo previewTarget = FindNodeById(mapScan, targetId);
            bool targetIsNearestFood = targetId == nearestFood.EntityId;

            MelonLogger.Msg(
                "Food preview diagnostic: nearestFreeFoodId="
                + nearestFood.EntityId.ToString(CultureInfo.InvariantCulture)
                + " remaining=" + nearestFood.Remaining.ToString(CultureInfo.InvariantCulture)
                + " distance=" + nearestFood.Distance.ToString("F1", CultureInfo.InvariantCulture)
                + " mapPos=(" + nearestFood.X.ToString("F1", CultureInfo.InvariantCulture)
                + "," + nearestFood.Y.ToString("F1", CultureInfo.InvariantCulture) + ")"
                + " previewTargetId=" + targetId.ToString(CultureInfo.InvariantCulture)
                + " previewTargetType=" + (command == null ? "<none>" : command.TargetType.ToString())
                + " previewTargetIsNearestFreeFood=" + targetIsNearestFood
                + " previewTargetInLatestScan=" + (previewTarget != null)
                + " collectCapacity=" + marchData.CollectCapacity.ToString(CultureInfo.InvariantCulture));

            if (previewTarget != null)
            {
                MelonLogger.Msg(
                    "Food preview target node: id=" + previewTarget.EntityId.ToString(CultureInfo.InvariantCulture)
                    + " resource=" + previewTarget.Resource
                    + " remaining=" + previewTarget.Remaining.ToString(CultureInfo.InvariantCulture)
                    + " ownerId=" + previewTarget.OwnerId.ToString(CultureInfo.InvariantCulture)
                    + " ownerName=" + (string.IsNullOrEmpty(previewTarget.OwnerName)
                        ? "<empty>"
                        : previewTarget.OwnerName)
                    + " distance=" + previewTarget.Distance.ToString("F1", CultureInfo.InvariantCulture)
                    + " mapPos=(" + previewTarget.X.ToString("F1", CultureInfo.InvariantCulture)
                    + "," + previewTarget.Y.ToString("F1", CultureInfo.InvariantCulture) + ")");
            }

            HeroInfo mainHero = deployPanel.SelectedMainHero;
            HeroInfo secondHero = deployPanel.SelectedSecondHero;
            MelonLogger.Msg("Food preview heroes: main=" + DescribeHero(mainHero)
                + " second=" + DescribeHero(secondHero));

            try
            {
                ArmyData nativeEditedArmy = deployPanel.GetEditedArmyData();
                if (nativeEditedArmy == null)
                {
                    MelonLogger.Warning(
                        "Food preview native ArmyData diagnostic returned null; dispatchInvoked=false.");
                }
                else
                {
                    LogArmyDataPayload("native-panel-edited", nativeEditedArmy);
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Error(
                    "Food preview native ArmyData diagnostic failed; dispatchInvoked=false: " + ex);
            }

            var armyRows = deployPanel.m_armyDataList;
            int selectedArmyTypes = 0;
            if (armyRows != null)
            {
                for (int i = 0; i < armyRows.Count; i++)
                {
                    DeployMainPanel.ArmyForSelect row = armyRows[i];
                    if (row == null || row.SelectArmyCount == 0)
                        continue;

                    selectedArmyTypes++;
                    string troopName = row.TroopConfig == null
                        ? "<unknown>"
                        : row.TroopConfig.GetName();
                    MelonLogger.Msg(
                        "Food preview troop: id=" + row.ArmyId.ToString(CultureInfo.InvariantCulture)
                        + " name=" + (string.IsNullOrEmpty(troopName) ? "<empty>" : troopName)
                        + " selected=" + row.SelectArmyCount.ToString(CultureInfo.InvariantCulture)
                        + " available=" + row.ArmyCount.ToString(CultureInfo.InvariantCulture));
                }
            }

            MelonLogger.Msg("Food preview diagnostic end: selectedTroopTypes="
                + selectedArmyTypes.ToString(CultureInfo.InvariantCulture)
                + " dispatchInvoked=false"
                + " previewTargetInLatestScan=" + (previewTarget != null));

            if (previewTarget == null)
                return "Preview logged; target was not in the last map scan. Scan again for target details.";
            if (previewTarget.Resource != "Food" || previewTarget.OwnerId != 0)
                return "Preview logged; target is not an unowned Food node. Check MelonLoader.";
            return "Preview logged. Target stock and troop details are in MelonLoader.";
        }

        public string SendOneTestMarch(WorldMapScanResult mapScan)
        {
            return SendMarch(mapScan, "Food", 100, false);
        }

        public string StartFarm(WorldMapScanResult mapScan, FarmSettings settings)
        {
            if (_farmRunning)
                return "Farming is already running.";
            if (mapScan == null || !mapScan.HasUsableNodeSnapshot)
                return "Scan the world map once before starting farming.";
            if (!mapScan.CurrentOriginValidated)
                return "Could not validate your current castle position; open and scan the world map before farming.";
            if (mapScan.SnapshotOriginChanged)
                return "Your castle moved since this map scan; open the world map and scan again.";
            if (settings == null)
                return "Farm settings are unavailable.";

            settings.Validate();
            bool hasEnabledSlot = false;
            for (int i = 0; i < settings.Slots.Count; i++)
            {
                FarmSlotSettings slot = settings.Slots[i];
                if (slot == null || !slot.Enabled || slot.TargetAmount <= 0)
                    continue;

                hasEnabledSlot = true;
                if (!slot.UseCustomHeroes || slot.MainHeroId == 0 || slot.Troops.Count == 0)
                {
                    return "Direct dispatch needs a main hero and at least one troop type with a positive count "
                        + "in every enabled slot. Use Load Squad in Army setup.";
                }
            }
            if (!hasEnabledSlot)
                return "Enable at least one slot and set a positive amount.";

            _farmSettings = settings;
            _farmRunning = true;
            if (_activeFarmMarches.Count == 0)
                _farmSlotIndex = -1;
            _farmStatus = "Starting";
            _nextFarmAttemptUtc = DateTime.MinValue;
            MelonLogger.Warning(
                "Farm automation started: filling available march slots from enabled farm slots; "
                + "each slot is kept unique while its march is active.");
            TryStartNextFarmMarch(mapScan);
            return _farmStatus;
        }

        public string StopFarm()
        {
            if (!_farmRunning)
                return "Farming is already stopped.";

            _farmRunning = false;
            _farmStatus = _activeFarmMarches.Count > 0 || _awaitingDispatchOutcome || _pendingGameOpen
                ? "Stopping after the active march finishes"
                : "Stopped";
            MelonLogger.Warning(
                "Farm automation stop requested. No new march will be started; "
                + "any already-submitted march is left to the game.");
            if (_pendingGameOpen)
                LogPendingAbort("Farm stopped before GO; no march was submitted.");
            return _farmStatus;
        }

        private string SendMarch(
            WorldMapScanResult mapScan,
            string resource,
            long requestedAmount,
            bool farmManaged,
            FarmSlotSettings slotSettings = null)
        {
            _pendingFarmManaged = farmManaged;
            MelonLogger.Warning(
                (farmManaged ? "Farm" : "One-shot test")
                + " march button clicked: resource=" + resource
                + " requestedAmount=" + requestedAmount.ToString(CultureInfo.InvariantCulture));
            if (!farmManaged && _testMarchAttempted)
                return Skip("The one-shot test march has already been attempted this game session.");
            if (_pendingGameOpen || _awaitingDispatchOutcome)
                return Skip("A one-shot march attempt is already being processed.");

            if (mapScan == null || !mapScan.HasUsableNodeSnapshot)
                return Skip("Scan the world map before attempting the test march.");
            if (!mapScan.CurrentOriginValidated || mapScan.SnapshotOriginChanged)
                return Skip("The saved map scan does not match a validated current castle position; scan the world map again.");

            HashSet<ulong> activeTargets = farmManaged ? GetActiveFarmTargetIds() : null;
            NearestResourceNodeInfo target = FindNearestFreeResource(
                mapScan, resource, requestedAmount, activeTargets);
            if (target == null)
                return Skip("No unowned, non-depleted " + resource + " node is present in the saved map scan.");
            var player = AppCache.Player;
            if (player == null)
                return Skip("PlayerCache is unavailable; cannot verify an open march slot.");

            uint maxSlots = player.GetDispatchMaxMarchCount();
            int usedSlots = Math.Max(0, player.GetMarchCount());
            if (maxSlots <= (uint)usedSlots)
                return Skip("No free march slot is available.");

            ArmyData armyData;
            string armyError;
            if (!TryBuildArmyData(slotSettings, out armyData, out armyError))
                return Skip(armyError);

            MelonLogger.Warning(
                "Auto-march direct route begin: route=MarchModule.API.CreateBaseCommandRequestData"
                + "->MarchModule.API.ReqCreateMarchRequest (no deployment panel)"
                + " targetId=" + target.EntityId.ToString(CultureInfo.InvariantCulture)
                + " resource=" + resource
                + " remaining=" + target.Remaining.ToString(CultureInfo.InvariantCulture)
                + " distance=" + target.Distance.ToString("F1", CultureInfo.InvariantCulture)
                + " mapPos=(" + target.X.ToString("F1", CultureInfo.InvariantCulture)
                + "," + target.Y.ToString("F1", CultureInfo.InvariantCulture) + ")"
                + " slots=max:" + maxSlots.ToString(CultureInfo.InvariantCulture)
                + ",used:" + usedSlots.ToString(CultureInfo.InvariantCulture)
                + ",free:" + (maxSlots - (uint)usedSlots).ToString(CultureInfo.InvariantCulture));

            ClientMarchCommandRequestData requestData;
            try
            {
                requestData = MarchModule.API.CreateBaseCommandRequestData(
                    GetMarchTargetType(resource),
                    MarchCommandTarget.KMarchCommandTargetCollect,
                    target.EntityId);
            }
            catch (Exception ex)
            {
                MelonLogger.Error("Auto-march stage=request-factory failed: " + ex);
                return Skip("The game's base march request factory threw an exception; see the detailed log.");
            }

            if (requestData == null)
                return Skip("The game's base march request factory returned null.");

            MarchRequestData marchData = requestData.commandData;
            MarchCommand command = marchData.Command;
            MelonLogger.Msg(
                "Auto-march request factory result: requestType=" + requestData.commandType
                + " commandNull=" + (command == null)
                + " targetType=" + (command == null ? "<none>" : command.TargetType.ToString())
                + " targetId=" + (command == null
                    ? "0"
                    : command.TargetId.ToString(CultureInfo.InvariantCulture))
                + " marchEndEventType=" + marchData.MarchEndEventType
                + " selectedTeamId=" + marchData.SelectedTeamId.ToString(CultureInfo.InvariantCulture)
                + " collectCapacity=" + marchData.CollectCapacity.ToString(CultureInfo.InvariantCulture)
                + " isAuto=" + marchData.IsAuto);

            if (command == null
                || command.TargetType != MarchCommandTarget.KMarchCommandTargetCollect
                || command.TargetId != target.EntityId)
            {
                return Skip("The game's request factory returned a command that does not target the selected " + resource + " node.");
            }

            uint apiCapacity;
            try
            {
                apiCapacity = MarchModule.API.GetCollectCapacity(GetCollectType(resource));
            }
            catch (Exception ex)
            {
                MelonLogger.Error("Auto-march stage=capacity-validation failed: " + ex);
                return Skip("The game's " + resource + " capacity API failed; no request was submitted.");
            }

            uint capacityBeforeClamp = marchData.CollectCapacity;
            uint capacityForNode = capacityBeforeClamp == 0 ? apiCapacity : capacityBeforeClamp;
            if (capacityForNode > target.Remaining)
                capacityForNode = target.Remaining;
            uint configuredAmount = (uint)Math.Min((ulong)Math.Max(0, requestedAmount), uint.MaxValue);
            if (capacityForNode > configuredAmount)
                capacityForNode = configuredAmount;
            if (capacityForNode == 0)
                return Skip("The native request and " + resource + " capacity API both produced zero usable Load.");
            _requestedCollectCapacity = capacityForNode;

            if (marchData.CollectCapacity != capacityForNode)
            {
                marchData.CollectCapacity = capacityForNode;
                requestData.commandData = marchData;
            }

            MelonLogger.Msg(
                "Auto-march request normalized: nativeCapacity=" + capacityBeforeClamp
                + " apiResourceCapacity=" + apiCapacity
                + " nodeRemaining=" + target.Remaining
                + " finalLoad=" + capacityForNode
                + " requestCapacityChanged=" + (capacityBeforeClamp != capacityForNode));

            _pendingTargetId = target.EntityId;
            _pendingSinceUtc = DateTime.UtcNow;
            _marchCountBeforeDispatch = usedSlots;
            _dispatchInvokedAtUtc = DateTime.UtcNow;
            _lastOutcomePollUtc = DateTime.MinValue;
            if (farmManaged)
            {
                _marchIdsBeforeDispatch = CapturePlayerMarchIds();
                if (_marchIdsBeforeDispatch == null)
                    return Skip("Could not capture the pre-dispatch march IDs safely.");
            }

            _testMarchAttempted = !farmManaged;
            _awaitingDispatchOutcome = true;
            try
            {
                MelonLogger.Warning(
                    "Auto-march stage=direct-native-submit: targetId="
                    + target.EntityId.ToString(CultureInfo.InvariantCulture)
                    + " load=" + capacityForNode.ToString(CultureInfo.InvariantCulture)
                    + " hero1=" + armyData.Hero1.ToString(CultureInfo.InvariantCulture)
                    + " hero2=" + armyData.Hero2.ToString(CultureInfo.InvariantCulture)
                    + " troops=" + DescribeConfiguredTroops(slotSettings.Troops)
                    + " panelOpened=false");
                MarchModule.API.ReqCreateMarchRequest(requestData, armyData);
                MelonLogger.Warning(
                    "Auto-march direct native create-march API returned; this is not treated as server acceptance. "
                    + "Monitoring march-slot state for up to "
                    + DispatchOutcomeTimeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)
                    + " seconds. No automatic retry will occur.");
                _farmStatus = farmManaged
                    ? "Direct march request sent; waiting for game confirmation"
                    : "Direct test march request sent; waiting for game confirmation";
                return "Direct native march-create request submitted without opening the deployment panel.";
            }
            catch (Exception ex)
            {
                MelonLogger.Error(
                    "Auto-march direct native create-march call threw; attempt remains locked and will not retry: "
                    + ex);
                _farmStatus = "Direct request threw; checking whether the game accepted it";
                return "Native create-march call threw; checking march slots before any further action.";
            }
        }

        public void AdvanceOneShotMarch(WorldMapScanResult mapScan)
        {
            PollActiveFarmMarches();

            if (_awaitingDispatchOutcome)
            {
                PollDispatchOutcome();
                return;
            }

            if (!_pendingGameOpen)
            {
                if (_farmRunning && DateTime.UtcNow >= _nextFarmAttemptUtc)
                    TryStartNextFarmMarch(mapScan);
                return;
            }

            if ((DateTime.UtcNow - _pendingSinceUtc) >= PanelReadyTimeout)
            {
                LogPendingAbort("Timed out waiting for the game's deployment preview to initialize.");
                return;
            }

            if (mapScan == null || !mapScan.HasUsableNodeSnapshot)
            {
                LogPendingAbort("World-map scan is no longer available while preparing the march.");
                return;
            }
            if (!mapScan.CurrentOriginValidated || mapScan.SnapshotOriginChanged)
            {
                LogPendingAbort("The saved map scan no longer matches the validated castle position.");
                return;
            }

            NearestResourceNodeInfo target = FindNodeById(mapScan, _pendingTargetId);
            if (target == null || target.OwnerId != 0 || target.Remaining == 0)
            {
                LogPendingAbort(target == null
                    ? "The requested node disappeared from the current scan before dispatch."
                    : "The requested " + target.Resource
                        + " node became owned or depleted before dispatch.");
                return;
            }

            var player = AppCache.Player;
            if (player == null)
            {
                LogPendingAbort("PlayerCache became unavailable before dispatch.");
                return;
            }

            uint maxSlots = player.GetDispatchMaxMarchCount();
            int usedSlots = Math.Max(0, player.GetMarchCount());
            if (maxSlots <= (uint)usedSlots)
            {
                LogPendingAbort("The free march slot was consumed before dispatch.");
                return;
            }

            PanelMgr panelManager = PanelMgr.Inst;
            DeployMainPanel panel = panelManager == null
                ? null
                : FindOpenDeployPanel(panelManager, false);
            if (panel == null)
                return;

            if (!HideDeploymentPanel(panel))
            {
                LogPendingAbort("The deployment panel became visible and could not be hidden.");
                return;
            }

            ClientMarchCommandRequestData requestData = panel.m_marchCommandRequestData;
            MarchRequestData marchData = requestData.commandData;
            MarchCommand command = marchData.Command;
            HeroInfo mainHero = panel.SelectedMainHero;
            HeroInfo secondHero = panel.SelectedSecondHero;
            int selectedArmyTypes = 0;
            ulong selectedTroops = 0;
            int armyRowCount = panel.m_armyDataList == null ? 0 : panel.m_armyDataList.Count;
            if (panel.m_armyDataList != null)
            {
                for (int i = 0; i < panel.m_armyDataList.Count; i++)
                {
                    DeployMainPanel.ArmyForSelect row = panel.m_armyDataList[i];
                    if (row == null || row.SelectArmyCount == 0)
                        continue;
                    selectedArmyTypes++;
                    selectedTroops += row.SelectArmyCount;
                }
            }

            ulong panelTargetId = command == null ? 0UL : command.TargetId;
            string snapshot = "open=" + panel.IsOpen
                + "|target=" + panelTargetId.ToString(CultureInfo.InvariantCulture)
                + "|targetType=" + (command == null ? "<none>" : command.TargetType.ToString())
                + "|load=" + marchData.CollectCapacity.ToString(CultureInfo.InvariantCulture)
                + "|hero1=" + (mainHero == null ? 0U : mainHero.HeroId).ToString(CultureInfo.InvariantCulture)
                + "|hero2=" + (secondHero == null ? 0U : secondHero.HeroId).ToString(CultureInfo.InvariantCulture)
                + "|selectedArmyTypes=" + selectedArmyTypes.ToString(CultureInfo.InvariantCulture)
                + "|selectedTroops=" + selectedTroops.ToString(CultureInfo.InvariantCulture)
                + "|rows=" + armyRowCount.ToString(CultureInfo.InvariantCulture);
            if (!string.Equals(snapshot, _lastPendingSnapshot, StringComparison.Ordinal))
            {
                _lastPendingSnapshot = snapshot;
                MelonLogger.Msg("Auto-march native preview snapshot: " + snapshot);
                LogSelectedDeployment(panel, target, marchData);
            }

            if (!panel.IsOpen
                || command == null
                || command.TargetType != MarchCommandTarget.KMarchCommandTargetCollect
                || panelTargetId != _pendingTargetId)
            {
                return;
            }

            if (!HideDeploymentPanel(panel))
            {
                LogPendingAbort("The deployment panel could not be hidden for the background dispatch.");
                return;
            }

            if (!_nativeAutoSelectionAttempted)
            {
                _nativeAutoSelectionAttempted = true;
                try
                {
                    MelonLogger.Warning("Auto-march native auto-select: invoking DeployMainPanel.OnRecmPowerSelected().");
                    panel.OnRecmPowerSelected();
                    HideDeploymentPanel(panel);
                }
                catch (Exception ex)
                {
                    MelonLogger.Error("Auto-march native auto-select OnRecmPowerSelected failed: " + ex);
                }

                try
                {
                    MelonLogger.Warning("Auto-march native troop-fill: invoking DeployMainPanel.OnMaxClick().");
                    panel.OnMaxClick();
                    HideDeploymentPanel(panel);
                }
                catch (Exception ex)
                {
                    MelonLogger.Error("Auto-march native troop-fill OnMaxClick failed: " + ex);
                }

                if (_pendingFarmManaged
                    && _farmSettings != null
                    && _farmSlotIndex >= 0
                    && _farmSlotIndex < _farmSettings.Slots.Count)
                {
                    string selectionError = ApplyCustomSlotSelection(
                        panel, _farmSettings.Slots[_farmSlotIndex]);
                    if (selectionError != null)
                    {
                        LogPendingAbort(selectionError);
                        return;
                    }
                }

                return;
            }

            if (!panel.IsOpen
                || command == null
                || command.TargetType != MarchCommandTarget.KMarchCommandTargetCollect
                || panelTargetId != _pendingTargetId
                || marchData.CollectCapacity == 0
                || mainHero == null
                || mainHero.HeroId == 0
                || selectedArmyTypes == 0)
            {
                return;
            }

            int travelSeconds;
            string travelTimeError;
            if (!TryGetNativeTravelTime(panel, out travelSeconds, out travelTimeError))
            {
                if (DateTime.UtcNow - _lastRouteWaitLogUtc >= TimeSpan.FromSeconds(2))
                {
                    _lastRouteWaitLogUtc = DateTime.UtcNow;
                    MelonLogger.Msg("Auto-march route-time wait: " + travelTimeError);
                }
                return;
            }

            MelonLogger.Warning(
                "Auto-march native travel-time check: targetId="
                + target.EntityId.ToString(CultureInfo.InvariantCulture)
                + " routeDistance=" + panel.m_pathLength.ToString("F2", CultureInfo.InvariantCulture)
                + " travelSeconds=" + travelSeconds.ToString(CultureInfo.InvariantCulture)
                + " maximumSeconds=300");
            if (travelSeconds >= 300)
            {
                LogPendingAbort(
                    "The game's native travel-time calculation is "
                    + travelSeconds.ToString(CultureInfo.InvariantCulture)
                    + " seconds; only routes under five minutes are eligible.");
                return;
            }

            if (marchData.CollectCapacity != _requestedCollectCapacity)
            {
                uint boundedCapacity = (uint)Math.Min((ulong)_requestedCollectCapacity, (ulong)target.Remaining);
                if (boundedCapacity == 0)
                {
                    LogPendingAbort("The target no longer has stock for the requested 100-unit load.");
                    return;
                }

                marchData.CollectCapacity = boundedCapacity;
                requestData.commandData = marchData;
                panel.m_marchCommandRequestData = requestData;
                MelonLogger.Warning(
                    "Auto-march requested-load enforcement: game preview capacity was overridden after native "
                    + "army auto-selection; requested=" + boundedCapacity.ToString(CultureInfo.InvariantCulture)
                    + " targetRemaining=" + target.Remaining.ToString(CultureInfo.InvariantCulture));
            }

            MelonLogger.Warning(
                "Auto-march pre-GO validation passed: targetId="
                + target.EntityId.ToString(CultureInfo.InvariantCulture)
                + " ownerId=" + target.OwnerId.ToString(CultureInfo.InvariantCulture)
                + " remaining=" + target.Remaining.ToString(CultureInfo.InvariantCulture)
                + " load=" + marchData.CollectCapacity.ToString(CultureInfo.InvariantCulture)
                + " mainHero=" + DescribeHero(mainHero)
                + " secondHero=" + DescribeHero(secondHero)
                + " selectedArmyTypes=" + selectedArmyTypes.ToString(CultureInfo.InvariantCulture)
                + " selectedTroops=" + selectedTroops.ToString(CultureInfo.InvariantCulture)
                + " freeSlots=" + (maxSlots - (uint)usedSlots).ToString(CultureInfo.InvariantCulture));

            _testMarchAttempted = true;
            if (_pendingFarmManaged)
                _testMarchAttempted = false;
            _pendingGameOpen = false;
            _marchCountBeforeDispatch = usedSlots;
            _dispatchInvokedAtUtc = DateTime.UtcNow;
            _lastOutcomePollUtc = DateTime.MinValue;
            if (_pendingFarmManaged)
                _marchIdsBeforeDispatch = CapturePlayerMarchIds();
            if (_pendingFarmManaged && _marchIdsBeforeDispatch == null)
            {
                LogPendingAbort("Could not capture the pre-dispatch march IDs safely.");
                return;
            }

            try
            {
                panel.visible = false;
                MelonLogger.Warning(
                    "Auto-march stage=GO-invoked: panelWasVisible=" + _panelWasVisible
                    + " panelHidden=" + !panel.visible
                    + " targetId=" + target.EntityId.ToString(CultureInfo.InvariantCulture)
                    + " load=" + marchData.CollectCapacity.ToString(CultureInfo.InvariantCulture));
                LogPendingAbort("The legacy panel-based GO route is disabled; no march was sent.");
            }
            catch (Exception ex)
            {
                KeepDeploymentPanelHidden();
                MelonLogger.Error("Auto-march stage=GO-invoked failed; attempt remains locked: " + ex);
                MelonLogger.Warning("Auto-march was not retried after the GO exception.");
            }
        }

        private void PollDispatchOutcome()
        {
            TimeSpan elapsed = DateTime.UtcNow - _dispatchInvokedAtUtc;
            if (DateTime.UtcNow - _lastOutcomePollUtc < TimeSpan.FromSeconds(1))
                return;
            _lastOutcomePollUtc = DateTime.UtcNow;
            var player = AppCache.Player;
            int usedSlots = -1;
            if (player != null)
            {
                usedSlots = Math.Max(0, player.GetMarchCount());
            }
            else
            {
                MelonLogger.Warning("Auto-march outcome poll: PlayerCache is currently unavailable.");
            }

            ulong acceptedMarchId = _pendingFarmManaged
                ? FindNewFarmMarchEntityId(_pendingTargetId, _marchIdsBeforeDispatch)
                : 0UL;
            bool accepted = _pendingFarmManaged
                ? acceptedMarchId != 0
                : usedSlots > _marchCountBeforeDispatch;

            MelonLogger.Msg(
                "Auto-march outcome poll: elapsedSeconds="
                + elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)
                + " usedSlotsBefore=" + _marchCountBeforeDispatch.ToString(CultureInfo.InvariantCulture)
                + " usedSlotsNow=" + (usedSlots < 0
                    ? "<unavailable>"
                    : usedSlots.ToString(CultureInfo.InvariantCulture))
                + " slotCountChanged=" + (usedSlots > _marchCountBeforeDispatch)
                + " matchedNewMarchId=" + acceptedMarchId.ToString(CultureInfo.InvariantCulture)
                + " targetId=" + _pendingTargetId.ToString(CultureInfo.InvariantCulture));

            if (accepted)
            {
                MelonLogger.Warning(
                    _pendingFarmManaged
                        ? "Auto-march locally confirmed by matching a new march entity to target "
                            + _pendingTargetId.ToString(CultureInfo.InvariantCulture)
                            + "; entityId=" + acceptedMarchId.ToString(CultureInfo.InvariantCulture) + "."
                        : "Auto-march locally observed: used march slots increased from "
                            + _marchCountBeforeDispatch.ToString(CultureInfo.InvariantCulture)
                            + " to " + usedSlots.ToString(CultureInfo.InvariantCulture)
                            + ". Confirm the march target in-game.");
                _awaitingDispatchOutcome = false;
                if (_pendingFarmManaged)
                {
                    _activeFarmMarches.Add(new FarmMarchTracker
                    {
                        SlotIndex = _farmSlotIndex,
                        TargetId = _pendingTargetId,
                        PriorMarchIds = _marchIdsBeforeDispatch,
                        MarchEntityId = acceptedMarchId,
                        AcceptedAtUtc = DateTime.UtcNow,
                        NextPollUtc = DateTime.MinValue
                    });
                    _farmStatus = _farmRunning
                        ? "March active; filling available march slots ("
                            + _activeFarmMarches.Count.ToString(CultureInfo.InvariantCulture) + " farm marches)"
                        : "Stopped; active march continues in game";
                }
                _pendingFarmManaged = false;
                _marchIdsBeforeDispatch = null;
                return;
            }

            if (elapsed >= DispatchOutcomeTimeout)
            {
                string reason = _pendingFarmManaged
                    ? "no new march entity targeting "
                        + _pendingTargetId.ToString(CultureInfo.InvariantCulture)
                        + " was found within "
                        + DispatchOutcomeTimeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)
                        + " seconds"
                    : "used march slots did not increase within "
                        + DispatchOutcomeTimeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)
                        + " seconds";
                MelonLogger.Warning("Auto-march outcome is unconfirmed: " + reason + ". The request will not be retried.");
                _awaitingDispatchOutcome = false;
                if (_farmRunning && _pendingFarmManaged)
                {
                    _farmRunning = false;
                    _farmStatus = "Stopped: dispatch acceptance was not confirmed (" + reason + ")";
                }
                _pendingFarmManaged = false;
                _marchIdsBeforeDispatch = null;
            }
        }

        private void LogPendingAbort(string reason)
        {
            MelonLogger.Warning(
                "Auto-march preparation aborted: " + reason
                + " targetId=" + _pendingTargetId.ToString(CultureInfo.InvariantCulture)
                + " elapsedSeconds=" + (DateTime.UtcNow - _pendingSinceUtc)
                    .TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)
                + ". GO was not invoked.");
            _pendingGameOpen = false;
            _pendingTargetId = 0;
            _lastPendingSnapshot = null;
            if (_farmRunning && _pendingFarmManaged)
            {
                _farmRunning = false;
                _farmStatus = "Stopped: " + reason;
            }
            _pendingFarmManaged = false;
            CloseOrRevealHiddenDeploymentPanel("preparation aborted");
        }

        private void CloseOrRevealHiddenDeploymentPanel(string reason)
        {
            DeployMainPanel panel = _panelToRestore;
            if (panel == null || !panel.IsOpen)
            {
                RestoreDeploymentBackdrop(reason);
                ClearDeploymentPanelTracking();
                return;
            }

            try
            {
                panel.Close();
                if (panel.IsOpen)
                {
                    panel.visible = true;
                    RestoreDeploymentBackdrop(reason);
                    ClearDeploymentPanelTracking();
                    MelonLogger.Error(
                        "Auto-march safety recovery: deployment panel remained open after Close(); "
                        + "restored visibility so it cannot invisibly block the game UI. reason=" + reason);
                    return;
                }

                RestoreDeploymentBackdrop(reason);
                ClearDeploymentPanelTracking();
                MelonLogger.Warning(
                    "Auto-march safety recovery: closed hidden deployment panel and restored its backdrop state; "
                    + "reason=" + reason);
            }
            catch (Exception ex)
            {
                try
                {
                    panel.visible = true;
                }
                catch (Exception revealException)
                {
                    MelonLogger.Error(
                        "Auto-march safety recovery could not restore deployment-panel visibility: "
                        + revealException);
                }

                RestoreDeploymentBackdrop(reason);
                ClearDeploymentPanelTracking();
                MelonLogger.Error(
                    "Auto-march safety recovery could not close hidden deployment panel; "
                    + "attempted to restore visibility. reason=" + reason + " error=" + ex);
            }
        }

        private void RestoreDeploymentBackdrop(string reason)
        {
            if (!_maskVisibilityCaptured)
                return;

            try
            {
                if (_maskToRestore != null)
                {
                    _maskToRestore.visible = _maskWasVisible;
                    MelonLogger.Warning(
                        "Auto-march backdrop restored: visible=" + _maskWasVisible
                        + " reason=" + reason);
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Error(
                    "Auto-march backdrop visibility restore failed; reason=" + reason + " error=" + ex);
            }
            finally
            {
                _maskToRestore = null;
                _maskVisibilityCaptured = false;
            }
        }

        private void ClearDeploymentPanelTracking()
        {
            _panelToRestore = null;
            _panelVisibilityCaptured = false;
        }

        private void TryStartNextFarmMarch(WorldMapScanResult mapScan)
        {
            if (!_farmRunning || _farmSettings == null)
                return;

            if (mapScan == null || !mapScan.HasUsableNodeSnapshot)
            {
                _farmRunning = false;
                _farmStatus = "Stopped: no saved map snapshot";
                MelonLogger.Warning("Farm automation stopped because no saved node snapshot is available.");
                return;
            }

            var player = AppCache.Player;
            if (player == null)
            {
                _farmStatus = "Waiting for PlayerCache";
                _nextFarmAttemptUtc = DateTime.UtcNow + TimeSpan.FromSeconds(5);
                return;
            }

            uint maxSlots = player.GetDispatchMaxMarchCount();
            int usedSlots = Math.Max(0, player.GetMarchCount());
            if (maxSlots <= (uint)usedSlots)
            {
                _farmStatus = "Waiting for a free march slot";
                _nextFarmAttemptUtc = DateTime.UtcNow + TimeSpan.FromSeconds(5);
                return;
            }

            int slotCount = _farmSettings.Slots.Count;
            for (int attempt = 0; attempt < slotCount; attempt++)
            {
                int slotIndex = (_farmSlotIndex + 1 + attempt) % slotCount;
                FarmSlotSettings slot = _farmSettings.Slots[slotIndex];
                if (slot == null || !slot.Enabled || slot.TargetAmount <= 0)
                    continue;
                if (IsFarmSlotActive(slotIndex))
                    continue;

                string resource = slot.Resource.ToString();
                _farmSlotIndex = slotIndex;
                string result = SendMarch(mapScan, resource, slot.TargetAmount, true, slot);
                if (_pendingGameOpen || _awaitingDispatchOutcome)
                {
                    _farmStatus = "Slot " + (slotIndex + 1).ToString(CultureInfo.InvariantCulture)
                        + ": " + resource + " | " + result;
                    return;
                }

                MelonLogger.Warning(
                    "Farm slot " + (slotIndex + 1).ToString(CultureInfo.InvariantCulture)
                    + " could not start: " + result);
            }

            _farmStatus = _activeFarmMarches.Count > 0
                ? "All enabled slots are active; waiting for a march slot or return"
                : "No eligible target; retrying in 15 seconds";
            _nextFarmAttemptUtc = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        }

        private void PollActiveFarmMarches()
        {
            if (_activeFarmMarches.Count == 0)
                return;

            DateTime now = DateTime.UtcNow;
            bool anyCompleted = false;
            for (int i = _activeFarmMarches.Count - 1; i >= 0; i--)
            {
                FarmMarchTracker tracker = _activeFarmMarches[i];
                if (now < tracker.NextPollUtc)
                    continue;
                tracker.NextPollUtc = now + TimeSpan.FromSeconds(1);

                if (tracker.MarchEntityId == 0)
                {
                    tracker.MarchEntityId = FindNewFarmMarchEntityId(
                        tracker.TargetId, tracker.PriorMarchIds);
                    if (tracker.MarchEntityId == 0)
                    {
                        if (now - tracker.AcceptedAtUtc > TimeSpan.FromSeconds(30))
                        {
                            _farmRunning = false;
                            _farmStatus = "Stopped: could not identify a farm march safely";
                            MelonLogger.Warning(
                                "Farm stopped: accepted march entity could not be matched to target "
                                + tracker.TargetId.ToString(CultureInfo.InvariantCulture)
                                + "; no duplicate dispatch will be attempted.");
                        }
                        continue;
                    }

                    MelonLogger.Warning(
                        "Farm tracked march entity: slot="
                        + (tracker.SlotIndex + 1).ToString(CultureInfo.InvariantCulture)
                        + " entityId=" + tracker.MarchEntityId.ToString(CultureInfo.InvariantCulture)
                        + " targetId=" + tracker.TargetId.ToString(CultureInfo.InvariantCulture));
                }

                bool marchStillActive;
                if (!IsPlayerMarchActive(tracker.MarchEntityId, out marchStillActive))
                    continue;
                if (marchStillActive)
                {
                    tracker.MissingSinceUtc = DateTime.MinValue;
                    continue;
                }
                if (tracker.MissingSinceUtc == DateTime.MinValue)
                {
                    tracker.MissingSinceUtc = now;
                    continue;
                }
                if (now - tracker.MissingSinceUtc < MarchRemovalConfirmDelay)
                    continue;

                MelonLogger.Warning(
                    "Farm march returned: slot="
                    + (tracker.SlotIndex + 1).ToString(CultureInfo.InvariantCulture)
                    + " entityId=" + tracker.MarchEntityId.ToString(CultureInfo.InvariantCulture));
                _activeFarmMarches.RemoveAt(i);
                anyCompleted = true;
            }

            if (anyCompleted && _farmRunning)
            {
                _farmStatus = "A march returned; filling available march slots ("
                    + _activeFarmMarches.Count.ToString(CultureInfo.InvariantCulture) + " active)";
                _nextFarmAttemptUtc = DateTime.UtcNow;
            }
        }

        private bool IsFarmSlotActive(int slotIndex)
        {
            for (int i = 0; i < _activeFarmMarches.Count; i++)
            {
                if (_activeFarmMarches[i].SlotIndex == slotIndex)
                    return true;
            }

            return false;
        }

        private HashSet<ulong> GetActiveFarmTargetIds()
        {
            var targetIds = new HashSet<ulong>();
            for (int i = 0; i < _activeFarmMarches.Count; i++)
                targetIds.Add(_activeFarmMarches[i].TargetId);
            return targetIds;
        }

        private static HashSet<ulong> CapturePlayerMarchIds()
        {
            var ids = new HashSet<ulong>();
            var player = AppCache.Player;
            if (player == null)
                return ids;

            var marchIds = player.GetAllMarchEntities();
            if (marchIds == null)
                return ids;
            int marchCount = Math.Max(0, player.GetMarchCount());
            try
            {
                for (int i = 0; i < marchCount; i++)
                    ids.Add(marchIds[i]);
            }
            catch (Exception ex)
            {
                MelonLogger.Error("Could not snapshot current march entity IDs: " + ex);
                return null;
            }
            return ids;
        }

        private static ulong FindNewFarmMarchEntityId(ulong targetId, HashSet<ulong> priorMarchIds)
        {
            var player = AppCache.Player;
            var worldMap = AppCache.WorldMap;
            if (player == null || worldMap == null)
                return 0;

            var marchIds = player.GetAllMarchEntities();
            if (marchIds == null)
                return 0;
            int marchCount = Math.Max(0, player.GetMarchCount());
            var newMarchIds = new List<ulong>();
            try
            {
                for (int i = 0; i < marchCount; i++)
                {
                    ulong marchId = marchIds[i];
                    if (marchId == 0)
                        continue;
                    if (priorMarchIds != null && priorMarchIds.Contains(marchId))
                        continue;
                    newMarchIds.Add(marchId);

                    GEntity entity = worldMap.MainEntity == null
                        ? null
                        : worldMap.MainEntity.GetEntity(marchId);
                    if (entity == null && worldMap.Entity != null)
                        entity = worldMap.Entity.GetEntity(marchId);
                    ECompMarchCommand commandComponent = GetMarchCommandComponent(entity);
                    MarchCommand command = commandComponent == null
                        ? null
                        : commandComponent.MarchCommandData;
                    if (command != null && command.TargetId == targetId
                        && command.TargetType == MarchCommandTarget.KMarchCommandTargetCollect)
                    {
                        return marchId;
                    }
                }

                if (newMarchIds.Count == 1)
                {
                    MelonLogger.Warning(
                        "Auto-march accepted entity identified from the single new player march ID; "
                        + "the active map cache does not expose its target component. entityId="
                        + newMarchIds[0].ToString(CultureInfo.InvariantCulture)
                        + " targetId=" + targetId.ToString(CultureInfo.InvariantCulture));
                    return newMarchIds[0];
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Error("Could not identify the accepted farm march: " + ex);
            }

            return 0;
        }

        private static bool IsPlayerMarchActive(ulong marchEntityId, out bool isActive)
        {
            isActive = false;
            var player = AppCache.Player;
            var worldMap = AppCache.WorldMap;
            if (player == null || worldMap == null)
                return false;

            var marchIds = player.GetAllMarchEntities();
            if (marchIds == null)
                return false;
            int marchCount = Math.Max(0, player.GetMarchCount());
            try
            {
                for (int i = 0; i < marchCount; i++)
                {
                    if (marchIds[i] != marchEntityId)
                        continue;
                    isActive = true;
                    return true;
                }

                GEntity entity = worldMap.MainEntity == null
                    ? null
                    : worldMap.MainEntity.GetEntity(marchEntityId);
                if (entity == null && worldMap.Entity != null)
                    entity = worldMap.Entity.GetEntity(marchEntityId);
                isActive = entity != null;
                return true;
            }
            catch (Exception ex)
            {
                MelonLogger.Error("Could not verify march presence in player and world-map entities: " + ex);
                return false;
            }
        }

        private static ECompMarchCommand GetMarchCommandComponent(GEntity entity)
        {
            if (entity == null || entity.Components == null)
                return null;

            var components = entity.Components;
            for (int i = 0; i < components.Count; i++)
            {
                var component = components[i];
                if (component == null)
                    continue;

                var componentType = component.m_typeId.GetCompType();
                if (componentType == null || componentType.Name != nameof(ECompMarchCommand))
                    continue;

                var typedComponent = entity.GetComponent(componentType);
                if (typedComponent != null)
                    return Il2CppObjectPool.Get<ECompMarchCommand>(
                        IL2CPP.Il2CppObjectBaseToPtr(typedComponent));
            }

            return null;
        }

        private bool HideDeploymentPanel(DeployMainPanel panel)
        {
            try
            {
                if (!_panelVisibilityCaptured)
                {
                    _panelToRestore = panel;
                    _panelWasVisible = panel.visible;
                    _panelVisibilityCaptured = true;
                }
                else if (_panelToRestore == null || !_panelToRestore.IsOpen)
                {
                    _panelToRestore = panel;
                }

                HideDeploymentBackdrop(panel, _pendingTargetId, "while-hiding");
                bool wasVisible = panel.visible;
                panel.visible = false;
                if (wasVisible)
                {
                    MelonLogger.Warning(
                        "Auto-march stage=preview-hidden: wasVisible=true"
                        + " isHidden=" + !panel.visible
                        + " targetId=" + _pendingTargetId.ToString(CultureInfo.InvariantCulture));
                }
                return !panel.visible;
            }
            catch (Exception ex)
            {
                MelonLogger.Error("Auto-march deployment panel hide failed: " + ex);
                return false;
            }
        }

        private void EnforceDeploymentPanelHidden()
        {
            if (!_panelVisibilityCaptured)
                return;

            try
            {
                if (_panelToRestore != null && _panelToRestore.IsOpen)
                {
                    HideDeploymentBackdrop(_panelToRestore, _pendingTargetId, "update-enforcement");
                    if (_panelToRestore.visible)
                    {
                        _panelToRestore.visible = false;
                        MelonLogger.Warning(
                            "Auto-march panel re-hidden after native UI update: isHidden="
                            + !_panelToRestore.visible);
                    }
                    return;
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Error("Auto-march panel hide enforcement failed: " + ex);
            }

            RestoreDeploymentBackdrop("deployment panel closed");
            ClearDeploymentPanelTracking();
        }

        private void HideDeploymentBackdrop(DeployMainPanel panel, ulong targetId, string stage)
        {
            if (panel == null)
                return;

            try
            {
                var mask = panel.m_mask;
                if (!_maskVisibilityCaptured && mask != null)
                {
                    _maskToRestore = mask;
                    _maskWasVisible = mask.visible;
                    _maskVisibilityCaptured = true;
                }
                bool? maskWasVisible = mask == null ? (bool?)null : mask.visible;
                if (mask != null && mask.visible)
                    mask.visible = false;

                string diagnostic = "stage=" + stage
                    + " panelVisible=" + panel.visible
                    + " panelOption=" + panel.Option
                    + " mask=" + (mask == null
                        ? "<null>"
                        : "name=" + mask.name
                            + ",wasVisible=" + maskWasVisible
                            + ",visible=" + mask.visible
                            + ",alpha=" + mask.alpha.ToString("F2", CultureInfo.InvariantCulture)
                            + ",resourceURL=" + mask.resourceURL)
                    + " targetId=" + targetId.ToString(CultureInfo.InvariantCulture);
                string signature = diagnostic
                    .Replace("stage=" + stage + " ", string.Empty);
                if (!string.Equals(_lastBackdropDiagnostic, signature, StringComparison.Ordinal))
                {
                    _lastBackdropDiagnostic = signature;
                    MelonLogger.Warning("Auto-march deployment backdrop diagnostic: " + diagnostic);
                }

                if (maskWasVisible == true)
                {
                    MelonLogger.Warning(
                        "Auto-march deployment m_mask hidden: stage=" + stage
                        + " targetId=" + targetId.ToString(CultureInfo.InvariantCulture));
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    "Auto-march deployment backdrop inspection failed: stage=" + stage + " error=" + ex);
            }
        }

        private static DeployMainPanel GetCachedDeployPanel(PanelMgr panelManager)
        {
            if (panelManager == null)
                return null;

            try
            {
                return panelManager.GetPanel<DeployMainPanel>();
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("Auto-march cached deployment-panel lookup failed: " + ex.Message);
                return null;
            }
        }

        private void KeepDeploymentPanelHidden()
        {
            EnforceDeploymentPanelHidden();
        }

        private static MarchTargetType GetMarchTargetType(string resource)
        {
            switch (resource)
            {
                case "Food":
                    return MarchTargetType.CollectFood;
                case "Wood":
                    return MarchTargetType.CollectWood;
                case "Steel":
                    return MarchTargetType.CollectSteal;
                case "Oil":
                    return MarchTargetType.CollectOil;
                default:
                    throw new ArgumentOutOfRangeException(nameof(resource), resource, "Unknown farm resource.");
            }
        }

        private static CollectType GetCollectType(string resource)
        {
            switch (resource)
            {
                case "Food":
                    return CollectType.KCollectTypeFood;
                case "Wood":
                    return CollectType.KCollectTypeWood;
                case "Steel":
                    return CollectType.KCollectTypeSteel;
                case "Oil":
                    return CollectType.KCollectTypeOil;
                default:
                    throw new ArgumentOutOfRangeException(nameof(resource), resource, "Unknown farm resource.");
            }
        }

        private static string Skip(string reason)
        {
            MelonLogger.Warning("Food preview diagnostic skipped: " + reason);
            return reason;
        }

        private static NearestResourceNodeInfo FindNearestFreeFood(WorldMapScanResult mapScan)
        {
            return FindNearestFreeResource(mapScan, "Food", 0);
        }

        private static NearestResourceNodeInfo FindNearestFreeResource(
            WorldMapScanResult mapScan,
            string resource,
            long minimumRemaining = 0,
            HashSet<ulong> excludedEntityIds = null)
        {
            NearestResourceNodeInfo nearest = null;
            for (int i = 0; i < mapScan.LoadedResourceNodes.Count; i++)
            {
                NearestResourceNodeInfo candidate = mapScan.LoadedResourceNodes[i];
                if (!string.Equals(candidate.Resource, resource, StringComparison.Ordinal)
                    || candidate.OwnerId != 0
                    || candidate.Remaining == 0)
                {
                    continue;
                }
                if (excludedEntityIds != null && excludedEntityIds.Contains(candidate.EntityId))
                    continue;

                if (candidate.Remaining < minimumRemaining)
                    continue;

                if (nearest == null || candidate.DistanceSquared < nearest.DistanceSquared)
                    nearest = candidate;
            }

            return nearest;
        }

        private static ulong GetPanelTargetId(DeployMainPanel panel)
        {
            if (panel == null)
                return 0;

            MarchCommand command = panel.m_marchCommandRequestData.commandData.Command;
            return command == null ? 0UL : command.TargetId;
        }

        private static void LogSelectedDeployment(
            DeployMainPanel panel,
            NearestResourceNodeInfo target,
            MarchRequestData marchData)
        {
            MelonLogger.Msg(
                "Auto-march panel details: name=" + panel.Name
                + " isOpen=" + panel.IsOpen
                + " visible=" + panel.visible
                + " targetId=" + target.EntityId.ToString(CultureInfo.InvariantCulture)
                + " resource=" + target.Resource
                + " remaining=" + target.Remaining.ToString(CultureInfo.InvariantCulture)
                + " collectCapacity=" + marchData.CollectCapacity.ToString(CultureInfo.InvariantCulture)
                + " mainHero=" + DescribeHero(panel.SelectedMainHero)
                + " secondHero=" + DescribeHero(panel.SelectedSecondHero)
                + " armyRows=" + (panel.m_armyDataList == null
                    ? "null"
                    : panel.m_armyDataList.Count.ToString(CultureInfo.InvariantCulture)));

            if (panel.m_armyDataList == null)
                return;

            for (int i = 0; i < panel.m_armyDataList.Count; i++)
            {
                DeployMainPanel.ArmyForSelect row = panel.m_armyDataList[i];
                if (row == null || row.SelectArmyCount == 0)
                    continue;

                string troopName = row.TroopConfig == null
                    ? "<unknown>"
                    : row.TroopConfig.GetName();
                MelonLogger.Msg(
                    "Auto-march selected troop: row=" + i.ToString(CultureInfo.InvariantCulture)
                    + " id=" + row.ArmyId.ToString(CultureInfo.InvariantCulture)
                    + " name=" + (string.IsNullOrEmpty(troopName) ? "<empty>" : troopName)
                    + " selected=" + row.SelectArmyCount.ToString(CultureInfo.InvariantCulture)
                    + " available=" + row.ArmyCount.ToString(CultureInfo.InvariantCulture));
            }
        }

        private static string ApplyCustomSlotSelection(DeployMainPanel panel, FarmSlotSettings slot)
        {
            bool hasCustomHeroes = slot.UseCustomHeroes;
            bool hasCustomTroop = slot.Troops != null && slot.Troops.Count > 0;
            if (!hasCustomHeroes && !hasCustomTroop)
                return null;

            if (hasCustomHeroes)
            {
                if (slot.MainHeroId == 0)
                    return "Custom hero selection requires a main hero; no march was sent.";
                if (slot.SecondHeroId != 0 && slot.SecondHeroId == slot.MainHeroId)
                    return "Main and second hero must be different; no march was sent.";

                var heroCache = AppCache.Hero;
                if (heroCache == null || heroCache.HeroInfos == null)
                    return "The game hero roster is unavailable; no march was sent.";

                HeroInfo mainHero = null;
                HeroInfo secondHero = null;
                for (int i = 0; i < heroCache.HeroInfos.Count; i++)
                {
                    HeroInfo hero = heroCache.HeroInfos[i];
                    if (hero == null)
                        continue;
                    if (hero.HeroId == slot.MainHeroId)
                        mainHero = hero;
                    if (slot.SecondHeroId != 0 && hero.HeroId == slot.SecondHeroId)
                        secondHero = hero;
                }

                if (mainHero == null)
                    return "Configured main hero #" + slot.MainHeroId
                        + " is no longer in the game roster; no march was sent.";
                if (slot.SecondHeroId != 0 && secondHero == null)
                    return "Configured second hero #" + slot.SecondHeroId
                        + " is no longer in the game roster; no march was sent.";

                panel.SelectedMainHero = mainHero;
                panel.SelectedSecondHero = secondHero;
            }

            if (hasCustomTroop)
            {
                if (panel.m_armyDataList == null)
                    return "Native troop selection rows are unavailable; no march was sent.";

                var rowsById = new Dictionary<uint, DeployMainPanel.ArmyForSelect>();
                for (int i = 0; i < panel.m_armyDataList.Count; i++)
                {
                    DeployMainPanel.ArmyForSelect row = panel.m_armyDataList[i];
                    if (row != null)
                        rowsById[row.ArmyId] = row;
                }

                for (int i = 0; i < panel.m_armyDataList.Count; i++)
                {
                    DeployMainPanel.ArmyForSelect row = panel.m_armyDataList[i];
                    if (row != null)
                        row.SelectArmyCount = 0;
                }

                ulong totalTroops = 0;
                for (int i = 0; i < slot.Troops.Count; i++)
                {
                    FarmTroopSelection selection = slot.Troops[i];
                    DeployMainPanel.ArmyForSelect selectedRow;
                    if (selection == null || selection.Id == 0 || selection.Count <= 0)
                        return "A saved troop selection is invalid; no march was sent.";
                    if (!rowsById.TryGetValue(selection.Id, out selectedRow))
                        return "Configured troop #" + selection.Id
                            + " is not selectable for this march; no march was sent.";

                    ulong requestedCount = (ulong)selection.Count;
                    if (requestedCount > selectedRow.ArmyCount)
                        return "Configured troop count " + requestedCount.ToString(CultureInfo.InvariantCulture)
                            + " exceeds available " + selectedRow.ArmyCount.ToString(CultureInfo.InvariantCulture)
                            + "; no march was sent.";

                    selectedRow.SelectArmyCount = requestedCount;
                    totalTroops += requestedCount;
                }

                if (panel.m_canTakeMaxCount > 0 && totalTroops > panel.m_canTakeMaxCount)
                    return "Configured troop count exceeds this march's native troop limit "
                        + panel.m_canTakeMaxCount.ToString(CultureInfo.InvariantCulture)
                        + "; no march was sent.";
            }

            MelonLogger.Warning(
                "Auto-march configured slot selection applied: mainHero="
                + (hasCustomHeroes ? slot.MainHeroId.ToString(CultureInfo.InvariantCulture) : "<native>")
                + " secondHero="
                + (hasCustomHeroes
                    ? slot.SecondHeroId.ToString(CultureInfo.InvariantCulture)
                    : "<native>")
                + " troops="
                + (hasCustomTroop ? DescribeConfiguredTroops(slot.Troops) : "<native>"));
            return null;
        }

        private static bool TryBuildArmyData(
            FarmSlotSettings slot,
            out ArmyData armyData,
            out string error)
        {
            armyData = null;
            error = null;
            if (slot == null)
            {
                error = "Direct dispatch requires a configured farm slot; no deployment panel will be opened.";
                return false;
            }
            if (!slot.UseCustomHeroes || slot.MainHeroId == 0)
            {
                error = "Choose a main hero and enable custom heroes for this slot; direct dispatch does not open the deployment panel.";
                return false;
            }
            if (slot.SecondHeroId != 0 && slot.SecondHeroId == slot.MainHeroId)
            {
                error = "Main and second hero must be different; no march was sent.";
                return false;
            }
            if (slot.Troops == null || slot.Troops.Count == 0)
            {
                error = "Choose at least one troop type with a positive count for this slot; no march was sent.";
                return false;
            }

            HeroCache heroCache = AppCache.Hero;
            if (heroCache == null || heroCache.HeroInfos == null)
            {
                error = "The game hero roster is unavailable; no march was sent.";
                return false;
            }

            HeroInfo mainHero = null;
            HeroInfo secondHero = null;
            for (int i = 0; i < heroCache.HeroInfos.Count; i++)
            {
                HeroInfo hero = heroCache.HeroInfos[i];
                if (hero == null)
                    continue;
                if (hero.HeroId == slot.MainHeroId)
                    mainHero = hero;
                if (slot.SecondHeroId != 0 && hero.HeroId == slot.SecondHeroId)
                    secondHero = hero;
            }

            if (mainHero == null)
            {
                error = "Configured main hero #" + slot.MainHeroId
                    + " is no longer in the game roster; no march was sent.";
                return false;
            }
            if (slot.SecondHeroId != 0 && secondHero == null)
            {
                error = "Configured second hero #" + slot.SecondHeroId
                    + " is no longer in the game roster; no march was sent.";
                return false;
            }

            var troopCache = AppCache.Troop;
            if (troopCache == null)
            {
                error = "The game troop roster is unavailable; no march was sent.";
                return false;
            }

            var troopInfos = troopCache.GetUniqueTroopArmyInfos();
            var troopInfoById = new Dictionary<uint, ArmyInfo>();
            if (troopInfos != null)
            {
                for (int i = 0; i < troopInfos.Count; i++)
                {
                    ArmyInfo troop = troopInfos[i];
                    if (troop != null)
                        troopInfoById[troop.ArmyId] = troop;
                }
            }

            ulong totalAvailableTroops = 0;
            for (int i = 0; i < slot.Troops.Count; i++)
            {
                FarmTroopSelection selection = slot.Troops[i];
                ArmyInfo troopInfo;
                if (selection == null
                    || selection.Id == 0
                    || selection.Count <= 0
                    || (ulong)selection.Count > uint.MaxValue)
                {
                    error = "A saved troop selection is invalid; no march was sent.";
                    return false;
                }

                if (!troopInfoById.TryGetValue(selection.Id, out troopInfo))
                {
                    error = "Configured troop #" + selection.Id
                        + " is no longer in the game roster; no march was sent.";
                    return false;
                }

                if ((ulong)selection.Count > troopInfo.Count)
                {
                    error = "Configured troop count " + selection.Count.ToString(CultureInfo.InvariantCulture)
                        + " exceeds the available count " + troopInfo.Count.ToString(CultureInfo.InvariantCulture)
                        + " for troop #" + selection.Id.ToString(CultureInfo.InvariantCulture)
                        + "; no march was sent.";
                    return false;
                }

                totalAvailableTroops += troopInfo.Count;
            }

            armyData = new ArmyData
            {
                Hero1 = mainHero.HeroId,
                Hero1Level = mainHero.HeroLv,
                Hero1IsAwaken = mainHero.IsAwaken ? 1U : 0U,
                Hero1SkinId = mainHero.HeroSkinId,
                Hero1InitBattlePower = mainHero.HeroBattlePower,
                Hero2 = secondHero == null ? 0U : secondHero.HeroId,
                Hero2Level = secondHero == null ? 0U : secondHero.HeroLv,
                Hero2IsAwaken = secondHero != null && secondHero.IsAwaken ? 1U : 0U,
                Hero2SkinId = secondHero == null ? 0U : secondHero.HeroSkinId,
                Hero2InitBattlePower = secondHero == null ? 0UL : secondHero.HeroBattlePower
            };

            for (int i = 0; i < slot.Troops.Count; i++)
            {
                FarmTroopSelection selection = slot.Troops[i];
                var troopData = new TroopData
                {
                    TroopId = selection.Id,
                    TroopUints = (uint)selection.Count
                };
                armyData.InitTroops.Add(troopData);
                armyData.CurrentTroops.Add(new TroopData
                {
                    TroopId = selection.Id,
                    TroopUints = (uint)selection.Count
                });
            }

            MelonLogger.Warning(
                "Auto-march direct army built from slot: hero1=" + mainHero.HeroId
                + " hero1Level=" + mainHero.HeroLv
                + " hero2=" + (secondHero == null ? 0U : secondHero.HeroId)
                + " troops=" + DescribeConfiguredTroops(slot.Troops)
                + " troopAvailable=" + totalAvailableTroops);
            return true;
        }

        private static NearestResourceNodeInfo FindNodeById(WorldMapScanResult mapScan, ulong entityId)
        {
            if (entityId == 0)
                return null;

            for (int i = 0; i < mapScan.LoadedResourceNodes.Count; i++)
            {
                NearestResourceNodeInfo node = mapScan.LoadedResourceNodes[i];
                if (node.EntityId == entityId)
                    return node;
            }

            return null;
        }

        private static DeployMainPanel FindOpenDeployPanel(
            PanelMgr panelManager,
            bool logLookups = true)
        {
            string[] panelKeys =
            {
                "DeployMainPanel",
                "BaseDeployMainPanel",
                BaseDeployMainPanel.URL
            };

            for (int i = 0; i < panelKeys.Length; i++)
            {
                string key = panelKeys[i];
                if (string.IsNullOrEmpty(key))
                    continue;

                IPanel panel = panelManager.GetPanelByName(key);
                if (panel == null)
                {
                    if (logLookups)
                        MelonLogger.Msg("Food preview panel key lookup: key=" + key + " result=null");
                    continue;
                }

                if (logLookups)
                {
                    MelonLogger.Msg("Food preview panel key lookup: key=" + key
                        + " name=" + (string.IsNullOrEmpty(panel.Name) ? "<empty>" : panel.Name)
                        + " isOpen=" + panel.IsOpen);
                }
                if (!panel.IsOpen)
                    continue;

                if (string.Equals(panel.Name, "DeployMainPanel", StringComparison.Ordinal))
                    return Il2CppObjectPool.Get<DeployMainPanel>(IL2CPP.Il2CppObjectBaseToPtr(panel));
            }

            return null;
        }

        private static string DescribeHero(HeroInfo hero)
        {
            if (hero == null || hero.HeroId == 0)
                return "<none>";

            HeroDao dao = HeroDao.Inst;
            string heroName = dao == null ? string.Empty : dao.GetHeroName(hero.HeroId);
            return hero.HeroId.ToString(CultureInfo.InvariantCulture)
                + ":" + (string.IsNullOrEmpty(heroName) ? "<unknown>" : heroName);
        }

        private static string DescribeConfiguredTroops(List<FarmTroopSelection> troops)
        {
            if (troops == null || troops.Count == 0)
                return "<none>";

            var parts = new List<string>(troops.Count);
            for (int i = 0; i < troops.Count; i++)
            {
                FarmTroopSelection troop = troops[i];
                if (troop == null)
                    continue;
                parts.Add((string.IsNullOrEmpty(troop.Name)
                        ? troop.Id.ToString(CultureInfo.InvariantCulture)
                        : troop.Name)
                    + "x" + troop.Count.ToString(CultureInfo.InvariantCulture));
            }

            return string.Join(",", parts);
        }

        private static void LogArmyDataPayload(string label, ArmyData army)
        {
            MelonLogger.Warning(
                "Auto-march ArmyData payload: source=" + label
                + " hero1=" + army.Hero1.ToString(CultureInfo.InvariantCulture)
                + " hero1Level=" + army.Hero1Level.ToString(CultureInfo.InvariantCulture)
                + " hero1Awaken=" + army.Hero1IsAwaken.ToString(CultureInfo.InvariantCulture)
                + " hero1Skin=" + army.Hero1SkinId.ToString(CultureInfo.InvariantCulture)
                + " hero1Power=" + army.Hero1InitBattlePower.ToString(CultureInfo.InvariantCulture)
                + " hero1BreakAll=" + army.IsHero1BreakAll
                + " hero2=" + army.Hero2.ToString(CultureInfo.InvariantCulture)
                + " hero2Level=" + army.Hero2Level.ToString(CultureInfo.InvariantCulture)
                + " hero2Awaken=" + army.Hero2IsAwaken.ToString(CultureInfo.InvariantCulture)
                + " hero2Skin=" + army.Hero2SkinId.ToString(CultureInfo.InvariantCulture)
                + " hero2Power=" + army.Hero2InitBattlePower.ToString(CultureInfo.InvariantCulture)
                + " hero2BreakAll=" + army.IsHero2BreakAll
                + " pet1=" + army.Pet1SerialId.ToString(CultureInfo.InvariantCulture)
                + " pet2=" + army.Pet2SerialId.ToString(CultureInfo.InvariantCulture)
                + " radius=" + army.Radius.ToString(CultureInfo.InvariantCulture)
                + " initTroops=" + DescribeTroopPayload(army.InitTroops)
                + " currentTroops=" + DescribeTroopPayload(army.CurrentTroops)
                + " slightWoodTroops=" + DescribeTroopPayload(army.SlightWoodTroops)
                + " dispatchInvoked=false");
        }

        private static string DescribeTroopPayload(
            Il2CppGoogle.Protobuf.Collections.RepeatedField<TroopData> troops)
        {
            if (troops == null)
                return "<null>";

            var parts = new List<string>();
            for (int i = 0; i < troops.Count; i++)
            {
                TroopData troop = troops[i];
                if (troop == null)
                {
                    parts.Add("<null>");
                    continue;
                }

                parts.Add(troop.TroopId.ToString(CultureInfo.InvariantCulture)
                    + "x" + troop.TroopUints.ToString(CultureInfo.InvariantCulture));
            }

            return parts.Count == 0 ? "<empty>" : string.Join(",", parts);
        }

        private static bool TryGetNativeTravelTime(
            DeployMainPanel panel,
            out int travelSeconds,
            out string error)
        {
            travelSeconds = 0;
            error = null;

            if (!panel.m_pathsRequested || panel.m_pathLength <= 0f)
            {
                error = "waiting for the game's route request (requested="
                    + panel.m_pathsRequested + ", routeDistance="
                    + panel.m_pathLength.ToString("F2", CultureInfo.InvariantCulture) + ").";
                return false;
            }

            if (panel.m_armyDataList == null || panel.m_armyDataList.Count == 0)
            {
                error = "native route is ready, but selected troop data is unavailable.";
                return false;
            }

            var troops = new Il2CppSystem.Collections.Generic.List<TroopData>();
            for (int i = 0; i < panel.m_armyDataList.Count; i++)
            {
                DeployMainPanel.ArmyForSelect row = panel.m_armyDataList[i];
                if (row == null || row.SelectArmyCount == 0)
                    continue;
                if (row.SelectArmyCount > uint.MaxValue)
                {
                    error = "selected troop count exceeds the native time API range.";
                    return false;
                }

                troops.Add(new TroopData
                {
                    TroopId = row.ArmyId,
                    TroopUints = (uint)row.SelectArmyCount
                });
            }

            if (troops.Count == 0)
            {
                error = "native route is ready, but no troops are selected.";
                return false;
            }

            try
            {
                travelSeconds = MarchHelper.ComputeMoveTime(
                    panel.SelectedMainHero,
                    panel.SelectedSecondHero,
                    troops,
                    panel.m_pathLength);
                if (travelSeconds <= 0)
                {
                    error = "the game's travel-time API returned "
                        + travelSeconds.ToString(CultureInfo.InvariantCulture) + ".";
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = "the game's travel-time API threw: " + ex.Message;
                MelonLogger.Error("Auto-march native travel-time calculation failed: " + ex);
                return false;
            }
        }
    }
}
