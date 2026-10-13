using System;
using System.Collections.Generic;
using System.Globalization;
using Il2CppFairyGUI;
using Il2CppIGG.Framework.Panel;
using Il2CppIGG.Game.Data.Cache.Hero;
using Il2CppIGG.Game.Data.Config;
using Il2CppIGG.Game.Module.March;
using Il2CppIGG.Game.Module.March.View;
using Il2CppIGG.Game.UI.Deploy;
using Il2CppInterop.Runtime;
using Il2CppProtomsg;
using MelonLoader;

namespace FesticalAutoFarm
{
    internal sealed class SquadCaptureController
    {
        private static readonly TimeSpan CaptureTimeout = TimeSpan.FromSeconds(30);
        private int _slotIndex = -1;
        private ulong _targetId;
        private DateTime _captureStartedUtc;
        private string _lastSavedSignature;
        private string _lastAttemptSignature;
        private DateTime _retrySaveAfterUtc;
        private string _lastError;
        private bool _panelObserved;
        private bool _marchButtonInterceptionEnabled;
        private bool _marchButtonDisabled;
        private bool _marchButtonWasTouchable;
        private Action _captureCompleted;

        public string Status { get; private set; }
        public bool IsCapturing
        {
            get { return _slotIndex >= 0; }
        }

        public void SetStatus(string status)
        {
            Status = status;
        }

        public void SetMarchButtonInterceptionEnabled(bool enabled)
        {
            _marchButtonInterceptionEnabled = enabled;
        }

        public void SetCaptureCompletedCallback(Action callback)
        {
            _captureCompleted = callback;
        }

        public bool HandleMarchButtonPressed(
            DeployMainPanel panel,
            FarmSettings settings,
            Func<FarmSettings, bool> saveSettings)
        {
            if (_slotIndex < 0)
                return true;

            try
            {
                if (panel == null || GetPanelTargetId(panel) != _targetId)
                {
                    Status = "March was blocked because this is not the panel opened for squad capture.";
                    return false;
                }

                if (!CaptureCurrentSelection(panel, settings, saveSettings, true))
                    return false;

                RestoreMarchButton(panel);
                panel.Close();
                EndCapture("Squad saved to Slot "
                    + (_slotIndex + 1).ToString(CultureInfo.InvariantCulture)
                    + ". The panel closed without dispatching a march.");
                if (_captureCompleted != null)
                    _captureCompleted();
                MelonLogger.Msg("Native March button used to save squad; dispatch suppressed.");
                return false;
            }
            catch (Exception ex)
            {
                Status = "March was blocked; squad capture failed. See the MelonLoader log.";
                MelonLogger.Error("Farm native March-button capture failed; dispatch suppressed: " + ex);
                return false;
            }
        }

        public bool BeginCapture(int slotIndex, FarmSettings settings, WorldMapScanResult mapScan)
        {
            if (settings == null || slotIndex < 0 || slotIndex >= settings.Slots.Count)
            {
                Status = "The selected farm slot is unavailable.";
                return false;
            }

            if (mapScan == null || !mapScan.HasUsableNodeSnapshot)
            {
                Status = "No saved resource nodes yet. Open the world map once so a squad target can be used.";
                return false;
            }

            if (_slotIndex >= 0)
            {
                Status = "Finish or close the current game's deployment panel first.";
                return false;
            }

            PanelMgr panelManager = PanelMgr.Inst;
            if (panelManager == null)
            {
                Status = "The game's panel manager is unavailable.";
                return false;
            }

            DeployMainPanel existingPanel = panelManager.GetPanel<DeployMainPanel>();
            if (existingPanel != null && existingPanel.IsOpen)
            {
                Status = "Close the current deployment panel before loading a squad.";
                return false;
            }

            FarmSlotSettings slot = settings.Slots[slotIndex];
            NearestResourceNodeInfo target = FindNearestNode(mapScan, GetResourceName(slot.Resource));
            if (target == null)
            {
                Status = "No unowned " + GetResourceName(slot.Resource)
                    + " node is available in the current map scan.";
                return false;
            }

            try
            {
                ClientMarchCommandRequestData requestData =
                    MarchModule.API.CreateBaseCommandRequestData(
                        GetMarchTargetType(slot.Resource),
                        MarchCommandTarget.KMarchCommandTargetCollect,
                        target.EntityId);
                if (requestData == null)
                {
                    Status = "The game could not create a deployment panel request.";
                    return false;
                }

                if (!MarchModule.API.CheckToOpenDeployMainPanel(requestData))
                {
                    Status = "The game declined to open its deployment panel for this map target.";
                    return false;
                }

                _slotIndex = slotIndex;
                _targetId = target.EntityId;
                _captureStartedUtc = DateTime.UtcNow;
                _lastSavedSignature = null;
                _lastAttemptSignature = null;
                _retrySaveAfterUtc = DateTime.MinValue;
                _panelObserved = false;
                Status = _marchButtonInterceptionEnabled
                    ? "Choose heroes and troops, then click March to save and close without dispatching."
                    : "Choose heroes and troops; the March button is disabled. Close the panel when finished.";
                MelonLogger.Msg(
                    "Squad capture started: slot=" + (slotIndex + 1).ToString(CultureInfo.InvariantCulture)
                    + " target=" + target.EntityId.ToString(CultureInfo.InvariantCulture)
                    + " resource=" + GetResourceName(slot.Resource));
                return true;
            }
            catch (Exception ex)
            {
                Status = "The game's deployment panel could not be opened; see the MelonLoader log.";
                MelonLogger.Error("Farm native squad panel open failed: " + ex);
                return false;
            }
        }

        public void Update(FarmSettings settings, Func<FarmSettings, bool> saveSettings)
        {
            if (_slotIndex < 0)
                return;

            try
            {
                PanelMgr panelManager = PanelMgr.Inst;
                DeployMainPanel panel = panelManager == null
                    ? null
                    : panelManager.GetPanel<DeployMainPanel>();
                if (panel == null || !panel.IsOpen || GetPanelTargetId(panel) != _targetId)
                {
                    RestoreMarchButton(panel);
                    if (_panelObserved)
                    {
                        EndCapture("Squad capture ended. Saved selections remain in the slot.");
                    }
                    else if (DateTime.UtcNow - _captureStartedUtc >= CaptureTimeout)
                    {
                        EndCapture("The game's deployment panel did not stay open; squad capture ended.");
                    }
                    return;
                }

                _panelObserved = true;
                if (!_marchButtonInterceptionEnabled && !DisableMarchButton(panel))
                {
                    Status = "Waiting for the game's March button so squad capture can be made safe.";
                    return;
                }
                CaptureCurrentSelection(panel, settings, saveSettings, false);
                _lastError = null;
            }
            catch (Exception ex)
            {
                Status = "Could not read the game's squad selection; see the MelonLoader log.";
                if (!string.Equals(_lastError, ex.Message, StringComparison.Ordinal))
                {
                    _lastError = ex.Message;
                    MelonLogger.Error("Farm native squad capture failed: " + ex);
                }
            }
        }

        private bool CaptureCurrentSelection(
            DeployMainPanel panel,
            FarmSettings settings,
            Func<FarmSettings, bool> saveSettings,
            bool forceSave)
        {
            HeroInfo mainHero = panel.SelectedMainHero;
            if (mainHero == null || mainHero.HeroId == 0 || panel.m_armyDataList == null)
            {
                Status = "Select a main hero and at least one troop type in the game panel.";
                return false;
            }

            HeroInfo secondHero = panel.SelectedSecondHero;
            var selectedTroops = new List<FarmTroopSelection>();
            for (int i = 0; i < panel.m_armyDataList.Count; i++)
            {
                DeployMainPanel.ArmyForSelect row = panel.m_armyDataList[i];
                if (row == null || row.SelectArmyCount == 0)
                    continue;

                if (row.SelectArmyCount > (ulong)FarmSettingsStore.MaximumTroopCount)
                {
                    Status = "A selected troop count exceeds the mod's supported limit.";
                    return false;
                }

                string troopName = row.TroopConfig == null ? string.Empty : row.TroopConfig.GetName();
                selectedTroops.Add(new FarmTroopSelection
                {
                    Id = row.ArmyId,
                    Name = troopName,
                    Count = (int)row.SelectArmyCount
                });
            }

            if (selectedTroops.Count == 0)
            {
                Status = "Select at least one troop type and count in the game panel.";
                return false;
            }

            string mainHeroName = GetHeroName(mainHero.HeroId);
            string secondHeroName = secondHero == null ? string.Empty : GetHeroName(secondHero.HeroId);
            string signature = CreateSignature(mainHero.HeroId, secondHero, selectedTroops);
            if (!forceSave
                && string.Equals(signature, _lastSavedSignature, StringComparison.Ordinal))
            {
                return true;
            }
            if (!forceSave
                && string.Equals(signature, _lastAttemptSignature, StringComparison.Ordinal)
                && DateTime.UtcNow < _retrySaveAfterUtc)
            {
                return false;
            }

            FarmSlotSettings slot = settings.Slots[_slotIndex];
            slot.MainHeroId = mainHero.HeroId;
            slot.MainHeroName = mainHeroName;
            slot.SecondHeroId = secondHero == null ? 0U : secondHero.HeroId;
            slot.SecondHeroName = secondHeroName;
            slot.UseCustomHeroes = true;
            slot.UseRecommendedHeroes = false;
            slot.Troops = selectedTroops;
            slot.TroopId = selectedTroops[0].Id;
            slot.TroopCount = selectedTroops[0].Count;
            settings.Validate();
            _lastAttemptSignature = signature;
            _retrySaveAfterUtc = DateTime.UtcNow + TimeSpan.FromSeconds(2);

            if (!saveSettings(settings))
            {
                Status = "Squad selected, but saving settings failed; check the MelonLoader log.";
                return false;
            }

            _lastSavedSignature = signature;
            Status = "Squad saved to Slot " + (_slotIndex + 1).ToString(CultureInfo.InvariantCulture)
                + ". Keep selecting in the game panel to update it.";
            return true;
        }

        private bool DisableMarchButton(DeployMainPanel panel)
        {
            BaseDeployMainAniView animatedView = panel.m_mainAniView;
            BaseDeployMainView mainView = animatedView == null ? null : animatedView.m_mainView;
            GButton marchButton = mainView == null ? null : mainView.m_btnMarch;
            if (marchButton == null)
                return false;
            if (_marchButtonDisabled)
                return true;

            _marchButtonWasTouchable = marchButton.touchable;
            marchButton.touchable = false;
            _marchButtonDisabled = true;
            return true;
        }

        private void RestoreMarchButton(DeployMainPanel panel)
        {
            if (!_marchButtonDisabled)
                return;

            BaseDeployMainAniView animatedView = panel == null ? null : panel.m_mainAniView;
            BaseDeployMainView mainView = animatedView == null ? null : animatedView.m_mainView;
            GButton marchButton = mainView == null ? null : mainView.m_btnMarch;
            if (marchButton != null)
                marchButton.touchable = _marchButtonWasTouchable;
            _marchButtonDisabled = false;
        }

        private void EndCapture(string status)
        {
            _slotIndex = -1;
            _targetId = 0;
            _lastSavedSignature = null;
            _lastAttemptSignature = null;
            _retrySaveAfterUtc = DateTime.MinValue;
            _panelObserved = false;
            _lastError = null;
            Status = status;
        }

        private static ulong GetPanelTargetId(DeployMainPanel panel)
        {
            if (panel.m_marchCommandRequestData == null)
                return 0;

            MarchCommand command = panel.m_marchCommandRequestData.commandData.Command;
            return command == null ? 0UL : command.TargetId;
        }

        private static NearestResourceNodeInfo FindNearestNode(
            WorldMapScanResult mapScan,
            string resource)
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

                if (nearest == null || candidate.DistanceSquared < nearest.DistanceSquared)
                    nearest = candidate;
            }
            return nearest;
        }

        private static MarchTargetType GetMarchTargetType(FarmResource resource)
        {
            switch (resource)
            {
                case FarmResource.Food:
                    return MarchTargetType.CollectFood;
                case FarmResource.Wood:
                    return MarchTargetType.CollectWood;
                case FarmResource.Steel:
                    return MarchTargetType.CollectSteal;
                case FarmResource.Oil:
                    return MarchTargetType.CollectOil;
                default:
                    throw new ArgumentOutOfRangeException(nameof(resource), resource, null);
            }
        }

        private static string GetResourceName(FarmResource resource)
        {
            return resource.ToString();
        }

        private static string GetHeroName(uint heroId)
        {
            HeroDao dao = HeroDao.Inst;
            string name = dao == null ? string.Empty : dao.GetHeroName(heroId);
            return string.IsNullOrEmpty(name) ? "Hero " + heroId.ToString(CultureInfo.InvariantCulture) : name;
        }

        private static string CreateSignature(
            uint mainHeroId,
            HeroInfo secondHero,
            List<FarmTroopSelection> troops)
        {
            var parts = new List<string>
            {
                mainHeroId.ToString(CultureInfo.InvariantCulture),
                secondHero == null ? "0" : secondHero.HeroId.ToString(CultureInfo.InvariantCulture)
            };
            for (int i = 0; i < troops.Count; i++)
            {
                FarmTroopSelection troop = troops[i];
                parts.Add(troop.Id.ToString(CultureInfo.InvariantCulture)
                    + ":" + troop.Count.ToString(CultureInfo.InvariantCulture));
            }
            return string.Join("|", parts);
        }
    }
}
