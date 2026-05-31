using System.Threading.Tasks;
using RimWorldAgent.Core.AgentRuntime;
using Verse;

namespace RimWorldAgent
{
    /// <summary>MOD 模式 — 直接从 Find.TickManager 读取，无需 MCP 往返</summary>
    public class DirectGameStateProvider : GameStateBase
    {
        public override bool IsPaused
        {
            get
            {
                var tm = Find.TickManager;
                return tm != null && tm.Paused;
            }
        }

        public override Task SyncGameStatusAsync()
        {
            var tm = Find.TickManager;
            if (tm != null)
                _gameTick = tm.TicksGame;
            return Task.CompletedTask;
        }
    }
}
