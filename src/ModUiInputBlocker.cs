using System;
using System.Reflection;
using HarmonyLib;
using Il2CppFairyGUI;
using Il2CppIGG.Game.Module.WorldMap.Comps;
using MelonLoader;
using UnityEngine;

namespace FesticalAutoFarm
{
    internal static class ModUiInputBlocker
    {
        private const string HarmonyId = "com.alirezafestical.dlsresourcefarm.uiinput";
        private static Func<bool> _shouldBlockInput;

        public static bool Apply(FarmPanelUi ui)
        {
            _shouldBlockInput = () => ui != null && ui.IsScreenPointerInsidePanel();

            MethodInfo guiEvents = AccessTools.Method(typeof(Stage), "HandleGUIEvents");
            MethodInfo mouseEvents = AccessTools.Method(typeof(Stage), "HandleMouseEvents");
            MethodInfo mapClickEvents = AccessTools.Method(typeof(CompMapObjClickMgr), "UpdateInner");
            MethodInfo mapCameraDrag = AccessTools.Method(typeof(CompWorldCamera), "DragCameraScreen");
            MethodInfo guiPrefix = AccessTools.Method(typeof(ModUiInputBlocker), "BeforeHandleGUIEvents");
            MethodInfo mousePrefix = AccessTools.Method(typeof(ModUiInputBlocker), "BeforeHandleMouseEvents");
            MethodInfo mapClickPrefix = AccessTools.Method(typeof(ModUiInputBlocker), "BeforeMapClickUpdate");
            MethodInfo mapCameraPrefix = AccessTools.Method(typeof(ModUiInputBlocker), "BeforeMapCameraDrag");
            if (guiEvents == null || mouseEvents == null || mapClickEvents == null || mapCameraDrag == null
                || guiPrefix == null || mousePrefix == null || mapClickPrefix == null || mapCameraPrefix == null)
            {
                MelonLogger.Error("Mod UI input blocking was not installed: game input method lookup failed.");
                return false;
            }

            try
            {
                var harmony = new HarmonyLib.Harmony(HarmonyId);
                harmony.Patch(guiEvents, prefix: new HarmonyMethod(guiPrefix));
                harmony.Patch(mouseEvents, prefix: new HarmonyMethod(mousePrefix));
                harmony.Patch(mapClickEvents, prefix: new HarmonyMethod(mapClickPrefix));
                harmony.Patch(mapCameraDrag, prefix: new HarmonyMethod(mapCameraPrefix));
                MelonLogger.Msg("Mod UI mouse-input blocking is active for FairyGUI and world-map input.");
                return true;
            }
            catch (Exception ex)
            {
                MelonLogger.Error("Mod UI FairyGUI input blocking failed: " + ex);
                return false;
            }
        }

        private static bool BeforeHandleGUIEvents(Event __0)
        {
            return !IsMouseEvent(__0) || !ShouldBlockInput(__0.mousePosition);
        }

        private static bool BeforeHandleMouseEvents()
        {
            return !(_shouldBlockInput != null && _shouldBlockInput());
        }

        private static bool BeforeMapClickUpdate()
        {
            return !(_shouldBlockInput != null && _shouldBlockInput());
        }

        private static bool BeforeMapCameraDrag(ref bool __result)
        {
            if (_shouldBlockInput == null || !_shouldBlockInput())
                return true;

            __result = false;
            return false;
        }

        private static bool ShouldBlockInput(Vector2 guiPosition)
        {
            return _shouldBlockInput != null
                && FarmPanelUi.IsPointerInsidePanel(guiPosition)
                && _shouldBlockInput();
        }

        private static bool IsMouseEvent(Event inputEvent)
        {
            if (inputEvent == null)
                return false;

            switch (inputEvent.type)
            {
                case EventType.MouseDown:
                case EventType.MouseUp:
                case EventType.MouseDrag:
                case EventType.MouseMove:
                case EventType.ScrollWheel:
                case EventType.ContextClick:
                    return true;
                default:
                    return false;
            }
        }
    }
}
