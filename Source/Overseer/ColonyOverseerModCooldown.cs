using RimMind.Application.Common.Interfaces.Extension;

namespace RimMind.Advisor.Overseer
{
    /// <summary>
    /// Core queue cooldown for overseer requests: none. The overseer schedules itself
    /// (<see cref="ColonyOverseerPolicy.ShouldRun"/>) and needs its feedback turns to go out immediately;
    /// without this registration Core would apply its default per-mod cooldown to the unknown mod id.
    /// </summary>
    internal sealed class ColonyOverseerModCooldown : IModCooldown
    {
        public string Id => ColonyOverseerDriver.RequestModId;
        public string OwnerModId => "RimMind.Advisor";
        public int CooldownTicks => 0;
    }
}
