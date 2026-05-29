namespace RimWorldMCP.Harmony
{
    public enum NotificationType
    {
        Letter,
        Message,
        AlertStart,
        AlertEnd
    }

    public class Notification
    {
        public NotificationType Type { get; set; }
        public string Label { get; set; } = string.Empty;
        public string? Text { get; set; }
        public string DangerLabel { get; set; } = string.Empty;
        public int Priority { get; set; }
        public System.Collections.Generic.List<string>? Culprits { get; set; }
        public int Tick { get; set; }

        public string PriorityLabel => Priority switch
        {
            2 => "CRITICAL",
            1 => "HIGH",
            _ => "MEDIUM"
        };
    }
}
