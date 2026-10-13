using System;
using HarmonyLib;
using Il2CppIGG.Game.Module.March.View;
using MelonLoader;

namespace FesticalAutoFarm
{
    internal static class SquadMarchButtonPatch
    {
        private const string HarmonyId = "com.alirezafestical.dlsresourcefarm.squadcapture";
        private static SquadCaptureController _controller;
        private static FarmSettings _settings;
        private static Func<FarmSettings, bool> _saveSettings;

        public static bool Apply(
            SquadCaptureController controller,
            FarmSettings settings,
            Func<FarmSettings, bool> saveSettings)
        {
            _controller = controller;
            _settings = settings;
            _saveSettings = saveSettings;

            var harmony = new HarmonyLib.Harmony(HarmonyId);
            var original = AccessTools.Method(typeof(DeployMainPanel), "OnBtnGO");
            var prefix = AccessTools.Method(typeof(SquadMarchButtonPatch), "BeforeMarchButton");
            if (original == null || prefix == null)
            {
                MelonLogger.Error("Squad capture March-button patch was not applied: method lookup failed.");
                return false;
            }

            try
            {
                harmony.Patch(original, prefix: new HarmonyMethod(prefix));
                MelonLogger.Msg("Squad capture March-button interception is ready.");
                return true;
            }
            catch (Exception ex)
            {
                MelonLogger.Error("Squad capture March-button patch failed: " + ex);
                return false;
            }
        }

        private static bool BeforeMarchButton(DeployMainPanel __instance)
        {
            SquadCaptureController controller = _controller;
            if (controller == null || !controller.IsCapturing)
                return true;

            try
            {
                return controller.HandleMarchButtonPressed(__instance, _settings, _saveSettings);
            }
            catch (Exception ex)
            {
                MelonLogger.Error("Squad capture March-button prefix failed; dispatch suppressed: " + ex);
                return false;
            }
        }
    }
}
