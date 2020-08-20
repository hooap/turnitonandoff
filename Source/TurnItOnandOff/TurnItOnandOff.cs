using System;
using RimWorld;
using Verse;
using Verse.AI;
using System.Collections.Generic;
using UnityEngine;
using HugsLib;
using HarmonyLib;
using System.Linq;
using System.Text;

using System.Reflection;


namespace TurnItOnandOff
{
    // Track the power users
    [HarmonyPatch(typeof(Building_WorkTable), "UsedThisTick", new Type[] { })]
    public static class Building_WorkTable_UsedThisTick_Patch
    {
        [HarmonyPrefix]
        public static void UsedThisTick(Building_WorkTable __instance)
        {
            // The Hook for tracking things used:            
            TurnItOnandOff.AddBuildingUsed(__instance);
        }
    }

    [HarmonyPatch(typeof(JobDriver_WatchBuilding), "WatchTickAction", new Type[] { })]
    public static class JobDriver_WatchBuilding_WatchTickAction_Patch
    {
        [HarmonyPrefix]
        public static void WatchTickAction(JobDriver_WatchBuilding __instance)
        {
            // The Hook for tracking things used:
            TurnItOnandOff.AddBuildingUsed(__instance.job.targetA.Thing as Building);
        }
    }

    public class TurnItOnandOff : ModBase
    {
        public override string ModIdentifier
        {
            get
            {
                return "TurnItOnandOff";
            }
        }

        // Track the number of buildings on the map
        // When this changes, rescan now instead of delayed
        // (This seems to be the best way of figuring out when a new building is placed)
        // For simplicity, cheese it and only care about the visible map
        int lastVisibleBuildings = 0;

        int ticksToRescan = 0; // Tick tracker for rescanning
        public override void Tick(int currentTick)
        {
            if (inUseTick != currentTick)
            {
                inUseTick = currentTick;

                buildingsThatWereUsedLastTick.Clear();
                buildingsThatWereUsedLastTick.UnionWith(buildingsInUseThisTick);
                buildingsInUseThisTick.Clear();
            }

            EvalBeds();
            EvalResearchTables();
            EvalAutodoors();
            EvalDeepDrills();

            foreach (Thing thing in buildingsToModifyPowerOn)
            {
                if (thing == null)
                {
                    Logger.Message("Tried to modify power level for thing which no longer exists");
                    continue;
                }

                var powerComp = thing.TryGetComp<CompPowerTrader>();
                if (powerComp != null)
                {
                    // Set the power requirement 
                    powerComp.PowerOutput = powerLevels[thing.def.defName][0];
                }
            }

            var visibleBuildings = Find.AnyPlayerHomeMap.listerBuildings.allBuildingsColonist.Count;
            if (visibleBuildings != lastVisibleBuildings)
            {
                lastVisibleBuildings = visibleBuildings;
                ticksToRescan = 0; // Rescan now
            }

            --ticksToRescan;
            if (ticksToRescan < 0)
            {
                ticksToRescan = 2000;
                // Destructively modifies the things to modify power on, do the state resetting first
                ScanForThings();
            }

            foreach (Building building in buildingsThatWereUsedLastTick)
            {
                // Skip modifying power on things we're not supposed to modify power on
                if (!buildingsToModifyPowerOn.Contains(building)) continue;

                var powerComp = building.TryGetComp<CompPowerTrader>();
                if (powerComp != null)
                {
                    // Set the power requirement to high if the building is in use
                    powerComp.PowerOutput = powerLevels[building.def.defName][1];
                }
            }
        }

        public static TurnItOnandOff instance;
        public static void Log(string log)
        {
            if (instance == null) return;
            instance.Logger.Message(log);
        }

        public override void DefsLoaded()
        {
            var defs = DefDatabase<TurnItOnandOffDef>.AllDefs;
            int num = 0, loaded = 0;
            foreach (var def in defs)
            {
                ++num;
                var target = def.targetDef;
                var namedDef = DefDatabase<ThingDef>.GetNamedSilentFail(target);
                if (namedDef == null)
                {
                    Logger.Message(string.Format("No def named {0} to load, skipping.", target));
                    continue;
                }
                else
                {
                    ++loaded;
                    Logger.Message(string.Format("Registering def named {0}", target));
                }

                if (def.poweredWorkbench)
                    RegisterWorkTable(namedDef.defName, def.lowPower, def.highPower);

                if (def.poweredReservable)
                    RegisterExternalReservable(namedDef.defName, def.lowPower, def.highPower);

            }
            Logger.Message(string.Format("Loaded {1} of {0} mod support defs.", num, loaded));

            medicalBedDef = ThingDef.Named("HospitalBed");
            HiTechResearchBenchDef = ThingDef.Named("HiTechResearchBench");
            AutodoorDef = ThingDef.Named("Autodoor");
            DeepDrillDef = ThingDef.Named("DeepDrill");
        }

        public override void Initialize()
        {
            // *** Start of Fluffy's code
            // register all other Building_Worktable that use power
            // lowPowerMultiplier is not being used anymore, just using a fixed value of 1 watt
            // var lowPowerMultiplier = 0f;
            var highPowerMultiplier = 1f;

            foreach (var def in DefDatabase<ThingDef>.AllDefsListForReading)
            {
                // check if this is a worktable (has class Building_Worktable), or inherits from Building_Worktable.
                if (typeof(Building_WorkTable).IsAssignableFrom(def.thingClass))
                {
                    // check if this uses power.
                    // first, get the power properties.
                    var powerProps = def.GetCompProperties<CompProperties_Power>();
                    if (powerProps != null && typeof(CompPowerTrader).IsAssignableFrom(powerProps.compClass))
                    {
                        // bingo!
                        // check if we don't already know about this worktable
                        if (!powerLevels.ContainsKey(def.defName))
                        {
                            // register the worktable. Use mutlipliers to change low/high power draw, * -1 because the def contains draw as a positive number.

                            // *** Fluffy's old code
                            // RegisterWorkTable(def.defName,
                            //    powerProps.basePowerConsumption * lowPowerMultiplier * -1,
                            //    powerProps.basePowerConsumption * highPowerMultiplier * -1);
                            // *** end of Fluffy's old code
// However, we're not using lowPowerMultiplier, we're just using a fixed value of -1 (i.e. 1 watt)
// The reason we don't use zero as the minimum power usage for when a machine is not being used is because then, when a generator runs out
// of fuel, there is zero power available to machines. So, if we have set zero watts as the minimum requirement for a machine to run, then
// pawns will keep trying to use the machine again and again the entire time the generator is empty providing zero power. So, we set the
// minimum watts to use the machines at 1 watt. That way, when there is no fuel in a generator, pawns will not try to use the machines. 
                            RegisterWorkTable(def.defName,                                 
                                -1,
                                powerProps.basePowerConsumption * highPowerMultiplier * -1);
                        }
                    }
                }
            }
            // *** end of Fluffy's code

            //RegisterWorkTable("ElectricTailoringBench", -1, -120); // 1W Idle, 120W active
            //RegisterWorkTable("ElectricSmithy", -1, -210); // 1W Idle, 210W Active
            //RegisterWorkTable("TableMachining", -1, -350); // 1W Idle, 350W Active
            //RegisterWorkTable("ElectricStove", -1, -350); // 1W Idle, 350W Active
            //RegisterWorkTable("ElectricSmelter", -1, -700); // 1W Idle, 700W Active
            //RegisterWorkTable("BiofuelRefinery", -1, -170); // 1W Idle, 170W Active
            //RegisterWorkTable("FabricationBench", -1, -250); // 1W Idle, 250W Active
            //RegisterWorkTable("ElectricCrematorium", -1, -250); // 1W Idle, 250W Active

            RegisterSpecialPowerTrader("MultiAnalyzer", -1, -200); // 1W Idle, 200W Active
            RegisterSpecialPowerTrader("VitalsMonitor", -1, -80); // 1W Idle, 80W Active
            RegisterSpecialPowerTrader("HiTechResearchBench", -1, -250); // 1W Idle, 250W Active
            //RegisterSpecialPowerTrader("Autodoor", -1, -100); // 1W Idle, 100W Active

            // Televisions!
            RegisterSpecialPowerTrader("TubeTelevision", -1, -200); // 1W Idle, 200W Active
            RegisterSpecialPowerTrader("FlatscreenTelevision", -1, -330); // 1W Idle, 330W Active
            RegisterSpecialPowerTrader("MegascreenTelevision", -1, -400); // 1W Idle, 400W Active

            // Drill
            RegisterSpecialPowerTrader("DeepDrill", -1, -300); // 1W Idle, 300W Active

            Logger.Message("Initialized Components");

            instance = this;

            Logger.Message("Registered instance");
        }

        // Power levels pairs as Vector2's, X = Idling, Y = In Use
        static Dictionary<string, Vector2> powerLevels = new Dictionary<string, Vector2>();
        //static HashSet<ThingDef> workTablesRegistered = new HashSet<ThingDef> ();
        static void RegisterWorkTable(string defName, float idlePower, float activePower)
        {
            powerLevels.Add(defName, new Vector2(idlePower, activePower));
            //workTablesRegistered.Add (ThingDef.Named (defName));
        }

        static void RegisterSpecialPowerTrader(string defName, float idlePower, float activePower)
        {
            if (!powerLevels.ContainsKey(defName))
            {
                powerLevels.Add(defName, new Vector2(idlePower, activePower));
            }            
        }

        static public float PowerFactor(CompPowerTrader trader, Building building)
        {
            var defName = building.def.defName;

            //instance.Logger.Message (defName + " checked for power factor");

            if (powerLevels.ContainsKey(defName))
            {
                bool inUse = buildingsThatWereUsedLastTick.Contains(building);

                instance.Logger.Message(string.Format("{0} ({1}) power adjusted", building.ThingID, defName));

                // Return the idle power if not in use, otherwise, return the active power
                return powerLevels[defName][inUse ? 1 : 0];
            }

            return 1;
        }

        #region tracking
        public static int inUseTick = 0;
        public static HashSet<Building> buildingsThatWereUsedLastTick = new HashSet<Building>();
        public static HashSet<Building> buildingsInUseThisTick = new HashSet<Building>();
        public static HashSet<Building> buildingsToModifyPowerOn = new HashSet<Building>();

        public static HashSet<ThingDef> buildingDefsReservable = new HashSet<ThingDef>();
        public static HashSet<Building> reservableBuildings = new HashSet<Building>();

        public static HashSet<Building_Bed> MedicalBeds = new HashSet<Building_Bed>();
        public static HashSet<Building> HiTechResearchBenches = new HashSet<Building>();

        public static HashSet<Building_Door> Autodoors = new HashSet<Building_Door>();
        public static HashSet<Building> DeepDrills = new HashSet<Building>();

        private static ThingDef medicalBedDef;
        private static ThingDef HiTechResearchBenchDef;
        private static ThingDef AutodoorDef;
        private static ThingDef DeepDrillDef;

        public static void AddBuildingUsed(Building building)
        {
            buildingsInUseThisTick.Add(building);
        }

        public static void RegisterExternalReservable(string defName, int lowPower, int highPower)
        {
            try
            {
                var def = DefDatabase<ThingDef>.GetNamedSilentFail(defName);

                if (defName == null)
                {
                    instance.Logger.Message(string.Format("Def Named {0} could not be found, it's respective mod probably isn't loaded", defName));
                    return;
                }
                else
                {
                    instance.Logger.Message(string.Format("Attempting to register def named {0}", defName));
                }

                RegisterWorkTable(defName, lowPower, highPower);
                buildingDefsReservable.Add(def);
            }
            catch (System.Exception e)
            {
                instance.Logger.Message(e.Message);
            }
        }

        public static void ScanExternalReservable()
        {
            reservableBuildings.Clear();
            foreach (ThingDef def in buildingDefsReservable)
            {
                foreach (var map in Find.Maps)
                {
                    if (map == null) continue;
                    var buildings = map.listerBuildings.AllBuildingsColonistOfDef(def);
                    foreach (var building in buildings)
                    {
                        if (building == null) continue;
                        reservableBuildings.Add(building);
                    }
                }
            }
        }

        public static void EvalExternalReservable()
        {
            foreach (var building in reservableBuildings)
            {
                // Cache misses
                if (building?.Map == null) continue;

                if (building.Map.reservationManager.IsReservedByAnyoneOf(building, building.Faction))
                {
                    buildingsInUseThisTick.Add(building);
                }
            }
        }

        // Evaluate medical beds for medical beds in use, to register that the vitals monitors should be in high power mode
        public static void EvalBeds()
        {
            foreach (var mediBed in MedicalBeds)
            {
                if (mediBed?.Map == null) continue;

                bool occupied = false;
                foreach (var occupant in mediBed.CurOccupants)
                {
                    occupied = true;
                }

                if (!occupied) continue;
                var facilityAffector = mediBed.GetComp<CompAffectedByFacilities>();
                foreach (var facility in facilityAffector.LinkedFacilitiesListForReading)
                {
                    buildingsInUseThisTick.Add(facility as Building);
                }
            }
        }

        public static void EvalDeepDrills()
        {
            foreach (var deepDrill in DeepDrills)
            {
                if (deepDrill?.Map == null) continue;

                var inUse = deepDrill.Map.reservationManager.IsReservedByAnyoneOf(deepDrill, deepDrill.Faction);

                if (!inUse) continue;

                buildingsInUseThisTick.Add(deepDrill);
            }
        }

        // How to tell if a research table is in use?
        // I can't figure it out. Instead let's base it on being reserved for use
        public static void EvalResearchTables()
        {
            foreach (var researchTable in HiTechResearchBenches)
            {
                if (researchTable?.Map == null) continue;

                // Determine if we are reserved:
                var inUse = researchTable.Map.reservationManager.IsReservedByAnyoneOf(researchTable, researchTable.Faction);

                if (!inUse) continue;

                buildingsInUseThisTick.Add(researchTable);
                var facilityAffector = researchTable.GetComp<CompAffectedByFacilities>();
                foreach (var facility in facilityAffector.LinkedFacilitiesListForReading)
                {
                    buildingsInUseThisTick.Add(facility as Building);
                }
            }
        }

        public static void EvalAutodoors()
        {
            foreach (var autodoor in Autodoors)
            {
                if (autodoor == null) continue;
                if (autodoor.Map == null) continue;

                // If the door allows passage and isn't blocked by an object
                var inUse = autodoor.Open && (!autodoor.BlockedOpenMomentary);
                if (inUse) buildingsInUseThisTick.Add(autodoor);
            }
        }

        public static HashSet<ThingDef> thingDefsToLookFor;
        public static void ScanForThings()
        {
            // Build the set of def names to look for if we don't have it
            if (thingDefsToLookFor == null)
            {
                thingDefsToLookFor = new HashSet<ThingDef>();
                var defNames = powerLevels.Keys;
                foreach (var defName in defNames)
                {
                    thingDefsToLookFor.Add(ThingDef.Named(defName));
                }
            }

            ScanExternalReservable(); // Handle the scanning of external reservable objects

            buildingsToModifyPowerOn.Clear();
            MedicalBeds.Clear();
            HiTechResearchBenches.Clear();
            Autodoors.Clear();
            DeepDrills.Clear();

            var maps = Find.Maps;
            foreach (Map map in maps)
            {
                foreach (ThingDef def in thingDefsToLookFor)
                {
                    var matchingThings = map.listerBuildings.AllBuildingsColonistOfDef(def);
                    // Merge in all matching things
                    buildingsToModifyPowerOn.UnionWith(matchingThings);
                }

                // Register the medical beds in the watch list
                var mediBeds = map.listerBuildings.AllBuildingsColonistOfDef(medicalBedDef);
                foreach (var mediBed in mediBeds)
                {
                    var medicalBed = mediBed as Building_Bed;
                    MedicalBeds.Add(medicalBed);
                }

                // Register Hightech research tables too
                var researchTables = map.listerBuildings.AllBuildingsColonistOfDef(HiTechResearchBenchDef);
                HiTechResearchBenches.UnionWith(researchTables);

                var doors = map.listerBuildings.AllBuildingsColonistOfDef(AutodoorDef);
                foreach (var door in doors)
                {
                    var autodoor = door as Building_Door;
                    Autodoors.Add(autodoor);
                }

                var deepDrills = map.listerBuildings.AllBuildingsColonistOfDef(DeepDrillDef);
                DeepDrills.UnionWith(deepDrills);
            }
        }
        #endregion
    }
}