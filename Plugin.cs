using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace AnyFactionLiveries
{
    /// <summary>
    /// Client-side Nuclear Option mod: lets you pick liveries that are restricted to another faction.
    ///
    /// How the game restricts them (Assembly-CSharp, LoadoutSelector.GetLiveryOptions):
    ///   a livery is offered only if its faction is empty/None/Neutral, OR
    ///   (allowFactionLivery is true AND its faction equals the aircraft's faction).
    /// Passing aircraftFaction = "" with allowFactionLivery = true makes that test succeed for every livery.
    /// </summary>
    [BepInPlugin(Guid, Name, Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "local.nuclearoption.anyfactionliveries";
        public const string Name = "Any Faction Liveries";
        public const string Version = "1.0.0";

        internal static ManualLogSource Log;
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> TagFaction;
        internal static ConfigEntry<bool> OwnFactionFirst;

        private void Awake()
        {
            Log = Logger;

            Enabled = Config.Bind("General", "Enabled", true,
                "Show liveries from every faction in the loadout livery dropdown.");
            TagFaction = Config.Bind("General", "TagFactionInName", true,
                "Append [FactionName] to built-in livery names that belong to a faction.");
            OwnFactionFirst = Config.Bind("General", "OwnFactionFirst", true,
                "Keep the liveries you could already pick at the top of the list, so the default selection is unchanged.");

            var harmony = new Harmony(Guid);
            harmony.PatchAll(typeof(Plugin).Assembly);
            Log.LogInfo(Name + " " + Version + " loaded.");
        }
    }

    [HarmonyPatch(typeof(LoadoutSelector), "GetLiveryOptions")]
    internal static class GetLiveryOptionsPatch
    {
        // The prefix calls the original once with the unmodified arguments to learn which
        // liveries were already allowed. This flag stops that inner call from recursing.
        private static bool _inner;

        private static bool IsNeutral(string f)
        {
            return string.IsNullOrEmpty(f) || f == "None" || f == "Neutral";
        }

        [HarmonyPrefix]
        private static void Prefix(
            AircraftDefinition aircraft,
            ref string aircraftFaction,
            ref bool allowFactionLivery,
            out List<ValueTuple<LiveryKey, string>> __state)
        {
            __state = null;
            if (_inner || !Plugin.Enabled.Value) return;

            if (Plugin.OwnFactionFirst.Value)
            {
                _inner = true;
                try
                {
                    var native = new List<ValueTuple<LiveryKey, string>>();
                    LoadoutSelector.GetLiveryOptions(native, aircraft, aircraftFaction, allowFactionLivery);
                    __state = native;
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning("Could not compute native livery list: " + e.Message);
                    __state = new List<ValueTuple<LiveryKey, string>>();
                }
                finally { _inner = false; }
            }
            else
            {
                __state = new List<ValueTuple<LiveryKey, string>>();
            }

            // Make the game's faction check pass for everything.
            aircraftFaction = "";
            allowFactionLivery = true;
        }

        [HarmonyPostfix]
        private static void Postfix(
            List<ValueTuple<LiveryKey, string>> resultsList,
            AircraftDefinition aircraft,
            List<ValueTuple<LiveryKey, string>> __state)
        {
            if (__state == null || resultsList == null) return; // disabled, or the inner call

            try
            {
                // Tag built-in liveries with the faction they belong to.
                if (Plugin.TagFaction.Value && aircraft != null && aircraft.aircraftParameters != null)
                {
                    var liveries = aircraft.aircraftParameters.liveries;
                    for (int i = 0; i < resultsList.Count; i++)
                    {
                        var entry = resultsList[i];
                        if (entry.Item1.Type != LiveryKey.KeyType.Builtin) continue;
                        int idx = entry.Item1.Index;
                        if (liveries == null || idx < 0 || idx >= liveries.Count) continue;
                        var fac = liveries[idx].faction;
                        if (fac == null || IsNeutral(fac.factionName)) continue;
                        resultsList[i] = new ValueTuple<LiveryKey, string>(
                            entry.Item1, entry.Item2 + " [" + fac.factionName + "]");
                    }
                }

                // Stable partition: previously-allowed entries first, everything else after.
                if (Plugin.OwnFactionFirst.Value && __state.Count > 0)
                {
                    var first = new List<ValueTuple<LiveryKey, string>>();
                    var rest = new List<ValueTuple<LiveryKey, string>>();
                    foreach (var entry in resultsList)
                    {
                        bool wasAllowed = false;
                        foreach (var n in __state)
                        {
                            if (n.Item1.Equals(entry.Item1)) { wasAllowed = true; break; }
                        }
                        (wasAllowed ? first : rest).Add(entry);
                    }
                    resultsList.Clear();
                    resultsList.AddRange(first);
                    resultsList.AddRange(rest);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Livery list post-processing failed: " + e);
            }
        }
    }
}
