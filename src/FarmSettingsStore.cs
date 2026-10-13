using System;
using System.IO;
using System.Text.Json;
using MelonLoader;

namespace FesticalAutoFarm
{
    internal static class FarmSettingsStore
    {
        internal const int MaximumFarmSlots = 5;
        internal const long MaximumTargetAmount = 1_000_000_000_000L;
        internal const int MaximumTroopCount = 1_000_000_000;

        private static readonly string SettingsPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "UserData", "FesticalAutoFarm.json");

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        public static FarmSettings Load()
        {
            if (!File.Exists(SettingsPath))
                return FarmSettings.CreateDefault();

            try
            {
                string json = File.ReadAllText(SettingsPath);
                FarmSettings settings = JsonSerializer.Deserialize<FarmSettings>(json, JsonOptions);
                if (settings == null)
                {
                    MelonLogger.Warning("Farm settings were empty; using the default farm settings.");
                    return FarmSettings.CreateDefault();
                }

                settings.Validate();
                return settings;
            }
            catch (Exception ex)
            {
                MelonLogger.Error("Could not load farm settings from " + SettingsPath + ": " + ex.Message);
                return FarmSettings.CreateDefault();
            }
        }

        public static bool Save(FarmSettings settings)
        {
            try
            {
                settings.Validate();
                string directory = Path.GetDirectoryName(SettingsPath);
                if (string.IsNullOrEmpty(directory))
                    throw new InvalidOperationException("The settings directory could not be resolved.");

                Directory.CreateDirectory(directory);
                string json = JsonSerializer.Serialize(settings, JsonOptions);
                File.WriteAllText(SettingsPath, json);
                return true;
            }
            catch (Exception ex)
            {
                MelonLogger.Error("Could not save farm settings to " + SettingsPath + ": " + ex.Message);
                return false;
            }
        }
    }
}
