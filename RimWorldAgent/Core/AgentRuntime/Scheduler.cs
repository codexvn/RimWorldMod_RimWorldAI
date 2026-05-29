using System.Collections.Generic;

namespace RimWorldAgent.Core.AgentRuntime
{
    /// <summary>Scheduler 输入数据（由 MCP get_world_summary 提供，或 MOD 模式下注入）</summary>
    public class SchedulerInput
    {
        public int EnemyCount;       // 活跃敌人
        public int DownedEnemyCount; // 倒地敌人
        public int ColonistCount;    // 殖民者总数
        public int IdleCount;        // 空闲殖民者
        public float FoodDays;       // 食物天数
        public int MedicineCount;    // 药品总数
        public int CurrentTick;      // 当前游戏 tick（用于定时判断）
    }

    public static class Scheduler
    {
        public static int LoadScore { get; private set; }
        public static string Mode { get; private set; } = "Normal";
        private static readonly Dictionary<string, int> _agentLastWake = new();

        /// <summary>根据外部输入更新 Load Score</summary>
        public static void Tick(SchedulerInput input)
        {
            float threat = MathMin(100f, input.EnemyCount * 10f + input.DownedEnemyCount * 5f);
            float workload = input.ColonistCount > 0 ? (input.IdleCount * 30f) / input.ColonistCount : 0f;
            float resourceStress = 0f;
            if (input.FoodDays < 3f) resourceStress += 40f;
            else if (input.FoodDays < 5f) resourceStress += 20f;
            if (input.MedicineCount < 5) resourceStress += 20f;
            else if (input.MedicineCount < 10) resourceStress += 5f;

            LoadScore = (int)MathMax(0, MathMin(100, threat * 0.5f + workload * 0.3f + MathMin(100f, resourceStress) * 0.2f));
            Mode = LoadScore switch { < 20 => "Peace", < 40 => "Normal", < 60 => "Busy", < 80 => "HighPressure", _ => "Crisis" };
        }

        public static bool ShouldWake(string agentName, int intervalGameHours, int currentTick)
        {
            int interval = intervalGameHours * 2500;
            if (!_agentLastWake.TryGetValue(agentName, out var lastWake)) _agentLastWake[agentName] = currentTick;
            if (currentTick - lastWake >= interval) { _agentLastWake[agentName] = currentTick; return true; }
            return false;
        }

        public static void MarkWoken(string agentName, int currentTick) { _agentLastWake[agentName] = currentTick; }
        public static bool IsCrisis => LoadScore >= 80;
        public static int CrisisIntervalTicks => LoadScore >= 90 ? 300 : 600;

        private static float MathMin(float a, float b) => a < b ? a : b;
        private static float MathMax(float a, float b) => a > b ? a : b;
    }
}
