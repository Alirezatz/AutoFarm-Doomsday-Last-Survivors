using System;
using System.Collections.Generic;
using Il2CppIGG.Game.Data.Cache;
using Il2CppIGG.Game.Data.Cache.Hero;
using Il2CppIGG.Game.Data.Cache.Troop;
using Il2CppProtomsg;
using MelonLoader;

namespace FesticalAutoFarm
{
    internal sealed class PlayerHeroOption
    {
        public uint Id { get; set; }
    }

    internal sealed class PlayerTroopOption
    {
        public uint Id { get; set; }
        public ulong Available { get; set; }
    }

    internal sealed class PlayerRosterSnapshot
    {
        public List<PlayerHeroOption> Heroes { get; } = new List<PlayerHeroOption>();
        public List<PlayerTroopOption> Troops { get; } = new List<PlayerTroopOption>();
        public string Error { get; set; }
        public DateTime ReadAtUtc { get; set; }
    }

    internal sealed class PlayerRosterReader
    {
        private string _lastError;

        public PlayerRosterSnapshot Current { get; private set; }

        public void Refresh()
        {
            var snapshot = new PlayerRosterSnapshot { ReadAtUtc = DateTime.UtcNow };
            try
            {
                HeroCache heroCache = AppCache.Hero;
                if (heroCache != null && heroCache.HeroInfos != null)
                {
                    for (int i = 0; i < heroCache.HeroInfos.Count; i++)
                    {
                        HeroInfo hero = heroCache.HeroInfos[i];
                        if (hero == null || hero.HeroId == 0)
                            continue;

                        snapshot.Heroes.Add(new PlayerHeroOption
                        {
                            Id = hero.HeroId
                        });
                    }
                }
                TroopCache troopCache = AppCache.Troop;
                if (troopCache != null)
                {
                    var troopInfos = troopCache.GetUniqueTroopArmyInfos();
                    if (troopInfos != null)
                    {
                        for (int i = 0; i < troopInfos.Count; i++)
                        {
                            ArmyInfo troop = troopInfos[i];
                            if (troop == null || troop.ArmyId == 0 || troop.Count == 0)
                                continue;

                            snapshot.Troops.Add(new PlayerTroopOption
                            {
                                Id = troop.ArmyId,
                                Available = troop.Count
                            });
                        }
                    }
                }

                snapshot.Heroes.Sort((left, right) => left.Id.CompareTo(right.Id));
                snapshot.Troops.Sort((left, right) => left.Id.CompareTo(right.Id));
                if (snapshot.Heroes.Count == 0 && snapshot.Troops.Count == 0)
                    snapshot.Error = "Game hero/troop caches are not ready or contain no owned units.";
            }
            catch (Exception ex)
            {
                snapshot.Error = ex.Message;
                if (!string.Equals(_lastError, snapshot.Error, StringComparison.Ordinal))
                {
                    _lastError = snapshot.Error;
                    MelonLogger.Error("Farm hero/troop roster read failed: " + ex);
                }
            }

            if (snapshot.Error == null)
                _lastError = null;
            Current = snapshot;
        }
    }
}
