using System.Collections.Generic;
using RimMind.Advisor.Perception;
using RimMind.Presentation.Api;
using Verse;

namespace RimMind.Advisor.Overseer
{
    /// <summary>
    /// Colony overseer: the one colony-level decision loop (colonist agents handle their own needs). Per player-home
    /// map, evaluates <see cref="ColonyMacroPerceiver"/> every 250 ticks and starts a decision cycle on schedule or when
    /// a new bottleneck appears (<see cref="ColonyOverseerPolicy.ShouldRun"/>). Gated by the Advisor setting
    /// <c>enableColonyOverseer</c> (default off). Plan memory is saved with the game.
    /// </summary>
    public sealed class ColonyOverseerGameComponent : GameComponent
    {
        private const int EvaluateEveryTicks = 250;

        private Dictionary<int, ColonyOverseerMapState> _maps = new Dictionary<int, ColonyOverseerMapState>();
        private readonly Dictionary<int, ColonyOverseerDriver> _drivers = new Dictionary<int, ColonyOverseerDriver>();
        private List<int>? _mapKeys;
        private List<ColonyOverseerMapState>? _mapValues;

        public ColonyOverseerGameComponent(Game game) { }

        public override void GameComponentTick()
        {
            int now = Find.TickManager.TicksGame;
            if (now % EvaluateEveryTicks != 0) return;

            var settings = RimMindAdvisorMod.Settings;
            if (settings == null || !settings.enableColonyOverseer || !RimMindAPI.Settings.IsConfigured()) return;

            foreach (var map in Find.Maps)
            {
                if (map == null || !map.IsPlayerHome) continue;

                if (!_drivers.TryGetValue(map.uniqueID, out var driver))
                    _drivers[map.uniqueID] = driver = new ColonyOverseerDriver(map.uniqueID);
                if (driver.IsBusy(now)) continue;

                if (!_maps.TryGetValue(map.uniqueID, out var state))
                    _maps[map.uniqueID] = state = new ColonyOverseerMapState();

                var macro = ColonyMacroPerceiver.Evaluate(ColonyMacroPerceiver.CaptureSnapshot(map));
                var keys = ColonyOverseerPolicy.BottleneckKeys(macro.CriticalBottlenecks);
                if (!ColonyOverseerPolicy.ShouldRun(now, state.LastRunTick, keys, state.LastBottlenecks,
                        settings.colonyOverseerIntervalTicks, out var reason))
                {
                    continue;
                }

                state.LastRunTick = now;
                state.LastBottlenecks = keys;
                driver.Run(map, macro, state, reason);
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref _maps, "colonyOverseerMaps", LookMode.Value, LookMode.Deep, ref _mapKeys, ref _mapValues);
            if (Scribe.mode == LoadSaveMode.PostLoadInit && _maps == null)
                _maps = new Dictionary<int, ColonyOverseerMapState>();
        }
    }

    /// <summary>Persisted per-map overseer state: schedule, last bottlenecks, recent actions.</summary>
    public sealed class ColonyOverseerMapState : IExposable
    {
        public int LastRunTick = -1;
        public List<string> LastBottlenecks = new List<string>();
        public List<ColonyPlanEntry> Memory = new List<ColonyPlanEntry>();

        public void ExposeData()
        {
            Scribe_Values.Look(ref LastRunTick, "lastRunTick", -1);
            Scribe_Collections.Look(ref LastBottlenecks, "lastBottlenecks", LookMode.Value);

            // ColonyPlanEntry is Verse-free (unit-tested); persist it as parallel value lists.
            List<int>? ticks = null;
            List<string>? tools = null, reasons = null, results = null;
            List<bool>? oks = null;
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                ticks = new List<int>(); tools = new List<string>(); reasons = new List<string>();
                results = new List<string>(); oks = new List<bool>();
                foreach (var e in Memory)
                {
                    ticks.Add(e.Tick); tools.Add(e.Tool); reasons.Add(e.Reason); results.Add(e.Result); oks.Add(e.Ok);
                }
            }
            Scribe_Collections.Look(ref ticks, "memoryTicks", LookMode.Value);
            Scribe_Collections.Look(ref tools, "memoryTools", LookMode.Value);
            Scribe_Collections.Look(ref reasons, "memoryReasons", LookMode.Value);
            Scribe_Collections.Look(ref results, "memoryResults", LookMode.Value);
            Scribe_Collections.Look(ref oks, "memoryOk", LookMode.Value);

            // Value lists are only populated during LoadingVars (each Scribe pass is a separate call).
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                Memory = new List<ColonyPlanEntry>();
                int n = ticks?.Count ?? 0;
                for (int i = 0; i < n; i++)
                {
                    Memory.Add(new ColonyPlanEntry
                    {
                        Tick = ticks![i],
                        Tool = tools != null && i < tools.Count ? tools[i] : string.Empty,
                        Reason = reasons != null && i < reasons.Count ? reasons[i] : string.Empty,
                        Result = results != null && i < results.Count ? results[i] : string.Empty,
                        Ok = oks != null && i < oks.Count && oks[i],
                    });
                }
            }

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
                LastBottlenecks ??= new List<string>();
        }
    }
}
