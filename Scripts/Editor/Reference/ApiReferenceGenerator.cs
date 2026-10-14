using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace KodachiGames.Markdown.Editor.Reference
{
    public static class ApiReferenceGenerator
    {
        [Serializable]
        private sealed class PackageManifest
        {
            public string displayName;
        }

        [MenuItem("Kodachi/Generate API Reference")]
        private static void GenerateFromMenu()
        {
            string[] roots = UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages()
                .Select(p => p.resolvedPath)
                .Where(root => Directory.Exists(SummariesFolder(root)))
                .OrderBy(root => root, StringComparer.Ordinal)
                .ToArray();
            if (roots.Length == 0)
                throw new InvalidOperationException("No package has a Documentation~/Reference/Summaries folder, so there is no API reference to generate.");
            Debug.Log(Generate(roots));
        }

        public static string Generate(IReadOnlyList<string> packageRoots)
        {
            List<string> problems = new();
            Dictionary<string, Assembly> loaded = AppDomain.CurrentDomain.GetAssemblies()
                .GroupBy(a => a.GetName().Name)
                .ToDictionary(g => g.Key, g => g.First());
            ReferencePackage[] packages = packageRoots.Select(root => Load(root, loaded, problems)).ToArray();
            if (problems.Count > 0)
                throw new InvalidOperationException("The API reference summaries do not match the code:\n\n" + string.Join("\n\n", problems));

            Dictionary<Type, ReferenceType> pages = packages
                .SelectMany(p => p.Assemblies)
                .SelectMany(a => a.Types)
                .ToDictionary(t => t.Type);
            Dictionary<Type, IReadOnlyList<ReferenceType>> derived = pages.Values
                .SelectMany(t => Supertypes(t.Type).Select(s => (Super: ReferencePageWriter.Definition(s), Sub: t)))
                .Where(pair => pages.ContainsKey(pair.Super))
                .GroupBy(pair => pair.Super)
                .ToDictionary(g => g.Key, g => (IReadOnlyList<ReferenceType>)g.Select(p => p.Sub).OrderBy(t => t.Key, StringComparer.Ordinal).ToList());

            ReferencePageWriter writer = new(pages, derived);
            int changed = 0;
            int deleted = 0;
            StringBuilder report = new("API reference generated:");
            foreach (ReferencePackage package in packages)
            {
                Directory.CreateDirectory(package.Folder);
                HashSet<string> written = new(StringComparer.OrdinalIgnoreCase);
                changed += Write(Path.Combine(package.Folder, "README.md"), writer.Index(package), written);
                foreach (ReferenceType type in package.Assemblies.SelectMany(a => a.Types))
                    changed += Write(type.PagePath, writer.Page(package, type), written);
                foreach (string stale in Directory.GetFiles(package.Folder, "*.md").Where(f => !written.Contains(Path.GetFullPath(f))))
                {
                    File.Delete(stale);
                    deleted++;
                }
                report.Append($"\n{package.DisplayName}: {written.Count} pages");
            }

            return report.Append($"\n{changed} written, {deleted} deleted.").ToString();
        }

        public static bool IsDocumented(MemberInfo member)
        {
            throw new NotImplementedException("TODO(human): decide which declared members appear on a reference page.");
        }

        private static ReferencePackage Load(string root, IReadOnlyDictionary<string, Assembly> loaded, List<string> problems)
        {
            string folder = Path.Combine(root, "Documentation~", "Reference");
            PackageManifest manifest = JsonUtility.FromJson<PackageManifest>(File.ReadAllText(Path.Combine(root, "package.json")));
            if (string.IsNullOrEmpty(manifest.displayName))
                throw new InvalidOperationException($"{root}/package.json has no displayName.");

            List<ReferenceAssembly> assemblies = new();
            Dictionary<string, string> keys = new(StringComparer.Ordinal);
            foreach (string path in Directory.GetFiles(SummariesFolder(root), "*.md").OrderBy(Path.GetFileNameWithoutExtension, StringComparer.Ordinal))
            {
                SummaryFile summaries = SummaryFile.Read(path);
                if (!loaded.TryGetValue(summaries.AssemblyName, out Assembly assembly))
                {
                    problems.Add($"{path} names the assembly {summaries.AssemblyName}, which is not loaded in this Editor.");
                    continue;
                }

                Type[] exported = assembly.GetExportedTypes()
                    .Where(t => !Signatures.IsCompilerGenerated(t))
                    .OrderBy(TypeNames.Key, StringComparer.Ordinal)
                    .ToArray();
                HashSet<string> exportedKeys = new(exported.Select(TypeNames.Key), StringComparer.Ordinal);

                string[] missing = exported.Select(TypeNames.Key).Where(k => !summaries.Entries.ContainsKey(k)).ToArray();
                if (missing.Length > 0)
                    problems.Add($"{path} has no summary for {missing.Length} public types. Add:\n" + string.Join("\n", missing.Select(k => $"- `{k}`: ")));
                string[] stale = summaries.Entries.Keys.Where(k => !exportedKeys.Contains(k)).ToArray();
                if (stale.Length > 0)
                    problems.Add($"{path} summarises types that are not public in {summaries.AssemblyName}: " + string.Join(", ", stale));

                List<ReferenceType> types = new();
                foreach (Type type in exported.Where(t => summaries.Entries.ContainsKey(TypeNames.Key(t))))
                {
                    ReferenceType page = new(type, summaries.Entries[TypeNames.Key(type)], folder);
                    if (keys.TryGetValue(page.Key, out string other))
                        problems.Add($"{page.Key} is public in both {other} and {summaries.AssemblyName}; one reference folder cannot hold two pages with that name.");
                    else
                        keys.Add(page.Key, summaries.AssemblyName);
                    types.Add(page);
                }
                assemblies.Add(new ReferenceAssembly(summaries, types));
            }

            return new ReferencePackage(root, manifest.displayName, assemblies);
        }

        private static IEnumerable<Type> Supertypes(Type type)
        {
            Type baseType = Signatures.BaseOf(type);
            if (baseType != null)
                yield return baseType;
            if (typeof(Delegate).IsAssignableFrom(type))
                yield break;
            foreach (Type implemented in Signatures.DirectInterfaces(type))
                yield return implemented;
        }

        private static int Write(string path, string content, HashSet<string> written)
        {
            written.Add(Path.GetFullPath(path));
            if (File.Exists(path) && File.ReadAllText(path) == content)
                return 0;
            File.WriteAllText(path, content, new UTF8Encoding(false));
            return 1;
        }

        private static string SummariesFolder(string root) => Path.Combine(root, "Documentation~", "Reference", "Summaries");
    }
}
