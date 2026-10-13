using System;
using System.IO;
using System.Text.Json;
using MelonLoader;

namespace FesticalAutoFarm
{
    internal static class WorldMapSnapshotStore
    {
        private static readonly string SnapshotPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "UserData", "FesticalAutoFarmMap.json");

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        public static WorldMapScanResult Load()
        {
            if (!File.Exists(SnapshotPath))
                return null;

            try
            {
                string json = File.ReadAllText(SnapshotPath);
                WorldMapScanResult snapshot = JsonSerializer.Deserialize<WorldMapScanResult>(json, JsonOptions);
                if (snapshot == null || !snapshot.HasScanned || snapshot.LoadedResourceNodes == null)
                    throw new InvalidDataException("The saved map snapshot is empty or incomplete.");

                snapshot.IsPersistedSnapshot = true;
                snapshot.State = WorldMapState.NotOnWorldMap;
                snapshot.CurrentOriginValidated = false;
                MelonLogger.Msg(
                    "Loaded saved world-map snapshot: nodes=" + snapshot.LoadedResourceNodes.Count
                    + " capturedUtc=" + snapshot.SnapshotCapturedAtUtc.ToString("O"));
                return snapshot;
            }
            catch (Exception ex)
            {
                MelonLogger.Error("Could not load map snapshot from " + SnapshotPath + ": " + ex);
                return null;
            }
        }

        public static bool Save(WorldMapScanResult snapshot)
        {
            try
            {
                if (snapshot == null || !snapshot.HasScanned || snapshot.State != WorldMapState.OnWorldMap)
                    throw new InvalidOperationException("Only a successful active world-map scan can be saved.");

                string directory = Path.GetDirectoryName(SnapshotPath);
                if (string.IsNullOrEmpty(directory))
                    throw new InvalidOperationException("The map snapshot directory could not be resolved.");

                Directory.CreateDirectory(directory);
                string json = JsonSerializer.Serialize(snapshot, JsonOptions);
                File.WriteAllText(SnapshotPath, json);
                return true;
            }
            catch (Exception ex)
            {
                MelonLogger.Error("Could not save map snapshot to " + SnapshotPath + ": " + ex);
                return false;
            }
        }
    }
}
