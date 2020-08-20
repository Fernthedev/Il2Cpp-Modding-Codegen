﻿using Il2CppModdingCodegen.Data;
using System;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.Linq;
using System.Text.RegularExpressions;

namespace Il2CppModdingCodegen.Serialization
{
    public class CppTypeContext
    {
#pragma warning disable CA1717 // Only FlagsAttribute enums should have plural names

        public enum NeedAs
#pragma warning restore CA1717 // Only FlagsAttribute enums should have plural names
        {
            Definition,
            Declaration,
            BestMatch
        }

        public enum ForceAsType
        {
            None,
            Literal
        }

        // Declarations that should be made by our includes (DefinitionsToGet)
        internal HashSet<string> PrimitiveDeclarations { get; } = new HashSet<string>();

        internal HashSet<TypeRef> Declarations { get; } = new HashSet<TypeRef>();
        internal HashSet<TypeRef> DeclarationsToMake { get; } = new HashSet<TypeRef>();
        internal HashSet<TypeRef> Definitions { get; } = new HashSet<TypeRef>();
        internal HashSet<TypeRef> DefinitionsToGet { get; } = new HashSet<TypeRef>();

        internal CppTypeContext? DeclaringContext { get; private set; }
        internal HashSet<TypeRef> UniqueInterfaces { get; }
        private static Dictionary<ITypeData, HashSet<TypeRef>> UniqueInterfaceMap = new Dictionary<ITypeData, HashSet<TypeRef>>();
        internal long NumDuplicateInterfaces { get; private set; } = 0;
        internal bool InPlace { get; private set; } = false;
        internal IReadOnlyList<CppTypeContext> NestedContexts { get => _nestedContexts; }

        private CppTypeContext _rootContext;

        private CppTypeContext RootContext
        {
            get
            {
                while (_rootContext.InPlace && _rootContext.DeclaringContext != null)
                    _rootContext = _rootContext.DeclaringContext;
                return _rootContext;
            }
        }

        internal string HeaderFileName { get => RootContext.LocalType.This.GetIncludeLocation() + ".hpp"; }
        internal string CppFileName { get => LocalType.This.GetIncludeLocation() + ".cpp"; }

        internal string TypeNamespace { get; }
        internal string TypeName { get; }
        internal string QualifiedTypeName { get; }
        internal ITypeData LocalType { get; }

        /// <summary>
        /// Returns true if this context uses primitive il2cpp types.
        /// </summary>
        internal bool NeedPrimitivesBeforeLateHeader { get; private set; } = false;

        internal void EnableNeedPrimitivesBeforeLateHeader() => NeedPrimitivesBeforeLateHeader = true;

        // whether the header will need to include il2cpp_utils before the DEFINE_IL2CPP_ARG_TYPEs
        internal bool NeedIl2CppUtilsFunctionsInHeader { get; private set; } = false;

        internal void EnableNeedIl2CppUtilsFunctionsInHeader() => NeedIl2CppUtilsFunctionsInHeader = true;

        internal bool NeedStdint { get; private set; } = false;

        // Holds generic types (ex: T1, T2, ...) defined by the type
        private readonly HashSet<TypeRef> _genericTypes = new HashSet<TypeRef>(TypeRef.fastComparer);

        private readonly List<CppTypeContext> _nestedContexts = new List<CppTypeContext>();
        private readonly ITypeCollection _types;

        private void AddGenericTypes(TypeRef? type)
        {
            if (type is null) return;
            if (type.IsGenericTemplate)
                foreach (var g in type.Generics)
                    _genericTypes.Add(g);
            AddGenericTypes(type.DeclaringType);
        }

        private static HashSet<TypeRef> GetUniqueInterfaces(List<TypeRef> interfaces, ITypeCollection _types)
        {
            // Iterate over each interface in interfaces.
            // For each one, resolve it (without storing it), add it to the hashset.
            // Then recurse on that interface if we successfully added a unique interface to the hashset.
            var set = new HashSet<TypeRef>();
            foreach (var face in interfaces)
            {
                if (set.Add(face))
                {
                    var td = face.Resolve(_types);
                    if (td is null)
                        throw new ArgumentException($"Could not resolve TypeRef: {face}!");
                    foreach (var item in GetUniqueInterfaces(td.ImplementingInterfaces, _types))
                        set.Add(item);
                }
            }
            return set;
        }

        private static HashSet<TypeRef> GetUniqueInterfaces(ITypeData data, ITypeCollection _types)
        {
            if (!UniqueInterfaceMap.ContainsKey(data))
            {
                // Iterate over all of my data's interfaces
                // For each, add its unique interfaces to a local hashset
                // Then, set my UniqueInterfaces to be my original interfaces - anything shared in the local hashset
                var uniqueInterfaces = new HashSet<TypeRef>();
                var nestedUnique = new List<TypeRef>();
                ITypeData CollectImplementingInterfaces(TypeRef face)
                {
                    var nested = face.Resolve(_types);
                    if (nested is null)
                        throw new ArgumentException($"Could not resolve TypeRef: {face}!");
                    var map = face.IsGenericInstance ? face.ExtractGenericMap(_types) : null;
                    nestedUnique.AddRange(nested.ImplementingInterfaces.Select(i => (!i.IsGeneric || map is null) ? i : i.MakeGenericInstance(map)));
                    return nested;
                }
                foreach (var face in data.ImplementingInterfaces)
                {
                    uniqueInterfaces.Add(face);
                    CollectImplementingInterfaces(face);
                }
                var parent = data.Parent;
                while (parent != null)
                    parent = CollectImplementingInterfaces(parent).Parent;

                var alreadyDefined = GetUniqueInterfaces(nestedUnique, _types);
                foreach (var i in data.ImplementingInterfaces)
                    if (alreadyDefined.Contains(i))
                        uniqueInterfaces.Remove(i);
                UniqueInterfaceMap[data] = uniqueInterfaces;
            }
            return UniqueInterfaceMap[data];
        }

        private bool HasDuplicateInterfaces(HashSet<TypeRef> existing, TypeRef iface)
        {
            bool ret = false;
            if (!existing.Add(iface))
                ret = true;
            else
            {
                var td = iface.Resolve(_types);
                if (td is null)
                    throw new ArgumentException($"Could not resolve TypeRef: {iface}!");
                foreach (var face in GetUniqueInterfaces(td, _types))
                    ret |= HasDuplicateInterfaces(existing, face);
            }
            return ret;
        }

        private int CountDuplicateInterfaces()
        {
            int numDuplicateInterfaces = 0;
            var seen = new HashSet<TypeRef>();
            foreach (var face in UniqueInterfaces)
                if (HasDuplicateInterfaces(seen, face))
                    numDuplicateInterfaces++;
            return numDuplicateInterfaces;
        }

        internal CppTypeContext(ITypeCollection types, ITypeData data, CppTypeContext? declaring)
        {
            _rootContext = this;
            _types = types;
            LocalType = data;

            // Add ourselves to our Definitions
            Definitions.Add(data.This);

            // Requiring it as a definition here simply makes it easier to remove (because we are asking for a definition of ourself, which we have)
            QualifiedTypeName = GetCppName(data.This, true, true, NeedAs.Definition, ForceAsType.Literal) ?? throw new Exception($"Could not get QualifiedTypeName for {data.This}");
            TypeNamespace = data.This.CppNamespace();
            TypeName = data.This.CppName();

            // Declaring types need to declare (or define) ALL of their nested types
            foreach (var nested in data.NestedTypes)
                AddNestedDeclaration(nested.This, nested.This.Resolve(_types));

            // Nested types need to define their declaring type
            if (data.This.DeclaringType != null)
                AddDefinition(data.This.DeclaringType);

            // Check all declaring types (and ourselves) if we have generic arguments/parameters. If we do, add them to _genericTypes.
            AddGenericTypes(data.This);

            // Create a hashset of all the unique interfaces implemented explicitly by this type.
            // Necessary for avoiding base ambiguity.
            UniqueInterfaces = GetUniqueInterfaces(data, _types);
            NumDuplicateInterfaces = CountDuplicateInterfaces();

            DeclaringContext = declaring;
            if (declaring != null)
                declaring.AddNestedContext(LocalType, this);
        }

        // Must be called by constructor
        internal void AddNestedContext(ITypeData type, CppTypeContext context)
        {
            Contract.Requires(LocalType.This.Equals(type.This.DeclaringType));
            // Add the type, context pair to our immediately nested contexts
            // TODO: Add a mapping from type --> context so we can search our immediate nesteds faster
            // atm, just add it because we can be lazy
            _nestedContexts.Add(context);
            // If this context is a generic template, then we need to InPlace the new nested context.
            if (LocalType.This.IsGenericTemplate)
                InPlaceNestedType(context);
        }

        /// <summary>
        /// Returns whether the given <see cref="TypeRef"/> is a generic template parameter within this context.
        /// </summary>
        /// <param name="type"></param>
        /// <returns></returns>
        internal bool IsGenericParameter(TypeRef type) => _genericTypes.Contains(type) || type.IsGenericParameter;

        internal string GetTemplateLine(bool localOnly = true)
        {
            var s = "";
            if (LocalType.This.IsGeneric)
            {
                var generics = LocalType.This.GetDeclaredGenerics(true);
                if (localOnly)
                    generics = generics.Except(LocalType.This.GetDeclaredGenerics(false), TypeRef.fastComparer);

                bool first = true;
                foreach (var g in generics)
                {
                    if (!first)
                        s += ", ";
                    s += "typename " + g.CppName();
                    first = false;
                }
            }
            if (!string.IsNullOrEmpty(s))
                s = $"template<{s}>";
            return s;
        }

        internal static string GetTemplateLine(ITypeData type, bool localOnly = true) => CppDataSerializer.TypeToContext[type].GetTemplateLine(localOnly);

        internal void AbsorbInPlaceNeeds()
        {
            // inherit DefinitionsToGet, Declarations from in-place NestedContexts
            var prevInPlace = new HashSet<CppTypeContext>();
            HashSet<CppTypeContext> newInPlace;
            do
            {
                newInPlace = new HashSet<CppTypeContext>(NestedContexts.Where(n => n.InPlace).Except(prevInPlace));
                foreach (var nested in newInPlace)
                    TakeDefsAndDeclares(nested);
                prevInPlace.UnionWith(newInPlace);
            } while (newInPlace.Count > 0);
        }

        // TODO: instead of having AbsorbInPlaceNeeds, call this when nested first becomes in-place and direct all ResolveAndStore calls to RootContext?
        private void TakeDefsAndDeclares(CppTypeContext nested)
        {
            Contract.Requires(nested.InPlace);
            Contract.Requires(this == RootContext);

            foreach (var dec in nested.DeclarationsToMake.Except(nested.LocalType.NestedTypes.Select(t => t.This)).Except(Definitions))
                AddDeclaration(dec, null);
            foreach (var def in nested.DefinitionsToGet.Except(Definitions))
                AddDefinition(def);
        }

        internal bool HasInNestedHierarchy(TypeRef type)
        {
            var resolved = type.Resolve(_types);
            if (resolved == null) throw new UnresolvedTypeException(LocalType.This, type);
            return HasInNestedHierarchy(resolved);
        }

        internal bool HasInNestedHierarchy(ITypeData resolved) => HasInNestedHierarchy(resolved, out var _);

        private bool HasInNestedHierarchy(ITypeData resolved, out CppTypeContext defContext)
        {
            if (CppDataSerializer.TypeToContext.TryGetValue(resolved, out defContext))
                return HasInNestedHierarchy(defContext);
            return false;
        }

        internal bool HasInNestedHierarchy(CppTypeContext context)
        {
            if (context.DeclaringContext is null) return false;
            else if (context.DeclaringContext == this) return true;
            return HasInNestedHierarchy(context.DeclaringContext);
        }

        /// <summary>
        /// Given a nested context, somewhere within our same RootContext, InPlace it and all of its DeclaringContexts up until RootContext.
        /// </summary>
        /// <param name="defContext"></param>
        private void InPlaceNestedType(CppTypeContext defContext)
        {
            Contract.Requires(RootContext.HasInNestedHierarchy(defContext));
            // If the type we want is a type that is nested within ourselves, our declaring context... till RootContext
            // Then we set InPlace to true.
            while (defContext != RootContext)
            {
                if (!defContext.InPlace)
                {
                    defContext.InPlace = true;
                    if (defContext.DeclaringContext != null)
                        foreach (var d in defContext.DeclaringContext.Definitions)
                        {
                            // Add each definition that exists in the declaring context to the InPlace nested context since they share definitions
                            defContext.Definitions.Add(d);
                            // Remove each definition that exists in the declaring context from the InPlace nested context since they share definitions
                            defContext.DefinitionsToGet.Remove(d);
                        }
                }
                // Add the now InPlace type to our own Definitions
                RootContext.Definitions.Add(defContext.LocalType.This);
                // Go to the DeclaringContext of the type we just InPlace'd into ourselves, and continue inplacing DeclaringContexts until we hit ourselves.
                defContext = defContext.DeclaringContext!;
            }
        }

        private void AddDefinition(TypeRef def, ITypeData? resolved = null)
        {
            if (Definitions.Contains(def)) return;
            if (DefinitionsToGet.Contains(def)) return;
            // Adding a definition is simple, ensure the type is resolved and add it
            if (resolved is null)
                resolved = def.Resolve(_types);
            if (resolved is null)
                throw new UnresolvedTypeException(LocalType.This, def);
            // Remove anything that is already declared, we only need to define it
            if (!DeclarationsToMake.Remove(def))
                Declarations.Remove(def);

            // When we add a definition, we add it to our DefinitionsToGet
            // However, if the type we are looking for is a nested type with a declaring type that we share:
            // We need to set the InPlace property for all declaring types of that desired type to true
            // (up until the DeclaringType is shared between them)

            // If the definition I am adding shares the same RootContext as me, I need to InPlace nest it.
            if (!RootContext.HasInNestedHierarchy(resolved, out var defContext))
                DefinitionsToGet.Add(def);
            else
                InPlaceNestedType(defContext);
        }

        private void AddNestedDeclaration(TypeRef def, ITypeData? resolved)
        {
            if (def.IsVoid()) throw new ArgumentException("cannot be void!", nameof(def));
            if (def.DeclaringType is null) throw new ArgumentException("DeclaringType cannot be void!", nameof(def));
            if (resolved is null) throw new ArgumentNullException(nameof(resolved));
            Contract.Requires(LocalType.Equals(resolved.This.DeclaringType));
            DeclarationsToMake.Add(def);
        }

        private void AddDeclaration(TypeRef def, ITypeData? resolved)
        {
            Contract.Requires(!def.IsVoid());

            // If we have it in our DefinitionsToGet, no need to declare as well
            if (DefinitionsToGet.Contains(def)) return;
            if (DeclarationsToMake.Contains(def)) return;  // this def is already queued for declaration
            if (Declarations.Contains(def)) return;

            if (resolved is null)
                resolved = def.Resolve(_types);
            if (resolved is null)
                throw new UnresolvedTypeException(LocalType.This, def);

            if (resolved?.This.DeclaringType != null && !Definitions.Contains(resolved.This.DeclaringType))
            {
                // If def's declaring type is not defined, we cannot declare def. Define def's declaring type instead.
                AddDefinition(resolved.This.DeclaringType);
                Declarations.Add(resolved.This);
            }
            else if (resolved != null)
                // Otherwise, we can safely add it to declarations
                DeclarationsToMake.Add(def);
        }

        private static NeedAs NeedAsForGeneric(NeedAs _) => NeedAs.BestMatch;

        /// <summary>
        /// Gets the C++ fully qualified name for the TypeRef.
        /// </summary>
        /// <returns>Null if the type has not been resolved (and is not a generic parameter or primitive)</returns>
        public string? GetCppName(TypeRef? data, bool qualified, bool generics = true, NeedAs needAs = NeedAs.BestMatch, ForceAsType forceAsType = ForceAsType.None)
        {
            if (data is null) throw new ArgumentNullException(nameof(data));
            // First we check if the type is a primitive type. If it is, we return the converted name.
            // This must happen first because we can have T* and T[] which need to be converted correctly.
            if (forceAsType != ForceAsType.Literal || !data.Equals(LocalType.This))
            {
                // If the TypeRef is a primitive, we need to convert it to a C++ name upfront.
                var primitiveName = ConvertPrimitive(data, forceAsType, needAs);
                if (!string.IsNullOrEmpty(primitiveName))
                    return primitiveName;
            }

            // If the TypeRef is a generic parameter, return its name
            if (IsGenericParameter(data))
                return data.Name;

            var resolved = ResolveAndStore(data, forceAsType, needAs);
            if (resolved is null)
                return null;
            var name = string.Empty;
            if (resolved.This.DeclaringType != null)
            {
                // Each declaring type must be defined, and must also have its generic parameters specified (confirm this is the case)
                // If data.IsGenericInstance, then we need to use the provided generic arguments instead of the template types
                // Create a map of declared generics (including our own) that map to each of data's generic arguments
                // First, we get a list of all the template parameters and a list of all our generic parameters/arguments from our reference
                var genericParams = resolved.This.GetDeclaredGenerics(true).ToList();
                var count = genericParams.Count;
                var genericArgs = genericParams;
                if (data.IsGenericInstance)
                {
                    genericArgs = data.GetDeclaredGenerics(true).ToList();
                    if (count != genericArgs.Count)
                        // In cases where we have non-matching counts, this usually means we need to remove generic parameters from our generic arguments
                        foreach (var gp in genericParams)
                            // Remove each remaining generic template parameter
                            genericArgs.Remove(gp);
                }
                // If the two lists are of differing lengths, throw
                if (count != genericArgs.Count)
                    throw new InvalidOperationException($"{nameof(genericParams)}.Count != {nameof(genericArgs)}.Count, {count} != {genericArgs.Count}");
                // Create a mapping from generic parameter to generic argument
                // If data is not a generic instance, no need to bother
                Dictionary<TypeRef, TypeRef>? argMapping = null;
                if (data.IsGenericInstance)
                {
                    argMapping = new Dictionary<TypeRef, TypeRef>(TypeRef.fastComparer);
                    for (int i = 0; i < count; i++)
                        argMapping.Add(genericParams[i], genericArgs[i]);
                }
                // Get full generic map of declaring type --> list of generic parameters declared in that type
                var genericMap = resolved.This.GetGenericMap(true);
                var declString = string.Empty;
                var declType = resolved.This;
                bool isThisType = true;
                while (declType != null)
                {
                    // Recurse upwards starting at ourselves
                    string declaringGenericParams = "";
                    if (genericMap.TryGetValue(declType, out var declaringGenerics))
                        // If we are thisType AND we DO NOT want generics, we should not write any generics.
                        // Otherwise, we write out the generics defined in this type.
                        if (!isThisType || generics)
                        {
                            declaringGenericParams += "<";
                            bool first = true;
                            bool allSuccess = true;
                            foreach (var g in declaringGenerics)
                            {
                                if (!first)
                                    declaringGenericParams += ", ";
                                string? str;
                                if (data.IsGenericInstance)
                                    str = GetCppName(argMapping![g], true, true, NeedAsForGeneric(needAs));
                                else
                                    // Here we need to ensure that we are actually getting this from the right place.
                                    // When data is NOT a generic instance, that means that this type has a generic template type.
                                    // If it is a generic parameter it should be gotten from declType
                                    // Or, we need to forward all of the generic parameters our declaring types have onto ourselves, thus allowing for resolution.
                                    str = GetCppName(g, true, true, NeedAsForGeneric(needAs));
                                if (str is null)
                                {
                                    Console.WriteLine($"Failed to get name for generic {g} while resolving type {data}!");
                                    allSuccess = false;
                                }
                                declaringGenericParams += str;
                                first = false;
                            }
                            declaringGenericParams += ">";
                            if (!allSuccess)
                                throw new Exception($"Attempted to write generic parameters, but actually wrote <>! for type being resolved: {data} type with generics: {declType}");
                        }

                    var temp = declType.CppName() + declaringGenericParams;
                    if (!isThisType)
                        temp += "::" + declString;
                    isThisType = false;

                    declString = temp;
                    if (declType.DeclaringType is null && qualified)
                        // Grab namespace for name here
                        name = declType.CppNamespace() + "::";
                    declType = declType.DeclaringType;
                }
                name += declString;
            }
            else
            {
                if (qualified)
                    name = resolved.This.CppNamespace() + "::";
                name += data.Name;
                name = name.Replace('`', '_').Replace('<', '$').Replace('>', '$');
                if (generics && data.Generics.Count > 0)
                {
                    name += "<";
                    bool first = true;
                    for (int i = 0; i < data.Generics.Count; i++)
                    {
                        var g = data.Generics[i];
                        if (!first)
                            name += ", ";

                        if (data.IsGenericTemplate)
                            // If this is a generic template, use literal names for our generic parameters
                            name += g.Name;
                        else if (data.IsGenericInstance)
                            // If this is a generic instance, call each of the generic's GetCppName
                            name += GetCppName(g, qualified, true, NeedAsForGeneric(needAs));
                        first = false;
                    }
                    name += ">";
                }
            }
            // Ensure the name has no bad characters
            // Append pointer as necessary
            if (resolved is null)
                return null;
            if (forceAsType == ForceAsType.Literal)
                return name;
            if (resolved.This.DeclaringType?.IsGeneric ?? false)  // note: it's important that ForceAsType.Literal is ruled out first
                name = "typename " + name;
            if (resolved.Info.Refness == Refness.ReferenceType)
                return name + "*";
            return name;
        }

        /// <summary>
        /// Simply adds the resolved <see cref="ITypeData"/> to either the declarations or definitions and returns it.
        /// If typeRef matches a generic parameter of this generic template, or the resolved type is null, returns false.
        /// </summary>
        /// <param name="typeRef"></param>
        /// <param name="needAs"></param>
        /// <returns>A bool representing if the type was resolved successfully</returns>
        internal ITypeData? ResolveAndStore(TypeRef typeRef, ForceAsType forceAs, NeedAs needAs = NeedAs.BestMatch)
        {
            if (IsGenericParameter(typeRef))
                // Generic parameters are resolved to nothing and shouldn't even attempted to be resolved.
                return null;
            var resolved = typeRef.Resolve(_types);
            if (resolved is null)
                return null;

            switch (needAs)
            {
                case NeedAs.Declaration:
                    AddDeclaration(typeRef, resolved);
                    break;

                case NeedAs.BestMatch:
                    if (forceAs != ForceAsType.Literal && (typeRef.IsPointer() || resolved.Info.Refness == Refness.ReferenceType))
                        AddDeclaration(typeRef, resolved);
                    else
                        AddDefinition(typeRef, resolved);
                    break;

                case NeedAs.Definition:
                default:
                    // If I need it as a definition, add it as one
                    AddDefinition(typeRef, resolved);
                    break;
            }

            if (typeRef.IsGenericTemplate && typeRef.Generics != null)
                // Resolve and store each generic argument
                foreach (var g in typeRef.Generics)
                    // Only need them as declarations, since we don't need the literal pointers.
                    ResolveAndStore(g, forceAs, NeedAs.Declaration);

            return resolved;
        }

        // We only need a declaration for the element type (if we aren't needed as a definition)
        private static NeedAs NeedAsForPrimitiveEtype(NeedAs needAs) => needAs == NeedAs.Definition ? needAs : NeedAs.Declaration;

        private string? ConvertPrimitive(TypeRef def, ForceAsType forceAs, NeedAs needAs)
        {
            string? s = null;
            if (def.IsArray())
                // We should ensure we aren't attemping to force it to something it shouldn't be, so it should still be ForceAsType.None
                s = $"Array<{GetCppName(def.ElementType, true, true, NeedAsForPrimitiveEtype(needAs))}>";
            else if (def.IsPointer())
                return GetCppName(def.ElementType, true, true, NeedAsForPrimitiveEtype(needAs)) + "*";
            else if (string.IsNullOrEmpty(def.Namespace) || def.Namespace == "System")
            {
                var name = def.Name.ToLower();
                if (name == "void")
                    s = "void";
                else if (name == "object")
                    s = Constants.ObjectCppName;
                else if (name == "string")
                    s = Constants.StringCppName;
                else if (name == "char")
                    s = "Il2CppChar";
                else if (def.Name == "bool" || def.Name == "Boolean")
                    s = "bool";
                else if (name == "sbyte")
                    s = "int8_t";
                else if (name == "byte")
                    s = "uint8_t";
                else if (def.Name == "short" || def.Name == "Int16")
                    s = "int16_t";
                else if (def.Name == "ushort" || def.Name == "UInt16")
                    s = "uint16_t";
                else if (def.Name == "int" || def.Name == "Int32")
                    s = "int";
                else if (def.Name == "uint" || def.Name == "UInt32")
                    s = "uint";
                else if (def.Name == "long" || def.Name == "Int64")
                    s = "int64_t";
                else if (def.Name == "ulong" || def.Name == "UInt64")
                    s = "uint64_t";
                else if (def.Name == "float" || def.Name == "Single")
                    s = "float";
                else if (name == "double")
                    s = "double";
            }
            if (s is null)
                return null;
            if (s.StartsWith("Il2Cpp") || s.StartsWith("Cs") || s.StartsWith("Array<"))
            {
                bool defaultPtr = (s != "Il2CppChar");

                if (!defaultPtr || forceAs == ForceAsType.Literal)
                    EnableNeedPrimitivesBeforeLateHeader();
                else
                {
                    if (s.Contains("<"))
                        PrimitiveDeclarations.Add("template<class T>\nstruct " + Regex.Replace(s, "<.*>", ""));
                    else
                        PrimitiveDeclarations.Add("struct " + s);
                    s += "*";
                }

                // For Il2CppTypes, should refer to type as :: to avoid ambiguity
                s = "::" + s;
            }
            else if (s.EndsWith("_t"))
                NeedStdint = true;
            return s;
        }
    }
}