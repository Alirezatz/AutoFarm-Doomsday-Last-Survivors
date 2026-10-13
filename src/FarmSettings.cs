using System;
using System.Collections.Generic;

namespace FesticalAutoFarm
{
    internal enum FarmResource
    {
        Oil,
        Wood,
        Food,
        Steel
    }

    internal sealed class FarmSlotSettings
    {
        public bool Enabled { get; set; } = true;
        public FarmResource Resource { get; set; }
        public long TargetAmount { get; set; } = 100;
        public int TroopCount { get; set; } = 10_000;
        public bool UseRecommendedHeroes { get; set; } = true;
        public bool UseCustomHeroes { get; set; }
        public uint MainHeroId { get; set; }
        public uint SecondHeroId { get; set; }
        public uint TroopId { get; set; }
        public string MainHeroName { get; set; }
        public string SecondHeroName { get; set; }
        public List<FarmTroopSelection> Troops { get; set; } = new List<FarmTroopSelection>();
    }

    internal sealed class FarmTroopSelection
    {
        public uint Id { get; set; }
        public string Name { get; set; }
        public int Count { get; set; }
    }

    internal sealed class FarmSettings
    {
        public int SchemaVersion { get; set; }
        public int DurationMinutes { get; set; } = 120;
        public List<FarmSlotSettings> Slots { get; set; } = new List<FarmSlotSettings>();

        public static FarmSettings CreateDefault()
        {
            var settings = new FarmSettings { SchemaVersion = 3 };
            for (int i = 0; i < FarmSettingsStore.MaximumFarmSlots; i++)
            {
                settings.Slots.Add(new FarmSlotSettings { Resource = (FarmResource)i });
            }

            return settings;
        }

        public void Validate()
        {
            if (SchemaVersion < 1)
            {
                if (Slots != null)
                {
                    for (int i = 0; i < Slots.Count; i++)
                    {
                        if (Slots[i] != null && Slots[i].TargetAmount == 5_000)
                            Slots[i].TargetAmount = 100;
                    }
                }
            }
            if (SchemaVersion < 2)
                SchemaVersion = 2;
            if (SchemaVersion < 3)
                SchemaVersion = 3;

            if (DurationMinutes < 1)
                DurationMinutes = 1;

            if (Slots == null)
                Slots = new List<FarmSlotSettings>();

            if (Slots.Count > FarmSettingsStore.MaximumFarmSlots)
                Slots.RemoveRange(FarmSettingsStore.MaximumFarmSlots, Slots.Count - FarmSettingsStore.MaximumFarmSlots);

            if (Slots.Count == 0)
                Slots.Add(new FarmSlotSettings());

            while (Slots.Count < FarmSettingsStore.MaximumFarmSlots)
            {
                int nextIndex = Slots.Count;
                Slots.Add(new FarmSlotSettings { Resource = (FarmResource)(nextIndex % 4) });
            }

            for (int i = 0; i < Slots.Count; i++)
            {
                FarmSlotSettings slot = Slots[i];
                if (slot == null)
                {
                    Slots[i] = new FarmSlotSettings { Resource = (FarmResource)(i % 4) };
                    slot = Slots[i];
                }

                if (!Enum.IsDefined(typeof(FarmResource), slot.Resource))
                    slot.Resource = FarmResource.Oil;

                if (slot.TargetAmount < 0)
                    slot.TargetAmount = 0;
                else if (slot.TargetAmount > FarmSettingsStore.MaximumTargetAmount)
                    slot.TargetAmount = FarmSettingsStore.MaximumTargetAmount;

                if (slot.TroopCount < 0)
                    slot.TroopCount = 0;
                else if (slot.TroopCount > FarmSettingsStore.MaximumTroopCount)
                    slot.TroopCount = FarmSettingsStore.MaximumTroopCount;

                if (slot.Troops == null)
                    slot.Troops = new List<FarmTroopSelection>();

                if (slot.Troops.Count == 0 && slot.TroopId != 0 && slot.TroopCount > 0)
                {
                    slot.Troops.Add(new FarmTroopSelection
                    {
                        Id = slot.TroopId,
                        Count = slot.TroopCount
                    });
                }

                for (int troopIndex = slot.Troops.Count - 1; troopIndex >= 0; troopIndex--)
                {
                    FarmTroopSelection troop = slot.Troops[troopIndex];
                    if (troop == null || troop.Id == 0 || troop.Count <= 0)
                    {
                        slot.Troops.RemoveAt(troopIndex);
                        continue;
                    }

                    if (troop.Count > FarmSettingsStore.MaximumTroopCount)
                        troop.Count = FarmSettingsStore.MaximumTroopCount;
                }

                if (slot.Troops.Count > 0)
                {
                    slot.TroopId = slot.Troops[0].Id;
                    slot.TroopCount = slot.Troops[0].Count;
                }
            }
        }
    }
}
