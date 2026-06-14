using Verse;
using Verse.AI;

namespace RimWorldMCP
{
    /// <summary>
    /// Job 排队模式
    /// </summary>
    public enum QueueMode
    {
        /// <summary>不排队，清空队列替换当前任务</summary>
        Replace,
        /// <summary>排到队尾（等价 Shift+右键）</summary>
        End,
        /// <summary>排到队首（MCP 指令优先）</summary>
        Front
    }

    /// <summary>
    /// 统一 Job 排队入口，封装 TryTakeOrderedJob + 队列位置控制。
    /// </summary>
    public static class JobQueueHelper
    {
        /// <summary>
        /// 尝试将 Job 加入队列。
        /// </summary>
        /// <param name="pawn">目标殖民者</param>
        /// <param name="job">要执行的 Job</param>
        /// <param name="mode">排队模式，默认 Front（MCP 指令优先）</param>
        /// <returns>是否成功加入队列</returns>
        public static bool TryTake(Pawn pawn, Job job, QueueMode mode = QueueMode.Front)
        {
            bool requestQueueing = mode != QueueMode.Replace;
            if (!pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc, requestQueueing))
                return false;

            // Front 模式：将刚排到队尾的 Job 提取到队首
            if (mode == QueueMode.Front && pawn.CurJob != job && pawn.jobs.jobQueue != null)
            {
                pawn.jobs.jobQueue.Extract(job);
                pawn.jobs.jobQueue.EnqueueFirst(job, JobTag.Misc);
            }
            return true;
        }
    }
}
