using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.ShortcutManagement;
using UnityEngine;
using UnityEngine.UIElements;

namespace KodachiGames.Markdown.Editor
{
    /// <summary>
    /// A slide-in quick-view for pinned Markdown files. Press the shortcut
    /// (Edit ▸ Shortcuts ▸ "Window/Markdown Pin Popup", default Ctrl/Cmd+Shift+W) and a
    /// borderless panel folds open from the left edge, showing the pinned file rendered
    /// like the Markdown Browser's preview — no file tree. When several files are pinned,
    /// tabs along the top switch between them; the last viewed file is remembered.
    /// A header dropdown switches between formatted preview, raw editing, and a prioritised
    /// Task view (<see cref="TaskView"/>); Ctrl/Cmd+E toggles Formatted/Edit. In Task view,
    /// right-click a task to set its priority. Press the shortcut again, or Esc, to dismiss.
    /// </summary>
    public sealed class MarkdownPinPopup : EditorWindow
    {
        const int PreviewCharLimit = 200_000;

        static MarkdownPinPopup _instance;

        [Shortcut("Window/Markdown Pin Popup", KeyCode.W, ShortcutModifiers.Action | ShortcutModifiers.Shift)]
        public static void Toggle()
        {
            if (_instance != null)
            {
                _instance.Close();
                return;
            }

            var w = CreateInstance<MarkdownPinPopup>();
            _instance = w;

            w.position = LoadRect();
            w.ShowPopup();
            w.Focus();
        }

        /// <summary>Last window rect, or a right-edge dock against the main window if none saved.</summary>
        static Rect LoadRect()
        {
            if (EditorPrefs.HasKey(RectKey + ".w"))
            {
                var r = new Rect(
                    EditorPrefs.GetFloat(RectKey + ".x"),
                    EditorPrefs.GetFloat(RectKey + ".y"),
                    EditorPrefs.GetFloat(RectKey + ".w"),
                    EditorPrefs.GetFloat(RectKey + ".h"));
                // Guard against off-screen / degenerate rects from a previous monitor layout.
                if (r.width >= 120 && r.height >= 120)
                    return r;
            }

            var main = EditorGUIUtility.GetMainWindowPosition();
            return new Rect(main.xMax - ExpandedWidth, main.y, ExpandedWidth, main.height);
        }

        void SaveRect()
        {
            var r = position;
            EditorPrefs.SetFloat(RectKey + ".x", r.x);
            EditorPrefs.SetFloat(RectKey + ".y", r.y);
            EditorPrefs.SetFloat(RectKey + ".w", r.width);
            EditorPrefs.SetFloat(RectKey + ".h", r.height);
        }

        // ── Layout / persistence ────────────────────────────────────────────────

        const float ExpandedWidth  = 640f;
        const string LastFileKey   = "KodachiMarkdown.PinPopup.LastFile"; // SessionState
        const string RectKey       = "KodachiMarkdown.PinPopup.Rect";     // EditorPrefs (persists)

        // ── Colors (matched to QuickAccessPopup) ─────────────────────────────────

        static readonly Color C_BG       = new(0.160f, 0.160f, 0.160f);
        static readonly Color C_HEADER   = new(0.130f, 0.130f, 0.130f);
        static readonly Color C_BORDER   = new(0.090f, 0.090f, 0.090f);
        static readonly Color C_ACCENT   = new(0.255f, 0.490f, 0.965f);
        static readonly Color C_TEXT     = new(0.850f, 0.850f, 0.850f);
        static readonly Color C_TEXT_DIM = new(0.500f, 0.500f, 0.500f);
        static readonly Color C_TAB      = new(0.130f, 0.130f, 0.130f);
        static readonly Color C_TAB_HOV  = new(0.200f, 0.200f, 0.200f);

        // ── State ─────────────────────────────────────────────────────────────────

        VisualElement _tabBar;
        VisualElement _emptyView;
        ScrollView    _scroll;
        VisualElement _content;
        Label         _title;
        DropdownField _modeDropdown;

        enum ViewMode { Formatted, Edit, Tasks }
        static readonly string[] ModeLabels = { "Formatted", "Edit", "Tasks" };
        const string ModeKey = "KodachiMarkdown.PinPopup.ViewMode"; // SessionState
        ViewMode _mode = ViewMode.Formatted;

        readonly List<TabRef> _tabs = new();
        // Scroll offset per pinned file, so switching tabs returns to where you left off.
        readonly Dictionary<string, float> _scrollByRel = new();
        string _activeRel;
        string _rawText;
        bool _truncated;

        sealed class TabRef { public string Rel; public Button Tab; }

        // ── Lifecycle ───────────────────────────────────────────────────────────

        void OnEnable()
        {
            _instance = this;
            MarkdownPins.Changed += OnPinsChanged;
            ActiveTask.Changed += OnActiveTaskChanged;
        }
        void OnDisable()
        {
            SaveRect();
            if (_instance == this) _instance = null;
            MarkdownPins.Changed -= OnPinsChanged;
            ActiveTask.Changed -= OnActiveTaskChanged;
        }

        void OnActiveTaskChanged()
        {
            // Structural change (task set/cleared, paused) — refresh the Task view in place.
            // Reload from disk first: Complete() may have modified the file.
            if (_mode == ViewMode.Tasks)
            {
                SaveScroll();
                if (!string.IsNullOrEmpty(_activeRel))
                    LoadRawText(MarkdownPins.ToFull(_activeRel));
                Render();
            }
        }

        void OnPinsChanged()
        {
            BuildTabs();
            // If the file being viewed was unpinned, fall back to the first pin.
            var pins = MarkdownPins.RelPaths;
            if (_activeRel == null || !pins.Contains(_activeRel))
                SelectFile(pins.Count > 0 ? pins[0] : null);
        }

        public void CreateGUI()
        {
            Build();
            BuildTabs();
            RestoreLastSelection();
        }

        // ── UI ──────────────────────────────────────────────────────────────────

        void Build()
        {
            var root = rootVisualElement;
            root.Clear();
            root.style.flexGrow = 1;
            root.style.backgroundColor = C_BG;
            root.style.borderLeftWidth = 1;
            root.style.borderLeftColor = C_ACCENT;
            root.focusable = true;
            root.RegisterCallback<KeyDownEvent>(OnKeyDown, TrickleDown.TrickleDown);

            // Header (badge + title + close).
            var hdr = HeaderBar();
            var badge = new Label("📌") { style = { fontSize = 13, marginRight = 6 } };
            _title = new Label("PINNED")
            { style = { fontSize = 11, color = C_TEXT, unityFontStyleAndWeight = FontStyle.Bold, flexGrow = 1, letterSpacing = 1f, overflow = Overflow.Hidden, whiteSpace = WhiteSpace.NoWrap } };
            _mode = (ViewMode)SessionState.GetInt(ModeKey, (int)ViewMode.Formatted);
            _modeDropdown = new DropdownField(ModeLabels.ToList(), (int)_mode)
            { style = { marginRight = 6, flexShrink = 0, minWidth = 90 } };
            _modeDropdown.RegisterValueChangedCallback(evt =>
            {
                _mode = (ViewMode)Array.IndexOf(ModeLabels, evt.newValue);
                SessionState.SetInt(ModeKey, (int)_mode);
                SaveScroll();
                Render();
            });
            var close = IconButton("✕", Close);
            hdr.Add(badge); hdr.Add(_title); hdr.Add(_modeDropdown); hdr.Add(close);
            root.Add(hdr);

            // Tab bar (one tab per pinned file).
            _tabBar = new VisualElement
            { style = { flexDirection = FlexDirection.Row, flexWrap = Wrap.Wrap, backgroundColor = C_HEADER,
                        paddingLeft = 4, paddingRight = 4, paddingTop = 4, paddingBottom = 4,
                        borderBottomWidth = 1, borderBottomColor = C_BORDER, flexShrink = 0} };
            root.Add(_tabBar);

            // Empty-state hint.
            _emptyView = new VisualElement { style = { flexGrow = 1, alignItems = Align.Center, justifyContent = Justify.Center, display = DisplayStyle.None } };
            _emptyView.Add(new Label("No pinned Markdown files.\n\nOpen the Markdown Browser, select a file,\nand press \"Pin\".")
            { style = { color = C_TEXT_DIM, unityTextAlign = TextAnchor.MiddleCenter, whiteSpace = WhiteSpace.Normal } });
            root.Add(_emptyView);

            // Rendered preview.
            _scroll = new ScrollView(ScrollViewMode.Vertical) { style = { flexGrow = 1 } };
            _content = new VisualElement { style = { paddingTop = 4, paddingLeft = 8, paddingRight = 8, paddingBottom = 8 } };
            _scroll.Add(_content);
            root.Add(_scroll);

            root.schedule.Execute(() => root.Focus()).StartingIn(50);
        }

        void BuildTabs()
        {
            if (_tabBar == null) return;
            _tabBar.Clear();
            _tabs.Clear();

            var pins = MarkdownPins.RelPaths;
            _tabBar.style.display = pins.Count > 1 ? DisplayStyle.Flex : DisplayStyle.None;

            foreach (var rel in pins)
            {
                var r = rel;
                var tab = new Button(() => SelectFile(r)) { text = Path.GetFileName(r), tooltip = r };
                tab.style.marginLeft = 2; tab.style.marginRight = 2; tab.style.marginTop = 1; tab.style.marginBottom = 1;
                tab.style.paddingLeft = 10; tab.style.paddingRight = 10; tab.style.paddingTop = 4; tab.style.paddingBottom = 4;
                tab.style.fontSize = 11;
                tab.style.borderTopWidth = tab.style.borderRightWidth = tab.style.borderLeftWidth = 0;
                tab.style.borderBottomWidth = 2;
                tab.style.borderBottomColor = C_TAB;
                tab.style.backgroundColor = C_TAB;
                tab.style.color = C_TEXT_DIM;
                SetRadius(tab.style, 3);
                tab.RegisterCallback<PointerEnterEvent>(_ => { if (r != _activeRel) tab.style.backgroundColor = C_TAB_HOV; });
                tab.RegisterCallback<PointerLeaveEvent>(_ => { if (r != _activeRel) tab.style.backgroundColor = C_TAB; });
                _tabs.Add(new TabRef { Rel = r, Tab = tab });
                _tabBar.Add(tab);
            }

            RefreshTabStyles();
        }

        void RefreshTabStyles()
        {
            foreach (var t in _tabs)
            {
                bool active = t.Rel == _activeRel;
                t.Tab.style.backgroundColor = active ? C_TAB_HOV : C_TAB;
                t.Tab.style.borderBottomColor = active ? C_ACCENT : C_TAB;
                t.Tab.style.color = active ? C_TEXT : C_TEXT_DIM;
                t.Tab.style.unityFontStyleAndWeight = active ? FontStyle.Bold : FontStyle.Normal;
            }
        }

        // ── Selection / rendering ─────────────────────────────────────────────────

        void RestoreLastSelection()
        {
            var pins = MarkdownPins.RelPaths;
            if (pins.Count == 0) { SelectFile(null); return; }

            var last = SessionState.GetString(LastFileKey, null);
            SelectFile(pins.Contains(last) ? last : pins[0]);
        }

        void SelectFile(string rel)
        {
            // Remember where we were in the file we're leaving.
            if (!string.IsNullOrEmpty(_activeRel))
                _scrollByRel[_activeRel] = _scroll.scrollOffset.y;

            _activeRel = rel;

            if (string.IsNullOrEmpty(rel))
            {
                _title.text = "PINNED";
                _emptyView.style.display = DisplayStyle.Flex;
                _scroll.style.display = DisplayStyle.None;
                _content.Clear();
                RefreshTabStyles();
                return;
            }

            SessionState.SetString(LastFileKey, rel);
            _title.text = Path.GetFileName(rel).ToUpperInvariant();
            _emptyView.style.display = DisplayStyle.None;
            _scroll.style.display = DisplayStyle.Flex;
            RefreshTabStyles();

            LoadRawText(MarkdownPins.ToFull(rel));
            Render();
        }

        void LoadRawText(string fullPath)
        {
            try
            {
                var info = new FileInfo(fullPath);
                if (!info.Exists)
                {
                    _rawText = "(file not found — it may have been moved or deleted)";
                    _truncated = true;
                    return;
                }

                if (info.Length > PreviewCharLimit)
                {
                    using var reader = info.OpenText();
                    var buffer = new char[PreviewCharLimit];
                    var read = reader.Read(buffer, 0, buffer.Length);
                    _rawText = new string(buffer, 0, read) + "\n\n… (truncated)";
                    _truncated = true;
                }
                else
                {
                    _rawText = File.ReadAllText(fullPath);
                    _truncated = false;
                }
            }
            catch (Exception e)
            {
                _rawText = $"(could not read file)\n{e.Message}";
                _truncated = true;
            }
        }

        void Render()
        {
            if (_rawText == null) return;

            if (_mode == ViewMode.Tasks)
            {
                // Tasks grouped under headings, with completion % and priority sorting.
                TaskView.Populate(_content, _rawText,
                    _truncated ? null : ToggleCheckbox,
                    _truncated ? null : SetPriority,
                    _truncated ? null : ReplaceLine,
                    _truncated ? null : InsertLinesAfter,
                    _truncated ? null : EditDescription,
                    _truncated ? null : DeleteRange,
                    _activeRel);
            }
            else if (_mode == ViewMode.Formatted)
            {
                // Truncated/unreadable content renders read-only checkboxes (null callback).
                MarkdownView.Populate(_content, _rawText, _truncated ? null : ToggleCheckbox);
            }
            else
            {
                _content.Clear();
                if (!_truncated && !string.IsNullOrEmpty(_activeRel))
                {
                    var field = new TextField { value = _rawText, multiline = true };
                    field.style.flexGrow = 1;
                    field.style.whiteSpace = WhiteSpace.Normal;
                    // Write back to disk on every change so the file stays in sync.
                    field.RegisterValueChangedCallback(evt =>
                    {
                        _rawText = evt.newValue;
                        WriteRawText();
                    });
                    _content.Add(field);
                }
                else
                {
                    // Truncated/unreadable files are shown read-only to avoid partial writes.
                    var raw = new Label(_rawText) { enableRichText = false };
                    raw.style.whiteSpace = WhiteSpace.Normal;
                    raw.style.color = C_TEXT;
                    raw.selection.isSelectable = true;
                    _content.Add(raw);
                }
            }

            // Restore the saved scroll position (after layout, so content extents exist).
            // SelectFile saves the outgoing file's offset; in-place re-renders save via
            // SaveScroll() so ticking a box or switching mode doesn't jump to the top.
            var targetY = _scrollByRel.TryGetValue(_activeRel ?? "", out var y) ? y : 0f;
            _scroll.scrollOffset = new Vector2(0, targetY);
            _scroll.schedule.Execute(() => _scroll.scrollOffset = new Vector2(0, targetY));
        }

        /// <summary>Remember the current scroll offset so the next Render restores it in place.</summary>
        void SaveScroll()
        {
            if (!string.IsNullOrEmpty(_activeRel))
                _scrollByRel[_activeRel] = _scroll.scrollOffset.y;
        }

        void WriteRawText()
        {
            if (string.IsNullOrEmpty(_activeRel) || _rawText == null) return;

            var fullPath = MarkdownPins.ToFull(_activeRel);
            try
            {
                File.WriteAllText(fullPath, _rawText);
                // Unity will pick up the change on editor focus; no need to call
                // AssetDatabase.ImportAsset on every keystroke.
            }
            catch (Exception e)
            {
                Debug.LogError($"Failed to write {_activeRel}: {e.Message}");
            }
        }

        static readonly Regex CheckboxMarker = new(@"\[[ xX]\]");
        static readonly Regex PriorityMarker = new(@"\s*\{[Pp]:-?\d+\}");

        void ToggleCheckbox(int lineIndex, bool isChecked)
        {
            EditLine(lineIndex, line => CheckboxMarker.Replace(line, isChecked ? "[x]" : "[ ]", 1));
        }

        /// <summary>Rewrites the <c>{P:n}</c> marker on a task line (0 removes it, since 0 is the default).</summary>
        void SetPriority(int lineIndex, int priority)
        {
            EditLine(lineIndex, line =>
            {
                line = PriorityMarker.Replace(line, "").TrimEnd();
                return priority == 0 ? line : line + $" {{P:{priority}}}";
            });
        }

        void ReplaceLine(int lineIndex, string newRawLine) =>
            EditLine(lineIndex, _ => newRawLine);

        void InsertLinesAfter(int lineIndex, string[] newLines)
        {
            if (string.IsNullOrEmpty(_activeRel) || _rawText == null) return;
            var lines = _rawText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();
            var insertAt = Math.Min(lineIndex + 1, lines.Count);
            lines.InsertRange(insertAt, newLines);
            _rawText = string.Join("\n", lines);
            PersistAndRender();
        }

        void EditDescription(int taskLine, int[] descLineIndices, string newDescription)
        {
            if (string.IsNullOrEmpty(_activeRel) || _rawText == null) return;
            var lines = _rawText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();

            // Remove old description lines in reverse order to keep indices valid.
            foreach (var idx in descLineIndices.OrderByDescending(x => x))
                if (idx >= 0 && idx < lines.Count)
                    lines.RemoveAt(idx);

            // Insert new description lines (each prefixed with a tab) after the task line.
            if (!string.IsNullOrWhiteSpace(newDescription))
            {
                var newDescLines = newDescription.Split('\n')
                    .Select(l => "\t" + l.TrimStart())
                    .ToArray();
                var insertAt = Math.Min(taskLine + 1, lines.Count);
                lines.InsertRange(insertAt, newDescLines);
            }

            _rawText = string.Join("\n", lines);
            PersistAndRender();
        }

        void DeleteRange(int fromLine, int toLine)
        {
            if (string.IsNullOrEmpty(_activeRel) || _rawText == null) return;
            var lines = _rawText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();
            var count = Math.Min(toLine, lines.Count - 1) - fromLine + 1;
            if (count > 0) lines.RemoveRange(fromLine, count);
            _rawText = string.Join("\n", lines);
            PersistAndRender();
        }

        void PersistAndRender()
        {
            if (string.IsNullOrEmpty(_activeRel)) return;
            var fullPath = MarkdownPins.ToFull(_activeRel);
            try
            {
                File.WriteAllText(fullPath, _rawText);
                var dataPath = Application.dataPath.Replace('\\', '/');
                if (fullPath.StartsWith(dataPath + "/", StringComparison.OrdinalIgnoreCase))
                    AssetDatabase.ImportAsset("Assets" + fullPath.Substring(dataPath.Length));
            }
            catch (Exception e)
            {
                Debug.LogError($"Failed to write {_activeRel}: {e.Message}");
            }
            SaveScroll();
            Render();
        }

        /// <summary>Applies <paramref name="transform"/> to one source line, persists, and re-renders in place.</summary>
        void EditLine(int lineIndex, Func<string, string> transform)
        {
            if (string.IsNullOrEmpty(_activeRel) || _rawText == null) return;

            var lines = _rawText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            if (lineIndex < 0 || lineIndex >= lines.Length) return;

            lines[lineIndex] = transform(lines[lineIndex]);
            _rawText = string.Join("\n", lines);

            var fullPath = MarkdownPins.ToFull(_activeRel);
            try
            {
                File.WriteAllText(fullPath, _rawText);

                var dataPath = Application.dataPath.Replace('\\', '/');
                if (fullPath.StartsWith(dataPath + "/", StringComparison.OrdinalIgnoreCase))
                {
                    var assetPath = "Assets" + fullPath.Substring(dataPath.Length);
                    AssetDatabase.ImportAsset(assetPath);
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"Failed to write change to {_activeRel}: {e.Message}");
            }

            // Keep the scroll position; in Tasks mode the list re-sorts under the cursor.
            SaveScroll();
            Render();
        }

        // ── Input ─────────────────────────────────────────────────────────────────

        void OnKeyDown(KeyDownEvent e)
        {
            if (e.keyCode == KeyCode.Escape)
            {
                // Let the focused TextField handle Escape (cancel edit) before closing.
                if (rootVisualElement.focusController?.focusedElement is TextField) return;
                e.StopPropagation();
                Close();
            }
            else if (e.keyCode == KeyCode.E && e.actionKey && _modeDropdown != null)
            {
                // Toggle between Formatted and Edit (leaves Tasks mode for Formatted).
                e.StopPropagation();
                var next = _mode == ViewMode.Edit ? ViewMode.Formatted : ViewMode.Edit;
                _modeDropdown.value = ModeLabels[(int)next];
            }
            else if ((e.keyCode == KeyCode.Tab || e.keyCode == KeyCode.RightArrow || e.keyCode == KeyCode.LeftArrow)
                     && _tabs.Count > 1
                     // Don't steal arrow keys from the editable raw-text field.
                     && _mode != ViewMode.Edit)
            {
                e.StopPropagation();
                CycleTab(e.keyCode == KeyCode.LeftArrow ? -1 : 1);
            }
        }

        void CycleTab(int dir)
        {
            var idx = _tabs.FindIndex(t => t.Rel == _activeRel);
            if (idx < 0) idx = 0;
            idx = (idx + dir + _tabs.Count) % _tabs.Count;
            SelectFile(_tabs[idx].Rel);
            // Re-rendering the content moves focus off root; take it back so the
            // next arrow/Tab keypress keeps cycling instead of dying after one step.
            rootVisualElement.Focus();
        }

        // ── Style helpers ─────────────────────────────────────────────────────────

        static VisualElement HeaderBar()
        {
            return new VisualElement
            { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, backgroundColor = C_HEADER,
                        paddingLeft = 8, paddingRight = 6, paddingTop = 6, paddingBottom = 6,
                        borderBottomWidth = 1, borderBottomColor = C_BORDER, flexShrink = 0} };
        }

        static Button IconButton(string text, Action onClick, string tooltip = null)
        {
            var b = new Button(onClick) { text = text, tooltip = tooltip };
            b.style.width = 20; b.style.height = 20;
            b.style.paddingLeft = b.style.paddingRight = b.style.paddingTop = b.style.paddingBottom = 0;
            b.style.marginLeft = 2;
            b.style.fontSize = 11;
            b.style.color = C_TEXT_DIM;
            b.style.backgroundColor = new Color(0, 0, 0, 0);
            b.style.borderTopWidth = b.style.borderRightWidth = b.style.borderBottomWidth = b.style.borderLeftWidth = 0;
            SetRadius(b.style, 3);
            b.RegisterCallback<PointerEnterEvent>(_ => { b.style.color = C_TEXT; b.style.backgroundColor = new Color(C_ACCENT.r, C_ACCENT.g, C_ACCENT.b, 0.15f); });
            b.RegisterCallback<PointerLeaveEvent>(_ => { b.style.color = C_TEXT_DIM; b.style.backgroundColor = new Color(0, 0, 0, 0); });
            return b;
        }

        static void SetRadius(IStyle s, float r)
        {
            s.borderTopLeftRadius = s.borderTopRightRadius = s.borderBottomLeftRadius = s.borderBottomRightRadius = r;
        }
    }
}
