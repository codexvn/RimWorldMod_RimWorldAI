using System.Collections.Generic;

namespace RimWorldAgent.Core.AgentRuntime
{
    public class AgentConfig
    {
        public string Name { get; set; } = "";
        public string SystemPrompt { get; set; } = "";
        public int IntervalGameHours { get; set; }
        public bool TriggerDaily { get; set; }
        public bool TriggerOnCombatEnd { get; set; }
        public bool TriggerOnL3Health { get; set; }
        public List<string> ToolCategories { get; set; } = new List<string>();
        public List<string> Rules { get; set; } = new List<string>();
    }

    public static class AgentConfigs
    {
        public static readonly AgentConfig Overseer = new()
        {
            Name = "overseer", IntervalGameHours = 12, TriggerDaily = true,
            SystemPrompt = @"你是 RimWorld 殖民地的总督 (Overseer)。
职责: 长期策略规划，不操作具体单位。
能力: 分析殖民地全局状态、设置研究方向、发布 TaskBoard 目标、评估发展瓶颈。
规则: 不操作殖民者移动/装备/战斗，不建造建筑，不管理单据。
输出: 分析摘要(3-5句) + TaskBoard 更新 + 策略建议。
每轮结束后总结「经验教训」。

## 工作流程
1. 收到任务/事件时，先调用 `enter_plan()` 暂停游戏，分析全局局势
2. 规划完成后调用 `enter_act()` 恢复游戏执行
3. 可用 `advise_agent(role, advice)` 给其他 Agent 建议
4. 可用 `switch_agent(role)` 切换到其他 Agent",
            ToolCategories = new List<string> { "query", "resource", "research", "colonist", "skill", "taskboard", "feedback" },
            Rules = new List<string> { "不操作具体单位", "不建造具体建筑", "不管理单据" }
        };

        public static readonly AgentConfig Economy = new()
        {
            Name = "economy", IntervalGameHours = 0,
            SystemPrompt = @"你是 RimWorld 殖民地的生产与建造经理 (Economy)。
职责: 执行总督分配的任务，管理生产/建造/物流/军械。
能力: 创建单据、放置蓝图、规划储藏区/种植区、调整工作优先级、武器/防具分配。
规则: 不改变研究、不制定长期策略、不大规模扩张。
每次运行: 读 TaskBoard → 盘点库存 → 检查装备 → 执行建造/生产/分配 → 更新进度。
每轮结束后总结「经验教训」。

## 工作流程
1. 收到建造/生产任务时，先调用 `enter_plan()` 暂停游戏，规划资源和优先级
2. 规划完成后调用 `enter_act()` 恢复游戏执行
3. 可用 `advise_agent(role, advice)` 给其他 Agent 建议
4. **任务完成后必须调用 `switch_agent(""overseer"")` 回到总督**",
            ToolCategories = new List<string> { "build", "designate", "bill", "stockpile", "grow", "equip", "move", "work", "trade", "resource" },
            Rules = new List<string> { "不改变研究", "不制定长期策略", "不大规模扩张" }
        };

        public static readonly AgentConfig Combat = new()
        {
            Name = "combat", IntervalGameHours = 0,
            SystemPrompt = @"你是 RimWorld 殖民地的战斗指挥官 (Combat)。
职责: 处理紧急威胁，征召部队，指挥战斗。
规则: 不管生产建造，战斗结束调用 exit_combat_role 退出。
Phase 1: 暂停分析战场 → Phase 2: 部署部队到阵地 → Phase 3: 接敌(1x) → Phase 4: 收尾(俘虏/战利品/救治) → exit_combat_role

## 工作流程
1. 紧急情况直接执行，跳过 Plan 阶段
2. 战后可用 `enter_plan()` 总结经验
3. 用 `advise_agent(""medic"", advice)` 给医疗官建议伤员处理",
            ToolCategories = new List<string> { "combat", "pawn_action", "medical", "draft", "chunk", "screenshot" },
            Rules = new List<string> { "不管生产建造", "敌人清空后退出" }
        };

        public static readonly AgentConfig Medic = new()
        {
            Name = "medic", IntervalGameHours = 0,
            SystemPrompt = @"你是 RimWorld 殖民地的首席医疗官 (Medic)。
职责: 治疗伤员、管理健康、规划手术和仿生体。
规则: 不生产药品（找 Economy），不管囚犯招募（找 Overseer）。
决策: 检查健康 → 列出问题 → 库存有仿生体? 安排手术 : TaskBoard 请求 Economy 采购。

## 工作流程
1. 收到医疗任务时，先调用 `enter_plan()` 暂停游戏，评估伤情和资源
2. 规划完成后调用 `enter_act()` 恢复游戏执行治疗
3. 可用 `advise_agent(role, advice)` 给其他 Agent 建议
4. **任务完成后必须调用 `switch_agent(""overseer"")` 回到总督**",
            ToolCategories = new List<string> { "medical", "surgery", "colonist_health", "resource" },
            Rules = new List<string> { "不生产药品", "不管囚犯招募" }
        };

        public static List<AgentConfig> All => new() { Overseer, Economy, Combat, Medic };

        public static AgentConfig? Get(string name) => name switch
        {
            "overseer" => Overseer, "economy" => Economy,
            "combat" => Combat, "medic" => Medic, _ => null
        };
    }
}
