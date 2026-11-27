using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimMind.Advisor.Advisor;
using RimMind.Advisor.Perception;
using RimMind.Application.Common.Models.Tools;
using RimMind.Application.Features.Json;
using RimMind.Application.Features.Llm;
using RimMind.Application.Features.Telemetry;
using RimMind.Domain.Common;
using RimMind.Domain.Llm;
using RimMind.Domain.ValueObjects;
using RimMind.Presentation.Agent;
using Newtonsoft.Json.Linq;
using RimMind.Presentation.Api;
using RimWorld;
using Verse;

namespace RimMind.Advisor.Overseer
{
    /// <summary>
    /// One colony-level decision cycle for one map: builds the colony prompt, sends it as <c>colony-&lt;mapId&gt;</c>
    /// (own OpenCode session) in the <see cref="ScenarioId"/> scenario, executes the returned colony tool calls through
    /// the Advisor executor, and feeds results back for up to <see cref="MaxFeedbackRounds"/> more turns.
    /// </summary>
    internal sealed class ColonyOverseerDriver
    {
        public const string ScenarioId = "colony_decision";
        public const string RequestModId = "RimMind.ColonyOverseer";
        public const int MaxCallsPerTurn = 3;
        private const int MaxFeedbackRounds = 2;
        private const int StaleRequestTicks = 60000;
        private const float StaleRequestRealSeconds = 300f;

        private readonly int _mapId;
        private readonly string _npcId;
        private readonly AdvisorFeedbackSession _session = new AdvisorFeedbackSession();
        private readonly AdvisorRecommendationParser _parser =
            new AdvisorRecommendationParser(id => RimMindAPI.ToolSet.Registry.FindById(id) != null);
        private readonly IAdvisorToolCallExecutor _executor = new AdvisorToolCallExecutor();
        private ColonyOverseerMapState? _state;
        private int _startedTick = -1;
        private float _startedRealtime;
        private bool _freeQueryRoundUsed;

        public ColonyOverseerDriver(int mapId)
        {
            _mapId = mapId;
            _npcId = $"colony-{mapId}";
        }

        /// <summary>A cycle is running. A request that never completes is released after one in-game day or five real
        /// minutes (popups pause the game, so ticks alone could keep it busy forever).</summary>
        public bool IsBusy(int nowTick)
        {
            if (_startedTick >= 0 && (nowTick - _startedTick > StaleRequestTicks
                    || UnityEngine.Time.realtimeSinceStartup - _startedRealtime > StaleRequestRealSeconds))
            {
                Log.Warning($"[RimMind-Overseer] map {_mapId}: request never completed, releasing.");
                Finish();
            }
            return _startedTick >= 0;
        }

        public void Run(Map map, ColonyMacroState macro, ColonyOverseerMapState state, string reason)
        {
            var tools = BuildTools();
            if (tools == null)
            {
                Log.Warning($"[RimMind-Overseer] map {_mapId}: no colony tools registered; skipping.");
                return;
            }

            _state = state;
            _startedTick = Find.TickManager.TicksGame;
            _startedRealtime = UnityEngine.Time.realtimeSinceStartup;
            _session.Begin(tools, null);
            Log.Message($"[RimMind-Overseer] map {_mapId}: deciding ({reason}), {tools.Count} tools.");
            LiveEvents.Emit("decision", new JObject
            {
                ["source"] = "overseer", ["npc"] = _npcId, ["pawn"] = null, ["action"] = "colony_decision", ["reason"] = reason,
            });
            Send(BuildMessages(map, macro, state));
        }

        /// <summary>Colony build/manage tools plus the popup tools (mechanism <c>world.dialog</c>, world-admin scope so
        /// colonist agents never get them; no other world-admin tool is given to the overseer).</summary>
        private static List<StructuredTool>? BuildTools()
        {
            var defs = RimMindAPI.ToolSet.GetDefinitionsForScope(
                ToolScope.ColonyBuild | ToolScope.ColonyManage,
                compactSchema: true)?.ToList();
            if (defs == null || defs.Count == 0) return null;
            defs.AddRange(RimMindAPI.ToolSet.GetDefinitionsForScope(ToolScope.WorldAdmin, compactSchema: true)
                .Where(d => IsDialogTool(d.Id) && defs.All(e => e.Id != d.Id)));
            return defs.Select(d => new StructuredTool
            {
                Name = d.Id,
                Description = d.Description,
                Parameters = d.ParametersSchema,
            }).ToList();
        }

        private static bool IsDialogTool(string? id) =>
            id != null && (id.StartsWith("world.dialog.", StringComparison.Ordinal)
                || id.StartsWith("world_dialog_", StringComparison.Ordinal));

        /// <summary>L0 rules (static) → L1 map topology (rarely changes) → L2 volatile state (moved to the final user
        /// turn by the wire codec) → decision prompt.</summary>
        private static List<ChatMessage> BuildMessages(Map map, ColonyMacroState macro, ColonyOverseerMapState state)
        {
            var mapContext = new MapContextBuilder();
            int now = Find.TickManager.TicksGame;

            var volatileState = new StringBuilder();
            volatileState.AppendLine("<layer_L2>");
            volatileState.AppendLine(ColonyMacroPerceiver.FormatPromptAugmentation(macro).TrimEnd());
            volatileState.AppendLine("[colony_resources] " + mapContext.BuildVolatileColonyResources(map).TrimEnd());
            volatileState.AppendLine("[colonists]");
            volatileState.AppendLine(BuildColonistRoster(map));
            volatileState.AppendLine("[recent_colony_actions]");
            volatileState.AppendLine(ColonyOverseerPolicy.FormatMemory(state.Memory, now));
            var pending = PendingChoices.Collect();
            if (pending.Count > 0)
            {
                volatileState.AppendLine("[pending_choices] answer these first (world_dialog_trigger action=choose)");
                foreach (var choice in pending)
                {
                    string text = choice.Text.Length > 700 ? choice.Text.Substring(0, 700) + "…" : choice.Text;
                    volatileState.Append("- choice_id ").Append(choice.Id);
                    if (choice.Title.Length > 0) volatileState.Append(" \"").Append(choice.Title).Append('"');
                    volatileState.Append(": ").AppendLine(text.Replace("\n", " "));
                    volatileState.Append("  options: ").AppendLine(string.Join(" | ", choice.Options.Select(o =>
                        $"{o.Index}) {o.Text}" + (o.Disabled ? $" [disabled: {o.DisabledReason}]" : string.Empty))));
                }
            }
            volatileState.Append("</layer_L2>");

            return new List<ChatMessage>
            {
                new ChatMessage { Role = "system", LayerTag = "L0", Content = ColonyOverseerPolicy.Instructions },
                new ChatMessage
                {
                    Role = "system",
                    LayerTag = "L1",
                    Content = "<layer_L1>\n[map_structure] " + mapContext.BuildStaticMapTopology(map).TrimEnd()
                        + "\n" + MapGeographyBuilder.Get(map).Text + "\n</layer_L1>",
                },
                new ChatMessage { Role = "system", LayerTag = "L2", Content = volatileState.ToString() },
                new ChatMessage { Role = "user", Content = ColonyOverseerPolicy.DecisionPrompt },
            };
        }

        /// <summary>Colonists with ids and their best skills, so work and construction go to the right people.</summary>
        private static string BuildColonistRoster(Map map)
        {
            var sb = new StringBuilder();
            foreach (var pawn in map.mapPawns.FreeColonistsSpawned)
            {
                var skills = pawn.skills?.skills?
                    .Where(s => !s.TotallyDisabled)
                    .OrderByDescending(s => s.Level)
                    .Take(3)
                    .Select(s => $"{s.def.defName} {s.Level}");
                sb.Append("- ").Append(pawn.Name?.ToStringShort ?? pawn.LabelShort)
                  .Append(" (pawn_id: ").Append(pawn.thingIDNumber).Append(')');
                if (pawn.Downed) sb.Append(" [downed]");
                if (skills != null) sb.Append(": ").Append(string.Join(", ", skills));
                sb.AppendLine();
            }
            return sb.ToString().TrimEnd();
        }

        private void Send(List<ChatMessage> messages)
        {
            var envelope = LlmRequestEnvelopeBuilder
                .ForScenario(ScenarioId)
                .WithModId(RequestModId)
                .WithNpcId(_npcId)
                .WithMessages(messages)
                .WithTools(_session.Tools)
                .WithToolDispatchMode(ToolCallDispatchMode.Manual)
                .WithMaxTokens(Math.Max(1024, RimMindAPI.Settings.MaxTokens))
                .WithTemperature(0.4f)
                .WithExpireAtTicks(Find.TickManager.TicksGame + StaleRequestTicks)
                .Build();

            RimMindAPI.Request.Send(envelope, (result, context) =>
            {
                _session.CaptureMessages(
                    AdvisorRequestAugmentationFactory.CaptureFeedbackMessages(context, envelope.Messages));
                OnResponse(result);
            });
        }

        private void OnResponse(Result<LlmResponse, RimMindError> result)
        {
            if (_state == null || _startedTick < 0) return;
            if (result.IsErr)
            {
                Log.Warning($"[RimMind-Overseer] map {_mapId}: request failed: {result.Error}");
                Finish();
                return;
            }

            var response = result.Value;
            _session.SetReasoningContent(response.ReasoningContent);
            var calls = ExtractCalls(response);
            if (calls.Count == 0)
            {
                string text = response.Content ?? string.Empty;
                Log.Message($"[RimMind-Overseer] map {_mapId}: no action ({(text.Length > 160 ? text.Substring(0, 160) + "…" : text)})");
                Finish();
                return;
            }

            if (calls.Count > MaxCallsPerTurn) calls = calls.Take(MaxCallsPerTurn).ToList();

            // The enableColonyOverseer setting is the player's consent: calls run with player authority.
            var results = AdvisorToolCallBatchRunner.ExecuteOnMainThread(
                _executor, calls, _npcId, response.RequestId, playerApproved: true,
                message => Log.Warning($"[RimMind-Overseer] {message}"));

            int now = Find.TickManager.TicksGame;
            for (int i = 0; i < calls.Count; i++)
            {
                var call = calls[i];
                var res = i < results.Count ? results[i] : ToolResult.Fail("no result", call.Id, call.Name);
                string reason = ToolCallSummaryFormatter.ExtractToolCallReason(call) ?? string.Empty;
                Log.Message($"[RimMind-Overseer] map {_mapId}: {call.Name} → {(res.IsError ? "FAILED" : "ok")} {Trim(res.Content)} args={Trim(call.Arguments)}");
                LiveEvents.Emit("action", new JObject
                {
                    ["source"] = "overseer", ["npc"] = _npcId, ["pawn"] = null, ["tool"] = call.Name,
                    ["args"] = Trim(call.Arguments), ["ok"] = !res.IsError, ["result"] = Trim(res.Content), ["reason"] = reason,
                });
                ColonyOverseerPolicy.Remember(_state.Memory, new ColonyPlanEntry
                {
                    Tick = now,
                    Tool = call.Name,
                    Reason = reason,
                    Result = res.Content ?? string.Empty,
                    Ok = !res.IsError,
                });
            }

            // A turn that only looked (query tools) gets one extra round, so looking before acting does not use up
            // the turns meant for actions (observed: two query-only turns, no action).
            if (calls.All(c => ColonyOverseerPolicy.IsQueryTool(c.Name)))
                _freeQueryRoundUsed = true;
            int budget = MaxFeedbackRounds + (_freeQueryRoundUsed ? 1 : 0);
            if (_session.CanRequestFeedback(budget))
            {
                RequestFeedback(calls, results);
                return;
            }
            Finish();
        }

        private List<StructuredToolCall> ExtractCalls(LlmResponse response)
        {
            if (!string.IsNullOrEmpty(response.ToolCallsJson)
                && _parser.TryParseNative(response.ToolCallsJson, out var native)
                && native.Count > 0)
            {
                return native;
            }

            // Tool calls written as text (models that drop out of native tool calling).
            return LlmPayloadParser.ParsePseudoFunctionCalls(response.Content)
                .Where(p => RimMindAPI.ToolSet.Registry.FindById(p.Name) != null)
                .Select(p => new StructuredToolCall
                {
                    Id = string.IsNullOrEmpty(p.Id) ? "call_" + Guid.NewGuid().ToString("N").Substring(0, 12) : p.Id!,
                    Name = p.Name,
                    Arguments = p.ArgumentsJson,
                })
                .ToList();
        }

        private void RequestFeedback(List<StructuredToolCall> calls, IReadOnlyList<ToolResult> results)
        {
            var messages = new List<ChatMessage>(_session.Messages ?? new List<ChatMessage>())
            {
                new ChatMessage
                {
                    Role = "assistant",
                    Content = string.Empty,
                    ReasoningContent = _session.ReasoningContent,
                    ToolCalls = calls.Select(c => new ChatToolCall { Id = c.Id, Name = c.Name, Arguments = c.Arguments }).ToList(),
                },
            };
            for (int i = 0; i < calls.Count; i++)
            {
                messages.Add(new ChatMessage
                {
                    Role = "tool",
                    Content = i < results.Count ? results[i].Content : "no result",
                    ToolCallId = calls[i].Id,
                });
            }

            _session.BeginFeedback(messages);
            Send(messages);
        }

        private void Finish()
        {
            _session.Clear();
            _startedTick = -1;
            _freeQueryRoundUsed = false;
            _state = null;
        }

        private static string Trim(string? s) =>
            string.IsNullOrEmpty(s) ? string.Empty : (s!.Length > 200 ? s.Substring(0, 200) + "…" : s);
    }
}
