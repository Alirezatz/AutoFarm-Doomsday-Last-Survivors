using System;
using MelonLoader;
using UnityEngine;

[assembly: MelonInfo(typeof(FesticalAutoFarm.ResourceFarmMod), "Festical Auto Farm", "1.0.0", "AlirezaFeStical")]
[assembly: MelonGame(null, null)]

namespace FesticalAutoFarm
{
    public sealed class ResourceFarmMod : MelonMod
    {
        private FarmPanelUi _ui;
        private WorldMapReader _worldMapReader;
        private PlayerRosterReader _playerRosterReader;
        private DeploymentPreviewLogger _deploymentPreviewLogger;
        private SquadCaptureController _squadCaptureController;
        private FarmSettings _settings;
        private string _lastUiError;
        private string _lastMapScanError;
        private bool _initialMapScanStarted;
        private bool _initialMapScanCompleted;
        private DateTime _initialMapScanDeadlineUtc;
        private DateTime _nextInitialMapScanUtc;
        private DateTime _nextMapDataRequestUtc;
        private DateTime _nextMapRescanUtc;
        private DateTime _mapRescanDeadlineUtc;
        private bool _mapRescanPending;

        public override void OnInitializeMelon()
        {
            _ui = new FarmPanelUi();
            ModUiInputBlocker.Apply(_ui);
            _worldMapReader = new WorldMapReader();
            _playerRosterReader = new PlayerRosterReader();
            _deploymentPreviewLogger = new DeploymentPreviewLogger();
            _squadCaptureController = new SquadCaptureController();
            _settings = FarmSettingsStore.Load();
            _squadCaptureController.SetCaptureCompletedCallback(() =>
            {
                if (_ui != null)
                    _ui.Visible = true;
            });
            bool marchButtonIntercepted = SquadMarchButtonPatch.Apply(
                _squadCaptureController,
                _settings,
                FarmSettingsStore.Save);
            _squadCaptureController.SetMarchButtonInterceptionEnabled(marchButtonIntercepted);
            MelonLogger.Msg("Festical Auto Farm v1.0.0 loaded. Press F8 to toggle the panel.");
            MelonLogger.Msg(
                "Telegram: @AlirezaFeStical  |  YouTube: DoomsdayFT");
            MelonLogger.Msg(
                "Farm settings and the last active world-map scan are saved locally. "
                + "Squad setup uses the native DeployMainPanel; farm dispatch calls the native create-march API directly.");
        }

        public override void OnUpdate()
        {
            if (_ui != null && Input.GetKeyDown(_ui.ToggleKey))
                _ui.Visible = !_ui.Visible;

            if (_deploymentPreviewLogger != null && _worldMapReader != null)
            {
                if (_squadCaptureController != null)
                    _squadCaptureController.Update(_settings, FarmSettingsStore.Save);

                _worldMapReader.RefreshLiveResourceNodesIfChanged();
                _worldMapReader.AdvancePendingMapRequest();
                AdvanceRequestedMapRescan();
                AdvanceInitialMapScan();

                _deploymentPreviewLogger.AdvanceOneShotMarch(_worldMapReader.Current);
            }
        }

        public override void OnGUI()
        {
            if (_ui == null || _worldMapReader == null || _settings == null)
                return;

            try
            {
                _ui.Draw(
                    _worldMapReader.Current,
                    _worldMapReader.LiveResourceNodes,
                    _settings,
                    StartFarm,
                    StopFarm,
                    _deploymentPreviewLogger.FarmRunning,
                    _deploymentPreviewLogger.FarmStatus,
                    FarmSettingsStore.Save,
                    LoadSquad,
                    _squadCaptureController == null ? null : _squadCaptureController.Status);
                _lastUiError = null;
            }
            catch (Exception ex)
            {
                if (_lastUiError != ex.Message)
                {
                    _lastUiError = ex.Message;
                    MelonLogger.Error("Farm panel error: " + ex);
                }
            }
        }

        private bool LoadSquad(int slotIndex)
        {
            if (_squadCaptureController == null || _settings == null || _worldMapReader == null)
                return false;
            if (_deploymentPreviewLogger != null && _deploymentPreviewLogger.FarmRunning)
            {
                _squadCaptureController.SetStatus("Stop farming before loading or changing a squad.");
                return false;
            }

            bool started = _squadCaptureController.BeginCapture(
                slotIndex,
                _settings,
                _worldMapReader.Current);
            if (started && _ui != null)
                _ui.Visible = false;
            return started;
        }

        private void RefreshRoster()
        {
            try
            {
                if (_playerRosterReader == null)
                    return;

                _playerRosterReader.Refresh();
            }
            catch (Exception ex)
            {
                MelonLogger.Error("Farm hero/troop roster refresh failed: " + ex);
            }
        }

        private string LogFoodPreview()
        {
            try
            {
                string result = _deploymentPreviewLogger.LogNearestFoodPreview(_worldMapReader.Current);
                MelonLogger.Msg("Food preview button result: " + result);
                return result;
            }
            catch (Exception ex)
            {
                MelonLogger.Error("Food preview diagnostic error: " + ex);
                return "Preview read failed; see MelonLoader error log.";
            }
        }

        private string StartFarm()
        {
            try
            {
                if (_squadCaptureController != null && _squadCaptureController.IsCapturing)
                    return "Close the game's deployment panel before starting farming.";

                string result = _deploymentPreviewLogger.StartFarm(_worldMapReader.Current, _settings);
                if (_deploymentPreviewLogger.FarmRunning)
                    _initialMapScanCompleted = true;
                MelonLogger.Msg("Farm start result: " + result);
                return result;
            }
            catch (Exception ex)
            {
                MelonLogger.Error("Farm start failed: " + ex);
                return "Farm could not start; check the MelonLoader log.";
            }
        }

        private string StopFarm()
        {
            try
            {
                string result = _deploymentPreviewLogger.StopFarm();
                MelonLogger.Msg("Farm stop result: " + result);
                return result;
            }
            catch (Exception ex)
            {
                MelonLogger.Error("Farm stop failed: " + ex);
                return "Farm could not stop cleanly; check the MelonLoader log.";
            }
        }

        private void ScanWorldMap()
        {
            try
            {
                if (_deploymentPreviewLogger != null && _deploymentPreviewLogger.FarmRunning)
                {
                    _lastMapScanError = "Stop farming before scanning; the active farm uses a fixed map snapshot.";
                    return;
                }

                _worldMapReader.Refresh();
                _lastMapScanError = null;
                if (_worldMapReader.Current != null
                    && _worldMapReader.Current.State == WorldMapState.OnWorldMap
                    && _worldMapReader.Current.LoadedResourceNodes.Count > 0)
                {
                    _initialMapScanCompleted = true;
                    _mapRescanPending = false;
                }
                else if (_worldMapReader.IsMainWorldActive)
                {
                    RequestMapDataForEmptyCache("manual map scan");
                }
            }
            catch (Exception ex)
            {
                _worldMapReader.MarkUnavailable();
                _lastMapScanError = ex.Message;
                MelonLogger.Error("World-map scan error: " + ex);
            }
        }

        private void AdvanceInitialMapScan()
        {
            if (_initialMapScanCompleted)
                return;

            DateTime now = DateTime.UtcNow;
            if (!_worldMapReader.IsMainWorldActive)
            {
                _initialMapScanStarted = false;
                return;
            }

            if (!_initialMapScanStarted)
            {
                _initialMapScanStarted = true;
                _initialMapScanDeadlineUtc = now + TimeSpan.FromSeconds(90);
                _nextInitialMapScanUtc = now + TimeSpan.FromSeconds(2);
                MelonLogger.Msg(
                    "First active main-world map context detected; an automatic local resource scan is scheduled.");
            }

            if (now < _nextInitialMapScanUtc)
                return;

            if (_deploymentPreviewLogger.FarmRunning)
            {
                _initialMapScanCompleted = true;
                MelonLogger.Warning(
                    "Automatic initial map scan skipped because farming is already active; "
                    + "the current map snapshot will remain unchanged.");
                return;
            }

            try
            {
                _worldMapReader.Refresh();
                WorldMapScanResult scan = _worldMapReader.Current;
                if (scan != null
                    && scan.State == WorldMapState.OnWorldMap
                    && scan.LoadedResourceNodes != null
                    && scan.LoadedResourceNodes.Count > 0)
                {
                    _initialMapScanCompleted = true;
                    _mapRescanPending = false;
                    MelonLogger.Msg(
                        "Automatic first-map scan completed: "
                        + scan.LoadedResourceNodes.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + " resource nodes saved from the initially loaded map area.");
                    return;
                }

                RequestMapDataForEmptyCache("automatic initial scan");
                if (now >= _initialMapScanDeadlineUtc)
                {
                    _initialMapScanCompleted = true;
                    MelonLogger.Warning(
                        "Automatic first-map scan timed out without loaded resource nodes; "
                        + "use Map scan when the local map data is ready.");
                    return;
                }

                _nextInitialMapScanUtc = now + TimeSpan.FromSeconds(5);
            }
            catch (Exception ex)
            {
                MelonLogger.Error("Automatic initial world-map scan failed: " + ex);
                if (now >= _initialMapScanDeadlineUtc)
                {
                    _initialMapScanCompleted = true;
                    MelonLogger.Warning(
                        "Automatic first-map scan stopped retrying after 30 seconds; "
                        + "use Map scan when the local map data is ready.");
                    return;
                }
                _nextInitialMapScanUtc = now + TimeSpan.FromSeconds(5);
            }
        }

        private void RequestMapDataForEmptyCache(string source)
        {
            DateTime now = DateTime.UtcNow;
            if (now < _nextMapDataRequestUtc)
                return;

            _nextMapDataRequestUtc = now + TimeSpan.FromSeconds(30);
            string result = _worldMapReader.RequestNearbyMapCells();
            MelonLogger.Msg(source + " found no loaded resource nodes; " + result);
            if (result.IndexOf("submitted", StringComparison.OrdinalIgnoreCase) >= 0
                || result.IndexOf("queued", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                _mapRescanPending = true;
                _nextMapRescanUtc = now + TimeSpan.FromSeconds(5);
                _mapRescanDeadlineUtc = now + TimeSpan.FromSeconds(45);
            }
        }

        private void AdvanceRequestedMapRescan()
        {
            if (!_mapRescanPending || DateTime.UtcNow < _nextMapRescanUtc)
                return;
            if (!_worldMapReader.IsMainWorldActive)
            {
                _nextMapRescanUtc = DateTime.UtcNow + TimeSpan.FromSeconds(5);
                return;
            }
            if (DateTime.UtcNow >= _mapRescanDeadlineUtc)
            {
                _mapRescanPending = false;
                MelonLogger.Warning(
                    "Nearby map-data request produced no resource nodes within 45 seconds; "
                    + "the saved snapshot was kept unchanged.");
                return;
            }

            try
            {
                _worldMapReader.Refresh();
                WorldMapScanResult scan = _worldMapReader.Current;
                if (scan != null
                    && scan.State == WorldMapState.OnWorldMap
                    && scan.LoadedResourceNodes != null
                    && scan.LoadedResourceNodes.Count > 0)
                {
                    _mapRescanPending = false;
                    _initialMapScanCompleted = true;
                    MelonLogger.Msg(
                        "Nearby map-data rescan completed: "
                        + scan.LoadedResourceNodes.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + " resource nodes are now available.");
                    return;
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Error("Nearby map-data rescan failed: " + ex);
            }

            _nextMapRescanUtc = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        }
    }
}
