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
            {
                container.Add(AddSectionFooter(totalLines, cb.OnInsertAfter));
                container.Add(KeyHint());

                // Keyboard focus lives permanently on the *container* — the one element that survives
                // every rebuild. Selection is plain data (_selectedLine) painted onto whichever row
                // currently owns that source line, so an edit that rebuilds the tree never loses the
                // cursor: we just re-highlight and (if needed) refocus the same stable container.
                container.focusable = true;
                container.AddToClassList(ListClass);

                // Controllers resolve the selected row's Task at event time. The container survives
                // rebuilds, so drop the previous handlers before adding fresh ones (which capture this
                // Populate's cb/totalLines); otherwise they'd accumulate.
                if (_kbdHandler != null) container.UnregisterCallback(_kbdHandler, TrickleDown.TrickleDown);
                _kbdHandler = e => OnRowKey(e, container, cb, totalLines);
                container.RegisterCallback(_kbdHandler, TrickleDown.TrickleDown);

                // Row-to-row movement rides the system's navigation (arrows / d-pad / stick) rather
                // than intercepting raw arrow KeyDownEvents, so we cooperate with the focus ring
                // instead of racing it. Listen on bubble-up, as Unity recommends for navigation events.
                if (_navHandler != null) container.UnregisterCallback(_navHandler);
                _navHandler = e => OnRowNav(e, container);
                container.RegisterCallback(_navHandler);

                // Normalise the remembered selection to a row that still exists (exact line, else the
                // nearest row at or before it, else the first), then paint it. This keeps the cursor
                // sensible after an edit shifted line numbers (e.g. a delete).
                var rows = Rows(container);
                if (rows.Count > 0)
                {
                    if (rows.All(r => ((Task)r.userData).Line != _selectedLine))
                    {
                        var below = rows.Where(r => ((Task)r.userData).Line <= _selectedLine).ToList();
                        var pick = below.Count > 0 ? below[^1] : rows[0];
                        _selectedLine = ((Task)pick.userData).Line;
                    }
                    Rehighlight(container);
                }

                // Keep the keyboard alive across rebuilds: if nothing is being edited (no TextField
                // focused), pull focus back onto the container.
                if (container.focusController?.focusedElement is not TextField)
                    FocusWhenReady(container);
            }
        }

        // ── Keyboard controller ────────────────────────────────────────────────────────

        static List<VisualElement> Rows(VisualElement container) =>
            container.Query<VisualElement>(className: RowClass).ToList();

        /// <summary>
        /// Focus the container reliably. Calling <c>Focus()</c> immediately after a rebuild is racy in
        /// an EditorWindow — the element may not be laid out yet and focus silently drops to null.
        /// Focusing again on the first <see cref="GeometryChangedEvent"/> (once it's positioned) is the
        /// canonical fix; the immediate call covers the already-laid-out case.
        /// </summary>
        static void FocusWhenReady(VisualElement element)
        {
            void Once(GeometryChangedEvent _)
            {
                element.UnregisterCallback<GeometryChangedEvent>(Once);
                element.Focus();
            }
            element.RegisterCallback<GeometryChangedEvent>(Once);
            element.Focus();
        }

        // ── Selection (plain data, decoupled from focus) ─────────────────────────────

        /// <summary>The row that currently owns <see cref="_selectedLine"/>, or null.</summary>
        static VisualElement CurrentRow(VisualElement container) =>
            Rows(container).FirstOrDefault(r => ((Task)r.userData).Line == _selectedLine);

        static Task CurrentTask(VisualElement container) =>
            CurrentRow(container)?.userData as Task;

        /// <summary>Paint the selection tint on the selected row and the active/clear colour on the rest.</summary>
        static void Rehighlight(VisualElement container)
        {
            foreach (var r in Rows(container))
            {
                var line = ((Task)r.userData).Line;
                r.style.backgroundColor = line == _selectedLine
                    ? FocusTint
                    : (r.ClassListContains(RowActiveClass) ? ActiveTint : Color.clear);
            }
        }

        static void MoveSelection(VisualElement container, int dir)
        {
            var rows = Rows(container);
            if (rows.Count == 0) return;
            var idx = rows.FindIndex(r => ((Task)r.userData).Line == _selectedLine);
            idx = idx < 0 ? 0 : Mathf.Clamp(idx + dir, 0, rows.Count - 1);
            _selectedLine = ((Task)rows[idx].userData).Line;
            Rehighlight(container);
            rows[idx].parent?.GetFirstAncestorOfType<ScrollView>()?.ScrollTo(rows[idx]);
        }

        static void OnRowKey(KeyDownEvent e, VisualElement container, Callbacks cb, int totalLines)
        {
            // Ignore keystrokes while an inline TextField is being edited — only drive from the container.
            if (container.focusController?.focusedElement is TextField) return;

            var task = CurrentTask(container);
            if (task == null) return;
            var row = CurrentRow(container);

            switch (e.keyCode)
            {
                // Arrows are handled by OnRowNav (the navigation system); J/K mirror them here.
                case KeyCode.J: MoveSelection(container, +1); e.StopPropagation(); return;
                case KeyCode.K: MoveSelection(container, -1); e.StopPropagation(); return;

                case KeyCode.Space:
                    if (!task.IsComposite && cb.OnToggled != null) cb.OnToggled(task.Line, !task.Done);
                    e.StopPropagation(); return;

                case KeyCode.Return:
                case KeyCode.KeypadEnter:
                    if (e.actionKey)   // Ctrl/Cmd+Enter → new section at end of document
                    {
                        if (cb.OnInsertAfter != null) ShowSectionInsert(container, cb.OnInsertAfter, totalLines);
                    }
                    else if (cb.OnReplaceLine != null) BeginRename(row, task, cb);   // rename
                    e.StopPropagation(); return;

                case KeyCode.F2:
                    if (cb.OnReplaceLine != null) BeginRename(row, task, cb);
                    e.StopPropagation(); return;

                case KeyCode.O:
                    if (cb.OnInsertAfter != null)
                    {
                        if (e.shiftKey) ShowChildInsert(row, task, cb.OnInsertAfter);    // child
                        else            ShowSiblingInsert(row, task, cb.OnInsertAfter);   // sibling
                    }
                    e.StopPropagation(); return;

                case KeyCode.Tab:
                    if (cb.OnReplaceLine != null)
                        cb.OnReplaceLine(task.Line, e.shiftKey ? Outdent(task.RawLine) : "\t" + task.RawLine);
                    e.StopPropagation(); return;

                case KeyCode.Delete:
                case KeyCode.Backspace:
                    if (cb.OnDeleteRange != null)
                    {
                        // Select the line that will slide into this slot, so the cursor stays put.
                        _selectedLine = task.Line;
                        cb.OnDeleteRange(task.Line, task.LastLine);
                    }
                    e.StopPropagation(); return;

                case KeyCode.D:
                    if (cb.OnEditDescription != null)
                        BeginDescriptionEdit(row, task, task.Description ?? "", cb.OnEditDescription);
                    e.StopPropagation(); return;

                case KeyCode.Alpha0: case KeyCode.Keypad0: SetPri(cb, task, 0); e.StopPropagation(); return;
                case KeyCode.Alpha1: case KeyCode.Keypad1: SetPri(cb, task, 1); e.StopPropagation(); return;
                case KeyCode.Alpha2: case KeyCode.Keypad2: SetPri(cb, task, 2); e.StopPropagation(); return;
                case KeyCode.Alpha3: case KeyCode.Keypad3: SetPri(cb, task, 3); e.StopPropagation(); return;
                case KeyCode.Alpha4: case KeyCode.Keypad4: SetPri(cb, task, 4); e.StopPropagation(); return;
                case KeyCode.Alpha5: case KeyCode.Keypad5: SetPri(cb, task, 5); e.StopPropagation(); return;
            }
        }

        static void BeginRename(VisualElement row, Task task, Callbacks cb)
        {
            var lbl = row?.Q<Label>(TaskLabelName);
            if (lbl != null)
                BeginInlineEdit(lbl, lbl.parent, task.Text, v => CommitRename(task, v, cb.OnReplaceLine));
        }

        /// <summary>Up/Down move the selection; every direction is PreventDefault'd so the focus ring
        /// can't drag focus off the container (which is the only thing keeping the keyboard alive).</summary>
        static void OnRowNav(NavigationMoveEvent e, VisualElement container)
        {
            if (container.focusController?.focusedElement is TextField) return;
            switch (e.direction)
            {
                case NavigationMoveEvent.Direction.Down: MoveSelection(container, +1); break;
                case NavigationMoveEvent.Direction.Up:   MoveSelection(container, -1); break;
            }
            e.PreventDefault();
            e.StopPropagation();
        }

        static void SetPri(Callbacks cb, Task task, int priority)
        {
            if (cb.OnSetPriority == null || task.IsComposite) return;
            cb.OnSetPriority(task.Line, priority);
        }

        /// <summary>Removes one leading tab (no-op at depth 0).</summary>
        static string Outdent(string rawLine) =>
            rawLine.StartsWith("\t") ? rawLine.Substring(1) : rawLine;

        /// <summary>Inline field to add a sibling task after <paramref name="task"/> at the same indent.</summary>
        static void ShowSiblingInsert(VisualElement wrapper, Task task, Action<int, string[]> onInsertAfter)
        {
            var prefix = new string('\t', LeadingTabs(task.RawLine)) + "- [ ] ";
            var insertRow = new VisualElement { style = { flexDirection = FlexDirection.Row, marginTop = 2, marginBottom = 2 } };
            var field = new TextField { style = { flexGrow = 1 } };
            insertRow.Add(field);

            // Place it right after the focused row in the same parent (true sibling position).
            var parent = wrapper.parent;
            parent.Insert(parent.IndexOf(wrapper) + 1, insertRow);

            var committed = false;
            void Commit(string val)
            {
                if (committed) return;
                committed = true;
                val = val?.Trim();
                if (!string.IsNullOrEmpty(val))
                { _selectedLine = task.LastLine + 1; onInsertAfter(task.LastLine, new[] { prefix + val }); }
                else
                { parent.Remove(insertRow); ListContainer(wrapper)?.Focus(); }
            }
            field.RegisterCallback<KeyDownEvent>(e =>
            {
                if (e.keyCode is KeyCode.Return or KeyCode.KeypadEnter) { e.StopPropagation(); Commit(field.value); }
                else if (e.keyCode == KeyCode.Escape)                    { e.StopPropagation(); Commit(null); }
            });
            field.RegisterCallback<FocusOutEvent>(_ => Commit(field.value));
            field.schedule.Execute(() => field.Focus()).StartingIn(10);
        }

        /// <summary>Inline field appended to the list to add a new "## " section at the end of the document.</summary>
        static void ShowSectionInsert(VisualElement container, Action<int, string[]> onInsertAfter, int totalLines)
        {
            var footer = new VisualElement { style = { marginTop = 6, marginBottom = 4, marginLeft = 4 } };
            var field = new TextField { style = { flexGrow = 1 } };
            footer.Add(field);
            container.Add(footer);

            var committed = false;
            void Commit(string val)
            {
                if (committed) return;
                committed = true;
                val = val?.Trim();
                if (!string.IsNullOrEmpty(val))
                { _selectedLine = totalLines + 2; onInsertAfter(totalLines, new[] { "", "## " + val }); }
                else { container.Remove(footer); container.Focus(); }
            }
            field.RegisterCallback<KeyDownEvent>(e =>
            {
                if (e.keyCode is KeyCode.Return or KeyCode.KeypadEnter) { e.StopPropagation(); Commit(field.value); }
                else if (e.keyCode == KeyCode.Escape)                    { e.StopPropagation(); Commit(null); }
            });
            field.RegisterCallback<FocusOutEvent>(_ => Commit(field.value));
            field.schedule.Execute(() => field.Focus()).StartingIn(10);
        }

        static VisualElement KeyHint()
        {
            var hint = PlainLabel("↑↓/JK move · space done · enter rename · O add · ⇧O child · ⇥ indent · 0–5 prio · del remove");
            hint.selection.isSelectable = false;
            hint.style.color = MutedColor;
            hint.style.fontSize = 10;
            hint.style.marginTop = 8;
            hint.style.marginLeft = 4;
            hint.style.whiteSpace = WhiteSpace.Normal;
            return hint;
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
        const string TaskLabelName = "__task_label";

        /// <summary>USS class on each task-row wrapper — lets the controller find/route rows by source line.</summary>
        public const string RowClass = "km-task-row";
        /// <summary>USS class marking the active-task row (so re-highlighting knows its base tint without cb).</summary>
        const string RowActiveClass = "km-task-row--active";
        /// <summary>USS class on the list container (the stable, always-focused element).</summary>
        const string ListClass = "km-task-list";

        // Selection tint (stronger than the active-task tint so the keyboard cursor stands out) and the
        // active-task tint used when a row is active but not selected.
        static readonly Color FocusTint  = new(AccentColor.r, AccentColor.g, AccentColor.b, 0.30f);
        static readonly Color ActiveTint = new(AccentColor.r, AccentColor.g, AccentColor.b, 0.18f);

        // The current keyboard selection, as a source-line index. Survives tree rebuilds: it's plain
        // data painted onto whichever row owns that line, not UI Toolkit focus (which dies on rebuild).
        static int _selectedLine = -1;

        // The list's KeyDownEvent / NavigationMoveEvent handlers — tracked so each rebuild
        // replaces (not stacks) them.
        static EventCallback<KeyDownEvent> _kbdHandler;
        static EventCallback<NavigationMoveEvent> _navHandler;

        static VisualElement TaskRow(Task task, int depth, Callbacks cb)
        {
            var effectiveDone = task.IsEffectivelyDone;
            var isActive = cb.FileRel != null && ActiveTask.IsActive(cb.FileRel, task.Text);

            var selected = task.Line == _selectedLine;

            var wrapper = new VisualElement
            {
                style =
                {
                    marginLeft = depth == 0 ? 12 : IndentPerLevel,
                    marginBottom = depth == 0 ? 4 : 2,
                    paddingLeft = 4, paddingTop = 1, paddingBottom = 1,
                    borderTopLeftRadius = 3, borderBottomLeftRadius = 3,
                    backgroundColor = selected ? FocusTint : (isActive ? ActiveTint : Color.clear)
                }
            };

            // The row isn't focusable — keyboard focus stays on the container. The row just carries its
            // Task and is tagged so the controller can find it by source line and repaint the selection.
            wrapper.userData = task;
            wrapper.AddToClassList(RowClass);
            if (isActive) wrapper.AddToClassList(RowActiveClass);

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
            // Keep the row wrapper the only focusable unit: if the Toggle joined the focus ring,
            // arrow navigation (and post-rebuild refocus) could park on it instead of the row,
            // and the keyboard controller would stop responding. Mouse toggling still works.
            toggle.focusable = false;
            if (!task.IsComposite && cb.OnToggled != null)
                toggle.RegisterValueChangedCallback(evt => cb.OnToggled(task.Line, evt.newValue));
            row.Add(toggle);

            if (task.Priority != 0)
                row.Add(PriorityBadge(task.Priority));

            // ── Label ─────────────────────────────────────────────────────
            var taskLabel = PlainLabel(task.Text);
            taskLabel.name = TaskLabelName;
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
                addBtn.focusable = false;   // don't let it join the focus ring — see the Toggle note above
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
                { _selectedLine = task.LastLine + 1; onInsertAfter(task.LastLine, new[] { childPrefix + val }); }
                else
                { wrapper.Remove(insertRow); ListContainer(wrapper)?.Focus(); }
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

            var listContainer = ListContainer(parent);
            var committed = false;
            void Commit(string val)
            {
                if (committed) return;
                committed = true;
                onCommit(val);
                // A changed value rebuilds the whole tree (this field is detached, panel == null) and
                // Populate repaints/refocuses. But a no-op/cancel commits nothing and leaves the field
                // orphaned in place — put the label back and hand focus to the list container so the
                // keyboard keeps working.
                if (field.panel != null)
                {
                    var i = parent.IndexOf(field);
                    if (i >= 0) { parent.RemoveAt(i); parent.Insert(i, label); }
                    listContainer?.Focus();
                }
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

        /// <summary>Nearest ancestor task-row (the focusable wrapper), or null (e.g. a section header).</summary>
        /// <summary>Nearest ancestor list container (the stable focusable element), or null.</summary>
        static VisualElement ListContainer(VisualElement el)
        {
            for (var e = el; e != null; e = e.parent)
                if (e.ClassListContains(ListClass)) return e;
            return null;
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
