using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.UIElements;

namespace KodachiGames.Markdown.Editor
{
    /// <summary>
    /// Renders a Markdown document as a prioritised task list. Every task-list checkbox
    /// (<c>- [ ]</c> / <c>- [x]</c>) is collected under the most recent heading; each
    /// heading shows the share of its tasks that are complete. Tasks carry an optional
    /// priority baked into the line as <c>{P:n}</c> (higher = more important, default 0);
    /// the marker is hidden in the rendered text.
    ///
    /// Within a heading, tasks are ordered uncompleted-first, then by descending priority.
    /// Like <see cref="MarkdownView"/>, every label keeps rich text disabled to avoid the
    /// TextCore rich-text measuring crash.
    /// </summary>
    public static class TaskView
    {
        static readonly Regex Heading  = new(@"^(#{1,6})\s+(.*)$", RegexOptions.Compiled);
        static readonly Regex Checkbox = new(@"^(\s*)[-*+]\s+\[([ xX])\]\s+(.*)$", RegexOptions.Compiled);
        static readonly Regex Priority = new(@"\{[Pp]:(-?\d+)\}", RegexOptions.Compiled);
        static readonly Regex InlineCode = new("`([^`]+)`", RegexOptions.Compiled);
        static readonly Regex Emphasis  = new(@"(\*\*|\*)(.+?)\1", RegexOptions.Compiled);
        static readonly Regex Link      = new(@"\[([^\]]+)\]\(([^)]+)\)", RegexOptions.Compiled);

        static readonly Color MutedColor = new(1f, 1f, 1f, 0.55f);
        static readonly Color AccentColor = new(0.255f, 0.490f, 0.965f);
        static readonly Color DoneColor = new(0.45f, 0.78f, 0.45f);

        sealed class Task
        {
            public int Line;       // source line index, for write-back
            public bool Done;
            public int Priority;
            public string Text;
        }

        sealed class Section
        {
            public string Title;   // null for tasks before the first heading
            public int Level;
            public readonly List<Task> Tasks = new();
        }

        /// <param name="onToggled">
        /// Invoked when a task checkbox is clicked, with the zero-based source line index and
        /// the new checked state. Pass <c>null</c> to render read-only (e.g. truncated source).
        /// </param>
        /// <param name="onSetPriority">
        /// Invoked from a task's right-click menu with the source line index and the chosen
        /// priority (0–5). Pass <c>null</c> to omit the menu (e.g. truncated source).
        /// </param>
        /// <param name="fileRel">
        /// Rel-path of the file being shown, used to match/set the global <see cref="ActiveTask"/>.
        /// </param>
        public static void Populate(VisualElement container, string markdown,
            Action<int, bool> onToggled = null, Action<int, int> onSetPriority = null, string fileRel = null)
        {
            container.Clear();
            if (string.IsNullOrEmpty(markdown)) return;

            var sections = Parse(markdown);
            var withTasks = sections.Where(s => s.Tasks.Count > 0).ToList();

            if (withTasks.Count == 0)
            {
                var empty = PlainLabel("No tasks found.\n\nAdd a line like \"- [ ] Do the thing {P:1}\".");
                empty.style.color = MutedColor;
                empty.style.whiteSpace = WhiteSpace.Normal;
                container.Add(empty);
                return;
            }

            // The active task, when it belongs to this file, is pinned as a highlighted card.
            if (fileRel != null && ActiveTask.HasActive && ActiveTask.Rel == fileRel)
                container.Add(ActiveCard());

            foreach (var section in withTasks)
            {
                container.Add(SectionHeader(section));

                // Uncompleted first; higher priority breaks ties. OrderBy is stable, so
                // equal-priority tasks keep their document order.
                var ordered = section.Tasks
                    .OrderBy(t => t.Done)
                    .ThenByDescending(t => t.Priority);

                foreach (var task in ordered)
                    container.Add(TaskRow(task, onToggled, onSetPriority, fileRel));
            }
        }

        static List<Section> Parse(string markdown)
        {
            var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var sections = new List<Section>();
            var current = new Section { Title = null, Level = 0 };
            sections.Add(current);

            var inFence = false;
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.TrimStart().StartsWith("```")) { inFence = !inFence; continue; }
                if (inFence) continue;

                var h = Heading.Match(line);
                if (h.Success)
                {
                    current = new Section { Title = Clean(h.Groups[2].Value).Trim(), Level = h.Groups[1].Value.Length };
                    sections.Add(current);
                    continue;
                }

                var c = Checkbox.Match(line);
                if (!c.Success) continue;

                var text = c.Groups[3].Value;
                var priority = 0;
                var p = Priority.Match(text);
                if (p.Success && int.TryParse(p.Groups[1].Value, out var parsed))
                    priority = parsed;
                text = Priority.Replace(text, "").Trim();

                current.Tasks.Add(new Task
                {
                    Line = i,
                    Done = c.Groups[2].Value is "x" or "X",
                    Priority = priority,
                    Text = Clean(text),
                });
            }

            return sections;
        }

        /// <summary>
        /// Highlighted banner for the active task: name, a live (self-ticking) timer, and a
        /// pause/resume button. Reads <see cref="ActiveTask"/> live so it reflects changes made
        /// from the toolbar without a full re-render.
        /// </summary>
        static VisualElement ActiveCard()
        {
            var card = new VisualElement
            {
                style =
                {
                    flexDirection = FlexDirection.Column,
                    backgroundColor = new Color(AccentColor.r, AccentColor.g, AccentColor.b, 0.18f),
                    borderLeftWidth = 3, borderLeftColor = AccentColor,
                    paddingTop = 6, paddingBottom = 6, paddingLeft = 8, paddingRight = 8,
                    marginBottom = 8,
                    borderTopLeftRadius = 3, borderTopRightRadius = 3,
                    borderBottomLeftRadius = 3, borderBottomRightRadius = 3
                }
            };

            var tag = new Label("ACTIVE TASK") { enableRichText = false };
            tag.style.fontSize = 9;
            tag.style.unityFontStyleAndWeight = FontStyle.Bold;
            tag.style.color = AccentColor;
            tag.style.letterSpacing = 1f;
            card.Add(tag);

            var name = PlainLabel(ActiveTask.Text);
            name.style.unityFontStyleAndWeight = FontStyle.Bold;
            name.style.fontSize = 14;
            name.style.marginBottom = 4;
            card.Add(name);

            var controls = new VisualElement
            { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center } };

            var timer = new Label { enableRichText = false };
            timer.style.fontSize = 18;
            timer.style.unityFontStyleAndWeight = FontStyle.Bold;
            timer.style.flexGrow = 1;
            controls.Add(timer);

            var pause = new Button { style = { marginLeft = 4 } };
            pause.clicked += () => { ActiveTask.TogglePause(); };
            controls.Add(pause);

            var clear = new Button(ActiveTask.Clear) { text = "Clear", style = { marginLeft = 2 } };
            controls.Add(clear);

            card.Add(controls);

            void Tick()
            {
                timer.text = ActiveTask.Format(ActiveTask.ElapsedSeconds);
                timer.style.color = ActiveTask.IsRunning ? Color.white : MutedColor;
                pause.text = ActiveTask.IsRunning ? "⏸ Pause" : "▶ Resume";
            }
            Tick();
            // Auto-stops when the element leaves the panel (tab switch / re-render).
            card.schedule.Execute(Tick).Every(500);

            return card;
        }

        static VisualElement SectionHeader(Section section)
        {
            var row = new VisualElement
            {
                style =
                {
                    flexDirection = FlexDirection.Row, alignItems = Align.Center,
                    marginTop = 8, marginBottom = 4
                }
            };

            var title = PlainLabel(string.IsNullOrEmpty(section.Title) ? "Tasks" : section.Title);
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.fontSize = section.Level switch { 0 => 15, 1 => 18, 2 => 16, 3 => 14, _ => 13 };
            title.style.flexGrow = 1;
            row.Add(title);

            var done = section.Tasks.Count(t => t.Done);
            var pct = Mathf.RoundToInt(100f * done / section.Tasks.Count);
            var badge = PlainLabel($"{pct}%  ({done}/{section.Tasks.Count})");
            badge.style.flexShrink = 0;
            badge.style.color = pct == 100 ? DoneColor : MutedColor;
            badge.style.unityFontStyleAndWeight = FontStyle.Bold;
            row.Add(badge);

            return row;
        }

        const int MaxPriority = 5;

        static VisualElement TaskRow(Task task, Action<int, bool> onToggled, Action<int, int> onSetPriority, string fileRel)
        {
            var isActive = fileRel != null && ActiveTask.IsActive(fileRel, task.Text);

            var row = new VisualElement
            {
                style =
                {
                    flexDirection = FlexDirection.Row, alignItems = Align.FlexStart,
                    marginLeft = 12, marginBottom = 2,
                    paddingLeft = 4, paddingTop = 1, paddingBottom = 1,
                    borderTopLeftRadius = 3, borderBottomLeftRadius = 3,
                    backgroundColor = isActive ? new Color(AccentColor.r, AccentColor.g, AccentColor.b, 0.18f) : Color.clear
                }
            };

            // Right-click a task: set priority (0–5; 0 clears the {P:n} marker) and pick the
            // active task. Priority entries need the write-back callback; the active-task
            // entries only need the file rel-path.
            row.AddManipulator(new ContextualMenuManipulator(evt =>
            {
                if (onSetPriority != null)
                {
                    var line = task.Line;
                    var current = task.Priority;
                    for (var p = 0; p <= MaxPriority; p++)
                    {
                        var value = p;
                        evt.menu.AppendAction(
                            p == 0 ? "Priority/0 (none)" : $"Priority/{p}",
                            _ => onSetPriority(line, value),
                            current == value ? DropdownMenuAction.Status.Checked : DropdownMenuAction.Status.Normal);
                    }
                }

                if (fileRel != null)
                {
                    if (isActive)
                        evt.menu.AppendAction("Clear Active Task", _ => ActiveTask.Clear());
                    else
                        evt.menu.AppendAction("Set as Active Task", _ => ActiveTask.Set(fileRel, task.Text));
                }
            }));

            var toggle = new Toggle { value = task.Done, style = { marginRight = 4, marginTop = 1 } };
            toggle.SetEnabled(onToggled != null);
            if (onToggled != null)
            {
                var line = task.Line;
                toggle.RegisterValueChangedCallback(evt => onToggled(line, evt.newValue));
            }
            row.Add(toggle);

            if (task.Priority != 0)
                row.Add(PriorityBadge(task.Priority));

            var label = PlainLabel(task.Text);
            label.style.flexGrow = 1;
            if (task.Done)
            {
                label.style.color = MutedColor;
                label.style.unityFontStyleAndWeight = FontStyle.Italic;
            }
            row.Add(label);
            return row;
        }

        static VisualElement PriorityBadge(int priority)
        {
            var badge = new Label($"P{priority}") { enableRichText = false };
            badge.style.fontSize = 10;
            badge.style.unityFontStyleAndWeight = FontStyle.Bold;
            badge.style.color = Color.white;
            badge.style.backgroundColor = priority > 0 ? AccentColor : MutedColor;
            badge.style.paddingLeft = 4; badge.style.paddingRight = 4;
            badge.style.marginRight = 5; badge.style.marginTop = 1;
            badge.style.borderTopLeftRadius = badge.style.borderTopRightRadius =
                badge.style.borderBottomLeftRadius = badge.style.borderBottomRightRadius = 3;
            badge.style.flexShrink = 0;
            return badge;
        }

        static Label PlainLabel(string text)
        {
            var label = new Label(text) { enableRichText = false };
            label.style.whiteSpace = WhiteSpace.Normal;
            label.selection.isSelectable = true;
            return label;
        }

        static string Clean(string text)
        {
            text = InlineCode.Replace(text, "$1");
            text = Link.Replace(text, "$1");
            text = Emphasis.Replace(text, "$2");
            return text;
        }
    }
}
