﻿using Il2CppModdingCodegen.Config;
using Il2CppModdingCodegen.Data;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Il2CppModdingCodegen.Serialization
{
    public class CppHeaderCreator
    {
        private readonly SerializationConfig _config;
        private readonly CppContextSerializer _serializer;

        internal CppHeaderCreator(SerializationConfig config, CppContextSerializer serializer)
        {
            _config = config;
            _serializer = serializer;
        }

        private bool hasIl2CppUtilsInclude;
        private void IncludeIl2CppUtilsIfNotAlready(CppStreamWriter writer)
        {
            if (hasIl2CppUtilsInclude) return;
            writer.WriteInclude("utils/il2cpp-utils.hpp");
            hasIl2CppUtilsInclude = true;
        }

        // Outputs a DEFINE_IL2CPP_ARG_TYPE call for all root or non-generic types defined by this file
        private void DefineIl2CppArgTypes(CppStreamWriter writer, CppTypeContext context)
        {
            var type = context.LocalType;
            // DEFINE_IL2CPP_ARG_TYPE
            var (ns, il2cppName) = type.This.GetIl2CppName();
            // For Name and Namespace here, we DO want all the `, /, etc
            if (!type.This.IsGeneric)
            {
                IncludeIl2CppUtilsIfNotAlready(writer);
                string fullName = context.GetCppName(context.LocalType.This, true, true, CppTypeContext.NeedAs.Definition, CppTypeContext.ForceAsType.Literal)
                    ?? throw new UnresolvedTypeException(context.LocalType.This, context.LocalType.This);
                if (context.LocalType.Info.Refness == Refness.ReferenceType) fullName += "*";
                writer.WriteLine($"DEFINE_IL2CPP_ARG_TYPE({fullName}, \"{ns}\", \"{il2cppName}\");");
            }
            else if (type.This.DeclaringType is null || !type.This.DeclaringType.IsGeneric)
            {
                IncludeIl2CppUtilsIfNotAlready(writer);
                string templateName = context.GetCppName(context.LocalType.This, true, false, CppTypeContext.NeedAs.Declaration, CppTypeContext.ForceAsType.Literal)
                    ?? throw new UnresolvedTypeException(context.LocalType.This, context.LocalType.This);
                var structStr = context.LocalType.Info.Refness == Refness.ReferenceType ? "CLASS" : "STRUCT";
                writer.WriteLine($"DEFINE_IL2CPP_ARG_TYPE_GENERIC_{structStr}({templateName}, \"{ns}\", \"{il2cppName}\");");
            }

            foreach (var nested in context.NestedContexts.Where(n => n.InPlace))
                DefineIl2CppArgTypes(writer, nested);
        }

        internal void Serialize(CppTypeContext context)
        {
            var data = context.LocalType;
            var headerLocation = Path.Combine(_config.OutputDirectory, _config.OutputHeaderDirectory, context.HeaderFileName);
            Directory.CreateDirectory(Path.GetDirectoryName(headerLocation));
            using var ms = new MemoryStream();
            using var rawWriter = new StreamWriter(ms);
            using var writer = new CppStreamWriter(rawWriter, "  ");
            // Write header
            writer.WriteComment($"Autogenerated from {nameof(CppHeaderCreator)}");
            writer.WriteComment("Created by Sc2ad");
            writer.WriteComment("=========================================================================");
            writer.WriteLine("#pragma once");
            // TODO: determine when/if we need this
            writer.WriteLine("#pragma pack(push, 8)");
            // Write SerializerContext and actual type
            try
            {
                _serializer.Serialize(writer, context, true);
            }
            catch (UnresolvedTypeException e)
            {
                if (_config.UnresolvedTypeExceptionHandling?.TypeHandling == UnresolvedTypeExceptionHandling.DisplayInFile)
                {
                    writer.WriteComment("Unresolved type exception!");
                    writer.WriteLine("/*");
                    writer.WriteLine(e);
                    writer.WriteLine("*/");
                }
                else if (_config.UnresolvedTypeExceptionHandling?.TypeHandling == UnresolvedTypeExceptionHandling.SkipIssue)
                    return;
                else if (_config.UnresolvedTypeExceptionHandling?.TypeHandling == UnresolvedTypeExceptionHandling.Elevate)
                    throw new InvalidOperationException($"Cannot elevate {e} to a parent type- there is no parent type!");
            }
            // End the namespace
            writer.CloseDefinition();
            hasIl2CppUtilsInclude = context.NeedIl2CppUtilsBeforeLateHeader;

            if (data.This.Namespace == "System" && data.This.Name == "ValueType")
            {
                IncludeIl2CppUtilsIfNotAlready(writer);
                writer.WriteLine("template<class T>");
                writer.WriteLine("struct is_value_type<T, typename std::enable_if_t<std::is_base_of_v<System::ValueType, T>>> : std::true_type{};");
            }

            DefineIl2CppArgTypes(writer, context);
            writer.Flush();

            writer.WriteLine("#pragma pack(pop)");
            writer.Flush();

            writer.WriteIfDifferent(headerLocation, context);
        }
    }
}
