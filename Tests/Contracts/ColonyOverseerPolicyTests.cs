using System.Collections.Generic;
using RimMind.Advisor.Overseer;
using Xunit;

namespace RimMind.Advisor.Tests.Contracts
{
    public sealed class ColonyOverseerPolicyTests
    {
        private static readonly string[] None = new string[0];

        [Fact]
        public void Bottleneck_keys_ignore_changing_numbers()
        {
            var a = ColonyOverseerPolicy.BottleneckKeys(new[] { "[Food Crisis] Colony only has 2.9 days of food remaining!" });
            var b = ColonyOverseerPolicy.BottleneckKeys(new[] { "[Food Crisis] Colony only has 2.1 days of food remaining!" });
            Assert.Equal(new[] { "Food Crisis" }, a);
            Assert.Equal(a, b);
        }

        [Fact]
        public void Runs_first_time_then_on_schedule()
        {
            Assert.True(ColonyOverseerPolicy.ShouldRun(100, -1, None, None, 15000, out var first));
            Assert.Equal("first evaluation", first);
            Assert.False(ColonyOverseerPolicy.ShouldRun(14000, 100, None, None, 15000, out _));
            Assert.True(ColonyOverseerPolicy.ShouldRun(15100, 100, None, None, 15000, out var scheduled));
            Assert.Equal("scheduled", scheduled);
        }

        [Fact]
        public void New_bottleneck_triggers_early_but_not_within_the_minimum_gap()
        {
            var now = new[] { "Food Crisis", "Shelter Shortage" };
            var last = new[] { "Shelter Shortage" };
            Assert.False(ColonyOverseerPolicy.ShouldRun(1000 + ColonyOverseerPolicy.MinGapTicks - 1, 1000, now, last, 15000, out _));
            Assert.True(ColonyOverseerPolicy.ShouldRun(1000 + ColonyOverseerPolicy.MinGapTicks, 1000, now, last, 15000, out var reason));
            Assert.Equal("new bottleneck: Food Crisis", reason);
            // A bottleneck that was already present at the last run does not re-trigger.
            Assert.False(ColonyOverseerPolicy.ShouldRun(1000 + ColonyOverseerPolicy.MinGapTicks, 1000, last, last, 15000, out _));
        }

        [Fact]
        public void Query_tools_are_recognised_and_actions_are_not()
        {
            Assert.True(ColonyOverseerPolicy.IsQueryTool("actions_query_buildable_structures"));
            Assert.True(ColonyOverseerPolicy.IsQueryTool("query_tool_help"));
            Assert.False(ColonyOverseerPolicy.IsQueryTool("actions_place_blueprint"));
            Assert.False(ColonyOverseerPolicy.IsQueryTool(null));
        }

        [Fact]
        public void Memory_is_capped_and_formats_failures_with_their_cause()
        {
            var memory = new List<ColonyPlanEntry>();
            for (int i = 0; i < ColonyOverseerPolicy.MaxMemoryEntries + 5; i++)
                ColonyOverseerPolicy.Remember(memory, new ColonyPlanEntry { Tick = i, Tool = "t" + i, Ok = true });
            Assert.Equal(ColonyOverseerPolicy.MaxMemoryEntries, memory.Count);
            Assert.Equal("t5", memory[0].Tool);

            var text = ColonyOverseerPolicy.FormatMemory(new List<ColonyPlanEntry>
            {
                new ColonyPlanEntry { Tick = 0, Tool = "actions_manage_zone", Reason = "grow rice", Ok = true, Result = "created" },
                new ColonyPlanEntry { Tick = 5000, Tool = "actions_place_blueprint", Ok = false, Result = "not enough steel" },
            }, 7500);
            Assert.Contains("- 3h ago: actions_manage_zone → ok (grow rice)", text);
            Assert.Contains("- 1h ago: actions_place_blueprint → FAILED: not enough steel", text);
            Assert.DoesNotContain("created", text);
        }
    }
}
