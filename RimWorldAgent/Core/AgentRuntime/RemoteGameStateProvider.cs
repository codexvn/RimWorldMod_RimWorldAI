using System;
using System.Threading;
using System.Threading.Tasks;
using RimWorldAgent.Core.Mcp;

namespace RimWorldAgent.Core.AgentRuntime
{
    /// <summary>EXE 模式 — tick 从 MCP 推送获取，暂停状态通过 MCP 查询</summary>
    public class RemoteGameStateProvider : GameStateBase
    {
        private readonly McpClient _mcp;
        private bool _isPaused;

        public RemoteGameStateProvider(McpClient mcp)
        {
            _mcp = mcp;
            mcp.OnGameTick += tick =>
            {
                Volatile.Write(ref _gameTick, tick);
                AgentOrchestrator.GameTick = tick;
            };
        }

        public override bool IsPaused => _isPaused;

        public override async Task SyncGameStatusAsync()
        {
            try
            {
                var speed = await _mcp.CallTool("get_game_speed");
                _isPaused = speed != null && speed.IndexOf("已暂停", StringComparison.Ordinal) >= 0;
            }
            catch (Exception ex) { CoreLog.Info($"[RemoteGameState] 查询暂停状态失败: {ex.Message}"); }
        }
    }
}
