using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace KodachiGames.Markdown.Editor.Reference
{
    public static class TypeNames
    {
        private static readonly Dictionary<Type, string> Aliases = new()
        {
            [typeof(void)] = "void",
            [typeof(bool)] = "bool",
            [typeof(byte)] = "byte",
            [typeof(sbyte)] = "sbyte",
            [typeof(char)] = "char",
            [typeof(decimal)] = "decimal",
            [typeof(double)] = "double",
            [typeof(float)] = "float",
            [typeof(int)] = "int",
            [typeof(uint)] = "uint",
            [typeof(long)] = "long",
            [typeof(ulong)] = "ulong",
            [typeof(short)] = "short",
            [typeof(ushort)] = "ushort",
            [typeof(object)] = "object",
            [typeof(string)] = "string",
        };

        public static string Key(Type type)
        {
            Type definition = type.IsGenericType ? type.GetGenericTypeDefinition() : type;
            string own = StripArity(definition.Name);
            Type[] parameters = OwnGenericArguments(definition);
            if (parameters.Length > 0)
                own += "<" + string.Join(", ", parameters.Select(p => p.Name)) + ">";
            return definition.IsNested ? Key(definition.DeclaringType) + "." + own : own;
        }

        public static string FileName(Type type) =>
            Key(type).Replace(", ", "-").Replace("<", "-").Replace(">", "") + ".md";

        public static string Display(Type type)
        {
            if (type.IsByRef)
                return Display(type.GetElementType());
            if (type.IsArray)
                return Display(type.GetElementType()) + "[" + new string(',', type.GetArrayRank() - 1) + "]";
            if (type.IsPointer)
                return Display(type.GetElementType()) + "*";
            if (type.IsGenericParameter)
                return type.Name;
            if (Aliases.TryGetValue(type, out string alias))
                return alias;
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Nullable<>))
                return Display(type.GetGenericArguments()[0]) + "?";
            if (IsValueTuple(type))
                return "(" + string.Join(", ", type.GetGenericArguments().Select(Display)) + ")";
            return Chain(type, type.IsGenericType ? type.GetGenericArguments() : Type.EmptyTypes);
        }

        public static IEnumerable<Type> Mentioned(Type type)
        {
            if (type.HasElementType)
            {
                foreach (Type inner in Mentioned(type.GetElementType()))
                    yield return inner;
                yield break;
            }
            if (type.IsGenericParameter)
                yield break;
            if (type.IsGenericType)
            {
                yield return type.GetGenericTypeDefinition();
                foreach (Type argument in type.GetGenericArguments())
                foreach (Type inner in Mentioned(argument))
                    yield return inner;
                yield break;
            }
            yield return type;
        }

        public static string Literal(object value, Type type)
        {
            if (value == null)
                return type.IsValueType ? "default" : "null";
            if (type.IsEnum || (Nullable.GetUnderlyingType(type)?.IsEnum ?? false))
            {
                Type enumType = type.IsEnum ? type : Nullable.GetUnderlyingType(type);
                return Display(enumType) + "." + Enum.GetName(enumType, value);
            }
            return value switch
            {
                bool b => b ? "true" : "false",
                string s => "\"" + s + "\"",
                char c => "'" + c + "'",
                float f => f.ToString(CultureInfo.InvariantCulture) + "f",
                double d => d.ToString(CultureInfo.InvariantCulture),
                decimal m => m.ToString(CultureInfo.InvariantCulture) + "m",
                IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
                _ => value.ToString(),
            };
        }

        public static string StripArity(string name)
        {
            int tick = name.IndexOf('`');
            return tick < 0 ? name : name.Substring(0, tick);
        }

        public static Type[] OwnGenericArguments(Type type)
        {
            Type[] all = type.GetGenericArguments();
            if (!type.IsNested)
                return all;
            int inherited = type.DeclaringType.GetGenericArguments().Length;
            return all.Skip(inherited).ToArray();
        }

        private static string Chain(Type type, Type[] arguments)
        {
            Type definition = type.IsGenericType ? type.GetGenericTypeDefinition() : type;
            int inherited = definition.IsNested ? definition.DeclaringType.GetGenericArguments().Length : 0;
            string own = StripArity(definition.Name);
            Type[] ownArguments = arguments.Skip(inherited).ToArray();
            if (ownArguments.Length > 0)
                own += "<" + string.Join(", ", ownArguments.Select(Display)) + ">";
            return definition.IsNested ? Chain(definition.DeclaringType, arguments.Take(inherited).ToArray()) + "." + own : own;
        }

        private static bool IsValueTuple(Type type) =>
            type.IsGenericType && type.Namespace == "System" && type.Name.StartsWith("ValueTuple`", StringComparison.Ordinal)
            && type.GetGenericArguments().Length < 8;
    }
}
