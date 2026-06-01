using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace KodachiGames.Markdown.Editor
{
    /// <summary>
    /// Pure (no-UI) discovery + tree model for the Markdown browser.
    /// Scans the project for .md files and builds a folder tree whose
    /// single-child, file-less folder chains are collapsed into one node
    /// (the folder names stay visible, joined with '/').
    /// </summary>
    public static class MarkdownFileTree
    {
        /// <summary>Directory names that are never worth scanning for documentation.</summary>
        static readonly HashSet<string> ExcludedDirs = new(System.StringComparer.OrdinalIgnoreCase)
        {
            "Library", "Temp", "Logs", "obj", "Bee", "Build", "Builds",
            ".git", ".idea", ".vs", ".vscode", ".gradle", "node_modules",
            "UserSettings", "MemoryCaptures", "Recordings"
        };

        public sealed class FileEntry
        {
            public string Name;     // file name with extension, e.g. "CLAUDE.md"
            public string RelPath;  // path relative to the project root, '/'-separated
            public string FullPath; // absolute path on disk
        }

        public sealed class FolderNode
        {
            public string Name;     // segment name, possibly compound after collapsing ("Scripts/Editor")
            public string FullPath; // absolute path on disk (of the deepest collapsed folder)
            public List<FolderNode> Folders = new();
            public List<FileEntry> Files = new();
        }

        /// <summary>
        /// Builds the (collapsed) folder tree rooted at <paramref name="projectRoot"/>.
        /// Only branches that ultimately contain a matching .md file are kept.
        /// </summary>
        /// <param name="filter">Optional case-insensitive substring matched against each file's relative path.</param>
        public static FolderNode Build(string projectRoot, string filter = null)
        {
            projectRoot = projectRoot.Replace('\\', '/').TrimEnd('/');
            var root = new FolderNode { Name = "", FullPath = projectRoot };

            var hasFilter = !string.IsNullOrWhiteSpace(filter);
            foreach (var fullPath in Find(projectRoot))
            {
                var rel = fullPath.Substring(projectRoot.Length + 1);
                if (hasFilter && rel.IndexOf(filter, System.StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                Insert(root, rel, fullPath);
            }

            // Collapse each top-level branch, but never the synthetic root itself.
            foreach (var child in root.Folders)
                Collapse(child);

            Sort(root);
            return root;
        }

        /// <summary>Total number of files contained anywhere under the node.</summary>
        public static int CountFiles(FolderNode node)
        {
            var total = node.Files.Count;
            foreach (var f in node.Folders) total += CountFiles(f);
            return total;
        }

        static IEnumerable<string> Find(string root)
        {
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var dir = stack.Pop();

                string[] files;
                string[] subDirs;
                try
                {
                    files = Directory.GetFiles(dir, "*.md");
                    subDirs = Directory.GetDirectories(dir);
                }
                catch (System.Exception)
                {
                    // Unreadable directory (permissions, races) — just skip it.
                    continue;
                }

                foreach (var file in files)
                    yield return file.Replace('\\', '/');

                foreach (var sub in subDirs)
                {
                    var name = Path.GetFileName(sub);
                    if (ExcludedDirs.Contains(name)) continue;
                    stack.Push(sub);
                }
            }
        }

        static void Insert(FolderNode root, string relPath, string fullPath)
        {
            var parts = relPath.Split('/');
            var node = root;
            for (var i = 0; i < parts.Length - 1; i++)
            {
                var seg = parts[i];
                var child = node.Folders.Find(f => f.Name == seg);
                if (child == null)
                {
                    child = new FolderNode { Name = seg, FullPath = node.FullPath + "/" + seg };
                    node.Folders.Add(child);
                }
                node = child;
            }

            node.Files.Add(new FileEntry
            {
                Name = parts[^1],
                RelPath = relPath,
                FullPath = fullPath
            });
        }

        static void Collapse(FolderNode node)
        {
            // Collapse children first so deep chains fold from the bottom up.
            foreach (var child in node.Folders)
                Collapse(child);

            // A folder with no files of its own and exactly one sub-folder is
            // merged into that sub-folder, joining the names with '/'.
            while (node.Files.Count == 0 && node.Folders.Count == 1)
            {
                var only = node.Folders[0];
                node.Name = node.Name + "/" + only.Name;
                node.FullPath = only.FullPath;
                node.Files = only.Files;
                node.Folders = only.Folders;
            }

            Sort(node);
        }

        static void Sort(FolderNode node)
        {
            node.Folders.Sort((a, b) => string.Compare(a.Name, b.Name, System.StringComparison.OrdinalIgnoreCase));
            node.Files.Sort((a, b) => string.Compare(a.Name, b.Name, System.StringComparison.OrdinalIgnoreCase));
        }
    }
}
