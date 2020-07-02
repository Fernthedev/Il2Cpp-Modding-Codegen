﻿using Il2Cpp_Modding_Codegen.Config;
using Il2Cpp_Modding_Codegen.Data;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Net;
using System.Text;

namespace Il2Cpp_Modding_Codegen.Serialization
{
    /// <summary>
    /// Serializes <see cref="CppTypeContext"/> objects
    /// This does so by including all definitions necessary, forward declaring all declarations necessary, and combining contexts.
    /// Configurable to avoid combining contexts (except for nested cases).
    /// </summary>
    public class CppContextSerializer
    {
        private ITypeCollection _collection;
        private Dictionary<CppTypeContext, (HashSet<CppTypeContext>, Dictionary<string, HashSet<TypeRef>>)> _headerContextMap = new Dictionary<CppTypeContext, (HashSet<CppTypeContext>, Dictionary<string, HashSet<TypeRef>>)>();
        private Dictionary<CppTypeContext, (HashSet<CppTypeContext>, Dictionary<string, HashSet<TypeRef>>)> _sourceContextMap = new Dictionary<CppTypeContext, (HashSet<CppTypeContext>, Dictionary<string, HashSet<TypeRef>>)>();
        private SerializationConfig _config;
        // Hold a type serializer to use for type serialization
        // We want to split up the type serialization into steps, managing nested types ourselves, instead of letting it do it.
        // Map contexts to CppTypeDataSerializers, one to one.
        private Dictionary<CppTypeContext, CppTypeDataSerializer> _typeSerializers = new Dictionary<CppTypeContext, CppTypeDataSerializer>();

        public CppContextSerializer(SerializationConfig config, ITypeCollection collection)
        {
            _config = config;
            _collection = collection;
        }

        /// <summary>
        /// Resolves the context using the provided map.
        /// Populates a mapping of this particular context to forward declares and includes.
        /// </summary>
        /// <param name="data"></param>
        /// <param name="context"></param>
        /// <param name="map"></param>
        public void Resolve(CppTypeContext context, Dictionary<ITypeData, CppTypeContext> map, bool asHeader)
        {
            var _contextMap = asHeader ? _headerContextMap : _sourceContextMap;
            if (_contextMap.ContainsKey(context)) return;

            if (!_typeSerializers.ContainsKey(context))
            {
                // TODO: Also add our type resolution here. We need to resolve all of our fields and method types for serialization of the type later
                var typeSerializer = new CppTypeDataSerializer(_config);
                typeSerializer.Resolve(context, context.LocalType);
                _typeSerializers.Add(context, typeSerializer);
            }
            // Attempts to change context's set's before this point will fail!

            if (asHeader)
            {
                // Under the no-wrapping-namespace paradigm, non-nested types need to be forward declared before their definition. Others do not.
                if (context.DeclaringContext is null)
                    context.DeclarationsToMake.Add(context.LocalType.This);
                else
                    context.DeclarationsToMake.Remove(context.LocalType.This);
            }

            // Recursively Resolve our nested types. However, we may go out of order. We may need to double check to ensure correct resolution, among other things.
            foreach (var nested in context.NestedContexts)
                Resolve(nested, map, asHeader);

            if (asHeader)
                context.AbsorbInPlaceNeeds();

            var includes = new HashSet<CppTypeContext>();

            var defs = context.Definitions;
            var defsToGet = context.DefinitionsToGet;
            if (!asHeader)
            {
                // Handle definitions in new sets so we don't lie to our future includers
                includes.Add(context);
                defs = new HashSet<TypeRef>(context.Definitions);
                defsToGet = new HashSet<TypeRef>(context.DeclarationsToMake);
                defsToGet.UnionWith(context.Declarations);
                //if (context.CppFileName.EndsWith("OVRPlugin_OVRP_1_31_0.cpp"))
                //    Console.WriteLine("Here's the problem, sir!");
            }

            foreach (var td in defsToGet)
            {
                if (context.Definitions.Contains(td))
                    // If we have the definition already in our context, continue.
                    // This could be because it is literally ourselves, a nested type, or we included something
                    continue;
                var type = td.Resolve(_collection);
                // Add the resolved context's HeaderFileName to includes
                if (map.TryGetValue(type, out var value))
                {
                    includes.Add(value);
                    AddIncludeDefinitions(context, defs, value.Definitions, asHeader);
                    //// Also inherit definitions from it for the cpp stage
                    //context.Declarations.UnionWith(value.DeclarationsToMake);
                    //context.Declarations.UnionWith(value.Declarations);
                }
                else
                    throw new UnresolvedTypeException(context.LocalType.This, td);
            }

            var forwardDeclares = new Dictionary<string, HashSet<TypeRef>>();
            if (asHeader)
            {
                foreach (var td in context.DeclarationsToMake)
                {
                    // Stratify by namespace
                    var ns = td.GetNamespace();
                    if (forwardDeclares.TryGetValue(ns, out var set))
                        set.Add(td);
                    else
                        forwardDeclares.Add(ns, new HashSet<TypeRef> { td });
                }
            }
            _contextMap.Add(context, (includes, forwardDeclares));
        }

        private void AddIncludeDefinitions(CppTypeContext context, HashSet<TypeRef> defs, HashSet<TypeRef> newDefs, bool asHeader)
        {
            foreach (var newDef in newDefs)
            {
                if (asHeader)
                {
                    if (newDef.Equals(context.LocalType.This))
                        // Cannot include something that includes us!
                        Console.Error.WriteLine($"Cannot add definition: {newDef} to context: {context.LocalType.This} because it is the same type!\nDefinitions to get: ({string.Join(", ", context.DefinitionsToGet.Select(d => d.GetQualifiedName()))})");
                    // TODO: Add a warning for including something that defines/includes our own nested type (i.e. a type that has us in its DeclaringContext chain)
                }

                // Always add the definition (if we don't throw)
                defs.Add(newDef);
            }
        }

        private void WriteForwardDeclaration(CppStreamWriter writer, ITypeData typeData)
        {
            var resolved = typeData.This;
            var comment = "Forward declaring type: " + resolved.Name;
            if (resolved.IsGenericTemplate)
            {
                // If the type being resolved is generic, we must template it.
                var genericStr = CppTypeContext.GetTemplateLine(typeData);
                writer.WriteComment(comment + "<" + string.Join(", ", resolved.Generics.Select(tr => tr.GetName())) + ">");
                if (!string.IsNullOrEmpty(genericStr))
                    writer.WriteLine(genericStr);
            }
            else
                writer.WriteComment(comment);
            // Write forward declarations
            writer.WriteDeclaration(typeData.Type.TypeName() + " " + resolved.GetName());
        }

        private void WriteIncludes(CppStreamWriter writer, CppTypeContext context, IEnumerable<CppTypeContext> defs,
            bool forHeader, bool forPart2 = false, bool asPart2 = true)
        {
            // Write includes
            var includesWritten = new HashSet<string>();
            writer.WriteComment("Begin includes");
            if (!forHeader || !forPart2)
            {
                if (context.NeedPrimitives)
                {
                    // Primitives include
                    writer.WriteInclude("utils/typedefs.h");
                    includesWritten.Add("utils/typedefs.h");
                }
                if (_config.OutputStyle == OutputStyle.Normal)
                {
                    // Optional include
                    writer.WriteLine("#include <optional>");
                    includesWritten.Add("optional");
                }
                // Overall il2cpp-utils include
                if (forHeader)
                {
                    writer.WriteInclude("utils/il2cpp-utils.hpp");
                    includesWritten.Add("utils/il2cpp-utils.hpp");
                }
                else
                {
                    writer.WriteInclude("utils/utils.h");
                    includesWritten.Add("utils/utils.h");
                }
            }
            foreach (var include in defs)
            {
                writer.WriteComment("Including type: " + include.LocalType.This);
                // Using the HeaderFileName property of the include here will automatically use the lowest non-InPlace type
                var incl = asPart2 ? include.HeaderFileName : include.Part1HeaderFileName;
                if (includesWritten.Add(incl))
                    writer.WriteInclude(incl);
                else
                    writer.WriteComment("Already included the same include: " + incl);
            }
            writer.WriteComment("Completed includes");
        }

        private void WriteDeclarations(CppStreamWriter writer, CppTypeContext context, Dictionary<string, HashSet<TypeRef>> declares)
        {
            // Write forward declarations
            writer.WriteComment("Begin forward declares");
            var completedFds = new HashSet<TypeRef>();
            foreach (var byNamespace in declares)
            {
                writer.WriteComment("Forward declaring namespace: " + byNamespace.Key);
                writer.WriteDefinition("namespace " + byNamespace.Key);
                foreach (var t in byNamespace.Value)
                {
                    var resolved = t.Resolve(_collection);
                    if (resolved is null)
                        throw new UnresolvedTypeException(context.LocalType.This, t);
                    var typeRef = resolved.This;
                    if (resolved != context.LocalType && context.Definitions.Contains(typeRef))
                    {
                        // Write a comment saying "we have already included this"
                        writer.WriteComment("Skipping declaration: " + typeRef.Name + " because it is already included!");
                        continue;
                    }
                    if (typeRef.DeclaringType != null)
                    {
                        if (!context.HasInNestedHierarchy(CppDataSerializer.TypeToContext[resolved]))
                            // TODO: move this error to Resolve or earlier
                            // If there are any nested types in declarations, the declaring type must be defined.
                            // If the declaration is a nested type that exists in the local type, then we will serialize it within the type itself.
                            // Thus, if this ever happens, it should not be a declaration.
                            throw new InvalidOperationException($"Type: {typeRef} (declaring type: {typeRef.DeclaringType} cannot be declared by {context.LocalType.This} because it is a nested type! It should be defined instead!");
                        continue;  // don't namespace declare our own types
                    }
                    if (!completedFds.Add(typeRef))
                        // If we have completed this reference already, continue.
                        continue;
                    WriteForwardDeclaration(writer, resolved);
                }
                // Close namespace after all types in the same namespace have been FD'd
                writer.CloseDefinition();
            }
            writer.WriteComment("Completed forward declares");
        }

        /// <summary>
        /// Write a declaration for the given <see cref="CppTypeContext"/> nested type
        /// </summary>
        /// <param name="writer"></param>
        /// <param name="nested"></param>
        private void AddNestedDeclare(CppStreamWriter writer, CppTypeContext nested)
        {
            var comment = "Nested type: " + nested.LocalType.This.GetQualifiedName();
            var typeStr = nested.LocalType.Type.TypeName();
            if (nested.LocalType.This.IsGenericTemplate)
            {
                /*
                var genericsDefined = nested.LocalType.This.GetDeclaredGenerics(false);
                // If the type being resolved is generic, we must template it, iff we have generic parameters that aren't in genericsDefined
                var generics = string.Empty;
                bool first = true;
                foreach (var g in nested.LocalType.This.GetDeclaredGenerics(true).Except(genericsDefined, TypeRef.fastComparer))
                {
                    if (!first)
                        generics += ", ";
                    else
                        first = false;
                    generics += "typename " + g.GetName();
                }
                */
                var genericStr = nested.GetTemplateLine(true);
                // Write the comment regardless
                writer.WriteComment(comment + "<" + string.Join(", ", nested.LocalType.This.Generics.Select(tr => tr.Name)) + ">");
                // if (!string.IsNullOrEmpty(generics)) writer.WriteLine("template<" + generics + ">");
                if (!string.IsNullOrEmpty(genericStr))
                    writer.WriteLine(genericStr);
            }
            else
                writer.WriteComment(comment);
            writer.WriteDeclaration(typeStr + " " + nested.LocalType.This.GetName());
        }

        private Dictionary<CppTypeContext, IEnumerable<CppTypeContext>> _midClassIncludes = new Dictionary<CppTypeContext, IEnumerable<CppTypeContext>>();

        public void Serialize(CppStreamWriter writer, CppTypeContext context, bool asHeader, bool part2)
        {
            var contextMap = asHeader ? _headerContextMap : _sourceContextMap;
            if (!contextMap.TryGetValue(context, out var defsAndDeclares))
                throw new InvalidOperationException("Must resolve context before attempting to serialize it! context: " + context);

            // Only write includes, declares for non-headers or if the type is InPlace = false, or has no declaring type
            if (!asHeader)
                WriteIncludes(writer, context, defsAndDeclares.Item1, asHeader);
            else if (context.IsRootContext)
            {
                if (!part2)
                {
                    var includes = defsAndDeclares.Item1;
                    if (includes != null && includes.Count > 0)
                    {
                        var includesByPreDef = includes.ToLookup(
                            c => _typeSerializers[context].DefinitionsToGetPreContents.Contains(c.LocalType.This)
                        );
                        // Include the complete types necessary to begin our own part 2
                        WriteIncludes(writer, context, includesByPreDef[true], asHeader);
                        // Include the part 1's of all other includes
                        if (includesByPreDef.Contains(false))
                        {
                            _midClassIncludes.Add(context, includesByPreDef[false]);
                            writer.WriteComment("midClassIncludes: ");
                            WriteIncludes(writer, context, _midClassIncludes[context], asHeader, asPart2: false);
                        }
                    }
                    else
                        WriteIncludes(writer, context, Enumerable.Empty<CppTypeContext>(), asHeader);
                    // Write the declarations
                    WriteDeclarations(writer, context, defsAndDeclares.Item2);
                }
                else
                    writer.WriteInclude(context.Part1HeaderFileName);
            }
            if (asHeader && !part2)
            {
                writer.Flush();
                return;
            }

            // We need to start by actually WRITING our type here. This includes the first portion of our writing, including the header.
            // Then, we write our nested types (declared/defined as needed)
            // Then, we write our fields (static fields first)
            // And finally our methods
            if (!_typeSerializers.TryGetValue(context, out var typeSerializer))
                throw new InvalidOperationException($"Must have a valid {nameof(CppTypeDataSerializer)} for context type: {context.LocalType.This}!");

            // Only write the initial type and nested declares/definitions if we are a header
            if (asHeader)
            {
                //if (!context.InPlace)
                //{
                //    // Write namespace
                //    writer.WriteComment("Type namespace: " + context.LocalType.This.Namespace);
                //    writer.WriteDefinition("namespace " + context.TypeNamespace);
                //}

                typeSerializer.WriteInitialTypeDefinition(writer, context.LocalType, context.InPlace);

                // Now, we must also write all of the nested contexts of this particular context object that have InPlace = true
                // We want to recurse on this, writing the declarations for our types first, followed by our nested types
                // TODO: The nested types should be written in a dependency-resolved way (ex: nested type A uses B, B should be written before A)
                // Alternatively, we don't even NEED to NOT nest in place, we could always just nest in place anyways.
                foreach (var nested in context.NestedContexts)
                {
                    // Regardless of if the nested context is InPlace or not, we can declare it within ourselves
                    AddNestedDeclare(writer, nested);
                }

                if (context.IsRootContext && _midClassIncludes.ContainsKey(context))
                    // Write complete includes for these this time
                    WriteIncludes(writer, context, _midClassIncludes[context], asHeader, forPart2: true);

                // After all nested contexts are completely declared, we write our nested contexts that have InPlace = true, in the correct ordering.
                foreach (var inPlace in context.NestedContexts.Where(nc => nc.InPlace))
                {
                    // Indent, create nested type definition
                    Serialize(writer, inPlace, asHeader, part2);
                }
            }
            // Fields may be converted to methods, so we handle writing these in non-header contexts just in case we need definitions of the methods
            typeSerializer.WriteFields(writer, context.LocalType, asHeader);
            // Method declarations are written in the header, definitions written when the body is needed.
            typeSerializer.WriteMethods(writer, context.LocalType, asHeader);
            writer.Flush();

            if (asHeader)
                typeSerializer.CloseDefinition(writer, context.LocalType);
        }
    }
}