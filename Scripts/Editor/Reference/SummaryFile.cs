using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace KodachiGames.Markdown.Editor.Reference
{
    public sealed class SummaryFile
    {
        private static readonly Regex Entry = new(@"^- `(?<key>[^`]+)`: (?<text>.+)$", RegexOptions.Compiled);

        public string Path { get; }
        public string AssemblyName { get; }
        public string AssemblySummary { get; }
        public IReadOnlyDictionary<string, string> Entries { get; }

        private SummaryFile(string path, string assemblyName, string assemblySummary, IReadOnlyDictionary<string, string> entries)
        {
            Path = path;
            AssemblyName = assemblyName;
            AssemblySummary = assemblySummary;
            Entries = entries;
        }

        public static SummaryFile Read(string path)
        {
            string assemblyName = System.IO.Path.GetFileNameWithoutExtension(path);
            string[] lines = File.ReadAllLines(path);

            string assemblySummary = string.Join(" ", lines.Where(l => l.StartsWith(">", StringComparison.Ordinal)).Select(l => l.TrimStart('>').Trim()));
            if (assemblySummary.Length == 0)
                throw new FormatException($"{path} has no blockquote line saying what the assembly {assemblyName} adds.");

            Dictionary<string, string> entries = new(StringComparer.Ordinal);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (!line.StartsWith("- `", StringComparison.Ordinal))
                    continue;
                Match match = Entry.Match(line);
                if (!match.Success)
                    throw new FormatException($"{path}:{i + 1} is not a summary entry of the form \"- `TypeName`: what it is\".");
                string key = match.Groups["key"].Value;
                if (!entries.TryAdd(key, match.Groups["text"].Value.Trim()))
                    throw new FormatException($"{path}:{i + 1} summarises {key} a second time.");
            }

            return new SummaryFile(path, assemblyName, assemblySummary, entries);
        }
    }
}
