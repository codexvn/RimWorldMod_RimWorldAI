using RimWorldAgent.Core.Data;
using Verse;

namespace RimWorldAgent
{
    /// <summary>
    /// Verse save/load 桥接：将 TodoStore 数据序列化到 RimWorld 存档。
    /// 数据存储完全委托给 Core 层 TodoStore，本类仅负责 Scribe 序列化。
    /// </summary>
    public static class TodoManager
    {
        public static void ExposeData()
        {
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                var items = TodoStore.Query(null);
                var count = items.Count;
                Scribe_Values.Look(ref count, "todoCount", 0);
                for (int i = 0; i < count; i++)
                {
                    var it = items[i];
                    var id = it.Id;
                    var desc = it.Description;
                    var prio = it.Priority;
                    var status = it.Status;
                    var tick = it.CreatedAtTick;
                    Scribe_Values.Look(ref id, $"id_{i}", "");
                    Scribe_Values.Look(ref desc, $"desc_{i}", "");
                    Scribe_Values.Look(ref prio, $"prio_{i}", 3);
                    Scribe_Values.Look(ref status, $"status_{i}", "pending");
                    Scribe_Values.Look(ref tick, $"createdAtTick_{i}", 0);
                }
            }
            else if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                TodoStore.Clear();
                int count = 0;
                Scribe_Values.Look(ref count, "todoCount", 0);
                for (int i = 0; i < count; i++)
                {
                    var id = "";
                    var desc = "";
                    var prio = 3;
                    var status = "pending";
                    var tick = 0;
                    Scribe_Values.Look(ref id, $"id_{i}", "");
                    Scribe_Values.Look(ref desc, $"desc_{i}", "");
                    Scribe_Values.Look(ref prio, $"prio_{i}", 3);
                    Scribe_Values.Look(ref status, $"status_{i}", "pending");
                    Scribe_Values.Look(ref tick, $"createdAtTick_{i}", 0);
                    TodoStore.AddExisting(new TodoItem
                    {
                        Id = id,
                        Description = desc,
                        Priority = prio,
                        Status = status,
                        CreatedAtTick = tick
                    });
                }
            }
        }
    }
}
