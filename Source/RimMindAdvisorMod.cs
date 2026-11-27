using RimMind.Advisor.Advisor;
using RimMind.Advisor.Settings;
using RimMind.Application.Common.Interfaces.Extension;
using RimMind.Presentation;
using RimMind.Presentation.Api;
using RimMind.Presentation.Settings;
using UnityEngine;
using Verse;

namespace RimMind.Advisor
{
    public class RimMindAdvisorMod : RimMindSubmodBase<RimMindAdvisorSettings>
    {
        public static new RimMindAdvisorSettings Settings = null!;

        public RimMindAdvisorMod(ModContentPack content) : base(content)
        {
            Settings = base.Settings;
            InitializeHarmony();

            RimMindAPI.Extensions<ISettingsTab>().Register(new AdvisorSettingsTab());
            RimMindAPI.Extensions<IToggleBehavior>().Register(new AdvisorToggleBehavior(Settings));
            RimMindAPI.Extensions<IModCooldown>().Register(new AdvisorModCooldown(Settings));
            RimMindAPI.Extensions<ISkipCheck>().Register(new AdvisorActionSkipCheck());
            AdvisorProviderRegistrar.RegisterAll();
            RimMindAPI.Extensions<IModCooldown>().Register(new Overseer.ColonyOverseerModCooldown());
            RimMindAPI.Context.RegisterScenario(Overseer.ColonyOverseerDriver.ScenarioId, 0,
                "Colony overseer: colony-level decisions (construction, food, storage, bills, work)");

            Log.Message("[RimMind-Advisor] Initialized.");
        }

        public override void DoSettingsWindowContents(Rect rect) =>
            AdvisorSettingsDrawer.Draw(rect);
    }
}
