using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace RimMind.Advisor.Overseer
{
    /// <summary>One executed overseer tool call, kept in the colony plan memory.</summary>
    public sealed class ColonyPlanEntry
    {
        public int Tick;
        public string Tool = string.Empty;
        public string Reason = string.Empty;
        public string Result = string.Empty;
        public bool Ok;
    }

    /// <summary>
    /// Pure scheduling and memory rules for the colony overseer (no Verse types, unit-testable).
    /// </summary>
    public static class ColonyOverseerPolicy
    {
        /// <summary>Scheduled cadence: 6 in-game hours.</summary>
        public const int DefaultIntervalTicks = 15000;

        /// <summary>A new bottleneck may trigger an early run at most once per in-game hour.</summary>
        public const int MinGapTicks = 2500;

        public const int MaxMemoryEntries = 20;

        private const int TicksPerHour = 2500;
        private static readonly Regex BottleneckTag = new Regex(@"^\s*\[([^\]]+)\]", RegexOptions.Compiled);

        /// <summary>
        /// Stable keys for ColonyMacroPerceiver bottlenecks: the leading "[Tag]" (the rest of the text carries
        /// changing numbers, which would make every evaluation look like a new bottleneck).
        /// </summary>
        public static List<string> BottleneckKeys(IEnumerable<string>? bottlenecks)
        {
            var keys = new List<string>();
            if (bottlenecks == null) return keys;
            foreach (var b in bottlenecks)
            {
                if (string.IsNullOrWhiteSpace(b)) continue;
                var m = BottleneckTag.Match(b);
                var key = m.Success ? m.Groups[1].Value.Trim() : b.Trim();
                if (!keys.Contains(key)) keys.Add(key);
            }
            return keys;
        }

        /// <summary>
        /// Whether the overseer should decide now: first evaluation, the scheduled interval elapsed, or a bottleneck
        /// appeared that was not present at the last run (rate-limited by <see cref="MinGapTicks"/>).
        /// </summary>
        public static bool ShouldRun(
            int nowTick,
            int lastRunTick,
            IReadOnlyCollection<string> bottleneckKeys,
            IReadOnlyCollection<string> lastBottleneckKeys,
            int intervalTicks,
            out string reason)
        {
            if (lastRunTick < 0)
            {
                reason = "first evaluation";
                return true;
            }

            int since = nowTick - lastRunTick;
            if (since >= Math.Max(MinGapTicks, intervalTicks))
            {
                reason = "scheduled";
                return true;
            }

            if (since >= MinGapTicks)
            {
                var added = bottleneckKeys.Where(k => !lastBottleneckKeys.Contains(k)).ToList();
                if (added.Count > 0)
                {
                    reason = "new bottleneck: " + string.Join(", ", added);
                    return true;
                }
            }

            reason = string.Empty;
            return false;
        }

        public static void Remember(List<ColonyPlanEntry> memory, ColonyPlanEntry entry)
        {
            memory.Add(entry);
            if (memory.Count > MaxMemoryEntries)
                memory.RemoveRange(0, memory.Count - MaxMemoryEntries);
        }

        /// <summary>Read-only tools (inspect, do not change the colony): ids containing "query" or "help".</summary>
        public static bool IsQueryTool(string? toolId) =>
            !string.IsNullOrEmpty(toolId)
            && (toolId!.IndexOf("query", StringComparison.OrdinalIgnoreCase) >= 0
                || toolId.IndexOf("help", StringComparison.OrdinalIgnoreCase) >= 0);

        /// <summary>Recent overseer actions for the prompt, newest last, with relative in-game time.</summary>
        public static string FormatMemory(IReadOnlyList<ColonyPlanEntry> memory, int nowTick)
        {
            if (memory == null || memory.Count == 0)
                return "No colony actions taken yet.";

            var sb = new StringBuilder();
            foreach (var e in memory)
            {
                int hours = Math.Max(0, (nowTick - e.Tick) / TicksPerHour);
                string when = hours == 0 ? "<1h ago" : $"{hours}h ago";
                string result = e.Result.Length > 160 ? e.Result.Substring(0, 160) + "…" : e.Result;
                sb.Append("- ").Append(when).Append(": ").Append(e.Tool)
                  .Append(e.Ok ? " → ok" : " → FAILED");
                if (!string.IsNullOrEmpty(e.Reason)) sb.Append(" (").Append(e.Reason).Append(')');
                if (!e.Ok && !string.IsNullOrEmpty(result)) sb.Append(": ").Append(result);
                sb.AppendLine();
            }
            return sb.ToString().TrimEnd();
        }

        /// <summary>Static overseer instructions (L0: byte-stable, cacheable).</summary>
        public const string Instructions =
            "<layer_L0>\n" +
            "[colony_overseer] You run a RimWorld colony as its player. Individual colonists already handle their own " +
            "needs (eating, sleeping, recreation); you make the colony-level decisions nobody else makes: shelter, " +
            "beds, food production, storage, production bills, resource gathering, work priorities, power and defense.\n" +
            "Priorities, in order: 1) survive the current threat or crisis; 2) food — a growing zone of fertile soil " +
            "(>= 15 cells per colonist), harvest/hunt designations, a cooking bill; 3) shelter — an enclosed room with a " +
            "bed per colonist; 4) storage — a big roofed storeroom: actions_plan_room_project room_type \"Storage\" " +
            "(size 11, up to 13; its interior becomes the stockpile, so food and goods stop rotting outdoors); an outdoor " +
            "stockpile zone is only a stopgap until it is built; 5) production and comfort; 6) power, defense, research.\n" +
            "Rules: act through the tools only. Make at most 3 tool calls per turn. Check what is buildable before " +
            "placing structures. Do not repeat an action from the recent-actions list unless it failed or its effect is " +
            "gone. If something failed, fix the cause or choose another action. If everything urgent is handled, advance " +
            "the next priority.\n" +
            "Popups: when [pending_choices] lists a popup or letter waiting for the player, answer it first with " +
            "world_dialog_trigger {\"action\":\"choose\",\"choice_id\":<id>,\"option\":<number>}. Decide as a careful " +
            "player: accept people who join when the colony is short-handed unless they bring danger the colony cannot " +
            "handle; refuse deals that cost colonists or more than the colony can afford; \"Jump to location\" and " +
            "\"Postpone\" decide nothing.\n" +
            "</layer_L0>";

        public const string DecisionPrompt =
            "Review the colony state above and take the most valuable colony-level actions now.";
    }
}
