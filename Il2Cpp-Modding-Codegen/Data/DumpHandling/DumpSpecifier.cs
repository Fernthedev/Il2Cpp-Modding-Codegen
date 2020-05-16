﻿using System;
using System.Collections.Generic;
using System.Text;

namespace Il2Cpp_Modding_Codegen.Data.DumpHandling
{
    internal class DumpSpecifier : ISpecifier
    {
        public string Value { get; }
        public bool Static => Value == "static";
        public bool Private => Value == "private";
        public bool Internal => Value == "internal";
        public bool Public => Value == "public";
        public bool Sealed => Value == "sealed";
        public bool Override => Value == "override";
        public bool Readonly => Value == "readonly";
        public bool Const => Value == "const";

        public DumpSpecifier(string specifier)
        {
            Value = specifier;
        }

        public override string ToString()
        {
            return Value;
        }
    }
}