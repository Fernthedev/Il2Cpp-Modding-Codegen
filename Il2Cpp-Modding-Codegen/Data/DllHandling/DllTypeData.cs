﻿using Il2Cpp_Modding_Codegen.Config;
using Il2Cpp_Modding_Codegen.Parsers;
using Mono.Cecil;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Il2Cpp_Modding_Codegen.Data.DllHandling
{
    internal class DllTypeData : ITypeData
    {
        /// <summary>
        /// Number of characters the namespace name starts on
        /// </summary>
        private const int NamespaceStartOffset = 13;

        private TypeDefinition _def;

        public TypeEnum Type { get; private set; }
        public TypeInfo Info { get; private set; }
        public TypeRef This { get; }
        public TypeRef Parent { get; private set; }
        public List<TypeRef> ImplementingInterfaces { get; } = new List<TypeRef>();
        public int TypeRefIndex { get; private set; }
        public List<IAttribute> Attributes { get; } = new List<IAttribute>();
        public List<ISpecifier> Specifiers { get; } = new List<ISpecifier>();
        public List<IField> Fields { get; } = new List<IField>();
        public List<IProperty> Properties { get; } = new List<IProperty>();
        public List<IMethod> Methods { get; } = new List<IMethod>();

        /// <summary>
        /// List of dependency TypeRefs to resolve in the type
        /// </summary>
        internal HashSet<TypeRef> References { get; } = new HashSet<TypeRef>();

        private DllConfig _config;

        private void ParseAttributes(TypeDefinition def)
        {
            Attributes.AddRange(DllAttribute.From(def));
        }

        static TypeEnum ExtractTypeEnum(TypeDefinition def)
        {
            if (def.IsEnum) return TypeEnum.Enum;
            if (def.IsInterface) return TypeEnum.Interface;
            if (def.IsClass) return TypeEnum.Class;
            Console.WriteLine($"Warning: assuming {def.FullName} is a struct!");
            // TODO: use def.IsPrimitive?
            return TypeEnum.Struct;
        }

        // Sets: TypeRefIndex and above
        private void ParseTypeName(TypeDefinition def)
        {
            // TODO: extract TypeDefIndex?
            foreach (var i in def.Interfaces)
            {
                ImplementingInterfaces.Add(new TypeRef(i));
            }
            Parent = new TypeRef(def.BaseType);

            This.Set(TypeRef.From(def));

            Type = ExtractTypeEnum(def);
            Specifiers.AddRange(DllSpecifier.From(def));
            Info = new TypeInfo
            {
                TypeFlags = Type == TypeEnum.Class || Type == TypeEnum.Interface ? TypeFlags.ReferenceType : TypeFlags.ValueType
            };
        }

        private void ParseFields(TypeDefinition def)
        {
            foreach (var f in def.Fields)
            {
                Fields.Add(new DllField(f, def));
            }
        }

        private void ParseProperties(TypeDefinition def)
        {
            foreach (var p in def.Properties)
            {
                Properties.Add(new DllProperty(p, def));
            }
        }

        private void ParseMethods(TypeDefinition def)
        {
            foreach (var m in def.Methods)
            {
                Methods.Add(new DllMethod(m, def));
            }
        }

        public DllTypeData(TypeDefinition def, DllConfig config)
        {
            _config = config;
            _def = def;

            This = new TypeRef();
            This.Namespace = def.Namespace;
            ParseAttributes(def);
            ParseTypeName(def);
            ParseFields(def);
            ParseProperties(def);
            ParseMethods(def);
        }

        public override string ToString()
        {
            var s = $"// Namespace: {This.Namespace}\n";
            foreach (var attr in Attributes)
            {
                s += $"{attr}\n";
            }
            foreach (var spec in Specifiers)
            {
                s += $"{spec} ";
            }
            s += $"{Type.ToString().ToLower()} {This.Name}";
            if (Parent != null)
            {
                s += $" : {Parent}";
            }
            s += "\n{";
            if (Fields.Count > 0)
            {
                s += "\n\t// Fields\n\t";
                foreach (var f in Fields)
                {
                    s += $"{f}\n\t";
                }
            }
            if (Properties.Count > 0)
            {
                s += "\n\t// Properties\n\t";
                foreach (var p in Properties)
                {
                    s += $"{p}\n\t";
                }
            }
            if (Methods.Count > 0)
            {
                s += "\n\t// Methods\n\t";
                foreach (var m in Methods)
                {
                    s += $"{m}\n\t";
                }
            }
            s = s.TrimEnd('\t');
            s += "}";
            return s;
        }
    }
}