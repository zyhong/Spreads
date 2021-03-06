﻿// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at http://mozilla.org/MPL/2.0/.

using Spreads.Buffers;
using System;
using System.Reflection;
using Spreads.Serialization;

namespace Spreads.DataTypes
{
    public readonly struct FixedArray<T>
    {
        // Note that Count is defined at run-time.
        // Idea is that BinarySerializer could write header to a fixed destination.
        // If the destination is empty a header is written, if not empty then a
        // header must be equal. So if we are filling a block with values one-by-one
        // the first value sets the FixedArray size and then any attempt to write
        // a different value will fail.

        public readonly T[] Array; // TODO Memory<T>
        public readonly byte Count;

        public FixedArray(T[] array, byte count)
        {
            Array = array;
            Count = count;
        }
    }

    internal interface IFixedArraySerializer
    {
        int FixedArrayCount<T>(T value);
    }

    internal static class FixedArraySerializerFactory
    {
        public static BinarySerializer<FixedArray<TElement>> GenericCreate<TElement>()
        {
            return new FixedArraySerializer<TElement>();
        }

        public static object Create(Type type)
        {
            var method = typeof(FixedArraySerializerFactory).GetTypeInfo().GetMethod(nameof(GenericCreate));
            var generic = method?.MakeGenericMethod(type);
            return generic?.Invoke(null, null);
        }

        internal class FixedArraySerializer<T> :  BinarySerializer<FixedArray<T>>, IFixedArraySerializer
        {
            public override byte KnownTypeId => throw new System.NotImplementedException();

            public override short FixedSize => throw new System.NotImplementedException();

            public override int SizeOf(in FixedArray<T> value, BufferWriter payload)
            {
                throw new NotImplementedException();
            }

            public override int Write(in FixedArray<T> value, DirectBuffer destination)
            {
                throw new System.NotImplementedException();
            }

            public override int Read(DirectBuffer source, out FixedArray<T> value)
            {
                throw new System.NotImplementedException();
            }

            public int FixedArrayCount<T1>(T1 value)
            {
                var fa = (FixedArray<T>)(object)(value);
                return fa.Count;
            }
        }
    }
}