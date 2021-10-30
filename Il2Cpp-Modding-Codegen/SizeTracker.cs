﻿using Il2CppModdingCodegen.Data;
using Il2CppModdingCodegen.Data.DllHandling;
using Il2CppModdingCodegen.Serialization;
using Mono.Cecil;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Il2CppModdingCodegen
{
    /// <summary>
    /// Tracks the size of all used value types.
    /// </summary>
    public class SizeTracker
    {
        private readonly int pointerSize;
        private readonly ConcurrentDictionary<TypeDefinition, int> sizeMap = new(new TypeDefinitionComparer());

        public SizeTracker(int pointerSize = 8)
        {
            this.pointerSize = pointerSize;
        }

        public int GetGenericInstanceSize(TypeReference type, TypeDefinition td)
        {
            // If we are a generic instance, we will compute our size right here.
            // In order to do so, we need to iterate over every field in the template type (with the correct alignment, presumably 8)
            // this is actually VERY IMPORTANT
            // For generic types, we may need to actually pad them ourselves so that they get alignment 8 as well
            // However, this means we have to actually understand the padding rules, which shouldn't be too bad.
            // Of course, padding is practically impossible if we have generic parameters as fields in the middle between other fields, so that can be taken with a grain of salt.
            // However, the size of generic instances should still be somewhat feasible to get in isolation, assuming their padding rules don't change based off of the generic type (please don't)
            // And count up their sizes, replacing each generic parameter that appears with the arguments provided in this instance.

            // Techincally speaking, we are fucked if a generic type has multiple generic arguments as fields in arbitrary locations since we can't pad them properly.
            // If that happens, we should have a compile time failure, but we probably want to handle it more reasonably.

            // 0x21: 0x21 bool
            // 0x22: padding[0x2]
            // 0x24: int, 0x2 padding
            // 0x28: bool
            // Padding = offset % sizeof(field)
            return -1;
        }

        public int GetSize(TypeReference type)
        {
            if (type is null)
                throw new ArgumentNullException(nameof(type));
            if (type.IsArray || type.IsPointer)
                return pointerSize;
            if (type.IsGenericParameter)
                // Can't compute size of a generic parameter
                // TODO: Probably could, if it has constraints, or we just assume it's a reference type/boxed value type
                return -1;
            var td = type.ResolveLocked();
            if (td is null)
                throw new InvalidOperationException("Cannot get size of something that cannot be resolved!");
            if (td.MetadataType == MetadataType.Object || td.MetadataType == MetadataType.Class || td.MetadataType == MetadataType.String)
                return pointerSize;
            if (type.IsGenericInstance)
                return GetGenericInstanceSize(type, td);
            return GetSize(td);
        }

        private int GetPrimitiveSize(TypeDefinition type)
        {
            // Handle primitive types explicitly
            if (type.Namespace == "System")
            {
                switch (type.Name)
                {
                    case "Boolean":
                    case "Byte":
                    case "SByte":
                        return 1;

                    case "Char":
                    case "UInt16":
                    case "Int16":
                        return 2;

                    case "Int32":
                    case "UInt32":
                    case "Single":
                        return 4;

                    case "Int64":
                    case "UInt64":
                    case "Double":
                        return 8;

                    case "Object":
                        return 2 * pointerSize;

                    case "ValueType":
                        return 0;
                }
            }
            return -1;
        }

        public int GetSize(TypeDefinition type)
        {
            if (type is null)
                throw new ArgumentNullException(nameof(type));
            if (sizeMap.TryGetValue(type, out var res))
                return res;
            // Otherwise, we need to compute the size of this type.
            if (type.IsGenericInstance)
            {
                // Generic templates actually DO have a size, however, it needs to be carefully computed and adjusted when calculating the size of an instance.
                // Do we pad this type explicitly? Does #pragma pack(push, 8) do the trick? Probably.
                sizeMap.TryAdd(type, -1);
                return -1;
            }

            var primSize = GetPrimitiveSize(type);
            if (primSize >= 0)
            {
                sizeMap.TryAdd(type, primSize);
                return primSize;
            }
            var instanceFields = type.Fields.Where(f => !f.IsStatic);
            var last = instanceFields.LastOrDefault();
            bool acceptZeroOffset = type.BaseType is null;
            var pt = type.BaseType?.ResolveLocked();

            if (last is null)
            {
                if (type.BaseType is null)
                {
                    sizeMap.TryAdd(type, 0);
                    // We have 0 size if we have no parent and no fields.
                    return 0;
                }
                // Our size is just our parent's size
                if (pt is null)
                {
                    sizeMap.TryAdd(type, -1);
                    // We probably inherit a generic type, or something similar
                    return -1;
                }
                int parentSize = GetSize(pt);
                // Add our size to the map
                sizeMap.TryAdd(type, parentSize);
                return parentSize;
            }
            if (instanceFields.Any(f => GetSize(f.FieldType) == -1))
            {
                // If we have any invalid fields, we take on a size of -1, even if we are potentially still capable of knowing our size.
                sizeMap.TryAdd(type, -1);
                return -1;
            }

            if (type.BaseType is not null)
            {
                // If we have a parent
                if (pt is null)
                {
                    // Couldn't find a valid parent!
                    sizeMap.TryAdd(type, -1);
                    return -1;
                }
                var pSize = GetSize(pt);
                if (pSize == -1)
                {
                    sizeMap.TryAdd(type, -1);
                    return -1;
                }
                acceptZeroOffset = GetSize(pt) == 0;
                // If our parent's size is -1, we are also -1.
            }
            // If the offset is greater than 0, we trust it.
            if (last.Offset > 0)
            {
                int fSize = GetSize(last.FieldType);
                if (fSize <= 0)
                {
                    sizeMap.TryAdd(type, -1);
                    return -1;
                }
                sizeMap.TryAdd(type, fSize + last.Offset);
                return fSize + last.Offset;
            }
            // If we have no parent, or we have a parent of size 0, and we only have one field, then we trust offset 0
            if (acceptZeroOffset && instanceFields.Count() == 1 && last.Offset == 0)
            {
                int fSize = GetSize(last.FieldType);
                sizeMap.TryAdd(type, fSize);
                return fSize;
            }
            // We don't know how to handle negative offsets, so we return -1 for those.
            return -1;
        }
    }
}