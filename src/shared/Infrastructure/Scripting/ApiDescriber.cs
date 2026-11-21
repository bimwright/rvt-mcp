using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace RvtMcp.Plugin
{
    /// <summary>
    /// Lists the public surface of one Revit API type as loaded in this Revit: constructors and static factories, members
    /// (inherited ones grouped by the API type that declares them), enum values, base types and direct subtypes. Lines read
    /// like C#, so a send_code script can be written from them. Host-free: it only reflects over the assemblies it is given.
    /// </summary>
    internal static class ApiDescriber
    {
        internal const int MaxLines = 200;
        private const int MaxSubtypes = 40;
        private const string ApiNamespace = "Autodesk.Revit";

        private static readonly ConcurrentDictionary<string, Type[]> TypeCache = new ConcurrentDictionary<string, Type[]>();

        internal sealed class Result
        {
            public string Error;
            public object Value;
        }

        internal static Result Describe(IEnumerable<Assembly> assemblies, string typeName, string memberFilter)
        {
            var wanted = (typeName ?? "").Trim().Replace('+', '.');
            if (wanted.Length == 0) return Fail("INVALID_ARGUMENT: type is required, e.g. \"Wall\" or \"Autodesk.Revit.DB.Wall\".");
            var types = Types(assemblies.ToArray());

            var matches = Find(types, wanted, StringComparison.Ordinal);
            if (matches.Length == 0) matches = Find(types, wanted, StringComparison.OrdinalIgnoreCase);
            if (matches.Length > 1)
                return Fail($"AMBIGUOUS_TYPE: '{wanted}' matches {string.Join(", ", matches.Take(10).Select(FullName))}. Pass the full name.");
            if (matches.Length == 0)
            {
                var shortName = wanted.Substring(wanted.LastIndexOf('.') + 1);
                var near = ScriptCompileHints.Nearest(types.Select(t => ShortName(t)), shortName, 6, minShared: 3);
                return Fail($"TYPE_NOT_FOUND: no public Revit API type '{wanted}' in this Revit version." +
                            (near.Length == 0 ? "" : " Closest: " + string.Join(", ", near) + "."));
            }

            var type = matches[0];
            var filter = string.IsNullOrWhiteSpace(memberFilter) ? null : memberFilter.Trim();
            var groups = new List<KeyValuePair<string, List<string>>>();
            var lines = 0;
            var total = 0;
            foreach (var owner in Chain(type))
            {
                var own = Members(owner, type, filter).ToList();
                total += own.Count;
                var room = Math.Max(0, MaxLines - lines);
                if (own.Count > room) own = own.Take(room).ToList();
                lines += own.Count;
                if (own.Count > 0) groups.Add(new KeyValuePair<string, List<string>>(owner == type ? "declared" : "from " + ShortName(owner), own));
            }

            var subtypes = types.Where(t => t.BaseType == type).Select(t => ShortName(t)).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            var value = new Dictionary<string, object>
            {
                ["type"] = FullName(type),
                ["kind"] = Kind(type),
                ["base_types"] = Chain(type).Skip(1).Select(t => ShortName(t)).ToArray(),
            };
            var interfaces = type.GetInterfaces().Where(IsApi).Select(t => ShortName(t)).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            if (interfaces.Length > 0) value["interfaces"] = interfaces;
            if (subtypes.Length > 0) value["subtypes"] = subtypes.Take(MaxSubtypes).ToArray();
            if (subtypes.Length > MaxSubtypes) value["subtypes_total"] = subtypes.Length;
            if (filter != null) value["member_filter"] = filter;
            value["members"] = groups.ToDictionary(g => g.Key, g => (object)g.Value.ToArray());
            value["member_count"] = total;
            if (lines < total)
            {
                value["truncated"] = true;
                value["note"] = $"Showing {lines} of {total} members. Pass member=\"<part of a name>\" to narrow the list.";
            }
            if (filter != null && total == 0)
                value["note"] = $"No member of {ShortName(type)} or its API base types has '{filter}' in its name.";
            return new Result { Value = value };
        }

        private static Result Fail(string error) => new Result { Error = error };

        private static Type[] Find(Type[] types, string wanted, StringComparison comparison) =>
            types.Where(t =>
            {
                var full = FullName(t);
                return full.Equals(wanted, comparison) || full.EndsWith("." + wanted, comparison);
            }).OrderBy(FullName, StringComparer.Ordinal).ToArray();

        /// <summary>Public API types of the given assemblies, cached per assembly set (types the loader cannot read are skipped).</summary>
        private static Type[] Types(Assembly[] assemblies)
        {
            var key = string.Join("|", assemblies.Select(a => a.FullName + "#" + a.ManifestModule.ModuleVersionId).OrderBy(s => s, StringComparer.Ordinal));
            return TypeCache.GetOrAdd(key, _ => assemblies.SelectMany(a =>
            {
                try { return a.GetExportedTypes(); }
                catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null && t.IsPublic || t?.IsNestedPublic == true).ToArray(); }
                catch { return new Type[0]; }
            }).Where(IsApi).ToArray());
        }

        /// <summary>The type and its base types inside the Revit API, nearest first.</summary>
        private static IEnumerable<Type> Chain(Type type)
        {
            for (var t = type; t != null && IsApi(t); t = t.BaseType) yield return t;
        }

        private static IEnumerable<string> Members(Type owner, Type described, string filter)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            bool Wanted(string name) => filter == null || name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
            var lines = new List<(int order, string name, string line)>();

            if (owner.IsEnum)
            {
                foreach (var name in Enum.GetNames(owner).Where(Wanted)) lines.Add((0, name, name));
                return lines.Select(l => l.line);
            }
            if (owner == described)
            {
                foreach (var c in owner.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
                    if (Wanted(owner.Name)) lines.Add((0, "", $"new {ShortName(owner)}({Parameters(c.GetParameters())}){Obsolete(c)}"));
            }
            var defaultMember = owner.GetCustomAttributesData().FirstOrDefault(a => a.AttributeType.FullName == "System.Reflection.DefaultMemberAttribute")
                ?.ConstructorArguments.FirstOrDefault().Value as string;

            foreach (var p in owner.GetProperties(flags))
            {
                var getter = p.GetGetMethod();
                var setter = p.GetSetMethod();
                var accessor = getter ?? setter;
                // A named indexed property is called as get_Name(...) in C#, so a filter written that way finds it too.
                if (accessor == null || !(Wanted(p.Name) || Wanted("get_" + p.Name) || Wanted("set_" + p.Name))) continue;
                var stat = accessor.IsStatic ? "static " : "";
                var index = p.GetIndexParameters();
                if (index.Length == 0)
                    lines.Add((accessor.IsStatic ? 1 : 2, p.Name, $"{stat}{p.Name}: {TypeName(p.PropertyType)} {{ {(getter != null ? "get; " : "")}{(setter != null ? "set; " : "")}}}{Obsolete(p)}"));
                else if (p.Name == defaultMember)
                    lines.Add((2, p.Name, $"this[{Parameters(index)}]: {TypeName(p.PropertyType)} {{ {(getter != null ? "get; " : "")}{(setter != null ? "set; " : "")}}}{Obsolete(p)}"));
                else
                {
                    // C# cannot use a named indexed property; it calls the accessors: element.get_Parameter(BuiltInParameter.X).
                    if (getter != null) lines.Add((3, p.Name, $"{stat}get_{p.Name}({Parameters(index)}) -> {TypeName(p.PropertyType)}{Obsolete(p)}"));
                    if (setter != null) lines.Add((3, p.Name, $"{stat}set_{p.Name}({Parameters(setter.GetParameters())}){Obsolete(p)}"));
                }
            }
            foreach (var f in owner.GetFields(flags).Where(f => Wanted(f.Name)))
                lines.Add((1, f.Name, $"{(f.IsLiteral ? "const " : f.IsStatic ? "static " : "")}{f.Name}: {TypeName(f.FieldType)}{Obsolete(f)}"));
            foreach (var e in owner.GetEvents(flags).Where(e => Wanted(e.Name)))
                lines.Add((4, e.Name, $"event {e.Name}: {TypeName(e.EventHandlerType)}{Obsolete(e)}"));
            foreach (var m in owner.GetMethods(flags).Where(m => !m.IsSpecialName && Wanted(m.Name)))
            {
                var generic = m.IsGenericMethodDefinition ? "<" + string.Join(", ", m.GetGenericArguments().Select(a => a.Name)) + ">" : "";
                // Static members of the described type are its factories (Line.CreateBound), listed before instance members.
                lines.Add((m.IsStatic ? 1 : 3, m.Name, $"{(m.IsStatic ? "static " : "")}{m.Name}{generic}({Parameters(m.GetParameters())}) -> {TypeName(m.ReturnType)}{Obsolete(m)}"));
            }
            return lines.OrderBy(l => l.order).ThenBy(l => l.name, StringComparer.Ordinal).ThenBy(l => l.line, StringComparer.Ordinal)
                .Select(l => l.line).Distinct();
        }

        private static string Parameters(ParameterInfo[] parameters) =>
            string.Join(", ", parameters.Select(p =>
            {
                var type = p.ParameterType;
                var prefix = "";
                if (type.IsByRef)
                {
                    prefix = p.IsOut ? "out " : "ref ";
                    type = type.GetElementType();
                }
                return prefix + TypeName(type) + " " + p.Name + (p.IsOptional ? " = optional" : "");
            }));

        private static string Obsolete(MemberInfo member)
        {
            CustomAttributeData attribute;
            try { attribute = member.GetCustomAttributesData().FirstOrDefault(a => a.AttributeType.FullName == "System.ObsoleteAttribute"); }
            catch { return ""; }
            if (attribute == null) return "";
            var message = attribute.ConstructorArguments.Count > 0 ? attribute.ConstructorArguments[0].Value as string : null;
            message = string.IsNullOrWhiteSpace(message) ? "" : ": " + message.Trim();
            if (message.Length > 160) message = message.Substring(0, 159) + "…";
            return " [obsolete" + message + "]";
        }

        // By name, not by Type: the Revit API may be reflected from another load context than this code's own types.
        private static readonly Dictionary<string, string> Keywords = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["System.Void"] = "void", ["System.Boolean"] = "bool", ["System.Byte"] = "byte", ["System.SByte"] = "sbyte", ["System.Char"] = "char",
            ["System.Int16"] = "short", ["System.UInt16"] = "ushort", ["System.Int32"] = "int", ["System.UInt32"] = "uint", ["System.Int64"] = "long",
            ["System.UInt64"] = "ulong", ["System.Single"] = "float", ["System.Double"] = "double", ["System.Decimal"] = "decimal",
            ["System.String"] = "string", ["System.Object"] = "object",
        };

        /// <summary>The C# spelling a script would use: int, IList&lt;ElementId&gt;, XYZ[], double?.</summary>
        internal static string TypeName(Type type)
        {
            if (type == null) return "?";
            if (type.IsByRef) return TypeName(type.GetElementType());
            if (type.FullName != null && Keywords.TryGetValue(type.FullName, out var keyword)) return keyword;
            if (type.IsArray) return TypeName(type.GetElementType()) + "[" + new string(',', type.GetArrayRank() - 1) + "]";
            if (type.IsGenericParameter) return type.Name;
            if (type.IsGenericType && type.GetGenericTypeDefinition().FullName == "System.Nullable`1") return TypeName(type.GetGenericArguments()[0]) + "?";
            var name = StripArity(type.Name);
            if (type.IsGenericType) name += "<" + string.Join(", ", type.GetGenericArguments().Select(TypeName)) + ">";
            return type.IsNested && !type.IsGenericParameter ? TypeName(type.DeclaringType) + "." + name : name;
        }

        private static string ShortName(Type type) => type.IsNested ? ShortName(type.DeclaringType) + "." + StripArity(type.Name) : StripArity(type.Name);

        private static string FullName(Type type) => StripArity((type.FullName ?? type.Name).Replace('+', '.'));

        private static string StripArity(string name)
        {
            var tick = name.IndexOf('`');
            return tick < 0 ? name : name.Substring(0, tick);
        }

        private static string Kind(Type type) =>
            type.IsEnum ? "enum" : type.IsInterface ? "interface" : type.IsValueType ? "struct" : type.IsAbstract && type.IsSealed ? "static class"
            : type.IsAbstract ? "abstract class" : "class";

        private static bool IsApi(Type type)
        {
            var ns = type?.Namespace;
            return ns != null && (ns == ApiNamespace || ns.StartsWith(ApiNamespace + ".", StringComparison.Ordinal));
        }
    }
}
