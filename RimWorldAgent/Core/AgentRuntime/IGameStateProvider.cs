using System.Threading.Tasks;

namespace RimWorldAgent.Core.AgentRuntime
{
    /// <summary>游戏状态提供者。调用方通过 SyncGameStatusAsync() 刷新缓存后读取各属性。</summary>
    public interface IGameStateProvider
    {
        int GameTick { get; }
        int GameDay { get; }
        int GameHour { get; }
        bool IsPaused { get; }

        bool ShouldMorningReport();
        void MarkMorningReportSent();
        bool ShouldWake(int intervalGameHours);
        void Reset();

        /// <summary>同步游戏状态到内部缓存：tick / paused。Direct 读 TickManager，Remote 调 MCP。</summary>
        Task SyncGameStatusAsync();
    }

    public abstract class GameStateBase : IGameStateProvider
    {
        protected int _gameTick;
        protected int _lastWakeTick;
        protected int _lastMorningDay = -1;

        public int GameTick => _gameTick;
        public int GameDay => _gameTick / 60000;
        public int GameHour => (_gameTick / 2500) % 24;
        public abstract bool IsPaused { get; }

        public bool ShouldMorningReport()
            => GameHour >= 6 && GameDay > _lastMorningDay;

        public void MarkMorningReportSent()
            => _lastMorningDay = GameDay;

        public bool ShouldWake(int intervalGameHours)
        {
            int interval = intervalGameHours * 2500;
            if (_gameTick - _lastWakeTick >= interval)
            {
                _lastWakeTick = _gameTick;
                return true;
            }
            return false;
        }

        public virtual void Reset()
        {
            _lastWakeTick = 0;
            _lastMorningDay = -1;
        }

        public virtual Task SyncGameStatusAsync() => Task.CompletedTask;
    }
}
