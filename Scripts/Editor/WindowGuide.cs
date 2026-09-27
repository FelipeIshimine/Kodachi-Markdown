using System;
using System.IO;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace KodachiGames.Markdown.Editor
{
    public static class WindowGuide
    {
        private const string Tooltip = "Open this window's guide";

        public static ToolbarButton ToolbarButton(Type window, string page) =>
            WithIcon(new ToolbarButton(() => Open(window, page)) { tooltip = Tooltip });

        public static Button Button(Type window, string page) =>
            WithIcon(new Button(() => Open(window, page)) { tooltip = Tooltip });

        public static void Open(Type window, string page) => MarkdownDocumentWindow.Open(PathOf(window, page));

        public static string PathOf(Type window, string page)
        {
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(window.Assembly)
                          ?? throw new InvalidOperationException($"{window.Name} does not live in a package, so it has no Documentation~ folder.");
            return Path.Combine(package.resolvedPath, "Documentation~", "Windows", page + ".md");
        }

        private static T WithIcon<T>(T button) where T : VisualElement
        {
            Texture icon = EditorGUIUtility.IconContent("_Help").image;
            button.style.flexDirection = FlexDirection.Row;
            button.style.alignItems = Align.Center;
            button.style.justifyContent = Justify.Center;
            button.style.flexShrink = 0;
            button.style.paddingLeft = 3;
            button.style.paddingRight = 3;
            button.Add(new Image { image = icon, scaleMode = ScaleMode.ScaleToFit, style = { width = 16, height = 16 } });
            return button;
        }
    }
}
