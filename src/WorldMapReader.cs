using System;
using System.Collections.Generic;
using System.Globalization;
using Il2CppIGG.Game.Data.Cache;
using Il2CppIGG.Game.Data.Cache.Collect.Entity.Comp;
using Il2CppIGG.Game.Data.Config;
using Il2CppIGG.Game.Data.Entity;
using Il2CppIGG.Game.Data.Cache.WorldMap.Entity;
using Il2CppIGG.Game.Data.Cache.WorldMap.Entity.Comp;
using Il2CppIGG.Game.Helper;
using Il2CppIGG.Game.Module.March;
using Il2CppIGG.Game.Module.WorldMap.Comps;
using Il2CppIGG.Game.Module.WorldMap.Help;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Runtime;
using Il2CppProtomsg;
using MelonLoader;
using Unity.Mathematics;
using LiveEntityList = Il2CppSystem.Collections.Generic.IReadOnlyList<Il2CppIGG.Game.Data.Entity.GEntity>;

namespace FesticalAutoFarm
{
    internal enum WorldMapState
    {
        CacheUnavailable,
        NotOnWorldMap,
        OnWorldMap
    }

    internal sealed class WorldMapScanResult
    {
        public WorldMapScanResult()
        {
        }

        public bool HasScanned { get; set; }
        public bool IsCacheOnlyProbe { get; set; }
        public bool IsPersistedSnapshot { get; set; }
        public bool CurrentOriginValidated { get; set; }
        public bool SnapshotOriginChanged { get; set; }
        public DateTime SnapshotCapturedAtUtc { get; set; }
        public bool HasUsableNodeSnapshot
        {
            get
            {
                return HasScanned
                    && LoadedResourceNodes != null
                    && LoadedResourceNodes.Count > 0
                    && (State == WorldMapState.OnWorldMap || IsPersistedSnapshot);
            }
        }
        public WorldMapState State { get; set; } = WorldMapState.CacheUnavailable;
        public int LoadedCollectNodeCount { get; set; }
        public int NodesWithCollectReference { get; set; }
        public int MainEntityCount { get; set; }
        public int MainCollectEntityCount { get; set; }
        public int MainMapObjectCount { get; set; }
        public int WorldEntityCount { get; set; }
        public int WorldCollectEntityCount { get; set; }
        public int WorldMapObjectCount { get; set; }
        public int NodesWithCollectData { get; set; }
        public ulong TotalRemainingResource { get; set; }
        public int FoodNodeCount { get; set; }
        public int WoodNodeCount { get; set; }
        public int SteelNodeCount { get; set; }
        public int OilNodeCount { get; set; }
        public int UnknownResourceNodeCount { get; set; }
        public ulong FoodRemaining { get; set; }
        public ulong WoodRemaining { get; set; }
        public ulong SteelRemaining { get; set; }
        public ulong OilRemaining { get; set; }
        public bool HasMarchSlotInfo { get; set; }
        public uint MaxMarchSlots { get; set; }
        public int UsedMarchSlots { get; set; }
        public uint FreeMarchSlots { get; set; }
        public string MarchSlotError { get; set; }
        public bool HasCollectCapacity { get; set; }
        public uint FoodCollectCapacity { get; set; }
        public uint WoodCollectCapacity { get; set; }
        public uint SteelCollectCapacity { get; set; }
        public uint OilCollectCapacity { get; set; }
        public string CollectCapacityError { get; set; }
        public bool HasCastlePosition { get; set; }
        public float CastleX { get; set; }
        public float CastleY { get; set; }
        public bool HasCastleGridPosition { get; set; }
        public int CastleGridX { get; set; }
        public int CastleGridY { get; set; }
        public string CastlePositionError { get; set; }
        public List<string> SampleNodes { get; set; } = new List<string>();
        public List<NearestResourceNodeInfo> NearestResourceNodes { get; set; } = new List<NearestResourceNodeInfo>();
        public List<NearestResourceNodeInfo> LoadedResourceNodes { get; set; } = new List<NearestResourceNodeInfo>();
        public List<string> Diagnostics { get; set; } = new List<string>();
    }

    internal sealed class NearestResourceNodeInfo
    {
        public NearestResourceNodeInfo()
        {
        }

        public string Resource { get; set; }
        public ulong EntityId { get; set; }
        public float X { get; set; }
        public float Y { get; set; }
        public bool HasGridPosition { get; set; }
        public int GridX { get; set; }
        public int GridY { get; set; }
        public uint Remaining { get; set; }
        public ulong OwnerId { get; set; }
        public string OwnerName { get; set; }
        public ulong CollectByEntityId { get; set; }
        public double Distance { get; set; }
        public double DistanceSquared { get; set; }
    }

    internal sealed class WorldMapReader
    {
        private const int MaximumSampleNodes = 5;
        private static readonly TimeSpan MapRequestWaitTimeout = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan MapRequestPollInterval = TimeSpan.FromSeconds(1);
        private bool _mapRequestPending;
        private DateTime _mapRequestDeadlineUtc;
        private DateTime _nextMapRequestCheckUtc;
        private readonly List<NearestResourceNodeInfo> _liveResourceNodes =
            new List<NearestResourceNodeInfo>();
        private readonly Dictionary<ulong, NearestResourceNodeInfo> _liveResourceNodesById =
            new Dictionary<ulong, NearestResourceNodeInfo>();
        private readonly Action<LiveEntityList> _liveEntityChangeHandler;
        private GEntityContainer _boundMainEntityContainer;
        private GEntityContainer _boundWorldEntityContainer;
        private IntPtr _boundMainEntityContainerPtr;
        private IntPtr _boundWorldEntityContainerPtr;
        private uint _lastMainEntitiesVersion;
        private uint _lastWorldEntitiesVersion;
        private bool _hasLiveEntityVersions;
        private bool _liveNodesDirty = true;
        private DateTime _nextLiveNodeScanUtc;
        private string _lastLiveScanError;

        public WorldMapScanResult Current { get; private set; } = LoadSnapshotOrEmpty();
        public IReadOnlyList<NearestResourceNodeInfo> LiveResourceNodes => _liveResourceNodes;

        public WorldMapReader()
        {
            _liveEntityChangeHandler = OnLiveEntityCollectionChanged;
            if (Current != null && Current.LoadedResourceNodes != null)
                MergeLiveResourceNodes(
                    Current.LoadedResourceNodes,
                    Current.HasCastlePosition,
                    Current.CastleX,
                    Current.CastleY);
        }

        public bool IsMainWorldActive
        {
            get
            {
                var worldMap = AppCache.WorldMap;
                return worldMap != null && worldMap.IsOnMainWorld;
            }
        }

        private static WorldMapScanResult LoadSnapshotOrEmpty()
        {
            return WorldMapSnapshotStore.Load() ?? new WorldMapScanResult();
        }

        public void MarkUnavailable()
        {
            if (Current != null && Current.HasUsableNodeSnapshot)
            {
                Current.CurrentOriginValidated = false;
                Current.IsPersistedSnapshot = true;
                Current.State = WorldMapState.NotOnWorldMap;
                return;
            }

            Current = new WorldMapScanResult();
        }

        public void RefreshLiveResourceNodesIfChanged()
        {
            var worldMap = AppCache.WorldMap;
            if (worldMap == null || !worldMap.IsOnMainWorld)
            {
                BindLiveEntityContainers(null, null);
                _hasLiveEntityVersions = false;
                return;
            }

            GEntityContainer mainContainer = worldMap.MainEntity;
            GEntityContainer worldContainer = worldMap.Entity;
            if (mainContainer == null || worldContainer == null)
                return;

            BindLiveEntityContainers(mainContainer, worldContainer);
            uint mainVersion = mainContainer.EntitiesVersion;
            uint worldVersion = worldContainer.EntitiesVersion;
            if (!_hasLiveEntityVersions
                || mainVersion != _lastMainEntitiesVersion
                || worldVersion != _lastWorldEntitiesVersion)
            {
                _liveNodesDirty = true;
                _lastMainEntitiesVersion = mainVersion;
                _lastWorldEntitiesVersion = worldVersion;
                _hasLiveEntityVersions = true;
            }

            if (!_liveNodesDirty || DateTime.UtcNow < _nextLiveNodeScanUtc)
                return;

            try
            {
                var result = new WorldMapScanResult
                {
                    HasScanned = true,
                    State = WorldMapState.OnWorldMap
                };
                ReadCastlePosition(result);
                var countedNodeIds = new HashSet<ulong>();
                var loggedNodeIds = new HashSet<ulong>();
                var collectDataNodeIds = new HashSet<ulong>();
                ScanContainer(
                    mainContainer, "MainEntity(live)", result,
                    countedNodeIds, loggedNodeIds, collectDataNodeIds);
                ScanContainer(
                    worldContainer, "WorldEntity(live)", result,
                    countedNodeIds, loggedNodeIds, collectDataNodeIds);
                MergeLiveResourceNodes(
                    result.LoadedResourceNodes,
                    result.HasCastlePosition,
                    result.CastleX,
                    result.CastleY);
                _liveNodesDirty = false;
                _nextLiveNodeScanUtc = DateTime.UtcNow + TimeSpan.FromMilliseconds(250);
                _lastLiveScanError = null;
            }
            catch (Exception ex)
            {
                if (_lastLiveScanError != ex.Message)
                {
                    _lastLiveScanError = ex.Message;
                    MelonLogger.Error("Live world-map node refresh failed: " + ex);
                }
                _nextLiveNodeScanUtc = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            }
        }

        private void OnLiveEntityCollectionChanged(LiveEntityList changedEntities)
        {
            _liveNodesDirty = true;
        }

        private void BindLiveEntityContainers(
            GEntityContainer mainContainer,
            GEntityContainer worldContainer)
        {
            BindLiveEntityContainer(
                ref _boundMainEntityContainer,
                ref _boundMainEntityContainerPtr,
                mainContainer);
            BindLiveEntityContainer(
                ref _boundWorldEntityContainer,
                ref _boundWorldEntityContainerPtr,
                worldContainer);
        }

        private void BindLiveEntityContainer(
            ref GEntityContainer boundContainer,
            ref IntPtr boundContainerPtr,
            GEntityContainer currentContainer)
        {
            IntPtr currentPointer = currentContainer == null
                ? IntPtr.Zero
                : IL2CPP.Il2CppObjectBaseToPtr(currentContainer);
            if (currentPointer == boundContainerPtr)
                return;

            if (boundContainer != null)
            {
                if (boundContainer.OnAddEntity != null)
                    boundContainer.OnAddEntity.Remove(_liveEntityChangeHandler);
                if (boundContainer.OnAddComp != null)
                    boundContainer.OnAddComp.Remove(_liveEntityChangeHandler);
            }

            boundContainer = currentContainer;
            boundContainerPtr = currentPointer;
            if (boundContainer == null)
                return;

            if (boundContainer.OnAddEntity != null)
                boundContainer.OnAddEntity.Add(_liveEntityChangeHandler);
            if (boundContainer.OnAddComp != null)
                boundContainer.OnAddComp.Add(_liveEntityChangeHandler);
            _liveNodesDirty = true;
            _hasLiveEntityVersions = false;
        }

        private void MergeLiveResourceNodes(
            List<NearestResourceNodeInfo> discoveredNodes,
            bool hasOrigin,
            float originX,
            float originY)
        {
            if (discoveredNodes == null || discoveredNodes.Count == 0)
                return;

            for (int i = 0; i < discoveredNodes.Count; i++)
            {
                NearestResourceNodeInfo discovered = discoveredNodes[i];
                if (discovered == null || discovered.EntityId == 0)
                    continue;

                _liveResourceNodesById[discovered.EntityId] = discovered;
            }

            _liveResourceNodes.Clear();
            foreach (NearestResourceNodeInfo node in _liveResourceNodesById.Values)
            {
                if (hasOrigin)
                {
                    double deltaX = node.X - originX;
                    double deltaY = node.Y - originY;
                    node.DistanceSquared = deltaX * deltaX + deltaY * deltaY;
                    node.Distance = Math.Sqrt(node.DistanceSquared);
                }
                _liveResourceNodes.Add(node);
            }
            _liveResourceNodes.Sort((left, right) =>
                left.DistanceSquared.CompareTo(right.DistanceSquared));
        }

        public void Refresh()
        {
            var result = new WorldMapScanResult { HasScanned = true };
            ReadMarchSlotInfo(result);
            ReadCollectCapacity(result);
            var worldMap = AppCache.WorldMap;
            ReadCastlePosition(result);
            if (worldMap == null)
            {
                if (Current != null && Current.HasUsableNodeSnapshot)
                {
                    UpdateSnapshotOriginState(Current, result);
                    Current.IsPersistedSnapshot = true;
                    Current.State = WorldMapState.NotOnWorldMap;
                }
                else
                {
                    Current = result;
                }
                return;
            }

            var isOnMainWorld = worldMap.IsOnMainWorld;
            LogMapRequestContext(worldMap, isOnMainWorld);
            if (!isOnMainWorld)
            {
                result.State = WorldMapState.NotOnWorldMap;
                result.IsCacheOnlyProbe = true;
                var cachedNodeIds = new HashSet<ulong>();
                var cachedLoggedNodeIds = new HashSet<ulong>();
                var cachedCollectDataNodeIds = new HashSet<ulong>();
                ScanContainer(worldMap.MainEntity, "MainEntity(cache-only)", result,
                    cachedNodeIds, cachedLoggedNodeIds, cachedCollectDataNodeIds);
                ScanContainer(worldMap.Entity, "WorldEntity(cache-only)", result,
                    cachedNodeIds, cachedLoggedNodeIds, cachedCollectDataNodeIds);
                if (Current != null && Current.HasUsableNodeSnapshot)
                {
                    UpdateSnapshotOriginState(Current, result);
                    Current.IsPersistedSnapshot = true;
                    Current.State = WorldMapState.NotOnWorldMap;
                }
                else
                {
                    Current = result;
                }
                WriteDiagnostics(result);
                return;
            }
            result.State = WorldMapState.OnWorldMap;
            result.State = WorldMapState.OnWorldMap;
            var countedNodeIds = new HashSet<ulong>();
            var loggedNodeIds = new HashSet<ulong>();
            var collectDataNodeIds = new HashSet<ulong>();
            ScanContainer(worldMap.MainEntity, "MainEntity", result, countedNodeIds, loggedNodeIds, collectDataNodeIds);
            ScanContainer(worldMap.Entity, "WorldEntity", result, countedNodeIds, loggedNodeIds, collectDataNodeIds);

            SortNodesByDistance(result);
            MergeLiveResourceNodes(
                result.LoadedResourceNodes,
                result.HasCastlePosition,
                result.CastleX,
                result.CastleY);
            result.SnapshotCapturedAtUtc = DateTime.UtcNow;
            if (result.LoadedResourceNodes.Count == 0)
            {
                MelonLogger.Warning(
                    "World-map scan found no resource nodes; the previous on-disk snapshot was left unchanged.");
                if (Current != null && Current.HasUsableNodeSnapshot)
                {
                    UpdateSnapshotOriginState(Current, result);
                    Current.IsPersistedSnapshot = true;
                    Current.State = WorldMapState.NotOnWorldMap;
                    WriteDiagnostics(result);
                    return;
                }
            }
            else if (WorldMapSnapshotStore.Save(result))
            {
                MelonLogger.Msg(
                    "World-map snapshot saved: nodes=" + result.LoadedResourceNodes.Count
                    + " capturedUtc=" + result.SnapshotCapturedAtUtc.ToString("O", CultureInfo.InvariantCulture));
            }
            result.CurrentOriginValidated = result.HasCastlePosition;
            result.SnapshotOriginChanged = false;
            Current = result;
            WriteDiagnostics(result);
        }

        private static void UpdateSnapshotOriginState(
            WorldMapScanResult snapshot,
            WorldMapScanResult currentScan)
        {
            if (snapshot == null)
                return;
            snapshot.CurrentOriginValidated = false;
            if (currentScan == null || !currentScan.HasCastlePosition || !snapshot.HasCastlePosition)
                return;

            if (snapshot.HasCastleGridPosition && currentScan.HasCastleGridPosition)
            {
                snapshot.SnapshotOriginChanged = snapshot.CastleGridX != currentScan.CastleGridX
                    || snapshot.CastleGridY != currentScan.CastleGridY;
            }
            else
            {
                double dx = snapshot.CastleX - currentScan.CastleX;
                double dy = snapshot.CastleY - currentScan.CastleY;
                snapshot.SnapshotOriginChanged = dx * dx + dy * dy > 0.01d;
            }

            if (snapshot.SnapshotOriginChanged)
            {
                MelonLogger.Warning(
                    "Saved node snapshot origin differs from the current castle position; "
                    + "farming must wait for a fresh open-map scan.");
            }
            snapshot.CurrentOriginValidated = true;
        }

        private static void SortNodesByDistance(WorldMapScanResult result)
        {
            result.LoadedResourceNodes.Sort((left, right) =>
                left.DistanceSquared.CompareTo(right.DistanceSquared));
            result.NearestResourceNodes.Sort((left, right) =>
                left.DistanceSquared.CompareTo(right.DistanceSquared));
        }

        public string RequestNearbyMapCells()
        {
            try
            {
                if (_mapRequestPending)
                {
                    MelonLogger.Msg("Nearby map-data request is already queued; waiting for native request eligibility.");
                    return "Map request already queued; waiting for game readiness.";
                }

                var worldMap = AppCache.WorldMap;
                if (worldMap == null)
                    return LogMapRequestSkipped("AppCache.WorldMap is unavailable.");

                if (!worldMap.IsOnMainWorld)
                    return LogMapRequestSkipped(
                        "The game's main-world context is inactive; refusing to create or switch scenes.");

                var requestContext = worldMap.ReqMapInfoContext;
                if (requestContext == null)
                    return LogMapRequestSkipped("ReqMapInfoContext is unavailable.");
                LogRequestEligibility(requestContext, worldMap.MainContext);
                if (!requestContext.CanRequest)
                {
                    _mapRequestPending = true;
                    _mapRequestDeadlineUtc = DateTime.UtcNow + MapRequestWaitTimeout;
                    _nextMapRequestCheckUtc = DateTime.UtcNow + MapRequestPollInterval;
                    MelonLogger.Warning(
                        "Nearby map-data request queued: CanRequest=false; will recheck once per second "
                        + "for up to " + MapRequestWaitTimeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)
                        + " seconds. No scene switch or dispatch.");
                    return "Map request queued; waiting for the game's request context.";
                }

                return SubmitNearbyMapCells(worldMap, requestContext);
            }
            catch (Exception ex)
            {
                MelonLogger.Error("Nearby map-data request failed: " + ex);
                return "Nearby map-data request failed; see MelonLoader log.";
            }
        }

        public void AdvancePendingMapRequest()
        {
            if (!_mapRequestPending || DateTime.UtcNow < _nextMapRequestCheckUtc)
                return;

            var now = DateTime.UtcNow;
            if (now >= _mapRequestDeadlineUtc)
            {
                _mapRequestPending = false;
                MelonLogger.Warning(
                    "Nearby map-data request timed out: ReqMapInfoContext did not become eligible within "
                    + MapRequestWaitTimeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)
                    + " seconds. No request was sent.");
                return;
            }

            _nextMapRequestCheckUtc = now + MapRequestPollInterval;
            try
            {
                var worldMap = AppCache.WorldMap;
                if (worldMap == null || !worldMap.IsOnMainWorld)
                {
                    _mapRequestPending = false;
                    MelonLogger.Warning(
                        "Nearby map-data request cancelled: the existing main-world context is no longer active.");
                    return;
                }

                var requestContext = worldMap.ReqMapInfoContext;
                if (requestContext == null)
                {
                    _mapRequestPending = false;
                    MelonLogger.Warning(
                        "Nearby map-data request cancelled: ReqMapInfoContext became unavailable.");
                    return;
                }

                var mainContext = worldMap.MainContext;
                bool mainContextReady = mainContext != null && mainContext.IsReady
                    && mainContext.CanUpdate && mainContext.CanSync;
                if (!requestContext.CanRequest || !mainContextReady)
                    return;

                _mapRequestPending = false;
                MelonLogger.Msg(
                    "Nearby map-data request became eligible after waiting; submitting one request now.");
                SubmitNearbyMapCells(worldMap, requestContext);
            }
            catch (Exception ex)
            {
                _mapRequestPending = false;
                MelonLogger.Error("Queued nearby map-data request failed: " + ex);
            }
        }

        private static string SubmitNearbyMapCells(
            Il2CppIGG.Game.Data.Cache.WorldMap.WorldMapCache worldMap,
            Il2CppIGG.Game.Data.Cache.WorldMap.ReqMapInfoContext requestContext)
        {
            var mainContext = worldMap.MainContext;
            if (mainContext == null || !mainContext.IsReady || !mainContext.CanUpdate
                || !mainContext.CanSync)
            {
                return LogMapRequestSkipped(
                    "MainContext is not ready for map data (ready/update/sync checks failed).");
            }

                var castle = new WorldMapScanResult();
                ReadCastlePosition(castle);
                if (!castle.HasCastleGridPosition)
                    return LogMapRequestSkipped(
                        "Could not read the account castle's world-map grid position.");

                var center = new int2(castle.CastleGridX, castle.CastleGridY);
                var viewFlags = EViewCellFlag.All;
                var request = new MsgCL2GSViewMapRequest
                {
                    RegionId = worldMap.RegionId,
                    KingdomId = worldMap.KingdomId,
                    Position = MapInfoReqHelper.SerViewPos2SendPos(center),
                    ViewLod = EntityMonitorType.KEntityMonitorTypeView,
                    ViewFlag = (uint)viewFlags,
                    WeakFlag = 0
                };

                LogRequestSubmissionState(requestContext, "before", request);
                try
                {
                    EntityDebugHelper.LogViewRequest(request);
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning(
                        "Game-native view-request logger failed: "
                        + ex.GetType().Name + ": " + ex.Message);
                }
                MelonLogger.Warning(
                    "Nearby map-data request: submitting native RequestViewLayer"
                    + " centerGrid=(" + center.x + "," + center.y + ")"
                    + " regionId=" + request.RegionId
                    + " kingdomId=" + request.KingdomId
                    + " viewLod=" + request.ViewLod
                    + " viewFlags=" + viewFlags
                    + " requestContextCanRequest=" + requestContext.CanRequest
                    + " sceneSwitch=false dispatch=false.");
                requestContext.RequestViewLayer(request, center, viewFlags, EViewCellFlag.All);
                LogRequestSubmissionState(requestContext, "after", request);
                MelonLogger.Msg(
                    "Nearby map-data request: native RequestViewLayer returned. "
                    + "This confirms local submission only; wait for game sync, then scan again.");
                return "Nearby map-data request submitted; wait a few seconds, then scan again.";
        }

        private static string LogMapRequestSkipped(string reason)
        {
            MelonLogger.Warning("Nearby map-data request skipped: " + reason);
            return "Request skipped: " + reason;
        }

        private static void LogRequestEligibility(
            Il2CppIGG.Game.Data.Cache.WorldMap.ReqMapInfoContext requestContext,
            Il2CppIGG.Game.Data.Cache.WorldMap.WorldContext mainContext)
        {
            try
            {
                int updateState;
                bool canUpdate = requestContext.CanUpdate(out updateState);
                var firstRequest = requestContext.m_firstReqInfo;
                var lastView = requestContext.LastViewReqInfo;
                var sequence = requestContext.m_reqSequence;
                var sequenceText = "null";
                if (sequence != null)
                {
                    var sequenceParts = new List<string>(sequence.Length);
                    for (int i = 0; i < sequence.Length; i++)
                        sequenceParts.Add(sequence[i].ToString());
                    sequenceText = "[" + string.Join(",", sequenceParts) + "]";
                }

                MelonLogger.Msg(
                    "Nearby map request eligibility at click:"
                    + " CanRequest=" + requestContext.CanRequest
                    + " CanUpdate=" + canUpdate
                    + " updateState=" + updateState
                    + " EnableTime=" + requestContext.EnableTime.ToString("F3", CultureInfo.InvariantCulture)
                    + " firstReq=(enable=" + firstRequest.Enable
                    + ",state=" + firstRequest.State
                    + ",timeout=" + firstRequest.Timeout.ToString("F3", CultureInfo.InvariantCulture) + ")"
                    + " LastEnterMapTime=" + requestContext.LastEnterMapTime.ToString("F3", CultureInfo.InvariantCulture)
                    + " topRequestComplete=" + requestContext.m_isReqTopComplete
                    + " lastViewReq=(version=" + lastView.Version
                    + ",lod=" + lastView.Lod
                    + ",pos=(" + lastView.Pos.x + "," + lastView.Pos.y + ")"
                    + ",flag=" + lastView.Flag + ")"
                    + " requestSequence=" + sequenceText
                    + " network=" + (requestContext.m_network == null ? "null" : "present")
                    + " MainContext=" + (mainContext == null
                        ? "null"
                        : "present(IsReady=" + mainContext.IsReady
                            + ",CanUpdate=" + mainContext.CanUpdate
                            + ",CanSync=" + mainContext.CanSync + ")") + ".");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    "Nearby map request eligibility diagnostic failed: "
                    + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void LogRequestSubmissionState(
            Il2CppIGG.Game.Data.Cache.WorldMap.ReqMapInfoContext requestContext,
            string stage,
            MsgCL2GSViewMapRequest request)
        {
            try
            {
                var delayRequest = requestContext.DelayRequest;
                int updateState;
                bool canUpdate = requestContext.CanUpdate(out updateState);
                var requestSequence = requestContext.m_reqSequence;
                string sequenceText = "null";
                if (requestSequence != null)
                {
                    var sequenceParts = new List<string>(requestSequence.Length);
                    for (int i = 0; i < requestSequence.Length; i++)
                        sequenceParts.Add(requestSequence[i].ToString());
                    sequenceText = "[" + string.Join(",", sequenceParts) + "]";
                }

                var delayState = delayRequest == null
                    ? "null"
                    : "present(index=" + delayRequest.m_index
                        + ",nextTime=" + delayRequest.m_nextTime.ToString("F3", CultureInfo.InvariantCulture)
                        + ",center=(" + delayRequest.m_centerPos.x + "," + delayRequest.m_centerPos.y + ")"
                        + ",viewCells=" + (delayRequest.m_viewCell == null
                            ? "null"
                            : delayRequest.m_viewCell.Length.ToString(CultureInfo.InvariantCulture))
                        + ",message=" + (delayRequest.m_reqMsg == null ? "null" : "present") + ")";
                var position = request.Position;
                var lastView = requestContext.LastViewReqInfo;
                var firstRequest = requestContext.m_firstReqInfo;
                MelonLogger.Msg(
                    "Nearby map request state " + stage + ":"
                    + " Dirty=" + requestContext.Dirty
                    + " CanRequest=" + requestContext.CanRequest
                    + " CanUpdate=" + canUpdate
                    + " updateState=" + updateState
                    + " lastReqPos=(" + requestContext.m_lastReqPos.x + ","
                        + requestContext.m_lastReqPos.y + ")"
                    + " lastViewCell=" + requestContext.m_lastViewCell
                    + " lastRegionId=" + requestContext.m_lastRegionId
                    + " requestVersion=" + requestContext.LastViewRequestVersion
                    + " lastViewReq=(version=" + lastView.Version
                        + ",lod=" + lastView.Lod
                        + ",pos=(" + lastView.Pos.x + "," + lastView.Pos.y + ")"
                        + ",flag=" + lastView.Flag + ")"
                    + " firstReq=(enable=" + firstRequest.Enable
                        + ",state=" + firstRequest.State
                        + ",timeout=" + firstRequest.Timeout.ToString("F3", CultureInfo.InvariantCulture) + ")"
                    + " enableTime=" + requestContext.EnableTime.ToString("F3", CultureInfo.InvariantCulture)
                    + " lastEnterMapTime=" + requestContext.LastEnterMapTime.ToString("F3", CultureInfo.InvariantCulture)
                    + " topRequestComplete=" + requestContext.m_isReqTopComplete
                    + " requestSequence=" + sequenceText
                    + " delay=" + delayState
                    + " payloadPosition=(" + (position == null ? "null" : position.X + "," + position.Y) + ")"
                    + " payloadFlags=" + request.ViewFlag
                    + " network=" + (requestContext.m_network == null ? "null" : "present") + ".");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    "Nearby map request state " + stage + " diagnostic failed: "
                    + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void LogMapRequestContext(
            Il2CppIGG.Game.Data.Cache.WorldMap.WorldMapCache worldMap,
            bool isOnMainWorld)
        {
            try
            {
                var requestContext = worldMap.ReqMapInfoContext;
                if (requestContext == null)
                {
                    MelonLogger.Msg(
                        "Map request context probe: IsOnMainWorld=" + isOnMainWorld
                        + " ReqMapInfoContext=null; "
                        + "no existing request context is available to reuse.");
                    return;
                }

                var mainContext = worldMap.MainContext;
                var delayRequest = requestContext.DelayRequest;
                MelonLogger.Msg(
                    "Map request context probe: IsOnMainWorld=" + isOnMainWorld
                    + " ReqMapInfoContext=present"
                    + " CanRequest=" + requestContext.CanRequest
                    + " LastViewCell=" + requestContext.LastViewCell
                    + " DelayRequest=" + (delayRequest == null ? "null" : "present")
                    + " MainContext=" + (mainContext == null
                        ? "null"
                        : "present(IsReady=" + mainContext.IsReady
                            + ",CanUpdate=" + mainContext.CanUpdate
                            + ",CanSync=" + mainContext.CanSync + ")")
                    + ". No map request was sent.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    "Map request context probe failed: "
                    + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void ReadMarchSlotInfo(WorldMapScanResult result)
        {
            try
            {
                var player = AppCache.Player;
                if (player == null)
                {
                    result.MarchSlotError = "PlayerCache is not initialized.";
                    MelonLogger.Warning("March-slot scan unavailable: PlayerCache is not initialized.");
                    return;
                }

                result.MaxMarchSlots = player.GetDispatchMaxMarchCount();
                result.UsedMarchSlots = System.Math.Max(0, player.GetMarchCount());
                result.FreeMarchSlots = result.MaxMarchSlots > (uint)result.UsedMarchSlots
                    ? result.MaxMarchSlots - (uint)result.UsedMarchSlots
                    : 0;
                result.HasMarchSlotInfo = true;

                MelonLogger.Msg(
                    "March slots: max=" + result.MaxMarchSlots
                    + ", used=" + result.UsedMarchSlots
                    + ", free=" + result.FreeMarchSlots
                    + " (game PlayerCache APIs).");
            }
            catch (Exception ex)
            {
                result.MarchSlotError = ex.Message;
                MelonLogger.Error("March-slot scan error: " + ex);
            }
        }

        private static void ReadCollectCapacity(WorldMapScanResult result)
        {
            try
            {
                result.FoodCollectCapacity = MarchModule.API.GetCollectCapacity(CollectType.KCollectTypeFood);
                result.WoodCollectCapacity = MarchModule.API.GetCollectCapacity(CollectType.KCollectTypeWood);
                result.SteelCollectCapacity = MarchModule.API.GetCollectCapacity(CollectType.KCollectTypeSteel);
                result.OilCollectCapacity = MarchModule.API.GetCollectCapacity(CollectType.KCollectTypeOil);
                result.HasCollectCapacity = true;

                MelonLogger.Msg(
                    "Game collect capacity API: Food=" + result.FoodCollectCapacity
                    + ", Wood=" + result.WoodCollectCapacity
                    + ", Steel=" + result.SteelCollectCapacity
                    + ", Oil=" + result.OilCollectCapacity
                    + ". Read-only diagnostic; does not create a march.");
            }
            catch (Exception ex)
            {
                result.CollectCapacityError = ex.Message;
                MelonLogger.Error("Collect-capacity scan error: " + ex);
            }
        }

        private static void ReadCastlePosition(WorldMapScanResult result)
        {
            try
            {
                var player = AppCache.Player;
                var castle = player == null ? null : player.MainCastleEntity;
                var mapObject = GetMapObjectComponent(castle);
                if (mapObject == null)
                {
                    result.CastlePositionError = "Main castle map position is not available.";
                    MelonLogger.Warning("Nearest-node scan unavailable: " + result.CastlePositionError);
                    return;
                }

                var position = mapObject.Position;
                if (float.IsNaN(position.x) || float.IsInfinity(position.x)
                    || float.IsNaN(position.y) || float.IsInfinity(position.y))
                {
                    result.CastlePositionError = "Main castle position is not finite.";
                    MelonLogger.Warning("Nearest-node scan unavailable: " + result.CastlePositionError);
                    return;
                }

                result.CastleX = position.x;
                result.CastleY = position.y;
                result.HasCastlePosition = true;
                ECompObjPos positionComponent = GetObjectPositionComponent(castle);
                result.HasCastleGridPosition = TryReadGridPosition(
                    positionComponent, out int gridX, out int gridY);
                if (result.HasCastleGridPosition)
                {
                    result.CastleGridX = gridX;
                    result.CastleGridY = gridY;
                }

                MelonLogger.Msg(
                    "Nearest-node origin: MainCastleEntity id="
                    + castle.Id.ToString(CultureInfo.InvariantCulture)
                    + " mapPos=(" + position.x.ToString("F1", CultureInfo.InvariantCulture)
                    + ", " + position.y.ToString("F1", CultureInfo.InvariantCulture) + ")"
                    + (result.HasCastleGridPosition
                        ? ", gridPos=(" + gridX.ToString(CultureInfo.InvariantCulture)
                            + ", " + gridY.ToString(CultureInfo.InvariantCulture) + ")."
                        : ", gridPos unavailable."));
            }
            catch (Exception ex)
            {
                result.CastlePositionError = ex.Message;
                MelonLogger.Error("Main-castle position scan error: " + ex);
            }
        }

        private static ECompMapObj GetMapObjectComponent(GEntity entity)
        {
            if (entity == null)
                return null;

            var components = entity.Components;
            if (components == null)
                return null;

            for (int i = 0; i < components.Count; i++)
            {
                var component = components[i];
                if (component == null)
                    continue;

                var componentType = component.m_typeId.GetCompType();
                if (componentType == null || componentType.Name != nameof(ECompMapObj))
                    continue;

                var typedComponent = entity.GetComponent(componentType);
                if (typedComponent != null)
                    return Il2CppObjectPool.Get<ECompMapObj>(IL2CPP.Il2CppObjectBaseToPtr(typedComponent));
            }

            return null;
        }

        private static ECompObjPos GetObjectPositionComponent(GEntity entity)
        {
            if (entity == null)
                return null;

            var components = entity.Components;
            if (components == null)
                return null;

            for (int i = 0; i < components.Count; i++)
            {
                var component = components[i];
                if (component == null)
                    continue;

                var componentType = component.m_typeId.GetCompType();
                if (componentType == null || componentType.Name != nameof(ECompObjPos))
                    continue;

                var typedComponent = entity.GetComponent(componentType);
                if (typedComponent != null)
                    return Il2CppObjectPool.Get<ECompObjPos>(IL2CPP.Il2CppObjectBaseToPtr(typedComponent));
            }

            return null;
        }

        private static bool TryReadGridPosition(ECompObjPos positionComponent, out int x, out int y)
        {
            x = 0;
            y = 0;
            if (positionComponent == null)
                return false;

            try
            {
                var position = positionComponent.LogicPos;
                x = position.x;
                y = position.y;
                return true;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("Logic-grid position unavailable: " + ex.Message);
                return false;
            }
        }

        private static void ScanContainer(
            GEntityContainer container,
            string containerName,
            WorldMapScanResult result,
            HashSet<ulong> countedNodeIds,
            HashSet<ulong> loggedNodeIds,
            HashSet<ulong> collectDataNodeIds)
        {
            if (container == null)
                return;

            var entities = container.Entities;
            if (entities == null)
                return;

            for (int i = 0; i < entities.Count; i++)
            {
                var entity = entities[i];
                if (entity == null)
                    continue;

                bool isMainEntity = containerName.StartsWith("MainEntity", StringComparison.Ordinal);
                if (isMainEntity)
                    result.MainEntityCount++;
                else
                    result.WorldEntityCount++;

                if (entity.Type != EntityType.KEntityTypeCollect)
                    continue;

                if (isMainEntity)
                    result.MainCollectEntityCount++;
                else
                    result.WorldCollectEntityCount++;

                ECompMapObj mapObject = null;
                ECompOwner owner = null;
                ECompObjPos positionComponent = null;
                ECompCollectInfo collectInfo = null;
                ECompTypeData typeData = null;
                var components = entity.Components;
                var componentSummary = new List<string>();
                if (components != null)
                {
                    for (int componentIndex = 0; componentIndex < components.Count; componentIndex++)
                    {
                        var component = components[componentIndex];
                        if (component == null)
                            continue;

                        var componentId = component.m_typeId;
                        var componentType = componentId.GetCompType();
                        string componentTypeName = componentType == null ? "unknown" : componentType.Name;
                        componentSummary.Add(
                            "id=" + componentId.ToBitString()
                            + "/" + componentTypeName);

                        if (componentTypeName == nameof(ECompMapObj) && mapObject == null)
                        {
                            var typedComponent = entity.GetComponent(componentType);
                            if (typedComponent != null)
                                mapObject = Il2CppObjectPool.Get<ECompMapObj>(IL2CPP.Il2CppObjectBaseToPtr(typedComponent));
                        }
                        else if (componentTypeName == nameof(ECompOwner) && owner == null)
                        {
                            var typedComponent = entity.GetComponent(componentType);
                            if (typedComponent != null)
                                owner = Il2CppObjectPool.Get<ECompOwner>(IL2CPP.Il2CppObjectBaseToPtr(typedComponent));
                        }
                        else if (componentTypeName == nameof(ECompObjPos) && positionComponent == null)
                        {
                            var typedComponent = entity.GetComponent(componentType);
                            if (typedComponent != null)
                                positionComponent = Il2CppObjectPool.Get<ECompObjPos>(IL2CPP.Il2CppObjectBaseToPtr(typedComponent));
                        }
                        else if (componentTypeName == nameof(ECompCollectInfo) && collectInfo == null)
                        {
                            var typedComponent = entity.GetComponent(componentType);
                            if (typedComponent != null)
                                collectInfo = Il2CppObjectPool.Get<ECompCollectInfo>(IL2CPP.Il2CppObjectBaseToPtr(typedComponent));
                        }
                        else if (componentTypeName == nameof(ECompTypeData) && typeData == null)
                        {
                            var typedComponent = entity.GetComponent(componentType);
                            if (typedComponent != null)
                                typeData = Il2CppObjectPool.Get<ECompTypeData>(IL2CPP.Il2CppObjectBaseToPtr(typedComponent));
                        }
                    }
                }

                bool hasCollectData = collectInfo != null && collectInfo.Data != null;
                uint remainingResourceCount = hasCollectData ? collectInfo.Data.RemainResourceCount : 0;
                uint maxCollectCount = hasCollectData ? collectInfo.Data.MaxCollectCount : 0;
                string resourceIcon = typeData == null
                    ? "unknown"
                    : IconHelper.GetWorldResourceIcon(typeData.ConfigId);
                var resourceConfig = GetWorldResourceConfig(typeData);
                string resourceType = GetResourceTypeName(resourceConfig);
                if (hasCollectData && collectDataNodeIds.Add(entity.Id))
                {
                    result.NodesWithCollectData++;
                    result.TotalRemainingResource += remainingResourceCount;
                }

                if (loggedNodeIds.Add(entity.Id))
                {
                    result.Diagnostics.Add(
                        "Collect container=" + containerName
                        + " id=" + entity.Id.ToString(CultureInfo.InvariantCulture)
                        + " configId=" + (typeData == null ? "unknown" : typeData.ConfigId.ToString(CultureInfo.InvariantCulture))
                        + " level=" + (typeData == null ? "unknown" : typeData.Level.ToString(CultureInfo.InvariantCulture))
                        + " icon=" + resourceIcon
                        + " resourceConfig=" + DescribeResourceConfig(resourceConfig)
                        + " remaining=" + (hasCollectData
                            ? remainingResourceCount.ToString(CultureInfo.InvariantCulture)
                            : "unknown")
                        + " max=" + (hasCollectData
                            ? maxCollectCount.ToString(CultureInfo.InvariantCulture)
                            : "unknown")
                        + " ownerId=" + (owner == null ? "unknown" : owner.Id.ToString(CultureInfo.InvariantCulture))
                        + " ownerName=" + (owner == null || string.IsNullOrEmpty(owner.Name) ? "<empty>" : owner.Name)
                        + " collectByEntityId=" + (owner == null
                            ? "unknown"
                            : owner.CollectByEntityId.ToString(CultureInfo.InvariantCulture))
                        + " components=" + (components == null ? 0 : components.Count)
                        + " [" + string.Join(", ", componentSummary) + "]");
                }

                if (mapObject == null || mapObject.EntityType != EntityType.KEntityTypeCollect)
                    continue;

                if (isMainEntity)
                    result.MainMapObjectCount++;
                else
                    result.WorldMapObjectCount++;

                if (countedNodeIds.Add(entity.Id))
                {
                    result.LoadedCollectNodeCount++;
                    CountResourceNode(result, resourceConfig, remainingResourceCount);
                    var position = mapObject.Position;
                    bool hasGridPosition = TryReadGridPosition(
                        positionComponent, out int gridX, out int gridY);
                    ulong collectByEntityId = owner == null ? 0UL : owner.CollectByEntityId;
                    if (collectByEntityId != 0)
                        result.NodesWithCollectReference++;
                    if (result.HasCastlePosition && hasCollectData && remainingResourceCount > 0)
                    {
                        RecordNearestResourceNode(
                            result,
                            entity.Id,
                            resourceConfig,
                            remainingResourceCount,
                            position.x,
                            position.y,
                            owner == null ? 0UL : owner.Id,
                            owner == null ? string.Empty : owner.Name,
                            collectByEntityId,
                            hasGridPosition,
                            gridX,
                            gridY);
                    }

                    if (result.SampleNodes.Count < MaximumSampleNodes)
                    {
                        result.SampleNodes.Add(
                            "ID " + entity.Id.ToString(CultureInfo.InvariantCulture)
                            + " | " + resourceType
                            + " | cfg " + (typeData == null ? "?" : typeData.ConfigId.ToString(CultureInfo.InvariantCulture))
                            + " L" + (typeData == null ? "?" : typeData.Level.ToString(CultureInfo.InvariantCulture))
                            + " | left " + (hasCollectData
                                ? remainingResourceCount.ToString("N0", CultureInfo.InvariantCulture)
                                    + "/" + maxCollectCount.ToString("N0", CultureInfo.InvariantCulture)
                                : "?")
                            + " | mapPos=(" + position.x.ToString("F1", CultureInfo.InvariantCulture)
                            + ", " + position.y.ToString("F1", CultureInfo.InvariantCulture) + ")"
                            + (hasGridPosition
                                ? " | gridPos=(" + gridX.ToString(CultureInfo.InvariantCulture)
                                    + ", " + gridY.ToString(CultureInfo.InvariantCulture) + ")"
                                : " | gridPos unavailable"));
                    }
                }
            }
        }

        private static void RecordNearestResourceNode(
            WorldMapScanResult result,
            ulong entityId,
            WorldResourceConfig config,
            uint remaining,
            float x,
            float y,
            ulong ownerId,
            string ownerName,
            ulong collectByEntityId,
            bool hasGridPosition,
            int gridX,
            int gridY)
        {
            if (config == null)
                return;

            string resource;
            switch ((CollectType)config.Type)
            {
                case CollectType.KCollectTypeFood:
                    resource = "Food";
                    break;
                case CollectType.KCollectTypeWood:
                    resource = "Wood";
                    break;
                case CollectType.KCollectTypeSteel:
                    resource = "Steel";
                    break;
                case CollectType.KCollectTypeOil:
                    resource = "Oil";
                    break;
                default:
                    return;
            }

            double deltaX = x - result.CastleX;
            double deltaY = y - result.CastleY;
            double distanceSquared = deltaX * deltaX + deltaY * deltaY;
            if (double.IsNaN(distanceSquared) || double.IsInfinity(distanceSquared))
                return;

            NearestResourceNodeInfo discoveredNode = CreateNearestNode(
                resource, entityId, x, y, remaining, distanceSquared,
                ownerId, ownerName, collectByEntityId,
                hasGridPosition, gridX, gridY);
            result.LoadedResourceNodes.Add(discoveredNode);

            for (int i = 0; i < result.NearestResourceNodes.Count; i++)
            {
                NearestResourceNodeInfo current = result.NearestResourceNodes[i];
                if (current.Resource != resource)
                    continue;

                if (distanceSquared < current.DistanceSquared)
                {
                    result.NearestResourceNodes[i] = discoveredNode;
                }

                return;
            }

            result.NearestResourceNodes.Add(discoveredNode);
        }

        private static NearestResourceNodeInfo CreateNearestNode(
            string resource,
            ulong entityId,
            float x,
            float y,
            uint remaining,
            double distanceSquared,
            ulong ownerId,
            string ownerName,
            ulong collectByEntityId,
            bool hasGridPosition,
            int gridX,
            int gridY)
        {
            return new NearestResourceNodeInfo
            {
                Resource = resource,
                EntityId = entityId,
                X = x,
                Y = y,
                HasGridPosition = hasGridPosition,
                GridX = gridX,
                GridY = gridY,
                Remaining = remaining,
                OwnerId = ownerId,
                OwnerName = ownerName ?? string.Empty,
                CollectByEntityId = collectByEntityId,
                DistanceSquared = distanceSquared,
                Distance = Math.Sqrt(distanceSquared)
            };
        }

        private static WorldResourceConfig GetWorldResourceConfig(ECompTypeData typeData)
        {
            if (typeData == null)
                return null;

            var dao = WorldResourceDao.Inst;
            if (dao == null)
                return null;

            return dao.GetCfg(typeData.ConfigId);
        }

        private static string GetResourceTypeName(WorldResourceConfig config)
        {
            if (config == null || !Enum.IsDefined(typeof(CollectType), (int)config.Type))
                return "Unknown";

            return ((CollectType)config.Type).ToString().Replace("KCollectType", string.Empty);
        }

        private static string DescribeResourceConfig(WorldResourceConfig config)
        {
            if (config == null)
                return "not-found";

            string collectType = GetResourceTypeName(config);
            return "id=" + config.Id.ToString(CultureInfo.InvariantCulture)
                + ",type=" + config.Type.ToString(CultureInfo.InvariantCulture) + "(" + collectType + ")"
                + ",level=" + config.Level.ToString(CultureInfo.InvariantCulture)
                + ",resourceCount=" + config.ResourceCount.ToString(CultureInfo.InvariantCulture)
                + ",model=" + (config.Model ?? string.Empty)
                + ",iconUi=" + (config.IconUi ?? string.Empty);
        }

        private static void CountResourceNode(
            WorldMapScanResult result,
            WorldResourceConfig config,
            uint remaining)
        {
            if (config == null)
            {
                result.UnknownResourceNodeCount++;
                return;
            }

            switch ((CollectType)config.Type)
            {
                case CollectType.KCollectTypeFood:
                    result.FoodNodeCount++;
                    result.FoodRemaining += remaining;
                    break;
                case CollectType.KCollectTypeWood:
                    result.WoodNodeCount++;
                    result.WoodRemaining += remaining;
                    break;
                case CollectType.KCollectTypeSteel:
                    result.SteelNodeCount++;
                    result.SteelRemaining += remaining;
                    break;
                case CollectType.KCollectTypeOil:
                    result.OilNodeCount++;
                    result.OilRemaining += remaining;
                    break;
                default:
                    result.UnknownResourceNodeCount++;
                    break;
            }
        }

        private static void WriteDiagnostics(WorldMapScanResult result)
        {
            MelonLogger.Msg(
                (result.IsCacheOnlyProbe
                    ? "Cache-only map probe (world map closed; cached data may be stale): "
                    : "Map scan: ")
                + result.MainEntityCount + " MainEntity, "
                + result.WorldEntityCount + " WorldEntity; Collect="
                + result.MainCollectEntityCount + "/" + result.WorldCollectEntityCount
                + "; map objects=" + result.MainMapObjectCount + "/" + result.WorldMapObjectCount
                + "; matched unique nodes=" + result.LoadedCollectNodeCount
                + "; collect references=" + result.NodesWithCollectReference
                + "; with collect data=" + result.NodesWithCollectData
                + "; remaining resource total=" + result.TotalRemainingResource
                + "; resource nodes Food=" + result.FoodNodeCount
                + ", Wood=" + result.WoodNodeCount
                + ", Steel=" + result.SteelNodeCount
                + ", Oil=" + result.OilNodeCount
                + ", Unknown=" + result.UnknownResourceNodeCount + ".");

            MelonLogger.Msg(
                "Remaining by resource: Food=" + result.FoodRemaining
                + ", Wood=" + result.WoodRemaining
                + ", Steel=" + result.SteelRemaining
                + ", Oil=" + result.OilRemaining + ".");

            if (result.HasCastlePosition)
            {
                for (int i = 0; i < result.NearestResourceNodes.Count; i++)
                {
                    NearestResourceNodeInfo node = result.NearestResourceNodes[i];
                    MelonLogger.Msg(
                        "Nearest loaded " + node.Resource + " node from MainCastleEntity: id="
                        + node.EntityId.ToString(CultureInfo.InvariantCulture)
                        + ", distance=" + node.Distance.ToString("F1", CultureInfo.InvariantCulture)
                        + ", mapPos=(" + node.X.ToString("F1", CultureInfo.InvariantCulture)
                        + ", " + node.Y.ToString("F1", CultureInfo.InvariantCulture) + ")"
                        + (node.HasGridPosition
                            ? ", gridPos=(" + node.GridX.ToString(CultureInfo.InvariantCulture)
                                + ", " + node.GridY.ToString(CultureInfo.InvariantCulture) + ")"
                            : ", gridPos unavailable")
                        + ", ownerId=" + node.OwnerId.ToString(CultureInfo.InvariantCulture)
                        + ", ownerName=" + (string.IsNullOrEmpty(node.OwnerName) ? "<empty>" : node.OwnerName)
                        + ", collectByEntityId=" + node.CollectByEntityId.ToString(CultureInfo.InvariantCulture)
                        + ", remaining=" + node.Remaining.ToString(CultureInfo.InvariantCulture) + ".");
                }
            }

            for (int i = 0; i < result.Diagnostics.Count; i++)
                MelonLogger.Msg(result.Diagnostics[i]);
        }
    }
}
