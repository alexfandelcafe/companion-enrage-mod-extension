using System;
using RDR2;

namespace EnRAGECompanionsExtension
{
    /// <summary>
    /// SHVDN V2 entry point. The script polls until enRAGE has loaded, then installs Harmony patches.
    /// It does not reference NemesisMod.dll at compile time; integration is reflection-based so
    /// this assembly remains a separate plugin.
    /// </summary>
    public sealed class EntryPoint : Script
    {
        private int _nextProbeAt;

        public EntryPoint()
        {
            CompanionStateRuntime.Initialize();
            Tick += OnTick;
        }

        private void OnTick(object sender, EventArgs e)
        {
            if (EnRAGEHooks.IsInstalled || Game.GameTime < _nextProbeAt)
                return;

            _nextProbeAt = Game.GameTime + 1500;
            EnRAGEHooks.TryInstall();
        }
    }
}
