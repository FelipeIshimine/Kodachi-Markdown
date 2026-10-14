using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace KodachiGames.Markdown.Editor.Reference
{
    public static class Signatures
    {
        private static readonly Dictionary<string, string> Operators = new()
        {
            ["op_Equality"] = "==",
            ["op_Inequality"] = "!=",
            ["op_LessThan"] = "<",
            ["op_GreaterThan"] = ">",
            ["op_LessThanOrEqual"] = "<=",
            ["op_GreaterThanOrEqual"] = ">=",
            ["op_Addition"] = "+",
            ["op_Subtraction"] = "-",
            ["op_Multiply"] = "*",
            ["op_Division"] = "/",
            ["op_Modulus"] = "%",
            ["op_UnaryNegation"] = "-",
            ["op_UnaryPlus"] = "+",
            ["op_LogicalNot"] = "!",
            ["op_OnesComplement"] = "~",
            ["op_BitwiseAnd"] = "&",
            ["op_BitwiseOr"] = "|",
            ["op_ExclusiveOr"] = "^",
            ["op_Increment"] = "++",
            ["op_Decrement"] = "--",
            ["op_True"] = "true",
            ["op_False"] = "false",
        };

        public static string KindOf(Type type)
        {
            if (type.IsEnum)
                return type.IsDefined(typeof(FlagsAttribute), false) ? "flags enum" : "enum";
            if (type.IsInterface)
                return "interface";
            if (typeof(Delegate).IsAssignableFrom(type))
                return "delegate";
            if (type.IsValueType)
            {
                string prefix = (HasAttribute(type, "IsReadOnlyAttribute") ? "readonly " : "") + (HasAttribute(type, "IsByRefLikeAttribute") ? "ref " : "");
                return prefix + (IsRecord(type) ? "record struct" : "struct");
            }
            string noun = IsRecord(type) ? "record" : "class";
            if (type.IsAbstract && type.IsSealed)
                return "static " + noun;
            if (type.IsAbstract)
                return "abstract " + noun;
            return type.IsSealed ? "sealed " + noun : noun;
        }

        public static Type BaseOf(Type type)
        {
            Type baseType = type.BaseType;
            if (baseType == null || baseType == typeof(object) || baseType == typeof(ValueType) || baseType == typeof(Enum)
                || baseType == typeof(MulticastDelegate))
                return null;
            return baseType;
        }

        public static Type[] DirectInterfaces(Type type)
        {
            Type[] all = type.GetInterfaces();
            HashSet<Type> inherited = new(type.BaseType?.GetInterfaces() ?? Type.EmptyTypes);
            foreach (Type candidate in all)
            foreach (Type implied in candidate.GetInterfaces())
                inherited.Add(implied);
            return all.Where(i => !inherited.Contains(i) && i.IsVisible).OrderBy(TypeNames.Display, StringComparer.Ordinal).ToArray();
        }

        public static string Declaration(Type type)
        {
            if (typeof(Delegate).IsAssignableFrom(type) && type != typeof(Delegate))
            {
                MethodInfo invoke = type.GetMethod("Invoke");
                return $"public delegate {Return(invoke)} {TypeNames.Key(type).Split('.').Last()}({Parameters(invoke)}){Constraints(TypeNames.OwnGenericArguments(type))};";
            }

            string name = TypeNames.Key(type).Split('.').Last();
            string kind = KindOf(type).Replace("flags ", "");
            List<string> supertypes = new();
            if (type.IsEnum)
            {
                Type underlying = Enum.GetUnderlyingType(type);
                if (underlying != typeof(int))
                    supertypes.Add(TypeNames.Display(underlying));
            }
            else
            {
                Type baseType = BaseOf(type);
                if (baseType != null)
                    supertypes.Add(TypeNames.Display(baseType));
                supertypes.AddRange(DirectInterfaces(type).Select(TypeNames.Display));
            }

            string flags = type.IsEnum && type.IsDefined(typeof(FlagsAttribute), false) ? "[Flags]\n" : "";
            string extends = supertypes.Count > 0 ? " : " + string.Join(", ", supertypes) : "";
            return $"{flags}public {kind} {name}{extends}{Constraints(TypeNames.OwnGenericArguments(type))}";
        }

        public static string Constructor(ConstructorInfo constructor, Access access) =>
            $"{Word(access)}{TypeNames.StripArity(constructor.DeclaringType.Name)}({Parameters(constructor)});";

        public static string Method(MethodInfo method, Access access)
        {
            string modifiers = Word(access) + Modifiers(method);
            if (Operators.TryGetValue(method.Name, out string symbol))
                return $"{modifiers}{Return(method)} operator {symbol}({Parameters(method)});";
            if (method.Name is "op_Implicit" or "op_Explicit")
                return $"{modifiers}{(method.Name == "op_Implicit" ? "implicit" : "explicit")} operator {TypeNames.Display(method.ReturnType)}({Parameters(method)});";
            if (method.Name.StartsWith("op_", StringComparison.Ordinal))
                throw new InvalidOperationException($"{method.DeclaringType.FullName}.{method.Name} is an operator the reference generator does not know how to print.");

            string generics = method.IsGenericMethodDefinition
                ? "<" + string.Join(", ", method.GetGenericArguments().Select(a => a.Name)) + ">"
                : "";
            string constraints = method.IsGenericMethodDefinition ? Constraints(method.GetGenericArguments()) : "";
            return $"{modifiers}{Return(method)} {method.Name}{generics}({Parameters(method)}){constraints};";
        }

        public static string Property(PropertyInfo property, Access access)
        {
            MethodInfo getter = property.GetMethod;
            MethodInfo setter = property.SetMethod;
            MethodInfo widest = getter ?? setter;
            List<string> accessors = new();
            if (getter != null && Signatures.AccessOf(getter) != Access.Hidden)
                accessors.Add(Prefix(Signatures.AccessOf(getter), access) + "get;");
            if (setter != null && Signatures.AccessOf(setter) != Access.Hidden)
                accessors.Add(Prefix(Signatures.AccessOf(setter), access) + (IsInitOnly(setter) ? "init;" : "set;"));

            ParameterInfo[] index = property.GetIndexParameters();
            string name = index.Length > 0 ? $"this[{string.Join(", ", index.Select(Parameter))}]" : property.Name;
            string byRef = property.PropertyType.IsByRef ? "ref " : "";
            return $"{Word(access)}{Modifiers(widest)}{byRef}{TypeNames.Display(property.PropertyType)} {name} {{ {string.Join(" ", accessors)} }}";
        }

        public static string Event(EventInfo eventInfo, Access access) =>
            $"{Word(access)}{Modifiers(eventInfo.AddMethod)}event {TypeNames.Display(eventInfo.EventHandlerType)} {eventInfo.Name};";

        public static string Field(FieldInfo field, Access access)
        {
            if (field.IsLiteral)
                return $"{Word(access)}const {TypeNames.Display(field.FieldType)} {field.Name} = {TypeNames.Literal(field.GetRawConstantValue(), field.FieldType)};";
            string modifiers = (field.IsStatic ? "static " : "") + (field.IsInitOnly ? "readonly " : "");
            return $"{Word(access)}{modifiers}{TypeNames.Display(field.FieldType)} {field.Name};";
        }

        public static string InspectorField(FieldInfo field, string name) =>
            $"[{(field.IsDefined(typeof(UnityEngine.SerializeReference), false) ? "SerializeReference" : "SerializeField")}] {TypeNames.Display(field.FieldType)} {name};";

        public static string EnumValue(FieldInfo field) =>
            $"{field.Name} = {Convert.ToString(field.GetRawConstantValue(), System.Globalization.CultureInfo.InvariantCulture)},";

        public static Access AccessOf(MemberInfo member)
        {
            switch (member)
            {
                case MethodBase method:
                    if (method.IsPublic)
                        return Access.Public;
                    return method.IsFamily || method.IsFamilyOrAssembly ? Access.Protected : Access.Hidden;
                case FieldInfo field:
                    if (field.IsPublic)
                        return Access.Public;
                    return field.IsFamily || field.IsFamilyOrAssembly ? Access.Protected : Access.Hidden;
                case PropertyInfo property:
                    return Widest(property.GetMethod, property.SetMethod);
                case EventInfo eventInfo:
                    return Widest(eventInfo.AddMethod, eventInfo.RemoveMethod);
                default:
                    throw new ArgumentException($"{member.MemberType} {member.Name} is not a member the reference generator documents.", nameof(member));
            }
        }

        public static bool IsCompilerGenerated(MemberInfo member) =>
            member.IsDefined(typeof(CompilerGeneratedAttribute), false) || member.Name.Contains('<');

        private static Access Widest(MethodBase first, MethodBase second)
        {
            Access a = first == null ? Access.Hidden : AccessOf(first);
            Access b = second == null ? Access.Hidden : AccessOf(second);
            return (Access)Math.Min((int)a, (int)b);
        }

        private static string Word(Access access) => access switch
        {
            Access.Public => "public ",
            Access.Protected => "protected ",
            _ => throw new ArgumentOutOfRangeException(nameof(access), access, "Hidden members are never printed."),
        };

        private static string Prefix(Access accessor, Access member) =>
            accessor == member ? "" : Word(accessor);

        private static string Modifiers(MethodInfo method)
        {
            string staticWord = method.IsStatic ? "static " : "";
            if (method.DeclaringType.IsInterface)
                return method.IsStatic && method.IsAbstract ? "static abstract " : staticWord;
            if (method.IsAbstract)
                return "abstract ";
            bool overrides = method.GetBaseDefinition().DeclaringType != method.DeclaringType;
            if (method.IsVirtual && overrides)
                return method.IsFinal ? "sealed override " : "override ";
            if (method.IsVirtual && !method.IsFinal)
                return "virtual ";
            return staticWord;
        }

        private static string Return(MethodInfo method)
        {
            Type type = method.ReturnType;
            if (!type.IsByRef)
                return TypeNames.Display(type);
            bool readOnly = method.ReturnParameter.GetCustomAttributes(false).Any(a => a.GetType().Name == "IsReadOnlyAttribute");
            return (readOnly ? "ref readonly " : "ref ") + TypeNames.Display(type);
        }

        private static string Parameters(MethodBase method)
        {
            ParameterInfo[] parameters = method.GetParameters();
            bool extension = method.IsDefined(typeof(ExtensionAttribute), false);
            return string.Join(", ", parameters.Select((p, i) => (extension && i == 0 ? "this " : "") + Parameter(p)));
        }

        private static string Parameter(ParameterInfo parameter)
        {
            string prefix = "";
            if (parameter.ParameterType.IsByRef)
            {
                if (parameter.IsOut)
                    prefix = "out ";
                else if (parameter.GetCustomAttributes(false).Any(a => a.GetType().Name == "IsReadOnlyAttribute"))
                    prefix = "in ";
                else
                    prefix = "ref ";
            }
            if (parameter.IsDefined(typeof(ParamArrayAttribute), false))
                prefix = "params ";
            string defaultValue = parameter.HasDefaultValue
                ? " = " + TypeNames.Literal(parameter.DefaultValue, parameter.ParameterType)
                : "";
            return $"{prefix}{TypeNames.Display(parameter.ParameterType)} {parameter.Name}{defaultValue}";
        }

        private static string Constraints(Type[] parameters)
        {
            List<string> clauses = new();
            foreach (Type parameter in parameters)
            {
                List<string> parts = new();
                GenericParameterAttributes attributes = parameter.GenericParameterAttributes;
                bool isStruct = (attributes & GenericParameterAttributes.NotNullableValueTypeConstraint) != 0;
                if (isStruct)
                    parts.Add(HasAttribute(parameter, "IsUnmanagedAttribute") ? "unmanaged" : "struct");
                else if ((attributes & GenericParameterAttributes.ReferenceTypeConstraint) != 0)
                    parts.Add("class");
                parts.AddRange(parameter.GetGenericParameterConstraints()
                    .Where(c => !(isStruct && c == typeof(ValueType)))
                    .Select(TypeNames.Display));
                if (!isStruct && (attributes & GenericParameterAttributes.DefaultConstructorConstraint) != 0)
                    parts.Add("new()");
                if (parts.Count > 0)
                    clauses.Add($" where {parameter.Name} : {string.Join(", ", parts)}");
            }
            return string.Concat(clauses);
        }

        private static bool IsInitOnly(MethodInfo setter) =>
            setter.ReturnParameter.GetRequiredCustomModifiers().Any(m => m.Name == "IsExternalInit");

        private static bool IsRecord(Type type) =>
            type.GetMethod("<Clone>$", BindingFlags.Public | BindingFlags.Instance) != null
            || (type.IsValueType && type.GetMethod("PrintMembers", BindingFlags.NonPublic | BindingFlags.Instance) is { } print
                && print.IsDefined(typeof(CompilerGeneratedAttribute), false));

        private static bool HasAttribute(MemberInfo member, string name) =>
            member.CustomAttributes.Any(a => a.AttributeType.Name == name);
    }

    public enum Access
    {
        Public,
        Protected,
        Hidden,
    }
}
