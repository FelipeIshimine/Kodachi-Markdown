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
    ///
    /// Indented lines following a task are parsed as subtasks (if they are checkboxes) or
    /// as a description (plain text), displayed below the task row.
    /// </summary>
    public static class TaskView
    {
        static readonly Regex Heading    = new(@"^(#{1,6})\s+(.*)$", RegexOptions.Compiled);
        static readonly Regex Checkbox   = new(@"^(\s*)[-*+]\s+\[([ xX])\]\s+(.*)$", RegexOptions.Compiled);
        static readonly Regex Priority   = new(@"\{[Pp]:(-?\d+)\}", RegexOptions.Compiled);
        static readonly Regex InlineCode = new("`([^`]+)`", RegexOptions.Compiled);
        static readonly Regex Emphasis   = new(@"(\*\*|\*)(.+?)\1", RegexOptions.Compiled);
        static readonly Regex Link       = new(@"\[([^\]]+)\]\(([^)]+)\)", RegexOptions.Compiled);

        static readonly Color MutedColor  = new(1f, 1f, 1f, 0.55f);
        static readonly Color AccentColor = new(0.255f, 0.490f, 0.965f);
        static readonly Color DoneColor   = new(0.45f, 0.78f, 0.45f);

        sealed class Task
        {
            public int Line;                              // source line index
            public int LastLine;                          // last consumed line (subtasks/description)
            public bool Done;
            public int Priority;
            public string Text;                           // cleaned display text
            public string RawLine;                        // original source line, for write-back
            public string Description;                    // joined plain-text continuation
            public readonly List<int> DescriptionLines = new();  // source line indices of description
            public readonly List<Task> Subtasks = new();
        }

        sealed class Section
        {
            public string Title;
            public int Level;
            public int HeadingLine = -1;                  // -1 for the implicit root section
            public readonly List<Task> Tasks = new();

            /// <summary>Last source line that belongs to this section (for insert-after).</summary>
            public int DocumentLastLine => Tasks.Count > 0
                ? Tasks.OrderBy(t => t.Line).Last().LastLine
                : HeadingLine;
        }

        // ── Public API ──────────────────────────────────────────────────────────────

        /// <param name="onToggled">Checkbox toggled (lineIndex, newValue).</param>
        /// <param name="onSetPriority">Priority changed via right-click (lineIndex, priority).</param>
        /// <param name="onReplaceLine">Replace one raw source line (lineIndex, newRawLine).</param>
        /// <param name="onInsertAfter">Insert raw source lines after a line (lineIndex, newLines).</param>
        /// <param name="onEditDescription">
        /// Replace description for a task (taskLineIndex, descLineIndices, newDescriptionText).
        /// Pass empty descLineIndices to add a new description.
        /// </param>
        /// <param name="fileRel">Rel-path used for ActiveTask matching.</param>
        public static void Populate(VisualElement container, string markdown,
            Action<int, bool> onToggled = null,
            Action<int, int> onSetPriority = null,
            Action<int, string> onReplaceLine = null,
            Action<int, string[]> onInsertAfter = null,
            Action<int, int[], string> onEditDescription = null,
            string fileRel = null)
        {
            container.Clear();
            if (string.IsNullOrEmpty(markdown)) return;

            var (sections, totalLines) = Parse(markdown);
            var editable = onInsertAfter != null;

            // Active task banner (belongs to this file).
            if (fileRel != null && ActiveTask.HasActive && ActiveTask.Rel == fileRel)
                container.Add(ActiveCard());

            var anyTasks = sections.Any(s => s.Tasks.Count > 0);
            if (!anyTasks && !editable)
            {
                var empty = PlainLabel("No tasks found.\n\nAdd a line like \"- [ ] Do the thing {P:1}\".");
                empty.style.color = MutedColor;
                empty.style.whiteSpace = WhiteSpace.Normal;
                container.Add(empty);
                return;
            }

            foreach (var section in sections)
            {
                if (!editable && section.Tasks.Count == 0) continue;

                // Section heading (skip null-title root if it has no tasks and a heading exists later).
                if (section.Title != null || section.Tasks.Count > 0)
                    container.Add(SectionHeader(section));

                var ordered = section.Tasks
                    .OrderBy(t => t.Done)
                    .ThenByDescending(t => t.Priority);

                foreach (var task in ordered)
                    container.Add(TaskRow(task, onToggled, onSetPriority,
                        onReplaceLine, onEditDescription, fileRel));

                if (editable)
                    container.Add(AddTaskFooter(section, onInsertAfter));
            }

            if (editable)
                container.Add(AddSectionFooter(totalLines, onInsertAfter));
        }

        // ── Parse ───────────────────────────────────────────────────────────────────

        static (List<Section> sections, int totalLines) Parse(string markdown)
        {
            var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var sections = new List<Section>();
            var current = new Section { Title = null, HeadingLine = -1 };
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
                    current = new Section
                    {
                        Title = Clean(h.Groups[2].Value).Trim(),
                        Level = h.Groups[1].Value.Length,
                        HeadingLine = i,
                    };
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

                var task = new Task
                {
                    Line = i,
                    LastLine = i,
                    Done = c.Groups[2].Value is "x" or "X",
                    Priority = priority,
                    Text = Clean(text),
                    RawLine = line,
                };

                // Consume indented continuation lines.
                var parentIndent = c.Groups[1].Value.Length;
                var descLines = new List<string>();
                while (i + 1 < lines.Length)
                {
                    var next = lines[i + 1];
                    if (string.IsNullOrWhiteSpace(next)) break;
                    var nextIndent = next.Length - next.TrimStart('\t', ' ').Length;
                    if (nextIndent <= parentIndent) break;

                    i++;
                    task.LastLine = i;

                    var sub = Checkbox.Match(next);
                    if (sub.Success)
                    {
                        var subText = sub.Groups[3].Value;
                        var subPriority = 0;
                        var sp = Priority.Match(subText);
                        if (sp.Success && int.TryParse(sp.Groups[1].Value, out var subParsed))
                            subPriority = subParsed;
                        subText = Priority.Replace(subText, "").Trim();
                        task.Subtasks.Add(new Task
                        {
                            Line = i, LastLine = i,
                            Done = sub.Groups[2].Value is "x" or "X",
                            Priority = subPriority,
                            Text = Clean(subText),
                            RawLine = next,
                        });
                    }
                    else
                    {
                        descLines.Add(next.TrimStart());
                        task.DescriptionLines.Add(i);
                    }
                }

                if (descLines.Count > 0)
                    task.Description = string.Join("\n", descLines);

                current.Tasks.Add(task);
            }

            return (sections, lines.Length - 1);
        }

        // ── Active task card ─────────────────────────────────────────────────────────

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
            pause.clicked += () => ActiveTask.TogglePause();
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
            card.schedule.Execute(Tick).Every(500);
            return card;
        }

        // ── Section header ───────────────────────────────────────────────────────────

        static VisualElement SectionHeader(Section section)
        {
            var row = new VisualElement
            {
                style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginTop = 8, marginBottom = 4 }
            };

            var title = PlainLabel(string.IsNullOrEmpty(section.Title) ? "Tasks" : section.Title);
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.fontSize = section.Level switch { 0 => 15, 1 => 18, 2 => 16, 3 => 14, _ => 13 };
            title.style.flexGrow = 1;
            row.Add(title);

            if (section.Tasks.Count > 0)
            {
                var done = section.Tasks.Count(t => t.Done);
                var pct  = Mathf.RoundToInt(100f * done / section.Tasks.Count);
                var badge = PlainLabel($"{pct}%  ({done}/{section.Tasks.Count})");
                badge.style.flexShrink = 0;
                badge.style.color = pct == 100 ? DoneColor : MutedColor;
                badge.style.unityFontStyleAndWeight = FontStyle.Bold;
                row.Add(badge);
            }

            return row;
        }

        // ── Task row ─────────────────────────────────────────────────────────────────

        const int MaxPriority = 5;

        static VisualElement TaskRow(Task task,
            Action<int, bool> onToggled,
            Action<int, int> onSetPriority,
            Action<int, string> onReplaceLine,
            Action<int, int[], string> onEditDescription,
            string fileRel)
        {
            var isActive = fileRel != null && ActiveTask.IsActive(fileRel, task.Text);

            var wrapper = new VisualElement
            {
                style =
                {
                    marginLeft = 12, marginBottom = 4,
                    paddingLeft = 4, paddingTop = 1, paddingBottom = 1,
                    borderTopLeftRadius = 3, borderBottomLeftRadius = 3,
                    backgroundColor = isActive
                        ? new Color(AccentColor.r, AccentColor.g, AccentColor.b, 0.18f)
                        : Color.clear
                }
            };

            var row = new VisualElement
            {
                style = { flexDirection = FlexDirection.Row, alignItems = Align.FlexStart }
            };

            row.AddManipulator(new ContextualMenuManipulator(evt =>
            {
                // Priority submenu
                if (onSetPriority != null)
                {
                    var line = task.Line;
                    var cur  = task.Priority;
                    for (var pp = 0; pp <= MaxPriority; pp++)
                    {
                        var value = pp;
                        evt.menu.AppendAction(
                            pp == 0 ? "Priority/0 (none)" : $"Priority/{pp}",
                            _ => onSetPriority(line, value),
                            cur == value ? DropdownMenuAction.Status.Checked : DropdownMenuAction.Status.Normal);
                    }
                }

                // Active task
                if (fileRel != null)
                {
                    if (isActive)
                        evt.menu.AppendAction("Clear Active Task", _ => ActiveTask.Clear());
                    else
                        evt.menu.AppendAction("Set as Active Task", _ => ActiveTask.Set(fileRel, task.Text));
                }

                // Rename
                if (onReplaceLine != null)
                {
                    evt.menu.AppendSeparator();
                    evt.menu.AppendAction("Rename task", _ =>
                    {
                        var label = row.Q<Label>();
                        if (label != null) BeginInlineEdit(label, row, task.Text,
                            newText => CommitRename(task, newText, onReplaceLine));
                    });
                }

                // Description
                if (onEditDescription != null)
                {
                    if (string.IsNullOrEmpty(task.Description))
                    {
                        evt.menu.AppendAction("Add description", _ =>
                            BeginDescriptionEdit(wrapper, task, "", onEditDescription));
                    }
                    else
                    {
                        evt.menu.AppendAction("Edit description", _ =>
                        {
                            var descLabel = wrapper.Q<Label>(DescLabelName);
                            if (descLabel != null)
                                BeginInlineEdit(descLabel, wrapper, task.Description,
                                    newText => CommitDescription(task, newText, onEditDescription));
                            else
                                BeginDescriptionEdit(wrapper, task, task.Description, onEditDescription);
                        });
                    }
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

            var taskLabel = PlainLabel(task.Text);
            taskLabel.style.flexGrow = 1;
            if (task.Done)
            {
                taskLabel.style.color = MutedColor;
                taskLabel.style.unityFontStyleAndWeight = FontStyle.Italic;
            }

            // Double-click to rename
            if (onReplaceLine != null)
            {
                taskLabel.RegisterCallback<MouseDownEvent>(e =>
                {
                    if (e.clickCount == 2)
                    {
                        e.StopPropagation();
                        BeginInlineEdit(taskLabel, row, task.Text,
                            newText => CommitRename(task, newText, onReplaceLine));
                    }
                });
            }

            row.Add(taskLabel);
            wrapper.Add(row);

            // Description
            if (!string.IsNullOrEmpty(task.Description))
            {
                var descLabel = PlainLabel(task.Description);
                descLabel.name = DescLabelName;
                descLabel.style.color = MutedColor;
                descLabel.style.fontSize = 11;
                descLabel.style.marginLeft = 40;
                descLabel.style.marginTop = 1;
                descLabel.style.marginBottom = 2;

                if (onEditDescription != null)
                {
                    descLabel.RegisterCallback<MouseDownEvent>(e =>
                    {
                        if (e.clickCount == 2)
                        {
                            e.StopPropagation();
                            BeginInlineEdit(descLabel, wrapper, task.Description,
                                newText => CommitDescription(task, newText, onEditDescription));
                        }
                    });
                }

                wrapper.Add(descLabel);
            }

            foreach (var sub in task.Subtasks)
                wrapper.Add(SubtaskRow(sub, onToggled));

            return wrapper;
        }

        const string DescLabelName = "__desc_label";

        static void CommitRename(Task task, string newText, Action<int, string> onReplaceLine)
        {
            if (newText == null || newText.Trim() == task.Text) return;
            newText = newText.Trim();
            if (string.IsNullOrEmpty(newText)) return;

            // Rebuild the raw line: preserve indent, bullet, checkbox state, and priority marker.
            var m = Checkbox.Match(task.RawLine);
            if (!m.Success) return;
            var prefix = task.RawLine.Substring(0, m.Groups[3].Index);
            var priorityMatch = Priority.Match(m.Groups[3].Value);
            var prioritySuffix = priorityMatch.Success ? " " + priorityMatch.Value : "";
            onReplaceLine(task.Line, prefix + newText + prioritySuffix);
        }

        static void CommitDescription(Task task, string newText, Action<int, int[], string> onEditDescription)
        {
            if (newText == null) return;
            onEditDescription(task.Line, task.DescriptionLines.ToArray(), newText.Trim());
        }

        static void BeginDescriptionEdit(VisualElement wrapper, Task task, string initial,
            Action<int, int[], string> onEditDescription)
        {
            // Find the description label slot (after the main row, before subtasks).
            var descLabel = wrapper.Q<Label>(DescLabelName);
            if (descLabel != null)
                BeginInlineEdit(descLabel, wrapper, initial,
                    newText => CommitDescription(task, newText, onEditDescription));
            else
            {
                // No description yet — inject a temporary TextField after the first row.
                var field = new TextField { value = initial, multiline = true };
                field.style.marginLeft = 22;
                field.style.marginTop = 2;
                field.style.marginBottom = 2;
                field.style.flexGrow = 1;

                var committed = false;
                void Commit(string val)
                {
                    if (committed) return;
                    committed = true;
                    CommitDescription(task, val ?? "", onEditDescription);
                }
                field.RegisterCallback<KeyDownEvent>(e =>
                {
                    if ((e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter) && !e.shiftKey)
                    { e.StopPropagation(); Commit(field.value); }
                    else if (e.keyCode == KeyCode.Escape)
                    { e.StopPropagation(); Commit(null); }
                });
                field.RegisterCallback<FocusOutEvent>(_ => Commit(field.value));

                // Insert after the main task row (index 0).
                wrapper.Insert(1, field);
                wrapper.schedule.Execute(() => { field.Focus(); }).StartingIn(10);
            }
        }

        // ── Subtask row ──────────────────────────────────────────────────────────────

        static VisualElement SubtaskRow(Task sub, Action<int, bool> onToggled)
        {
            var row = new VisualElement
            {
                style = { flexDirection = FlexDirection.Row, alignItems = Align.FlexStart, marginLeft = 22, marginBottom = 1 }
            };

            var toggle = new Toggle { value = sub.Done, style = { marginRight = 4, marginTop = 1 } };
            toggle.SetEnabled(onToggled != null);
            if (onToggled != null)
            {
                var line = sub.Line;
                toggle.RegisterValueChangedCallback(evt => onToggled(line, evt.newValue));
            }
            row.Add(toggle);

            var label = PlainLabel(sub.Text);
            label.style.flexGrow = 1;
            label.style.fontSize = 12;
            if (sub.Done)
            {
                label.style.color = MutedColor;
                label.style.unityFontStyleAndWeight = FontStyle.Italic;
            }
            row.Add(label);
            return row;
        }

        // ── Add task / Add section footers ────────────────────────────────────────────

        static VisualElement AddTaskFooter(Section section, Action<int, string[]> onInsertAfter)
        {
            var footer = new VisualElement
            {
                style = { flexDirection = FlexDirection.Row, marginLeft = 12, marginTop = 2, marginBottom = 6 }
            };

            Button btn = null;
            btn = MutedButton("+ Add task", () =>
            {
                footer.Clear();
                ShowInlineInsert(footer, btn,
                    val => onInsertAfter(section.DocumentLastLine, new[] { "- [ ] " + val }));
            });
            footer.Add(btn);
            return footer;
        }

        static VisualElement AddSectionFooter(int totalLines, Action<int, string[]> onInsertAfter)
        {
            var footer = new VisualElement
            {
                style = { marginTop = 8, marginBottom = 4, marginLeft = 4 }
            };

            Button btn = null;
            btn = MutedButton("+ Add section", () =>
            {
                footer.Clear();
                ShowInlineInsert(footer, btn,
                    val => onInsertAfter(totalLines, new[] { "", "## " + val }));
            });
            footer.Add(btn);
            return footer;
        }

        /// <summary>
        /// Replaces <paramref name="footer"/>'s content with a TextField; on commit calls
        /// <paramref name="onCommit"/> with the trimmed value, or restores <paramref name="restoreBtn"/>
        /// if cancelled or empty.
        /// </summary>
        static void ShowInlineInsert(VisualElement footer, Button restoreBtn, Action<string> onCommit)
        {
            var field = new TextField();
            field.style.flexGrow = 1;
            footer.Add(field);

            var committed = false;
            void Commit(string val)
            {
                if (committed) return;
                committed = true;
                val = val?.Trim();
                if (!string.IsNullOrEmpty(val))
                    onCommit(val);
                else
                {
                    footer.Clear();
                    footer.Add(restoreBtn);
                }
            }

            field.RegisterCallback<KeyDownEvent>(e =>
            {
                if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)
                { e.StopPropagation(); Commit(field.value); }
                else if (e.keyCode == KeyCode.Escape)
                { e.StopPropagation(); Commit(null); }
            });
            field.RegisterCallback<FocusOutEvent>(_ => Commit(field.value));
            footer.schedule.Execute(() => field.Focus()).StartingIn(10);
        }

        // ── Inline edit helper ───────────────────────────────────────────────────────

        /// <summary>
        /// Swaps <paramref name="label"/> for a TextField in-place; on commit/cancel calls
        /// <paramref name="onCommit"/> with the new text (or <c>null</c> on Escape).
        /// The caller's callback is responsible for triggering a re-render.
        /// </summary>
        static void BeginInlineEdit(Label label, VisualElement parent, string initial, Action<string> onCommit)
        {
            var field = new TextField { value = initial, multiline = label.style.whiteSpace == WhiteSpace.Normal };
            field.style.flexGrow = 1;

            var idx = parent.IndexOf(label);
            parent.RemoveAt(idx);
            parent.Insert(idx, field);

            var committed = false;
            void Commit(string val)
            {
                if (committed) return;
                committed = true;
                onCommit(val);
            }

            field.RegisterCallback<KeyDownEvent>(e =>
            {
                if ((e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter) && !e.shiftKey)
                { e.StopPropagation(); Commit(field.value); }
                else if (e.keyCode == KeyCode.Escape)
                { e.StopPropagation(); Commit(null); }
            });
            field.RegisterCallback<FocusOutEvent>(_ => Commit(field.value));
            parent.schedule.Execute(() => { field.Focus(); field.SelectAll(); }).StartingIn(10);
        }

        // ── Visual helpers ───────────────────────────────────────────────────────────

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

        static Button MutedButton(string text, Action onClick)
        {
            var btn = new Button(onClick) { text = text };
            btn.style.fontSize = 11;
            btn.style.color = MutedColor;
            btn.style.backgroundColor = new Color(0, 0, 0, 0);
            btn.style.borderTopWidth = btn.style.borderRightWidth =
                btn.style.borderBottomWidth = btn.style.borderLeftWidth = 0;
            btn.style.paddingLeft = btn.style.paddingRight = 4;
            btn.style.paddingTop = btn.style.paddingBottom = 2;
            btn.RegisterCallback<PointerEnterEvent>(_ =>
            {
                btn.style.color = Color.white;
                btn.style.backgroundColor = new Color(AccentColor.r, AccentColor.g, AccentColor.b, 0.15f);
            });
            btn.RegisterCallback<PointerLeaveEvent>(_ =>
            {
                btn.style.color = MutedColor;
                btn.style.backgroundColor = new Color(0, 0, 0, 0);
            });
            return btn;
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
