using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace Lilly.WaterBodyFishMod
{
    public sealed class WaterBodyFishMod : Mod
    {
        public const int DefaultMaxFishPopulation = 1000000;

        private static WaterBodyFishSettings settings;
        private string maxFishPopulationBuffer;

        internal static int MaxFishPopulation =>
            Math.Max(0, settings?.maxFishPopulation ?? DefaultMaxFishPopulation);

        public WaterBodyFishMod(ModContentPack content) : base(content)
        {
            settings = GetSettings<WaterBodyFishSettings>();
        }

        public override string SettingsCategory()
        {
            return "Water Body Fish Mod";
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            var listing = new Listing_Standard();
            listing.Begin(inRect);

            listing.Label("물고기 최대 개체수 설정");
            maxFishPopulationBuffer = settings.maxFishPopulation.ToString();
            listing.TextFieldNumeric(ref settings.maxFishPopulation, ref maxFishPopulationBuffer);
            settings.maxFishPopulation = Math.Max(0, settings.maxFishPopulation);

            if (listing.ButtonTextLabeled(
                "모든 맵에 물고기 종류 추가",
                "적용",
                tooltip: "기존 모든 맵의 물고기 종류와 개체 수에 적용합니다."))
            {
                WaterBodyFishPatches.ApplyToAllMaps();
            }

            if (listing.ButtonTextLabeled(
                "물고기 종류 출력 테스트",
                "디버그 출력",
                tooltip: "물고기 종류를 로그에 출력합니다."))
            {
                WaterBodyFishPatches.LogAllFishDefs();
            }

            listing.End();
        }
    }

    public sealed class WaterBodyFishSettings : ModSettings
    {
        public int maxFishPopulation = WaterBodyFishMod.DefaultMaxFishPopulation;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(
                ref maxFishPopulation,
                "maxFishPopulation",
                WaterBodyFishMod.DefaultMaxFishPopulation);
            maxFishPopulation = Math.Max(0, maxFishPopulation);
        }
    }

    [StaticConstructorOnStartup]
    internal static class WaterBodyFishPatchStartup
    {
        static WaterBodyFishPatchStartup()
        {
            WaterBodyFishPatches.Install();
        }
    }

    internal static class WaterBodyFishPatches
    {
        private const string HarmonyId = "Lilly.WaterBodyFishMod";
        private static Harmony harmony;

        internal static void Install()
        {
            if (harmony != null)
            {
                return;
            }

            var instance = new Harmony(HarmonyId);
            try
            {
                var setFishTypes = AccessTools.Method(typeof(WaterBody), "SetFishTypes");
                var loadGame = AccessTools.Method(typeof(SavedGameLoaderNow), "LoadGameFromSaveFileNow");
                var zoneConstructor = AccessTools.Constructor(
                    typeof(Zone_Fishing),
                    new[] { typeof(ZoneManager) });

                if (setFishTypes == null || loadGame == null || zoneConstructor == null)
                {
                    throw new MissingMethodException(
                        "A required RimWorld water-body or fishing method was not found.");
                }

                instance.Patch(
                    setFishTypes,
                    prefix: new HarmonyMethod(
                        typeof(WaterBodyFishPatches),
                        nameof(PreSetFishTypes)));
                instance.Patch(
                    loadGame,
                    postfix: new HarmonyMethod(
                        typeof(WaterBodyFishPatches),
                        nameof(ApplyToAllMaps)));
                instance.Patch(
                    zoneConstructor,
                    postfix: new HarmonyMethod(
                        typeof(WaterBodyFishPatches),
                        nameof(AfterFishingZoneConstructed)));
                harmony = instance;
            }
            catch (Exception exception)
            {
                instance.UnpatchAll(HarmonyId);
                Log.Error($"[WaterBodyFishMod] Failed to install Harmony patches: {exception}");
            }
        }

        internal static bool PreSetFishTypes(WaterBody __instance)
        {
            if (__instance?.map == null)
            {
                return true;
            }

            if (__instance.map.Biome?.fishTypes == null)
            {
                return false;
            }

            var type = __instance.GetType();
            var commonFishField = AccessTools.Field(type, "commonFish");
            var uncommonFishField = AccessTools.Field(type, "uncommonFish");
            var shouldHaveFishField = AccessTools.Field(type, "shouldHaveFish");
            if (commonFishField == null || uncommonFishField == null || shouldHaveFishField == null)
            {
                Log.Error("[WaterBodyFishMod] WaterBody fish fields were not found; using the vanilla fish setup.");
                return true;
            }

            var commonFish = commonFishField.GetValue(__instance) as List<ThingDef>;
            var uncommonFish = uncommonFishField.GetValue(__instance) as List<ThingDef>;
            if (commonFish == null || uncommonFish == null || ThingCategoryDefOf.Fish?.childThingDefs == null)
            {
                Log.Error("[WaterBodyFishMod] WaterBody fish lists were unavailable; using the vanilla fish setup.");
                return true;
            }

            commonFish.Clear();
            uncommonFish.Clear();
            shouldHaveFishField.SetValue(__instance, true);

            var fishDefs = ThingCategoryDefOf.Fish.childThingDefs;
            commonFish.AddRange(fishDefs);
            uncommonFish.AddRange(fishDefs);
            ApplyMaxPopulation(__instance);
            return false;
        }

        internal static void ApplyMaxPopulation(WaterBody waterBody)
        {
            if (waterBody?.map == null)
            {
                return;
            }

            waterBody.map.TileInfo.PrimaryBiome.maxFishPopulation = WaterBodyFishMod.MaxFishPopulation;
            waterBody.Population = waterBody.MaxPopulation;
        }

        internal static void ApplyToAllMaps()
        {
            if (Find.Maps == null)
            {
                return;
            }

            foreach (var map in Find.Maps)
            {
                if (map?.waterBodyTracker?.Bodies == null)
                {
                    continue;
                }

                foreach (var waterBody in map.waterBodyTracker.Bodies)
                {
                    waterBody.SetFishTypes();
                }
            }
        }

        internal static void LogAllFishDefs()
        {
            var fishDefs = ThingCategoryDefOf.Fish?.childThingDefs;
            if (fishDefs == null)
            {
                Log.Warning("[WaterBodyFishMod] Fish definitions are unavailable.");
                return;
            }

            Log.Message($"[WaterBodyFishMod] Fish definitions: {fishDefs.Count}");
            foreach (var fishDef in fishDefs)
            {
                Log.Message($"[WaterBodyFishMod] {fishDef.description}");
            }
        }

        internal static void AfterFishingZoneConstructed(Zone_Fishing __instance)
        {
            __instance.repeatMode = FishRepeatMode.DoForever;
        }
    }
}
