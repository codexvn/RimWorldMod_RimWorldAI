using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RimWorldAgent.Core.Mcp;

namespace RimWorldAgent.Core.AgentRuntime
{
    /// <summary>游戏节奏控制器 — Plan/Act 阶段强制暂停，仅 Advance 阶段允许推进</summary>
    public class GamePaceController : IDisposable
    {
        private readonly SemaphoreSlim _opLock = new(1, 1);

        /// <summary>进入 Plan 阶段，强制暂停游戏</summary>
        public async Task PauseForPlanning(McpClient mcp)
        {
            await _opLock.WaitAsync();
            try
            {
                await CallTogglePause(mcp, "paused");
                CoreLog.Info("[GamePace] Plan 阶段：强制暂停");
            }
            finally { _opLock.Release(); }
        }

        /// <summary>进入 Act 阶段，同样强制暂停游戏（仅 Advance 可推进）</summary>
        public async Task PauseForAction(McpClient mcp)
        {
            await _opLock.WaitAsync();
            try
            {
                await CallTogglePause(mcp, "paused");
                CoreLog.Info("[GamePace] Act 阶段：强制暂停");
            }
            finally { _opLock.Release(); }
        }

        /// <summary>确保游戏已暂停（finally / 会话结束时调用）</summary>
        public async Task EnsurePaused(McpClient mcp)
        {
            await _opLock.WaitAsync();
            try
            {
                await CallTogglePause(mcp, "paused");
                CoreLog.Info("[GamePace] 已恢复强制暂停 (EnsurePaused)");
            }
            finally { _opLock.Release(); }
        }

        private static async Task CallTogglePause(McpClient mcp, string speed)
        {
            try
            {
                CoreLog.Debug($"[GamePace] toggle_pause({speed}) 开始");
                var speedElement = JsonSerializer.SerializeToElement(speed);
                var args = new Dictionary<string, JsonElement> { ["speed"] = speedElement };
                await mcp.CallTool("toggle_pause", args);
                CoreLog.Debug($"[GamePace] toggle_pause({speed}) 完成");
            }
            catch (Exception ex)
            {
                var innerChain = "";
                for (var e = ex.InnerException; e != null; e = e.InnerException)
                    innerChain += $" ← {e.GetType().Name}: {e.Message}";
                CoreLog.Error($"[GamePace] toggle_pause({speed}) 失败: {ex.GetType().Name}: {ex.Message}{innerChain}\n{ex.StackTrace}");
                throw;
            }
        }

        /// <summary>
        /// 后台暂停强制：每 ~2s 由 TickAsync 调用。PLAN/ACT 阶段非推进期游戏必须在暂停状态。
        /// 设计为 advance_tick 的配套——advance_tick 推进并解锁游戏，本方法兜底确保 PLAN/ACT 下游戏始终暂停。
        /// toggle_pause(speed:"paused") 已是幂等操作（仅 !tm.Paused 时切换），重复调用无副作用。
        /// </summary>
        public static async Task EnforcePauseAsync(McpClient mcp, bool isPaused)
        {
            var phase = AgentOrchestrator.CurrentPhase;
            if (phase != GamePhase.Plan && phase != GamePhase.Act) return;

            // advance_tick 推进中：游戏已暂停=推进完成，清除标记；游戏运行中=推进进行中，跳过
            if (AgentOrchestrator.IsAdvancing)
            {
                if (isPaused) AgentOrchestrator.IsAdvancing = false;
                return;
            }
            try
            {
                var pc = AgentOrchestrator.PaceController ?? new GamePaceController();
                await pc.EnsurePaused(mcp);
                if (!ReferenceEquals(pc, AgentOrchestrator.PaceController))
                    pc.Dispose();
            }
            catch (Exception ex)
            {
                CoreLog.Warn($"[GamePace] 后台暂停强制失败: {ex.Message}");
            }
        }

        public void Dispose()
        {
            _opLock.Dispose();
        }
    }
}