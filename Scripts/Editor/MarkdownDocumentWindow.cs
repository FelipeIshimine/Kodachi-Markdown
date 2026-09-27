using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace KodachiGames.Markdown.Editor
{
    public sealed class MarkdownDocumentWindow : EditorWindow
    {
        [SerializeField] private string path;

        private ScrollView _scroll;
        private VisualElement _content;

        public static void Open(string fullPath)
        {
            if (!File.Exists(fullPath))
                throw new FileNotFoundException($"No Markdown file at {fullPath}.", fullPath);

            string normalized = Path.GetFullPath(fullPath);
            MarkdownDocumentWindow window = Resources.FindObjectsOfTypeAll<MarkdownDocumentWindow>()
                .FirstOrDefault(w => string.Equals(w.path, normalized, StringComparison.OrdinalIgnoreCase));
            if (window == null)
            {
                window = CreateInstance<MarkdownDocumentWindow>();
                window.path = normalized;
                window.minSize = new Vector2(420, 300);
                window.Show();
            }

            window.titleContent = new GUIContent(TitleOf(normalized), EditorGUIUtility.IconContent("TextAsset Icon").image);
            window.Render();
            window.Focus();
        }

        public void CreateGUI()
        {
            var toolbar = new Toolbar();
            toolbar.Add(new ToolbarSpacer { flex = true });
            toolbar.Add(new ToolbarButton(Render) { text = "Reload", tooltip = "Read the document from disk again" });
            toolbar.Add(new ToolbarButton(() => EditorUtility.OpenWithDefaultApp(path)) { text = "Open Externally" });
            toolbar.Add(new ToolbarButton(() => MarkdownBrowserWindow.OpenFile(path)) { text = "Show In Browser" });
            rootVisualElement.Add(toolbar);

            _scroll = new ScrollView(ScrollViewMode.Vertical) { style = { flexGrow = 1 } };
            _content = new VisualElement { style = { paddingLeft = 12, paddingRight = 12, paddingTop = 8, paddingBottom = 12 } };
            _scroll.Add(_content);
            rootVisualElement.Add(_scroll);

            Render();
        }

        private void Render()
        {
            if (_content == null)
                return;
            MarkdownView.Populate(_content, File.ReadAllText(path));
            _scroll.scrollOffset = Vector2.zero;
        }

        private static string TitleOf(string fullPath)
        {
            string heading = File.ReadLines(fullPath).FirstOrDefault(line => line.StartsWith("# "));
            return heading != null ? heading.Substring(2).Trim() : Path.GetFileNameWithoutExtension(fullPath);
        }
    }
}
