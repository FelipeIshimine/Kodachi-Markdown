using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.UIElements;

namespace KodachiGames.Markdown.Editor
{
    /// <summary>
    /// Renders a Markdown document as a prioritised, arbitrarily-nested task tree.
    ///
    /// Every task-list checkbox (<c>- [ ]</c> / <c>- [x]</c>) is collected under the most
    /// recent heading. Indented checkboxes become child tasks (infinite depth); indented
    /// non-checkbox lines become the parent task's description.
    ///
    /// A "composite" task (one with children) cannot be toggled manually — it tracks done
    /// state from its children. A "leaf" task is toggled directly.
    ///
    /// Tasks carry an optional priority marker <c>{P:n}</c> (higher = more important).
    /// Within a section, tasks are ordered uncompleted-first, then by descending priority.
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

        // ── Data model ───────────────────────────────────────────────────────────────

        sealed class Task
        {
            public int Line;
            public int LastLine;
            public bool Done;
            public int Priority;
            public string Text;
            public string RawLine;
            public string Description;
            public readonly List<int> DescriptionLines = new();
            public readonly List<Task> Children = new();

            public bool IsComposite        => Children.Count > 0;
            public bool IsEffectivelyDone  => IsComposite ? Children.All(c => c.IsEffectivelyDone) : Done;

            public int LeafCount => IsComposite ? Children.Sum(c => c.LeafCount) : 1;
            public int LeafDone  => IsComposite ? Children.Sum(c => c.LeafDone)  : (Done ? 1 : 0);
        }

        sealed class Section
        {
            public string Title;
            public int Level;
            public int HeadingLine = -1;
            public readonly List<Task> Tasks = new();

            public int DocumentLastLine => Tasks.Count > 0
                ? Tasks.OrderBy(t => t.Line).Last().LastLine
                : HeadingLine;
        }

        // ── Callbacks passed through the whole tree ───────────────────────────────────

        sealed class Callbacks
        {
            public Action<int, bool>      OnToggled;
            public Action<int, int>       OnSetPriority;
            public Action<int, string>    OnReplaceLine;
            public Action<int, string[]>  OnInsertAfter;
            public Action<int, int[], string> OnEditDescription;
            public Action<int, int>       OnDeleteRange;
            public string                 FileRel;
        }

        // ── Public API ───────────────────────────────────────────────────────────────

        public static void Populate(VisualElement container, string markdown,
            Action<int, bool>           onToggled          = null,
            Action<int, int>            onSetPriority      = null,
            Action<int, string>         onReplaceLine      = null,
            Action<int, string[]>       onInsertAfter      = null,
            Action<int, int[], string>  onEditDescription  = null,
            Action<int, int>            onDeleteRange      = null,
            string                      fileRel            = null)
        {
            container.Clear();
            if (string.IsNullOrEmpty(markdown)) return;

            var cb = new Callbacks
            {
                OnToggled         = onToggled,
                OnSetPriority     = onSetPriority,
                OnReplaceLine     = onReplaceLine,
                OnInsertAfter     = onInsertAfter,
                OnEditDescription = onEditDescription,
                OnDeleteRange     = onDeleteRange,
                FileRel           = fileRel,
            };

            var (sections, totalLines) = Parse(markdown);
            var editable = onInsertAfter != null;

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

                if (section.Title != null || section.Tasks.Count > 0)
                    container.Add(SectionHeader(section, cb));

                var ordered = section.Tasks
                    .OrderBy(t => t.IsEffectivelyDone)
                    .ThenByDescending(t => t.Priority);

                foreach (var task in ordered)
                    container.Add(TaskRow(task, depth: 0, cb));

                if (editable && section.Title != null)
                    container.Add(AddChildFooter(section.DocumentLastLine, indentDepth: 0, cb.OnInsertAfter));
            }

            if (editable)
                container.Add(AddSectionFooter(totalLines, cb.OnInsertAfter));
        }

        // ── Parse ────────────────────────────────────────────────────────────────────

        static (List<Section> sections, int totalLines) Parse(string markdown)
        {
            var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var sections = new List<Section>();
            var current = new Section { Title = null, HeadingLine = -1 };
            sections.Add(current);

            var inFence = false;
            var i = 0;
            while (i < lines.Length)
            {
                var line = lines[i];
                if (line.TrimStart().StartsWith("```")) { inFence = !inFence; i++; continue; }
                if (inFence) { i++; continue; }

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
                    i++;
                    continue;
                }

                var c = Checkbox.Match(line);
                if (c.Success && c.Groups[1].Value.Length == 0)
                {
                    current.Tasks.Add(ParseTask(lines, ref i));
                }
                else
                {
                    i++;
                }
            }

            return (sections, lines.Length - 1);
        }

        static Task ParseTask(string[] lines, ref int i)
        {
            var line = lines[i];
            var c = Checkbox.Match(line);
            var rawText = c.Groups[3].Value;
            var priority = 0;
            var p = Priority.Match(rawText);
            if (p.Success && int.TryParse(p.Groups[1].Value, out var parsed))
                priority = parsed;
            rawText = Priority.Replace(rawText, "").Trim();

            var taskIndent = c.Groups[1].Value.Length;
            var task = new Task
            {
                Line = i, LastLine = i,
                Done = c.Groups[2].Value is "x" or "X",
                Priority = priority,
                Text = Clean(rawText),
                RawLine = line,
            };
            i++;

            while (i < lines.Length)
            {
                var next = lines[i];
                if (string.IsNullOrWhiteSpace(next)) break;
                var nextIndent = next.Length - next.TrimStart('\t', ' ').Length;
                if (nextIndent <= taskIndent) break;

                if (Checkbox.IsMatch(next))
                {
                    var child = ParseTask(lines, ref i);
                    task.Children.Add(child);
                    task.LastLine = child.LastLine;
                }
                else
                {
                    task.DescriptionLines.Add(i);
                    task.Description = task.Description == null
                        ? next.TrimStart()
                        : task.Description + "\n" + next.TrimStart();
                    task.LastLine = i;
                    i++;
                }
            }

            return task;
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

            var controls = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center } };
            var timer = new Label { enableRichText = false };
            timer.style.fontSize = 18;
            timer.style.unityFontStyleAndWeight = FontStyle.Bold;
            timer.style.flexGrow = 1;
            controls.Add(timer);

            var pause = new Button { style = { marginLeft = 4 } };
            pause.clicked += ActiveTask.TogglePause;
            controls.Add(pause);
            controls.Add(new Button(ActiveTask.Clear) { text = "Clear", style = { marginLeft = 2 } });
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

        static VisualElement SectionHeader(Section section, Callbacks cb)
        {
            var row = new VisualElement
            {
                style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginTop = 8, marginBottom = 4 }
            };

            var displayTitle = string.IsNullOrEmpty(section.Title) ? "Tasks" : section.Title;
            var title = PlainLabel(displayTitle);
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.fontSize = section.Level switch { 0 => 15, 1 => 18, 2 => 16, 3 => 14, _ => 13 };
            title.style.flexGrow = 1;

            if (cb.OnReplaceLine != null && section.HeadingLine >= 0)
            {
                title.tooltip = "Double-click to rename";
                title.RegisterCallback<MouseDownEvent>(e =>
                {
                    if (e.clickCount != 2) return;
                    e.StopPropagation();
                    BeginInlineEdit(title, row, displayTitle, newText =>
                    {
                        if (string.IsNullOrWhiteSpace(newText) || newText.Trim() == displayTitle) return;
                        cb.OnReplaceLine(section.HeadingLine, new string('#', section.Level) + " " + newText.Trim());
                    });
                });
            }
            row.Add(title);

            if (section.Tasks.Count > 0)
            {
                var leafDone  = section.Tasks.Sum(t => t.LeafDone);
                var leafTotal = section.Tasks.Sum(t => t.LeafCount);
                var pct = Mathf.RoundToInt(100f * leafDone / leafTotal);
                var badge = PlainLabel($"{pct}%  ({leafDone}/{leafTotal})");
                badge.style.flexShrink = 0;
                badge.style.color = pct == 100 ? DoneColor : MutedColor;
                badge.style.unityFontStyleAndWeight = FontStyle.Bold;
                row.Add(badge);
            }

            return row;
        }

        // ── Task row (recursive) ─────────────────────────────────────────────────────

        const int MaxPriority  = 5;
        const int IndentPerLevel = 16;
        const string DescLabelName = "__desc_label";

        static VisualElement TaskRow(Task task, int depth, Callbacks cb)
        {
            var effectiveDone = task.IsEffectivelyDone;
            var isActive = cb.FileRel != null && ActiveTask.IsActive(cb.FileRel, task.Text);

            var wrapper = new VisualElement
            {
                style =
                {
                    marginLeft = depth == 0 ? 12 : IndentPerLevel,
                    marginBottom = depth == 0 ? 4 : 2,
                    paddingLeft = 4, paddingTop = 1, paddingBottom = 1,
                    borderTopLeftRadius = 3, borderBottomLeftRadius = 3,
                    backgroundColor = isActive
                        ? new Color(AccentColor.r, AccentColor.g, AccentColor.b, 0.18f)
                        : Color.clear
                }
            };

            var row = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.FlexStart } };

            // ── Context menu ─────────────────────────────────────────────
            row.AddManipulator(new ContextualMenuManipulator(evt =>
            {
                if (cb.OnSetPriority != null && !task.IsComposite)
                {
                    var cur = task.Priority;
                    for (var pp = 0; pp <= MaxPriority; pp++)
                    {
                        var value = pp;
                        evt.menu.AppendAction(
                            pp == 0 ? "Priority/0 (none)" : $"Priority/{pp}",
                            _ => cb.OnSetPriority(task.Line, value),
                            cur == value ? DropdownMenuAction.Status.Checked : DropdownMenuAction.Status.Normal);
                    }
                }

                if (cb.FileRel != null && !task.IsComposite)
                {
                    if (isActive)
                        evt.menu.AppendAction("Clear Active Task", _ => ActiveTask.Clear());
                    else
                        evt.menu.AppendAction("Set as Active Task", _ => ActiveTask.Set(cb.FileRel, task.Text));
                }

                if (cb.OnReplaceLine != null || cb.OnInsertAfter != null)
                    evt.menu.AppendSeparator();

                if (cb.OnReplaceLine != null)
                    evt.menu.AppendAction("Rename", _ =>
                    {
                        var lbl = row.Q<Label>();
                        if (lbl != null) BeginInlineEdit(lbl, row, task.Text,
                            v => CommitRename(task, v, cb.OnReplaceLine));
                    });

                if (cb.OnInsertAfter != null)
                    evt.menu.AppendAction("Add child task", _ =>
                        ShowChildInsert(wrapper, task, cb.OnInsertAfter));

                if (cb.OnEditDescription != null)
                {
                    evt.menu.AppendSeparator();
                    if (string.IsNullOrEmpty(task.Description))
                        evt.menu.AppendAction("Add description", _ =>
                            BeginDescriptionEdit(wrapper, task, "", cb.OnEditDescription));
                    else
                        evt.menu.AppendAction("Edit description", _ =>
                        {
                            var dl = wrapper.Q<Label>(DescLabelName);
                            if (dl != null)
                                BeginInlineEdit(dl, wrapper, task.Description,
                                    v => CommitDescription(task, v, cb.OnEditDescription));
                            else
                                BeginDescriptionEdit(wrapper, task, task.Description, cb.OnEditDescription);
                        });
                }

                if (cb.OnDeleteRange != null)
                {
                    evt.menu.AppendSeparator();
                    evt.menu.AppendAction("Remove", _ => cb.OnDeleteRange(task.Line, task.LastLine));
                }
            }));

            // ── Checkbox ──────────────────────────────────────────────────
            var toggle = new Toggle { value = effectiveDone, style = { marginRight = 4, marginTop = 1 } };
            // Composite tasks are auto-completed by children — toggle is read-only.
            toggle.SetEnabled(!task.IsComposite && cb.OnToggled != null);
            if (!task.IsComposite && cb.OnToggled != null)
                toggle.RegisterValueChangedCallback(evt => cb.OnToggled(task.Line, evt.newValue));
            row.Add(toggle);

            if (task.Priority != 0)
                row.Add(PriorityBadge(task.Priority));

            // ── Label ─────────────────────────────────────────────────────
            var taskLabel = PlainLabel(task.Text);
            taskLabel.style.flexGrow = 1;
            if (effectiveDone)
            {
                taskLabel.style.color = MutedColor;
                taskLabel.style.unityFontStyleAndWeight = FontStyle.Italic;
            }

            if (cb.OnReplaceLine != null)
                taskLabel.RegisterCallback<MouseDownEvent>(e =>
                {
                    if (e.clickCount != 2) return;
                    e.StopPropagation();
                    BeginInlineEdit(taskLabel, row, task.Text, v => CommitRename(task, v, cb.OnReplaceLine));
                });

            if (!string.IsNullOrEmpty(task.Description))
                taskLabel.tooltip = task.Description;

            row.Add(taskLabel);

            // ── Child progress badge ──────────────────────────────────────
            if (task.IsComposite)
            {
                var leafDone  = task.LeafDone;
                var leafTotal = task.LeafCount;
                var pct = Mathf.RoundToInt(100f * leafDone / leafTotal);
                var badge = PlainLabel($"{leafDone}/{leafTotal}");
                badge.style.flexShrink = 0;
                badge.style.fontSize = 10;
                badge.style.color = pct == 100 ? DoneColor : MutedColor;
                badge.style.marginLeft = 6;
                badge.style.marginTop = 2;
                row.Add(badge);
            }

            // ── Hover "add child" button ──────────────────────────────────
            if (cb.OnInsertAfter != null)
            {
                var addBtn = MutedButton("+", () => ShowChildInsert(wrapper, task, cb.OnInsertAfter));
                addBtn.tooltip = "Add child task";
                addBtn.style.display = DisplayStyle.None;
                addBtn.style.flexShrink = 0;
                addBtn.style.paddingLeft = addBtn.style.paddingRight = 3;
                addBtn.style.paddingTop =
	                addBtn.style.paddingBottom = 
		                addBtn.style.marginBottom = 
			                addBtn.style.marginTop = 0;
                row.Add(addBtn);

                row.RegisterCallback<PointerEnterEvent>(_ => addBtn.style.display = DisplayStyle.Flex);
                row.RegisterCallback<PointerLeaveEvent>(_ => addBtn.style.display = DisplayStyle.None);
            }

            wrapper.Add(row);

            // ── Description (hidden when done) ────────────────────────────
            if (!string.IsNullOrEmpty(task.Description) && !effectiveDone)
            {
                var descLabel = PlainLabel(task.Description);
                descLabel.name = DescLabelName;
                descLabel.style.color = MutedColor;
                descLabel.style.fontSize = 11;
                descLabel.style.marginLeft = 22;
                descLabel.style.marginTop = 1;
                descLabel.style.marginBottom = 2;

                if (cb.OnEditDescription != null)
                    descLabel.RegisterCallback<MouseDownEvent>(e =>
                    {
                        if (e.clickCount != 2) return;
                        e.StopPropagation();
                        BeginInlineEdit(descLabel, wrapper, task.Description,
                            v => CommitDescription(task, v, cb.OnEditDescription));
                    });

                wrapper.Add(descLabel);
            }

            // ── Children (recursive) ──────────────────────────────────────
            var orderedChildren = task.Children
                .OrderBy(c => c.IsEffectivelyDone)
                .ThenByDescending(c => c.Priority);

            foreach (var child in orderedChildren)
                wrapper.Add(TaskRow(child, depth + 1, cb));

            return wrapper;
        }

        // ── Commit helpers ────────────────────────────────────────────────────────────

        static void CommitRename(Task task, string newText, Action<int, string> onReplaceLine)
        {
            if (newText == null) return;
            newText = newText.Trim();
            if (string.IsNullOrEmpty(newText) || newText == task.Text) return;

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
            var dl = wrapper.Q<Label>(DescLabelName);
            if (dl != null)
            {
                BeginInlineEdit(dl, wrapper, initial, v => CommitDescription(task, v, onEditDescription));
                return;
            }

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
            wrapper.Insert(1, field);
            wrapper.schedule.Execute(() => field.Focus()).StartingIn(10);
        }

        // ── Insert helpers ────────────────────────────────────────────────────────────

        static void ShowChildInsert(VisualElement wrapper, Task task, Action<int, string[]> onInsertAfter)
        {
            var parentTabs = LeadingTabs(task.RawLine);
            var childPrefix = new string('\t', parentTabs + 1) + "- [ ] ";

            var insertRow = new VisualElement
            {
                style = { flexDirection = FlexDirection.Row, marginLeft = 22, marginTop = 2, marginBottom = 2 }
            };
            var field = new TextField();
            field.style.flexGrow = 1;
            insertRow.Add(field);
            wrapper.Add(insertRow);

            var committed = false;
            void Commit(string val)
            {
                if (committed) return;
                committed = true;
                val = val?.Trim();
                if (!string.IsNullOrEmpty(val))
                    onInsertAfter(task.LastLine, new[] { childPrefix + val });
                else
                    wrapper.Remove(insertRow);
            }
            field.RegisterCallback<KeyDownEvent>(e =>
            {
                if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)
                { e.StopPropagation(); Commit(field.value); }
                else if (e.keyCode == KeyCode.Escape)
                { e.StopPropagation(); Commit(null); }
            });
            field.RegisterCallback<FocusOutEvent>(_ => Commit(field.value));
            wrapper.schedule.Execute(() => field.Focus()).StartingIn(10);
        }

        static VisualElement AddChildFooter(int insertAfterLine, int indentDepth, Action<int, string[]> onInsertAfter)
        {
            var prefix = new string('\t', indentDepth) + "- [ ] ";
            var footer = new VisualElement
            {
                style = { flexDirection = FlexDirection.Row, marginLeft = indentDepth == 0 ? 12 : IndentPerLevel, marginTop = 2, marginBottom = indentDepth == 0 ? 6 : 2 }
            };

            Button btn = null;
            btn = MutedButton("+ Add task", () =>
            {
                footer.Clear();
                ShowInlineInsert(footer, btn, val => onInsertAfter(insertAfterLine, new[] { prefix + val }));
            });
            footer.Add(btn);
            return footer;
        }

        static VisualElement AddSectionFooter(int totalLines, Action<int, string[]> onInsertAfter)
        {
            var footer = new VisualElement { style = { marginTop = 8, marginBottom = 4, marginLeft = 4 } };
            Button btn = null;
            btn = MutedButton("+ Add section", () =>
            {
                footer.Clear();
                ShowInlineInsert(footer, btn, val => onInsertAfter(totalLines, new[] { "", "## " + val }));
            });
            footer.Add(btn);
            return footer;
        }

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

        // ── Inline edit ───────────────────────────────────────────────────────────────

        static void BeginInlineEdit(Label label, VisualElement parent, string initial, Action<string> onCommit)
        {
            var field = new TextField { value = initial };
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

        // ── Visual helpers ────────────────────────────────────────────────────────────

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

        static int LeadingTabs(string line)
        {
            var n = 0;
            while (n < line.Length && line[n] == '\t') n++;
            return n;
        }
    }
}
