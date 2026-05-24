using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace RimWorldMCP
{
    public class McpCommand
    {
        public Func<object?> Action { get; set; } = null!;
        public TaskCompletionSource<object?> Completion { get; set; } = new();
    }

    public static class McpCommandQueue
    {
        private static readonly ConcurrentQueue<McpCommand> _queue = new();

        public static void Enqueue(McpCommand command)
        {
            _queue.Enqueue(command);
        }

        /// <summary>主线程每帧调用。无条件执行所有待处理命令。</summary>
        public static void ProcessPending()
        {
            while (_queue.TryDequeue(out var command))
            {
                try
                {
                    var result = command.Action();
                    command.Completion.TrySetResult(result);
                }
                catch (Exception ex)
                {
                    command.Completion.TrySetException(ex);
                }
            }
        }

        public static int PendingCount => _queue.Count;

        private static readonly ConcurrentQueue<Action> _deferredCleanup = new();

        /// <summary>调度一个延迟到下一帧执行的操作（用于需要 Unity 帧末完成后再执行的清理）</summary>
        public static void ScheduleDeferred(Action action)
        {
            _deferredCleanup.Enqueue(action);
        }

        /// <summary>主线程每帧调用，在 ProcessPending 和 ProcessPendingUploads 之后执行延迟清理</summary>
        public static void ProcessDeferredCleanup()
        {
            while (_deferredCleanup.TryDequeue(out var action))
            {
                try { action(); }
                catch (Exception ex) { McpLog.Warn($"延迟清理操作异常: {ex.Message}"); }
            }
        }

        /// <summary>调度同步操作到主线程执行，等待结果（超时 30 秒）</summary>
        public static async Task<T> DispatchAsync<T>(Func<T> action, int timeoutMs = 30000)
        {
            var command = new McpCommand { Action = () => action() };
            _queue.Enqueue(command);

            var timeout = Task.Delay(timeoutMs);
            var completed = await Task.WhenAny(command.Completion.Task, timeout);
            if (completed == timeout)
                throw new TimeoutException("主线程命令执行超时");

            return (T)command.Completion.Task.Result!;
        }
    }
}
