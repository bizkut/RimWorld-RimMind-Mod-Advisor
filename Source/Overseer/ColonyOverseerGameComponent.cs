using System.Collections.Generic;
using RimMind.Advisor.Perception;
using RimMind.Presentation.Agent;
using RimMind.Presentation.Api;
using UnityEngine;
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
        private const int MaxChoiceAttempts = 2;

        private float _nextChoiceCheck;
        private readonly Dictionary<string, int> _choiceAttempts = new Dictionary<string, int>();
        private string _lastPendingKey = string.Empty;

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

                var driver = GetDriver(map);
                if (driver.IsBusy(now)) continue;

                var state = GetState(map);

                var macro = ColonyMacroPerceiver.Evaluate(ColonyMacroPerceiver.CaptureSnapshot(map));
                var keys = ColonyOverseerPolicy.BottleneckKeys(macro.CriticalBottlenecks);
                foreach (var alert in ColonySafetyNet.Alerts)
                    keys.Add($"[Alert:{alert.Code} {alert.Subject}]");
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

        /// <summary>
        /// Popups pause the game, so ticks stop: check every real second (also while paused) and run a decision as
        /// soon as a popup or decision letter waits. A popup still unanswered after <see cref="MaxChoiceAttempts"/>
        /// decisions is closed so the game never stays frozen (letters stay in the letter stack).
        /// </summary>
        public override void GameComponentUpdate()
        {
            var settings = RimMindAdvisorMod.Settings;
            bool active = settings != null && settings.enableColonyOverseer && RimMindAPI.Settings.IsConfigured();
            PendingChoices.AutoAnswerActive = active;
            ColonySafetyNet.Enabled = active;
            if (!active || Time.realtimeSinceStartup < _nextChoiceCheck) return;
            _nextChoiceCheck = Time.realtimeSinceStartup + 1f;

            var map = Find.AnyPlayerHomeMap;
            if (map == null) return;
            var pending = PendingChoices.Collect();
            string pendingKey = string.Join(",", pending.ConvertAll(c => c.Id));
            if (pendingKey != _lastPendingKey)
            {
                _lastPendingKey = pendingKey;
                Log.Message(pending.Count == 0
                    ? "[RimMind-Overseer] no popup waiting."
                    : "[RimMind-Overseer] popups waiting: " + string.Join("; ", pending.ConvertAll(c =>
                        $"{c.Id} \"{c.Title}\" \"{(c.Text.Length > 80 ? c.Text.Substring(0, 80) + "…" : c.Text)}\" ({c.Options.Count} options: "
                        + string.Join(" | ", c.Options.ConvertAll(o => o.Text)) + ")")));
            }
            if (pending.Count == 0)
            {
                _choiceAttempts.Clear();
                return;
            }

            var driver = GetDriver(map);
            if (driver.IsBusy(Find.TickManager.TicksGame)) return;

            foreach (var choice in pending)
            {
                _choiceAttempts.TryGetValue(choice.Id, out int attempts);
                if (attempts >= MaxChoiceAttempts)
                {
                    if (!choice.Id.StartsWith("letter:", System.StringComparison.Ordinal))
                    {
                        Log.Warning($"[RimMind-Overseer] popup {choice.Id} \"{choice.Title}\" unanswered after {attempts} decisions; closing it.");
                        PendingChoices.Dismiss(choice.Id);
                    }
                    continue;
                }

                _choiceAttempts[choice.Id] = attempts + 1;
                var macro = ColonyMacroPerceiver.Evaluate(ColonyMacroPerceiver.CaptureSnapshot(map));
                string label = choice.Title.Length > 0 ? choice.Title
                    : choice.Text.Length > 60 ? choice.Text.Substring(0, 60) + "…" : choice.Text;
                driver.Run(map, macro, GetState(map), $"popup waiting: {label}");
                return;
            }
        }

        private ColonyOverseerDriver GetDriver(Map map)
        {
            if (!_drivers.TryGetValue(map.uniqueID, out var driver))
                _drivers[map.uniqueID] = driver = new ColonyOverseerDriver(map.uniqueID);
            return driver;
        }

        private ColonyOverseerMapState GetState(Map map)
        {
            if (!_maps.TryGetValue(map.uniqueID, out var state))
                _maps[map.uniqueID] = state = new ColonyOverseerMapState();
            return state;
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
