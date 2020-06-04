﻿using Il2Cpp_Modding_Codegen.Config;
using Il2Cpp_Modding_Codegen.Data;
using Il2Cpp_Modding_Codegen.Serialization.Interfaces;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Il2Cpp_Modding_Codegen.Serialization
{
    public class CppMethodSerializer : ISerializer<IMethod>
    {
        private static readonly HashSet<string> IgnoredMethods = new HashSet<string>() { "op_Implicit", "op_Explicit" };
        private string _prefix;
        private bool _asHeader;
        private SerializationConfig _config;

        private Dictionary<IMethod, string> _resolvedTypeNames = new Dictionary<IMethod, string>();
        private Dictionary<IMethod, string> _declaringTypeNames = new Dictionary<IMethod, string>();
        private Dictionary<IMethod, List<string>> _parameterMaps = new Dictionary<IMethod, List<string>>();
        private string _declaringFullyQualified;

        public CppMethodSerializer(SerializationConfig config, string prefix = "", bool asHeader = true)
        {
            _config = config;
            _prefix = prefix;
            _asHeader = asHeader;
        }

        public void PreSerialize(ISerializerContext context, IMethod method)
        {
            // Get the fully qualified name of the context
            _declaringFullyQualified = context.QualifiedTypeName;
            // We need to forward declare/include all types that are either returned from the method or are parameters
            _resolvedTypeNames.Add(method, context.GetNameFromReference(method.ReturnType));
            // The declaringTypeName needs to be a reference, even if the type itself is a value type.
            _declaringTypeNames.Add(method, context.GetNameFromReference(method.DeclaringType, ForceAsType.Pointer));
            var parameterMap = new List<string>();
            foreach (var p in method.Parameters)
            {
                string s;
                if (p.Flags != ParameterFlags.None)
                    s = context.GetNameFromReference(p.Type, ForceAsType.Reference);
                else
                    s = context.GetNameFromReference(p.Type);
                parameterMap.Add(s);
            }
            _parameterMaps.Add(method, parameterMap);
        }

        private string WriteMethod(bool staticFunc, IMethod method, bool namespaceQualified)
        {
            // If the method is an instance method, first parameter should be a pointer to the declaringType.
            string paramString = "";
            var ns = "";
            var staticString = "";
            if (namespaceQualified)
                ns = _declaringFullyQualified + "::";
            if (!namespaceQualified && staticFunc)
                staticString = "static ";
            // Setup return type
            var retStr = _resolvedTypeNames[method];
            if (!method.ReturnType.IsVoid())
            {
                if (_config.OutputStyle == OutputStyle.Normal)
                    retStr = "std::optional<" + retStr + ">";
            }
            // Handles i.e. ".ctor"
            var nameStr = method.Name.Replace('.', '_');
            return $"{staticString}{retStr} {ns}{nameStr}({paramString + method.Parameters.FormatParameters(_parameterMaps[method], FormatParameterMode.Names | FormatParameterMode.Types)})";
        }

        // Write the method here
        public void Serialize(Stream stream, IMethod method)
        {
            if (_resolvedTypeNames[method] == null)
                throw new UnresolvedTypeException(method.DeclaringType, method.ReturnType);
            if (_declaringTypeNames[method] == null)
                throw new UnresolvedTypeException(method.DeclaringType, method.DeclaringType);
            var val = _parameterMaps[method].FindIndex(s => s == null);
            if (val != -1)
                throw new UnresolvedTypeException(method.DeclaringType, method.Parameters[val].Type);
            if (IgnoredMethods.Contains(method.Name) || _config.BlacklistMethods.Contains(method.Name))
                return;
            // Don't use a using statement here because it will close the underlying stream-- we want to keep it open
            var writer = new StreamWriter(stream);

            if (method.DeclaringType.Generic && !_asHeader)
                // Need to create the method ENTIRELY in the header, instead of split between the C++ and the header
                return;
            bool writeContent = !_asHeader || method.DeclaringType.Generic;

            if (_asHeader)
            {
                var methodString = "";
                bool staticFunc = false;
                foreach (var spec in method.Specifiers)
                {
                    methodString += $"{spec} ";
                    if (spec.Static)
                    {
                        staticFunc = true;
                    }
                }
                methodString += $"{method.ReturnType} {method.Name}({method.Parameters.FormatParameters()})";
                methodString += $" // Offset: 0x{method.Offset:X}";
                writer.WriteLine($"{_prefix}// {methodString}");
                if (method.ImplementedFrom != null)
                    writer.WriteLine($"{_prefix}// Implemented from: {method.ImplementedFrom}");
                if (!writeContent)
                    writer.WriteLine($"{_prefix}{WriteMethod(staticFunc, method, false)};");
            }
            else
            {
                writer.WriteLine($"{_prefix}// Autogenerated method: {method.DeclaringType}.{method.Name}");
            }
            if (writeContent)
            {
                bool isStatic = method.Specifiers.IsStatic();
                // Write the qualified name if not in the header
                writer.WriteLine(_prefix + WriteMethod(isStatic, method, !_asHeader) + " {");
                string restorePrefix = _prefix;
                // TODO: Prefix by configurable amount?
                _prefix += "  ";
                var s = "";
                var innard = "";
                var macro = "RET_V_UNLESS(";
                if (_config.OutputStyle == OutputStyle.CrashUnless)
                    macro = "CRASH_UNLESS(";
                if (!method.ReturnType.IsVoid())
                {
                    s = "return ";
                    innard = $"<{_resolvedTypeNames[method]}>";
                    if (_config.OutputStyle != OutputStyle.CrashUnless) macro = "";
                }
                // TODO: Replace with RET_NULLOPT_UNLESS or another equivalent (perhaps literally just the ret)
                s += $"{macro}il2cpp_utils::RunMethod{innard}(";
                if (!isStatic)
                {
                    s += "this, ";
                }
                else
                {
                    // TODO: Check to ensure this works with non-generic methods in a generic type
                    s += $"\"{method.DeclaringType.Namespace}\", \"{method.DeclaringType.Name}\", ";
                }
                var paramString = method.Parameters.FormatParameters(_parameterMaps[method], FormatParameterMode.Names);
                if (!string.IsNullOrEmpty(paramString))
                    paramString = ", " + paramString;
                s += $"\"{method.Name}\"{paramString}){(!string.IsNullOrEmpty(macro) ? ")" : "")};";
                // Write method with return
                writer.WriteLine($"{_prefix}{s}");
                // Close method
                _prefix = restorePrefix;
                writer.WriteLine(_prefix + "}");
            }
            writer.Flush();
        }
    }
}