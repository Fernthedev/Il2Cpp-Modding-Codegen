﻿using Il2Cpp_Modding_Codegen.Config;
using Il2Cpp_Modding_Codegen.Data;
using Il2Cpp_Modding_Codegen.Serialization.Interfaces;
using System;
using System.Collections.Generic;
using System.CodeDom.Compiler;
using System.IO;
using System.Text;

namespace Il2Cpp_Modding_Codegen.Serialization
{
    public class CppHeaderCreator
    {
        private SerializationConfig _config;
        private CppSerializerContext _context;

        public CppHeaderCreator(SerializationConfig config, CppSerializerContext context)
        {
            _config = config;
            _context = context;
        }

        private void WriteForwardDeclare(IndentedTextWriter writer, TypeName fd, bool putNamespace = true)
        {
            // TODO: handle this better?
            if (fd.Name == "Il2CppChar")  // cannot forward declare a primitive typedef without exactly copying typedef which is a bad idea
                return;

            if (fd.Namespace.Length == 0) putNamespace = false;
            if (putNamespace)
            {
                writer.WriteLine($"namespace {fd.Namespace} {{");
                writer.Indent++;
            }

            var name = fd.Name;
            if (fd.Generic)
            {
                // If the forward declare is a generic instance, we need to write an empty version of the template type instead
                if (fd.GenericParameters.Count > 0)  // better to forward declare nothing than something invalid
                {
                    var s = "template<";
                    for (int i = 0; i < fd.GenericParameters.Count; i++)
                    {
                        s += "typename " + fd.GenericParameters[i].Name;
                        if (i != fd.GenericParameters.Count - 1)
                            s += ", ";
                    }
                    s += ">";
                    writer.WriteLine(s);

                    // Remove the <blah> from the name for the upcoming print
                    var genericStart = name.IndexOf("<");
                    if (genericStart >= 0)
                    {
                        name = name.Substring(0, genericStart);
                    }
                }
                else name = "";
            }

            // TODO write class instead if we did so for the definition
            if (name.Length > 0)
                writer.WriteLine($"struct {name};");
            else
                writer.WriteLine($"// Aborted forward declaration of {fd}");

            if (putNamespace)
            {
                writer.Indent--;
                writer.WriteLine("}");
            }
        }

        public void Serialize(ISerializer<ITypeData> serializer, ITypeData data)
        {
            var headerLocation = Path.Combine(_config.OutputDirectory, _config.OutputHeaderDirectory, _context.FileName) + ".hpp";
            Directory.CreateDirectory(Path.GetDirectoryName(headerLocation));
            using (var ms = new MemoryStream())
            {
                var rawWriter = new StreamWriter(ms);
                var writer = new IndentedTextWriter(rawWriter, "  ");
                // Write header
                writer.WriteLine($"// Autogenerated from {nameof(CppHeaderCreator)} on {DateTime.Now}");
                writer.WriteLine($"// Created by Sc2ad");
                writer.WriteLine("// =========================================================================");
                writer.WriteLine("#pragma once");
                writer.WriteLine("#pragma pack(8)");
                // Write includes
                writer.WriteLine("// Includes");
                writer.WriteLine("#include \"utils/il2cpp-utils.hpp\"");
                if (_config.OutputStyle == OutputStyle.Normal)
                    writer.WriteLine("#include <optional>");
                if (data.Type != TypeEnum.Interface)
                {
                    foreach (var include in _context.Includes)
                    {
                        writer.WriteLine($"#include \"{include}\"");
                    }
                    writer.WriteLine("// End Includes");
                    // Write forward declarations
                    if (_context.ForwardDeclares.Count > 0)
                    {
                        writer.WriteLine("// Forward declarations");
                        foreach (var fd in _context.ForwardDeclares)
                        {
                            WriteForwardDeclare(writer, fd);
                        }
                        writer.WriteLine("// End Forward declarations");
                    }
                }
                // Write namespace
                writer.WriteLine("namespace " + _context.TypeNamespace + " {");
                writer.Flush();
                if (_context.NamespaceForwardDeclares.Count > 0)
                {
                    writer.Indent++;
                    writer.WriteLine("// Same-namespace forward declarations");
                    foreach (var fd in _context.NamespaceForwardDeclares)
                    {
                        WriteForwardDeclare(writer, fd, false);
                    }
                    writer.WriteLine("// End same-namespace forward declarations");
                }
                writer.Flush();
                // Write actual type
                try
                {
                    // TODO: use the indentWriter?
                    serializer.Serialize(rawWriter.BaseStream, data);
                }
                catch (UnresolvedTypeException e)
                {
                    if (_config.UnresolvedTypeExceptionHandling.TypeHandling == UnresolvedTypeExceptionHandling.DisplayInFile)
                    {
                        writer.WriteLine("// Unresolved type exception!");
                        writer.WriteLine("/*");
                        writer.WriteLine(e);
                        writer.WriteLine("*/");
                    }
                    else if (_config.UnresolvedTypeExceptionHandling.TypeHandling == UnresolvedTypeExceptionHandling.SkipIssue)
                        return;
                    else if (_config.UnresolvedTypeExceptionHandling.TypeHandling == UnresolvedTypeExceptionHandling.Elevate)
                        throw new InvalidOperationException($"Cannot elevate {e} to a parent type- there is no parent type!");
                }
                writer.Indent--;
                writer.WriteLine("}");
                if (!data.This.Generic)
                {
                    string arg0 = _context.QualifiedTypeName;
                    if (data.Info.TypeFlags == TypeFlags.ReferenceType)
                        arg0 += "*";
                    writer.WriteLine($"DEFINE_IL2CPP_ARG_TYPE({arg0}, \"{data.This.Namespace}\", \"{data.This.Name}\");");
                }
                writer.Flush();
                using (var fs = File.OpenWrite(headerLocation))
                {
                    rawWriter.BaseStream.Position = 0;
                    rawWriter.BaseStream.CopyTo(fs);
                }
            }
        }
    }
}