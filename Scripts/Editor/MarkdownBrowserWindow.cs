using System.IO;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace KodachiGames.Markdown.Editor
{
    /// <summary>
    /// Browses every Markdown file in the project (Assets, Packages, and loose files
    /// like the root CLAUDE.md or docs/). Empty folder chains are collapsed but stay
    /// visible in the path. Select a file to preview it; double-click to open it
    /// in the OS default application.
    /// </summary>
    public class MarkdownBrowserWindow : EditorWindow
    {
        const int PreviewCharLimit = 200_000;

        [MenuItem("Kodachi/Markdown Browser")]
        public static void Open() => GetWindow<MarkdownBrowserWindow>("Markdown");

        TreeView _tree;
        TwoPaneSplitView _split;
        VisualElement _leftPane;
        Button _toggleTreeButton;
        bool _treeVisible = true;
        ToolbarSearchField _search;
        Label _status;
        Label _previewPath;
        ScrollView _previewScroll;
        VisualElement _previewContent;
        Toggle _formatToggle;
        Button _openButton;
        Button _revealButton;

        FileEntrySelection _selected;
        string _rawText;
        bool _truncated;

        const string TreeVisibleKey = "KodachiMarkdown.TreeVisible";

        static string ProjectRoot => Directory.GetParent(Application.dataPath)!.FullName.Replace('\\', '/');

        Texture2D _folderIcon;
        Texture2D _fileIcon;

        void CreateGUI()
        {
            _folderIcon = (Texture2D)EditorGUIUtility.IconContent("Folder Icon").image;
            _fileIcon = (Texture2D)EditorGUIUtility.IconContent("TextAsset Icon").image;

            var root = rootVisualElement;
            root.style.flexDirection = FlexDirection.Column;

            // ---- Toolbar ----
            var toolbar = new VisualElement
            {
                style =
                {
                    flexDirection = FlexDirection.Row,
                    alignItems = Align.Center,
                    paddingTop = 4, paddingBottom = 4, paddingLeft = 6, paddingRight = 6,
                    borderBottomWidth = 1, borderBottomColor = new Color(0, 0, 0, 0.3f)
                }
            };

            _toggleTreeButton = new Button(ToggleTree) { text = "Hide Tree", style = { marginRight = 6 } };
            toolbar.Add(_toggleTreeButton);

            toolbar.Add(new Button(Refresh) { text = "Refresh" });

            _search = new ToolbarSearchField { style = { flexGrow = 1, marginLeft = 6, marginRight = 6 } };
            _search.RegisterValueChangedCallback(_ => Refresh());
            toolbar.Add(_search);

            _status = new Label { style = { unityTextAlign = TextAnchor.MiddleLeft, minWidth = 90 } };
            toolbar.Add(_status);

            root.Add(toolbar);

            // ---- Split: tree | preview ----
            _split = new TwoPaneSplitView(0, 320, TwoPaneSplitViewOrientation.Horizontal)
            {
                style = { flexGrow = 1 }
            };
            root.Add(_split);

            // Wrap the TreeView in a container: TwoPaneSplitView writes style.width onto
            // its direct pane child, which collapses a bare TreeView to zero size.
            _leftPane = new VisualElement { style = { flexGrow = 1, minWidth = 180 } };
            _tree = new TreeView { style = { flexGrow = 1 }, fixedItemHeight = 20 };
            _tree.makeItem = MakeRow;
            _tree.bindItem = BindRow;
            _tree.selectionChanged += _ => OnSelectionChanged();
            _tree.itemsChosen += _ => OpenSelectedExternally();
            _leftPane.Add(_tree);
            _split.Add(_leftPane);

            _split.Add(BuildPreviewPane());

            Refresh();

            // Restore tree visibility after layout (CollapseChild needs the split to be in the hierarchy).
            _treeVisible = SessionState.GetBool(TreeVisibleKey, true);
            if (!_treeVisible)
                rootVisualElement.schedule.Execute(() =>
                {
                    _split.CollapseChild(0);
                    _toggleTreeButton.text = "Show Tree";
                });
        }

        void ToggleTree()
        {
            _treeVisible = !_treeVisible;
            if (_treeVisible)
                _split.UnCollapse();
            else
                _split.CollapseChild(0);

            _toggleTreeButton.text = _treeVisible ? "Hide Tree" : "Show Tree";
            SessionState.SetBool(TreeVisibleKey, _treeVisible);
        }

        VisualElement BuildPreviewPane()
        {
            var pane = new VisualElement { style = { flexGrow = 1, paddingTop = 4, paddingLeft = 6, paddingRight = 6, paddingBottom = 6 } };

            var header = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginBottom = 4 } };
            _previewPath = new Label("(select a file)")
            {
                style = { flexGrow = 1, unityFontStyleAndWeight = FontStyle.Bold, overflow = Overflow.Hidden, whiteSpace = WhiteSpace.NoWrap }
            };
            header.Add(_previewPath);

            _formatToggle = new Toggle("Formatted") { value = true, style = { marginRight = 6, flexShrink = 0 } };
            _formatToggle.RegisterValueChangedCallback(_ => RenderPreview());
            header.Add(_formatToggle);

            _openButton = new Button(OpenSelectedExternally) { text = "Open", style = { display = DisplayStyle.None, flexShrink = 0} };
            _revealButton = new Button(RevealSelected) { text = "Reveal", style = { display = DisplayStyle.None , flexShrink = 0} };
            header.Add(_openButton);
            header.Add(_revealButton);
            pane.Add(header);

            _previewScroll = new ScrollView(ScrollViewMode.Vertical) { style = { flexGrow = 1 } };
            _previewContent = new VisualElement { style = { paddingRight = 4 } };
            _previewScroll.Add(_previewContent);
            pane.Add(_previewScroll);

            return pane;
        }

        static VisualElement MakeRow()
        {
            var row = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center } };
            row.Add(new Image { style = { width = 16, height = 16, marginRight = 4, flexShrink = 0 } });
            row.Add(new Label { style = { overflow = Overflow.Hidden, whiteSpace = WhiteSpace.NoWrap } });
            return row;
        }

        void BindRow(VisualElement element, int index)
        {
            var icon = (Image)element[0];
            var label = (Label)element[1];
            var data = _tree.GetItemDataForIndex<object>(index);

            switch (data)
            {
                case MarkdownFileTree.FolderNode folder:
                    icon.image = _folderIcon;
                    label.text = folder.Name;
                    label.tooltip = folder.FullPath;
                    break;
                case MarkdownFileTree.FileEntry file:
                    icon.image = _fileIcon;
                    label.text = file.Name;
                    label.tooltip = file.RelPath;
                    break;
            }
        }

        void Refresh()
        {
            var tree = MarkdownFileTree.Build(ProjectRoot, _search?.value);
            var count = MarkdownFileTree.CountFiles(tree);

            var id = 0;
            var items = new List<TreeViewItemData<object>>();
            foreach (var folder in tree.Folders)
                items.Add(BuildFolderItem(folder, ref id));
            foreach (var file in tree.Files)
                items.Add(new TreeViewItemData<object>(id++, file));

            _tree.SetRootItems(items);
            _tree.Rebuild();

            _status.text = count == 1 ? "1 file" : $"{count} files";

            // Selection is rebuilt away — reset the preview.
            ClearSelection();
        }

        static TreeViewItemData<object> BuildFolderItem(MarkdownFileTree.FolderNode folder, ref int id)
        {
            var children = new List<TreeViewItemData<object>>();
            foreach (var sub in folder.Folders)
                children.Add(BuildFolderItem(sub, ref id));
            foreach (var file in folder.Files)
                children.Add(new TreeViewItemData<object>(id++, file));

            return new TreeViewItemData<object>(id++, folder, children);
        }

        void OnSelectionChanged()
        {
            if (_tree.selectedItem is MarkdownFileTree.FileEntry file)
                ShowPreview(file);
            else
                ClearSelection();
        }

        void ShowPreview(MarkdownFileTree.FileEntry file)
        {
            _selected = new FileEntrySelection { FullPath = file.FullPath, RelPath = file.RelPath };
            _previewPath.text = file.RelPath;
            _openButton.style.display = DisplayStyle.Flex;
            _revealButton.style.display = DisplayStyle.Flex;

            try
            {
                var info = new FileInfo(file.FullPath);
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
                    _rawText = File.ReadAllText(file.FullPath);
                    _truncated = false;
                }
            }
            catch (System.Exception e)
            {
                _rawText = $"(could not read file)\n{e.Message}";
                _truncated = true;
            }

            RenderPreview();
        }

        void RenderPreview()
        {
            if (_rawText == null) return;

            if (_formatToggle.value)
            {
                MarkdownView.Populate(_previewContent, _rawText,
                    _truncated ? null : ToggleCheckbox);
            }
            else
            {
                _previewContent.Clear();
                if (_selected != null && !_truncated)
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
                    _previewContent.Add(field);
                }
                else
                {
                    // Truncated files are shown read-only to avoid partial writes.
                    var raw = new Label(_rawText) { enableRichText = false };
                    raw.style.whiteSpace = WhiteSpace.Normal;
                    raw.selection.isSelectable = true;
                    _previewContent.Add(raw);
                }
            }

            _previewScroll.scrollOffset = Vector2.zero;
        }

        static readonly System.Text.RegularExpressions.Regex CheckboxMarker =
            new(@"\[[ xX]\]");

        /// <summary>
        /// Flips the checkbox marker on the given source line, persists the change to disk,
        /// and re-renders. Line endings are normalised to <c>\n</c> on save.
        /// </summary>
        void ToggleCheckbox(int lineIndex, bool isChecked)
        {
            if (_selected == null || _rawText == null) return;

            var lines = _rawText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            if (lineIndex < 0 || lineIndex >= lines.Length) return;

            lines[lineIndex] = CheckboxMarker.Replace(lines[lineIndex], isChecked ? "[x]" : "[ ]", 1);
            _rawText = string.Join("\n", lines);

            try
            {
                File.WriteAllText(_selected.FullPath, _rawText);

                // Keep the asset database in sync when the file lives under Assets/.
                var dataPath = Application.dataPath.Replace('\\', '/');
                if (_selected.FullPath.StartsWith(dataPath + "/", System.StringComparison.OrdinalIgnoreCase))
                {
                    var assetPath = "Assets" + _selected.FullPath.Substring(dataPath.Length);
                    AssetDatabase.ImportAsset(assetPath);
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError($"Failed to write checkbox change to {_selected.RelPath}: {e.Message}");
            }

            RenderPreview();
        }

        void WriteRawText()
        {
            if (_selected == null || _rawText == null) return;
            try
            {
                File.WriteAllText(_selected.FullPath, _rawText);
                // Unity will pick up the change on editor focus; no need to call
                // AssetDatabase.ImportAsset on every keystroke.
            }
            catch (System.Exception e)
            {
                Debug.LogError($"Failed to write {_selected.RelPath}: {e.Message}");
            }
        }

        void ClearSelection()
        {
            _selected = null;
            _rawText = null;
            _previewPath.text = "(select a file)";
            _previewContent.Clear();
            _openButton.style.display = DisplayStyle.None;
            _revealButton.style.display = DisplayStyle.None;
        }

        void OpenSelectedExternally()
        {
            if (_selected == null) return;

            // Prefer Unity's asset pipeline when the file lives under Assets/ so it
            // honours the user's configured external script editor; fall back to the
            // OS default app for everything outside the project asset folders.
            var dataPath = Application.dataPath.Replace('\\', '/');
            if (_selected.FullPath.StartsWith(dataPath + "/", System.StringComparison.OrdinalIgnoreCase))
            {
                var assetPath = "Assets" + _selected.FullPath.Substring(dataPath.Length);
                var obj = AssetDatabase.LoadAssetAtPath<Object>(assetPath);
                if (obj != null)
                {
                    AssetDatabase.OpenAsset(obj);
                    return;
                }
            }

            EditorUtility.OpenWithDefaultApp(_selected.FullPath);
        }

        void RevealSelected()
        {
            if (_selected != null)
                EditorUtility.RevealInFinder(_selected.FullPath);
        }

        sealed class FileEntrySelection
        {
            public string FullPath;
            public string RelPath;
        }
    }
}
