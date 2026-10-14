using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

namespace KodachiGames.Markdown.Editor.Reference
{
    public sealed class ReferencePackage
    {
        public string Root { get; }
        public string DisplayName { get; }
        public string Folder => Path.Combine(Root, "Documentation~", "Reference");
        public IReadOnlyList<ReferenceAssembly> Assemblies { get; }

        public ReferencePackage(string root, string displayName, IReadOnlyList<ReferenceAssembly> assemblies)
        {
            Root = root;
            DisplayName = displayName;
            Assemblies = assemblies;
        }
    }

    public sealed class ReferenceAssembly
    {
        public SummaryFile Summaries { get; }
        public IReadOnlyList<ReferenceType> Types { get; }

        public ReferenceAssembly(SummaryFile summaries, IReadOnlyList<ReferenceType> types)
        {
            Summaries = summaries;
            Types = types;
        }
    }

    public sealed class ReferenceType
    {
        private const BindingFlags Declared = BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic
                                              | BindingFlags.Instance | BindingFlags.Static;

        private static readonly Regex BackingField = new(@"^<(?<name>.+)>k__BackingField$", RegexOptions.Compiled);

        public Type Type { get; }
        public string Key { get; }
        public string Summary { get; }
        public string PagePath { get; }
        public string AssemblyName => Type.Assembly.GetName().Name;

        public ReferenceType(Type type, string summary, string folder)
        {
            Type = type;
            Key = TypeNames.Key(type);
            Summary = summary;
            PagePath = Path.Combine(folder, TypeNames.FileName(type));
        }

        public IEnumerable<string> InspectorFields()
        {
            if (!typeof(UnityEngine.Object).IsAssignableFrom(Type) && !Type.IsDefined(typeof(SerializableAttribute), false))
                return Enumerable.Empty<string>();
            return Type.GetFields(BindingFlags.DeclaredOnly | BindingFlags.NonPublic | BindingFlags.Instance)
                .Where(f => f.IsDefined(typeof(UnityEngine.SerializeField), false) || f.IsDefined(typeof(UnityEngine.SerializeReference), false))
                .OrderBy(f => f.MetadataToken)
                .Select(f => Signatures.InspectorField(f, InspectorName(f)));
        }

        public IEnumerable<ConstructorInfo> Constructors() =>
            typeof(UnityEngine.Object).IsAssignableFrom(Type) || typeof(Delegate).IsAssignableFrom(Type)
                ? Enumerable.Empty<ConstructorInfo>()
                : Type.GetConstructors(Declared & ~BindingFlags.Static).Where(ApiReferenceGenerator.IsDocumented).OrderBy(m => m.MetadataToken);

        public IEnumerable<PropertyInfo> Properties() =>
            Type.GetProperties(Declared).Where(ApiReferenceGenerator.IsDocumented).OrderBy(m => m.MetadataToken);

        public IEnumerable<MethodInfo> Methods() =>
            typeof(Delegate).IsAssignableFrom(Type)
                ? Enumerable.Empty<MethodInfo>()
                : Type.GetMethods(Declared)
                    .Where(m => !m.IsSpecialName || m.Name.StartsWith("op_", StringComparison.Ordinal))
                    .Where(ApiReferenceGenerator.IsDocumented)
                    .OrderBy(m => m.MetadataToken);

        public IEnumerable<EventInfo> Events() =>
            Type.GetEvents(Declared).Where(ApiReferenceGenerator.IsDocumented).OrderBy(m => m.MetadataToken);

        public IEnumerable<FieldInfo> Fields() =>
            Type.IsEnum
                ? Enumerable.Empty<FieldInfo>()
                : Type.GetFields(Declared).Where(ApiReferenceGenerator.IsDocumented).OrderBy(m => m.MetadataToken);

        public IEnumerable<FieldInfo> EnumValues() =>
            Type.IsEnum
                ? Type.GetFields(BindingFlags.Public | BindingFlags.Static).OrderBy(f => f.MetadataToken)
                : Enumerable.Empty<FieldInfo>();

        public IEnumerable<Type> MentionedTypes()
        {
            List<Type> roots = new();
            Type baseType = Signatures.BaseOf(Type);
            if (baseType != null)
                roots.Add(baseType);
            roots.AddRange(Signatures.DirectInterfaces(Type));
            if (typeof(Delegate).IsAssignableFrom(Type))
                roots.AddRange(Signature(Type.GetMethod("Invoke")));
            roots.AddRange(Constructors().SelectMany(Signature));
            roots.AddRange(Methods().SelectMany(Signature));
            roots.AddRange(Properties().Select(p => p.PropertyType));
            roots.AddRange(Properties().SelectMany(p => p.GetIndexParameters()).Select(p => p.ParameterType));
            roots.AddRange(Events().Select(e => e.EventHandlerType));
            roots.AddRange(Fields().Select(f => f.FieldType));
            roots.AddRange(Type.GetFields(BindingFlags.DeclaredOnly | BindingFlags.NonPublic | BindingFlags.Instance)
                .Where(f => f.IsDefined(typeof(UnityEngine.SerializeField), false) || f.IsDefined(typeof(UnityEngine.SerializeReference), false))
                .Select(f => f.FieldType));
            return roots.SelectMany(TypeNames.Mentioned).Distinct();
        }

        private static IEnumerable<Type> Signature(MethodBase method)
        {
            foreach (ParameterInfo parameter in method.GetParameters())
                yield return parameter.ParameterType;
            if (method is MethodInfo info)
                yield return info.ReturnType;
        }

        private static string InspectorName(FieldInfo field)
        {
            Match match = BackingField.Match(field.Name);
            return match.Success ? match.Groups["name"].Value : field.Name;
        }
    }
}
