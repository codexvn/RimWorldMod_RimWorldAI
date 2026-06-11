using System.Collections.Generic;

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
        /// <summary>三级标签，格式 "一级/二级/三级"，与 RimWorld Wiki 分类对齐</summary>
        public List<string> Tags { get; set; } = new List<string>();
    }
}
