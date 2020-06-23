﻿using Il2Cpp_Modding_Codegen.Config;
using Il2Cpp_Modding_Codegen.Data;
using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Text;

namespace Il2Cpp_Modding_Codegen.Serialization
{
    public class CppStaticFieldSerializer : Serializer<IField>
    {
        private string _declaringFullyQualified;
        private Dictionary<IField, string> _resolvedTypes = new Dictionary<IField, string>();
        bool _asHeader;
        private SerializationConfig _config;

        public CppStaticFieldSerializer(SerializationConfig config)
        {
            _config = config;
        }

        public override void PreSerialize(CppTypeContext context, IField field)
        {
            _declaringFullyQualified = context.QualifiedTypeName;
            var resolved = context.GetCppName(field.Type, true);
            if (!(resolved is null))
            {
                // Add static field to forward declares, since it is used by the static _get and _set methods
                Resolved(field);
            }
            _resolvedTypes.Add(field, resolved);
        }

        private string SafeName(IField field)
        {
            return field.Name.Replace('<', '$').Replace('>', '$');
        }

        private string GetGetter(string fieldType, IField field, bool namespaceQualified)
        {
            var retStr = fieldType;
            if (_config.OutputStyle == OutputStyle.Normal)
                retStr = "std::optional<" + retStr + ">";
            var staticStr = string.Empty;
            var ns = string.Empty;
            if (namespaceQualified)
                ns = _declaringFullyQualified + "::";
            if (_asHeader)
                staticStr = "static ";
            // Collisions with this name are incredibly unlikely.
            return $"{staticStr + retStr} {ns}_get_{SafeName(field)}()";
        }

        private string GetSetter(string fieldType, IField field, bool namespaceQualified)
        {
            var ns = string.Empty;
            var staticStr = string.Empty;
            if (namespaceQualified)
                ns = _declaringFullyQualified + "::";
            if (_asHeader)
                staticStr = "static ";
            return $"{staticStr}void {ns}_set_{SafeName(field)}({fieldType} value)";
        }

        public override void Serialize(CppStreamWriter writer, IField field, bool asHeader)
        {
            _asHeader = asHeader;
            if (_resolvedTypes[field] == null)
                throw new UnresolvedTypeException(field.DeclaringType, field.Type);
            var fieldCommentString = "";
            foreach (var spec in field.Specifiers)
                fieldCommentString += $"{spec} ";
            fieldCommentString += $"{field.Type} {field.Name}";
            var resolvedName = _resolvedTypes[field];
            if (_asHeader && !field.DeclaringType.IsGenericTemplate)
            {
                // Create two method declarations:
                // static FIELDTYPE _get_FIELDNAME();
                // static void _set_FIELDNAME(FIELDTYPE value);
                writer.WriteComment("Get static field: " + fieldCommentString);
                writer.WriteDeclaration(GetGetter(resolvedName, field, !_asHeader));
                writer.WriteComment("Set static field: " + fieldCommentString);
                writer.WriteDeclaration(GetSetter(resolvedName, field, !_asHeader));
            }
            else
            {
                // Write getter
                writer.WriteComment("Autogenerated static field getter");
                writer.WriteComment("Get static field: " + fieldCommentString);
                writer.WriteDefinition(GetGetter(resolvedName, field, !_asHeader));

                var s = "return ";
                var innard = $"<{resolvedName}>";
                var macro = "CRASH_UNLESS((";
                if (_config.OutputStyle != OutputStyle.CrashUnless)
                    macro = "";

                s += $"{macro}il2cpp_utils::GetFieldValue{innard}(";
                s += $"\"{field.DeclaringType.Namespace}\", \"{field.DeclaringType.Name}\", \"{field.Name}\")";
                if (!string.IsNullOrEmpty(macro)) s += "))";
                s += ";";
                writer.WriteLine(s);
                writer.CloseDefinition();
                // Write setter
                writer.WriteComment("Autogenerated static field setter");
                writer.WriteComment("Set static field: " + fieldCommentString);
                writer.WriteDefinition(GetSetter(resolvedName, field, !_asHeader));
                s = "";
                if (_config.OutputStyle == OutputStyle.CrashUnless)
                    macro = "CRASH_UNLESS(";
                else
                    macro = "RET_V_UNLESS(";

                s += $"{macro}il2cpp_utils::SetFieldValue(";
                s += $"\"{field.DeclaringType.Namespace}\", \"{field.DeclaringType.Name}\", \"{field.Name}\", value));";
                writer.WriteLine(s);
                writer.CloseDefinition();
            }
            writer.Flush();
            Serialized(field);
        }
    }
}