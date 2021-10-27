﻿using Il2CppModdingCodegen.Data.DllHandling;
using Il2CppModdingCodegen.Serialization;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Il2CppModdingCodegen.Data
{
    public abstract class TypeRefakjshdfkjahsdfjk : IEquatable<TypeRefakjshdfkjahsdfjk>
    {
        private const string NoNamespace = "GlobalNamespace";

        public abstract string Namespace { get; }
        public abstract string Name { get; }

        public virtual bool IsGenericParameter { get; } = false;
        public virtual bool IsCovariant { get; } = false;
        public virtual IReadOnlyList<TypeRefakjshdfkjahsdfjk> GenericParameterConstraints { get; } = new List<TypeRefakjshdfkjahsdfjk>();

        // True iff the type has any Generics (generic arguments/parameters). Generic parameters themselves don't count!
        internal bool IsGeneric { get => IsGenericInstance || IsGenericTemplate; }

        public abstract bool IsGenericInstance { get; }
        public abstract bool IsGenericTemplate { get; }
        public abstract IReadOnlyList<TypeRefakjshdfkjahsdfjk> Generics { get; }

        protected bool UnNested { get; set; } = false;
        public TypeRefakjshdfkjahsdfjk? DeclaringType { get => UnNested ? null : OriginalDeclaringType; }
        public abstract TypeRefakjshdfkjahsdfjk? OriginalDeclaringType { get; }
        public abstract TypeRefakjshdfkjahsdfjk? ElementType { get; }

        private ITypeData? _resolvedType;

        internal DllTypeRef AsDllTypeRef { get => this as DllTypeRef ?? throw new Exception("DumpTypeRefs in my DllTypeRefs?!"); }

        public abstract TypeRefakjshdfkjahsdfjk MakePointer();

        /// <summary>
        /// Resolves the type from the given type collection
        /// </summary>
        internal ITypeData? Resolve(ITypeCollection types)
        {
#pragma warning disable 612, 618
            // TODO: if we upgrade to C# 8.0, change this to `_resolvedType ??= types.Resolve(this);`
            _resolvedType ??= types.Resolve(this);
#pragma warning restore 612, 618
            return _resolvedType;
        }

        internal class GenericTypeMap : Dictionary<TypeRefakjshdfkjahsdfjk, TypeRefakjshdfkjahsdfjk> { };

        internal abstract TypeRefakjshdfkjahsdfjk MakeGenericInstance(GenericTypeMap genericTypes);

        internal GenericTypeMap ExtractGenericMap(ITypeCollection types)
        {
            if (!IsGenericInstance) throw new InvalidOperationException("Must be called on a generic instance!");
            var resolved = Resolve(types) ?? throw new UnresolvedTypeException(this, this);
            var ret = new GenericTypeMap();
            foreach (var (a, b) in resolved.This.Generics.Zip(Generics, (a, b) => (a, b)))
                ret.Add(a, b);
            return ret;
        }

        public virtual bool IsVoid() => Name.Equals("void", StringComparison.OrdinalIgnoreCase);

        public virtual bool IsPointer()
        {
            // If type is not a value type, it is a pointer
            return _resolvedType?.Info.Refness == Refness.ReferenceType;
        }

        public abstract bool IsArray();

        public string CppNamespace() => string.IsNullOrEmpty(Namespace) ? NoNamespace : Namespace.Replace(".", "::");

        public string CppName()
        {
            if (Name.StartsWith("!"))
                throw new InvalidOperationException("Tried to get the name of a copied generic parameter!");
            var name = Name.Replace('`', '_').Replace('<', '$').Replace('>', '$');
            name = Utils.SafeName(name);
            if (UnNested)
            {
                if (OriginalDeclaringType == null)
                    throw new NullReferenceException("DeclaringType was null despite UnNested being true!");
                name = OriginalDeclaringType.CppName() + "_" + name;
            }
            return name;
        }

        public string GetQualifiedCppName()
        {
            var name = CppName();
            var dt = this;
            while (dt.DeclaringType != null)
            {
                name = dt.DeclaringType.CppName() + "::" + name;
                dt = dt.DeclaringType;
            }
            // Namespace obtained from final declaring type
            return dt.CppNamespace() + "::" + name;
        }

        public (string, string) GetIl2CppName()
        {
            var name = Name;
            var dt = this;
            while (dt.OriginalDeclaringType != null)
            {
                name = dt.OriginalDeclaringType.Name + "/" + name;
                dt = dt.OriginalDeclaringType;
            }
            // Namespace obtained from final declaring type
            return (dt.Namespace.Replace("::", "."), name);
        }

        // TODO: new method/param to easily allow for getting only the new generic templates that this TypeRefakjshdfkjahsdfjk brings to the table?
        public IEnumerable<TypeRefakjshdfkjahsdfjk> GetDeclaredGenerics(bool includeSelf)
        {
            var genericsDefined = new List<TypeRefakjshdfkjahsdfjk>();
            // Populate genericsDefined with all TypeRefs that are used in a declaring type
            var dt = includeSelf ? this : DeclaringType;
            var genericsColl = new List<IReadOnlyList<TypeRefakjshdfkjahsdfjk>>();
            if (!includeSelf)
                genericsColl.Add(Generics);
            while (dt != null)
            {
                if (dt.IsGeneric)
                {
                    foreach (var g in dt.Generics.Reverse())
                    {
                        if (IsGenericInstance && g.Name.StartsWith('!') && genericsColl.Count > 0)
                        {
                            // If we are a generic instance, and we see that the name of our generic parameter starts with a !
                            var idx = int.Parse(g.Name[1..]);
                            bool assigned = false;
                            foreach (var genericsOptions in genericsColl)
                            {
                                // Find the match
                                var match = genericsOptions[idx];
                                if (match.Name.StartsWith('!'))
                                {
                                    // If we are a reference, continue through the generics collections
                                    idx = int.Parse(match.Name[1..]);
                                }
                                else
                                {
                                    // Add the match if it isn't another reference
                                    genericsDefined.Insert(0, match);
                                    assigned = true;
                                    break;
                                }
                            }
                            if (!assigned)
                            {
                                throw new InvalidOperationException($"Could not find match for generic reference: {g.Name}!");
                            }
                        }
                        else
                            // We want the highest level declaring type's first generic parameter (template or argument) to be first in our genericsDefined list
                            genericsDefined.Insert(0, g);
                    }
                    genericsColl.Add(dt.Generics);
                }
                dt = dt.DeclaringType;
            }
            // Return only the first occurance of each of the generic parameters (template or argument)
            // Do not compare the generic types' declaring types (use the fastCompararer)
            return genericsDefined.Distinct(fastComparer);
        }

        // Returns true iff pred returns true for this type or any ElementType (or DeclaringType, except on generic parameters) recursively.
        internal bool IsOrContainsMatch(Predicate<TypeRefakjshdfkjahsdfjk> pred)
        {
            if (pred(this)) return true;
            if (ElementType != null && ElementType.IsOrContainsMatch(pred)) return true;
            if (DeclaringType != null && !IsGenericParameter && DeclaringType.IsOrContainsMatch(pred)) return true;
            return false;
        }

        internal bool ContainsOrEquals(TypeRefakjshdfkjahsdfjk targetType) => IsOrContainsMatch(t => t.Equals(targetType));

        /// <summary>
        /// Returns a mapping of <see cref="TypeRefakjshdfkjahsdfjk"/> to generics explicitly defined by that <see cref="TypeRefakjshdfkjahsdfjk"/>
        /// </summary>
        /// <param name="includeSelf"></param>
        /// <returns></returns>
        public Dictionary<TypeRefakjshdfkjahsdfjk, List<TypeRefakjshdfkjahsdfjk>> GetGenericMap(bool includeSelf)
        {
            // Use fastComparers here to avoid checking DeclaringType (and to be fast)
            var genericMap = new Dictionary<TypeRefakjshdfkjahsdfjk, List<TypeRefakjshdfkjahsdfjk>>(fastComparer);
            var genericParamToDeclaring = new Dictionary<TypeRefakjshdfkjahsdfjk, TypeRefakjshdfkjahsdfjk>(fastComparer);
            var dt = includeSelf ? this : DeclaringType;
            while (dt != null)
            {
                if (dt.IsGeneric)
                    foreach (var g in dt.Generics)
                        // Overwrite existing declaring type and add it to genericParamToDeclaring
                        genericParamToDeclaring[g] = dt;
                dt = dt.DeclaringType;
            }
            // Iterate over each generic param to declaring type and convert it to a mapping of declaring type to generic parameters
            foreach (var pair in genericParamToDeclaring)
                genericMap.GetOrAdd(pair.Value).Add(pair.Key);
            return genericMap;
        }

        internal string GetIncludeLocation()
        {
            var fileName = string.Join("-", CppName().Split(Path.GetInvalidFileNameChars())).Replace('$', '-');
            if (DeclaringType != null)
                return DeclaringType.GetIncludeLocation() + "_" + fileName;
            // Splits multiple namespaces into nested directories
            var directory = string.Join("-", string.Join("/", CppNamespace().Split(new string[] { "::" }, StringSplitOptions.RemoveEmptyEntries)).Split(Path.GetInvalidPathChars()));
            return directory + "/" + fileName;
        }

        public override string ToString()
        {
            var ret = CppNamespace() + "::" + CppName();
            if (IsGeneric)
                ret += "<" + string.Join(", ", Generics) + ">";
            return ret;
        }

        [Obsolete("The argument should be a TypeRefakjshdfkjahsdfjk!")]
#pragma warning disable 809  // "obsolete method extends non-obsolete mehtod object.Equals(object)
        public override bool Equals(object? obj) => Equals(obj as TypeRefakjshdfkjahsdfjk);

#pragma warning restore 809

        internal static FastTypeRefComparer fastComparer = new FastTypeRefComparer();

        public bool Equals(TypeRefakjshdfkjahsdfjk? other)
        {
            if (other is null) return false;
            return fastComparer.Equals(this, other) &&
                (IsArray() == other.IsArray()) &&
                (IsPointer() == other.IsPointer()) &&
                (DeclaringType?.Equals(other.DeclaringType) ?? other.DeclaringType == null) &&
                (ElementType?.Equals(other.ElementType) ?? other.ElementType == null);
        }

        public override int GetHashCode()
        {
            int hashCode = fastComparer.GetHashCode(this);
            if (IsArray()) hashCode *= 37;
            else if (IsPointer()) hashCode *= 59;
            hashCode = hashCode * -1521134295 + DeclaringType?.GetHashCode() ?? 0;
            hashCode = hashCode * -1521134295 + ElementType?.GetHashCode() ?? 0;
            return hashCode;
        }

        internal static bool SetsEqualOrPrint(IEnumerable<TypeRefakjshdfkjahsdfjk> a, IEnumerable<TypeRefakjshdfkjahsdfjk> b, bool fast = false)
        {
            bool equal = (fast ? a.Intersect(b, fastComparer) : a.Intersect(b)).Count() == a.Count();
            if (!equal)
            {
                Console.WriteLine($"Sets: {{{string.Join(", ", a)}}} == {{{string.Join(", ", b)}}}? {equal}");
                if (fast)
                {
                    var aSet = new HashSet<TypeRefakjshdfkjahsdfjk>(a, fastComparer);
                    var bSet = new HashSet<TypeRefakjshdfkjahsdfjk>(b, fastComparer);
                    Console.Error.WriteLine($"in a but not in b: {{{string.Join(", ", aSet.Except(bSet, fastComparer))}}}");
                    Console.Error.WriteLine($"in b but not in a: {{{string.Join(", ", bSet.Except(aSet, fastComparer))}}}");
                }
                else
                {
                    var aSet = new HashSet<TypeRefakjshdfkjahsdfjk>(a);
                    var bSet = new HashSet<TypeRefakjshdfkjahsdfjk>(b);
                    Console.Error.WriteLine($"in a but not in b: {{{string.Join(", ", aSet.Except(bSet))}}}");
                    Console.Error.WriteLine($"in b but not in a: {{{string.Join(", ", bSet.Except(aSet))}}}");
                }
            }
            return equal;
        }

        internal static bool SequenceEqualOrPrint(IEnumerable<TypeRefakjshdfkjahsdfjk> a, IEnumerable<TypeRefakjshdfkjahsdfjk> b, bool fast = false)
        {
            bool equal = fast ? a.SequenceEqual(b, fastComparer) : (a?.SequenceEqual(b) ?? b is null);
            if (!equal)
            {
                Console.WriteLine($"Generics: {{{string.Join(", ", a)}}} == {{{string.Join(", ", b)}}}? {equal}");
                if (a is null) return equal;
                var aList = a.ToList();
                var bList = b.ToList();
                if (aList.Count == bList.Count)
                    for (int i = 0; i < aList.Count; i++)
                    {
                        var one = aList[i];
                        var two = bList[i];
                        PrintEqual(one, two, fast);
                    }
            }
            return equal;
        }

        internal static bool PrintEqual(TypeRefakjshdfkjahsdfjk? a, TypeRefakjshdfkjahsdfjk? b, bool fast = false)
        {
            bool equal = fast ? fastComparer.Equals(a, b) : (a?.Equals(b) ?? b is null);
            if (!equal)
            {
                Console.WriteLine($"{a} == {b}? {equal}");
                if (a is null || b is null) return equal;
                equal = a.Namespace?.Equals(b.Namespace) ?? (b.Namespace == null);
                if (!equal)
                {
                    Console.WriteLine($"Namespace: {a.Namespace} != {b.Namespace}");
                    return equal;
                }
                equal = a.Name.Equals(b.Name);
                if (!equal)
                {
                    Console.WriteLine($"Name: {a.Name} != {b.Name}");
                    return equal;
                }
                equal = a.IsGenericInstance == b.IsGenericInstance;
                if (!equal)
                {
                    Console.WriteLine($"IsGenericInstance: {a.IsGenericInstance} != {b.IsGenericInstance}");
                    return equal;
                }
                equal = a.IsGenericTemplate == b.IsGenericTemplate;
                if (!equal)
                {
                    Console.WriteLine($"IsGenericTemplate: {a.IsGenericTemplate} != {b.IsGenericTemplate}");
                    return equal;
                }
                equal = SequenceEqualOrPrint(a.Generics, b.Generics, fast);
                if (!equal) return equal;
                if (!fast)
                {
                    Console.WriteLine($"DeclaringType: ");
                    equal = PrintEqual(a.DeclaringType, b.DeclaringType, fast);
                    if (!equal) return equal;
                    Console.WriteLine($"ElementType: ");
                    equal = PrintEqual(a.ElementType, b.ElementType, fast);
                }
            }
            return equal;
        }
    }
}