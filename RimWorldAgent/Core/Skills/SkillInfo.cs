namespace RimWorldAgent.Core.Skills
{
    public class SkillInfo
    {
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public string Content { get; set; } = "";
        public string FilePath { get; set; } = "";
        public string Source { get; set; } = "builtin";
        public bool IsOverride { get; set; }
    }
}
