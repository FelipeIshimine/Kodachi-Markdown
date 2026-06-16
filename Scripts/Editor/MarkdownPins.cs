using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace KodachiGames.Markdown.Editor
{
    /// <summary>
    /// Persistent store of "pinned" Markdown files. Pins survive editor restarts
    /// (stored in <see cref="EditorPrefs"/>, keyed per project) and drive the
    /// <see cref="MarkdownPinPopup"/> quick-view window.
    ///
    /// Paths are stored relative to the project root ('/'-separated) so the store
    /// stays portable if the project folder moves.
    /// </summary>
    public static class MarkdownPins
    {
        // EditorPrefs is machine-global, so namespace the key by project path to
        // avoid leaking pins between different projects opened on the same machine.
        static string PrefKey => "KodachiMarkdown.Pins::" + ProjectRoot;

        public static string ProjectRoot =>
            Directory.GetParent(Application.dataPath)!.FullName.Replace('\\', '/');

        /// <summary>Raised whenever the set of pins changes (add/remove/clear).</summary>
        public static event Action Changed;

        /// <summary>Relative paths of every pinned file, in pin order.</summary>
        public static IReadOnlyList<string> RelPaths => Load();

        public static bool IsPinned(string fullPath) =>
            Load().Contains(ToRel(fullPath), StringComparer.OrdinalIgnoreCase);

        public static void Toggle(string fullPath)
        {
            if (IsPinned(fullPath)) Remove(fullPath);
            else Add(fullPath);
        }

        public static void Add(string fullPath)
        {
            var rel = ToRel(fullPath);
            var list = Load();
            if (list.Any(p => string.Equals(p, rel, StringComparison.OrdinalIgnoreCase)))
                return;
            list.Add(rel);
            Save(list);
        }

        public static void Remove(string fullPath)
        {
            var rel = ToRel(fullPath);
            var list = Load();
            if (list.RemoveAll(p => string.Equals(p, rel, StringComparison.OrdinalIgnoreCase)) > 0)
                Save(list);
        }

        public static void Clear()
        {
            Save(new List<string>());
        }

        /// <summary>Absolute path on disk for a stored relative path.</summary>
        public static string ToFull(string relPath) => ProjectRoot + "/" + relPath.TrimStart('/');

        static string ToRel(string fullPath)
        {
            fullPath = fullPath.Replace('\\', '/');
            var root = ProjectRoot + "/";
            return fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                ? fullPath.Substring(root.Length)
                : fullPath;
        }

        static List<string> Load()
        {
            var raw = EditorPrefs.GetString(PrefKey, "");
            if (string.IsNullOrEmpty(raw))
                return new List<string>();

            var list = raw.Split('\n').Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
            var pruned = list.Where(p => File.Exists(ToFull(p))).ToList();
            if (pruned.Count != list.Count)
                Save(pruned);
            return pruned;
        }

        static void Save(List<string> list)
        {
            EditorPrefs.SetString(PrefKey, string.Join("\n", list));
            Changed?.Invoke();
        }
    }
}
