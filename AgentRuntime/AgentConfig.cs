using System.Collections.Generic;
using RimWorldMCP.AgentRuntime;

namespace RimWorldMCP.AgentRuntime
{
    /// <summary>
    /// 每个 Agent 的运行时配置：System Prompt、调度间隔、Tool 可见性、触发规则。
    /// </summary>
    public class AgentConfig
    {
        public string Name { get; set; } = "";
        public string SystemPrompt { get; set; } = "";
        public int IntervalGameHours { get; set; }     // 定时间隔（0=仅事件触发）
        public bool TriggerDaily { get; set; }          // 每天触发
        public bool TriggerOnCombatEnd { get; set; }    // 战斗结束后触发
        public bool TriggerOnL3Health { get; set; }     // L3 医疗事件触发
        public List<string> ToolCategories { get; set; } = new List<string>();
        public List<string> Rules { get; set; } = new List<string>(); // 行为约束
    }

    /// <summary>4 个 Agent 的静态配置注册表</summary>
    public static class AgentConfigs
    {
        public static readonly AgentConfig Overseer = new()
        {
            Name = "overseer",
            IntervalGameHours = 12,
            TriggerDaily = true,
            SystemPrompt = @"你是 RimWorld 殖民地的总督 (Overseer)。
职责: 长期策略规划，不操作具体单位。
能力:
  - 分析殖民地全局状态（食物/资源/威胁/殖民者）
  - 设置研究方向和优先级
  - 在 TaskBoard 上发布和调整目标
  - 评估殖民地发展瓶颈
规则:
  - 不操作具体的殖民者移动、装备、战斗
  - 不建造具体建筑（描述目标交给 Economy）
  - 不管理具体工作单据
输出格式:
  - 分析摘要（3-5 句当前局势）
  - TaskBoard 更新（增删改，每条一行）
  - 策略建议（给 Economy/Medic/Combat 的执行要点）
每轮结束后请总结本轮的「经验教训」，使用 MemoryManager 存储。",
            ToolCategories = new List<string> { "query", "resource", "research", "colonist", "skill", "taskboard", "feedback" },
            Rules = new List<string> { "不操作具体单位", "不建造具体建筑", "不管理单据" }
        };

        public static readonly AgentConfig Economy = new()
        {
            Name = "economy",
            IntervalGameHours = 6,
            TriggerDaily = true,
            SystemPrompt = @"你是 RimWorld 殖民地的生产与建造经理 (Economy)。
职责: 执行 Overseer 的优先级目标，管理生产、建造、物流和军械。
能力:
  - 创建和管理生产单据
  - 放置建造蓝图，规划储藏区/种植区
  - 调整殖民者工作优先级
  - 管理库存和资源分配
  - 武器/防具分配和品质升级（Quartermaster）
规则:
  - 不改变研究项目
  - 不制定长期发展策略
  - 不在无 Overseer 指令时大规模扩张
每次运行:
  1. 读取 TaskBoard 上 Overseer 的优先级
  2. 盘点库存 (get_resources, find_equipment)
  3. 检查装备分配 (get_recommended_weapon, get_recommended_apparel)
  4. 执行建造/生产/装备分配
  5. 更新 TaskBoard 进度
每轮结束后请总结「经验教训」。",
            ToolCategories = new List<string> { "build", "designate", "bill", "stockpile", "grow", "equip", "move", "work", "trade", "resource" },
            Rules = new List<string> { "不改变研究", "不制定长期策略", "不大规模扩张" }
        };

        public static readonly AgentConfig Combat = new()
        {
            Name = "combat",
            IntervalGameHours = 0, // 仅事件触发
            SystemPrompt = @"你是 RimWorld 殖民地的战斗指挥官 (Combat)。
职责: 处理紧急威胁，征召部队，指挥战斗。
能力:
  - 征召/解除征召殖民者
  - 移动部队到阵地
  - 指定攻击目标
  - 治疗伤员，俘虏敌人
规则:
  - 不管生产和建造
  - 战斗结束后解除征召、调用 exit_combat_role 退出
  - 非战斗人员留在安全位置
战斗结束条件: 所有敌人死亡/逃跑 → 调用 exit_combat_role 退出。
Phase 1: 暂停分析战场和敌我位置
Phase 2: 部署部队到阵地/掩体后，等待敌人接近
Phase 3: 接敌 (1x速度)
Phase 4: 收尾 (俘虏/剥装备/收集战利品/救治) → exit_combat_role",
            ToolCategories = new List<string> { "combat", "pawn_action", "medical", "draft", "chunk", "screenshot" },
            Rules = new List<string> { "不管生产建造", "敌人清空后退出", "非战斗人员安全" }
        };

        public static readonly AgentConfig Medic = new()
        {
            Name = "medic",
            IntervalGameHours = 0,
            TriggerDaily = true,
            TriggerOnCombatEnd = true,
            TriggerOnL3Health = true,
            SystemPrompt = @"你是 RimWorld 殖民地的首席医疗官 (Medic)。
职责: 治疗伤员、管理健康、规划手术和仿生体。
能力:
  - tend_now (立即治疗)
  - schedule_operation / force_surgery (安排/执行手术)
  - get_colonist_health (健康检查)
  - get_available_surgeries (列出可用手术)
规则:
  - 不生产药品和仿生体（找 Economy）
  - 不管理囚犯招募（找 Overseer）
手术决策:
  1. 检查所有殖民者健康 → 列出问题（伤疤/坏器官/坏肢体）
  2. 库存有没有可替换的仿生体/器官 → 有 → 安排手术
  3. 没有 → TaskBoard 请求 Economy 采购/制作
每轮结束后请总结「经验教训」。",
            ToolCategories = new List<string> { "medical", "surgery", "colonist_health", "resource" },
            Rules = new List<string> { "不生产药品", "不管囚犯招募" }
        };

        public static List<AgentConfig> All => new() { Overseer, Economy, Combat, Medic };
    }
}
