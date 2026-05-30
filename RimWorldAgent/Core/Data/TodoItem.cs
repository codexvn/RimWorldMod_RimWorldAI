namespace RimWorldAgent.Core.Data
{
    public class TodoItem
    {
        public string Id { get; set; } = "";
        public string Description { get; set; } = "";
        public int Priority { get; set; } = 3;
        public string Status { get; set; } = "pending";
        public int CreatedAtTick { get; set; }
    }
}
