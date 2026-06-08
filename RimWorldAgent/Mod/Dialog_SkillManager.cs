using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using RimWorld;
using RimWorldAgent.Core.AgentRuntime;
using RimWorldAgent.Core.Skills;
using UnityEngine;
using Verse;

namespace RimWorldAgent
{
    public class Dialog_SkillManager : Window
    {
        private readonly SkillRegistry _registry;
        private readonly SkillStore _store;
        private Vector2 _listScroll;
        private Vector2 _contentScroll;
        private string _selectedName = "";
        private string _editName = "";
        private string _editDescription = "";
        private string _editContent = "";
        private bool _editingNew;

        public override Vector2 InitialSize => new Vector2(980f, 720f);

        public Dialog_SkillManager()
        {
            doCloseX = true;
            absorbInputAroundWindow = true;
            forcePause = false;

            var activeRegistry = InternalToolRegistry.SkillRegistry;
            var activeStore = InternalToolRegistry.SkillStore;
            if (activeRegistry != null && activeStore != null)
            {
                _registry = activeRegistry;
                _store = activeStore;
            }
            else
            {
                var modRoot = Path.GetDirectoryName(typeof(RimWorldAgentMod).Assembly.Location) ?? ".";
                var settings = RimWorldAgentMod.Instance?.Settings;
                var builtinDir = !string.IsNullOrWhiteSpace(settings?.SkillsDir)
                    ? Path.Combine(modRoot, settings!.SkillsDir)
                    : Path.Combine(modRoot, "Skills");
                var userDir = Path.Combine(modRoot, "Skills.d");
                _store = new SkillStore(builtinDir, userDir);
                _registry = new SkillRegistry();
                _registry.LoadFromDirectories(_store.BuiltinSkillsDir, _store.UserSkillsDir);
            }

            SelectFirstSkill();
        }

        public override void DoWindowContents(Rect inRect)
        {
            DrawHeader(new Rect(inRect.x, inRect.y, inRect.width, 30f));

            var body = new Rect(inRect.x, inRect.y + 38f, inRect.width, inRect.height - 38f);
            var listRect = new Rect(body.x, body.y, 270f, body.height);
            var rightRect = new Rect(listRect.xMax + 12f, body.y, body.width - listRect.width - 12f, body.height);

            DrawSkillList(listRect);
            DrawEditor(rightRect);
        }

        private void DrawHeader(Rect rect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(rect.x, rect.y, 240f, rect.height), "Skill 管理");
            Text.Font = GameFont.Small;

            var dirRect = new Rect(rect.x + 250f, rect.y + 5f, rect.width - 250f, 22f);
            GUI.color = new Color(0.65f, 0.68f, 0.76f, 1f);
            Widgets.Label(dirRect, $"内置: {_store.BuiltinSkillsDir}    可写: {_store.UserSkillsDir}");
            GUI.color = Color.white;
        }

        private void DrawSkillList(Rect rect)
        {
            Widgets.DrawBox(rect);

            var top = new Rect(rect.x + 8f, rect.y + 8f, rect.width - 16f, 30f);
            if (Widgets.ButtonText(new Rect(top.x, top.y, 82f, top.height), "新建"))
                BeginNewSkill();
            if (Widgets.ButtonText(new Rect(top.x + 90f, top.y, 82f, top.height), "重载"))
                ReloadAndReselect(_selectedName);
            if (Widgets.ButtonText(new Rect(top.x + 180f, top.y, 74f, top.height), "目录"))
                OpenUserSkillsDir();

            var skills = _registry.GetAll();
            var scrollRect = new Rect(rect.x + 6f, rect.y + 46f, rect.width - 12f, rect.height - 52f);
            var viewHeight = Math.Max(scrollRect.height - 16f, skills.Count * 42f + 8f);
            var view = new Rect(0f, 0f, scrollRect.width - 16f, viewHeight);
            Widgets.BeginScrollView(scrollRect, ref _listScroll, view);

            var y = 4f;
            foreach (var skill in skills)
            {
                var row = new Rect(0f, y, view.width, 38f);
                var selected = !_editingNew && string.Equals(_selectedName, skill.Name, StringComparison.OrdinalIgnoreCase);
                if (selected)
                    Widgets.DrawBoxSolid(row, new Color(0.18f, 0.24f, 0.32f, 0.75f));
                else if (Mouse.IsOver(row))
                    Widgets.DrawBoxSolid(row, new Color(0.12f, 0.12f, 0.14f, 0.45f));

                if (Widgets.ButtonInvisible(row))
                    SelectSkill(skill);

                Text.Font = GameFont.Tiny;
                GUI.color = ColorForSkill(skill);
                Widgets.Label(new Rect(row.x + 6f, row.y + 3f, row.width - 12f, 16f), $"{skill.Name}  ·  {SourceLabel(skill)}");
                GUI.color = new Color(0.68f, 0.7f, 0.76f, 1f);
                Widgets.Label(new Rect(row.x + 6f, row.y + 20f, row.width - 12f, 16f), Truncate(skill.Description, 42));
                GUI.color = Color.white;
                Text.Font = GameFont.Small;

                y += 42f;
            }

            Widgets.EndScrollView();
        }

        private void DrawEditor(Rect rect)
        {
            Widgets.DrawBox(rect);
            var x = rect.x + 10f;
            var y = rect.y + 10f;
            var width = rect.width - 20f;

            Text.Font = GameFont.Tiny;
            GUI.color = new Color(0.65f, 0.68f, 0.76f, 1f);
            Widgets.Label(new Rect(x, y, width, 18f), _editingNew ? "新建 Skill" : "编辑 Skill");
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            y += 22f;

            Widgets.Label(new Rect(x, y, 90f, 24f), "名称");
            if (_editingNew)
                _editName = Widgets.TextField(new Rect(x + 100f, y, width - 100f, 24f), _editName);
            else
                Widgets.Label(new Rect(x + 100f, y, width - 100f, 24f), $"{_editName} ({CurrentSourceLabel()})");
            y += 32f;

            Widgets.Label(new Rect(x, y, 90f, 24f), "描述");
            _editDescription = Widgets.TextField(new Rect(x + 100f, y, width - 100f, 24f), _editDescription ?? "");
            y += 34f;

            var current = CurrentSkill();
            if (current != null && current.Source == "builtin")
            {
                GUI.color = new Color(1f, 0.82f, 0.35f, 1f);
                Widgets.Label(new Rect(x, y, width, 24f), "内置 Skill 不会被直接修改。保存会在 Skills.d 中创建同名覆盖版本。");
                GUI.color = Color.white;
                y += 28f;
            }

            Widgets.Label(new Rect(x, y, width, 24f), "Markdown 正文");
            y += 24f;

            var footerHeight = 42f;
            var editorRect = new Rect(x, y, width, rect.yMax - y - footerHeight - 8f);
            var contentHeight = Math.Max(editorRect.height - 16f, Text.CalcHeight(_editContent ?? "", editorRect.width - 28f) + 180f);
            var view = new Rect(0f, 0f, editorRect.width - 16f, contentHeight);
            Widgets.BeginScrollView(editorRect, ref _contentScroll, view);
            _editContent = Widgets.TextArea(new Rect(0f, 0f, view.width, contentHeight), _editContent ?? "");
            Widgets.EndScrollView();

            DrawEditorButtons(new Rect(x, rect.yMax - footerHeight, width, 34f));
        }

        private void DrawEditorButtons(Rect rect)
        {
            if (Widgets.ButtonText(new Rect(rect.x, rect.y, 110f, rect.height), "保存"))
                SaveCurrent();

            if (!_editingNew)
            {
                var skill = CurrentSkill();
                var canDelete = skill != null && skill.Source == "user";
                if (!canDelete) GUI.color = new Color(1f, 1f, 1f, 0.35f);
                if (Widgets.ButtonText(new Rect(rect.x + 118f, rect.y, 140f, rect.height), skill?.IsOverride == true ? "删除覆盖" : "删除自定义"))
                {
                    if (canDelete) DeleteCurrentUserSkill();
                }
                GUI.color = Color.white;
            }

            if (Widgets.ButtonText(new Rect(rect.xMax - 210f, rect.y, 96f, rect.height), "重载"))
                ReloadAndReselect(_editingNew ? _editName : _selectedName);
            if (Widgets.ButtonText(new Rect(rect.xMax - 106f, rect.y, 106f, rect.height), "打开目录"))
                OpenUserSkillsDir();
        }

        private void BeginNewSkill()
        {
            _editingNew = true;
            _selectedName = "";
            _editName = NextSkillName();
            _editDescription = "在相关殖民地场景中激活，提供可复用操作流程。";
            _editContent = "# 新 Skill\n\n## 使用原则\n\n- ";
            _contentScroll = Vector2.zero;
        }

        private void SelectSkill(SkillInfo skill)
        {
            _editingNew = false;
            _selectedName = skill.Name;
            _editName = skill.Name;
            _editDescription = skill.Description;
            _editContent = skill.Content;
            _contentScroll = Vector2.zero;
        }

        private void SelectFirstSkill()
        {
            var first = _registry.GetAll().FirstOrDefault();
            if (first != null) SelectSkill(first);
            else BeginNewSkill();
        }

        private void SaveCurrent()
        {
            var normalizedName = SkillStore.NormalizeName(_editName);
            var existing = _registry.Get(normalizedName);
            var overwrite = !_editingNew || existing != null;
            if (_editingNew && existing != null && existing.Source == "builtin")
                overwrite = true;

            var result = _store.SaveUserSkill(normalizedName, _editDescription, _editContent, overwrite);
            if (!result.Success)
            {
                Messages.Message(result.Message, MessageTypeDefOf.RejectInput, false);
                return;
            }

            ReloadActiveRegistry();
            ReloadAndReselect(normalizedName);
            Messages.Message($"Skill 已保存并热加载: {normalizedName}", MessageTypeDefOf.TaskCompletion, false);
        }

        private void DeleteCurrentUserSkill()
        {
            var name = _selectedName;
            var result = _store.DeleteUserSkill(name);
            if (!result.Success)
            {
                Messages.Message(result.Message, MessageTypeDefOf.RejectInput, false);
                return;
            }

            ReloadActiveRegistry();
            ReloadAndReselect(name);
            Messages.Message($"已删除 Skills.d 中的 Skill: {name}", MessageTypeDefOf.TaskCompletion, false);
        }

        private void ReloadAndReselect(string name)
        {
            _registry.Reload();
            var skill = _registry.Get(SkillStore.NormalizeName(name));
            if (skill != null) SelectSkill(skill);
            else SelectFirstSkill();
        }

        private void ReloadActiveRegistry()
        {
            try
            {
                InternalToolRegistry.SkillRegistry?.Reload();
            }
            catch (Exception ex)
            {
                SafeLog.Warning($"[SkillManager] 热加载失败: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private SkillInfo? CurrentSkill()
        {
            if (_editingNew || string.IsNullOrWhiteSpace(_selectedName)) return null;
            return _registry.Get(_selectedName);
        }

        private string CurrentSourceLabel()
        {
            var skill = CurrentSkill();
            return skill == null ? "新建" : SourceLabel(skill);
        }

        private static string SourceLabel(SkillInfo skill)
        {
            if (skill.Source == "user")
                return skill.IsOverride ? "Skills.d 覆盖" : "Skills.d 自定义";
            return "内置";
        }

        private static Color ColorForSkill(SkillInfo skill)
        {
            if (skill.Source == "user")
                return skill.IsOverride ? new Color(1f, 0.82f, 0.35f, 1f) : new Color(0.45f, 0.85f, 0.55f, 1f);
            return new Color(0.68f, 0.78f, 0.95f, 1f);
        }

        private string NextSkillName()
        {
            for (var i = 1; i < 1000; i++)
            {
                var name = i == 1 ? "new-skill" : $"new-skill-{i}";
                if (_registry.Get(name) == null) return name;
            }
            return "new-skill";
        }

        private void OpenUserSkillsDir()
        {
            try
            {
                Directory.CreateDirectory(_store.UserSkillsDir);
                Process.Start(new ProcessStartInfo
                {
                    FileName = _store.UserSkillsDir,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Messages.Message($"打开目录失败: {ex.Message}", MessageTypeDefOf.RejectInput, false);
            }
        }

        private static string Truncate(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxLength) return text ?? "";
            return text.Substring(0, maxLength - 1) + "…";
        }
    }
}
