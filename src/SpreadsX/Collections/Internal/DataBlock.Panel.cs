// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at http://mozilla.org/MPL/2.0/.

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Spreads.Buffers;
using Spreads.Native;
using Spreads.Utils;

namespace Spreads.Collections.Internal
{
    internal sealed partial class DataBlock
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void UnsafeGetRowKeyValue<TKey, TValue>(int index, out TKey key, out TValue value)
        {
            ThrowHelper.DebugAssert(index >= _head && index < RowCount, $"DangerousGetRowKeyValueRef: index [{index}] >=0 && index < _rowCount [{RowCount}]");
            var offset = _head + index;
            key = _rowKeys.UnsafeReadUnaligned<TKey>(offset);
            value = _values.UnsafeReadUnaligned<TValue>(offset);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T UnsafeGetRowKey<T>(int index)
        {
            return _rowKeys.UnsafeReadUnaligned<T>(_head + index);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T UnsafeGetValue<T>(int index)
        {
            return _values.UnsafeReadUnaligned<T>(_head + index);
        }

        [Conditional("DEBUG")]
        private void EnsurePanelLayout()
        {
            // TODO more checks
            if (_columns != null)
                ThrowHelper.ThrowInvalidOperationException("_columns != null in panel layout");

            ThrowHelper.DebugAssert((_columnKeys == default && ColumnCount == 1) || ColumnCount == _columnKeys.Length);

            var valueCount = _rowKeys.Length * ColumnCount;

            ThrowHelper.DebugAssert(valueCount <= _values.Length, "Values vector must have enough capacity for data");
        }

        /// <summary>
        /// Create a DataBlock for Series or Panel.
        /// </summary>
        public static DataBlock CreateForPanel(RetainedVec rowKeys = default, RetainedVec values = default, int rowCount = -1, RetainedVec columnKeys = default,
            int columnCount = -1)
        {
            // this must work with rowLength == 0 and empty vecs, this is how we initialize empty block that is not sentinel

            if (columnCount < 1)
            {
                columnCount = Math.Max(1, columnKeys.Length);
            }
            else
            {
                if ((uint) columnCount > columnKeys.Length)
                    ThrowHelper.ThrowArgumentOutOfRangeException(nameof(columnCount));
            }

            // RetainedVec is slice-able, easy to get exact layout 
            ThrowHelper.Assert(rowKeys.Length == values.Length * columnCount, "rowKeys.Length != values.Length * columnCount");

            var block = ObjectPool.Rent()!;
            block.EnsureDisposed();

            var rowCapacity = rowKeys.Length;

            block._rowKeys = rowKeys;
            block._columnKeys = columnKeys;
            block._values = values;

            if (rowCount == -1)
            {
                block.RowCount = rowCapacity;
            }
            else
            {
                if ((uint) rowCount > rowCapacity)
                    ThrowHelper.ThrowArgumentOutOfRangeException(nameof(rowCount));
                else
                    block.RowCount = rowCount;
            }

            block.ColumnCount = columnCount;

            block._rowCapacity = rowCapacity;
            block._head = 0;

            block._refCount = 0;

            block.EnsurePanelLayout();

            ThrowHelper.DebugAssert(!block.IsDisposed, "!block.IsDisposed");

            return block;
        }

        public static DataBlock CreateForVector<TValue>(int rowCapacity = -1)
        {
            return CreateForSeries<Index, TValue>(rowCapacity);
        }

        public static DataBlock CreateForSeries<TRowKey, TValue>(int rowCapacity = -1)
        {
            return CreateForPanel<TRowKey, Index, TValue>(rowCapacity, -1);
        }

        public static DataBlock CreateForPanel<TRowKey, TColumnKey, TValue>(int rowCapacity = -1,
            int columnCount = -1)
        {
            RetainedVec columnKeys = default;
            if (typeof(TColumnKey) != typeof(Index))
            {
                var columnKeysMemory = BufferPool<TRowKey>.MemoryPool.RentMemory(columnCount, exactBucket: true);
                columnKeys = RetainedVec.Create(columnKeysMemory, start: 0, columnKeysMemory.Length);
                ThrowHelper.Assert(columnCount >= 1);
            }

            var block = CreateForPanel(default, default, -1, columnKeys, columnCount);
            block.IncreaseRowsCapacity<TRowKey, TValue>(rowCapacity);
            return block;
        }

        /// <summary>
        /// Returns new <see cref="RowCapacity"/> or -1 on failure
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining
#if HAS_AGGR_OPT
                    | MethodImplOptions.AggressiveOptimization
#endif
        )]
        internal int IncreaseRowsCapacity<TRowKey, TValue>(int newCapacity = -1)
        {
            if (_head != 0)
                ThrowHelper.ThrowNotImplementedException();

            EnsureNotSentinel();
            EnsurePanelLayout();

            if (_rowKeys.RuntimeTypeId != 0)
            {
                if (_rowKeys.RuntimeTypeId != VecTypeHelper<TRowKey>.RuntimeTypeId)
                    throw new ArrayTypeMismatchException(
                        $"Type of TRowKey {typeof(TRowKey)} doesn't match existing row keys type {VecTypeHelper.GetInfo(_rowKeys.RuntimeTypeId).Type}");
            }
            else if (RowCount > 0)
            {
#pragma warning disable 618
                ThrowHelper.FailFast("Runtime type id for non-empty RowKeys was not set.");
#pragma warning restore 618
            }

            if (_values.RuntimeTypeId != 0)
            {
                if (_values.RuntimeTypeId != VecTypeHelper<TValue>.RuntimeTypeId)
                    throw new ArrayTypeMismatchException($"Type of TValue {typeof(TValue)} doesn't match existing value type {VecTypeHelper.GetInfo(_values.RuntimeTypeId).Type}");
            }
            else if (RowCount > 0)
            {
#pragma warning disable 618
                ThrowHelper.FailFast("Runtime type id for non-empty Values was not set.");
#pragma warning restore 618
            }

            var rowKeys = _rowKeys;
            var values = _values;

            var minCapacity = Math.Max(newCapacity, Settings.MIN_POOLED_BUFFER_LEN);
            var newRowCapacity = BitUtil.FindNextPositivePowerOfTwo(Math.Max(minCapacity, _rowCapacity + 1));

            RetainableMemory<TRowKey>? newRowKeysBuffer = null;
            RetainedVec newRowKeys = default;
            RetainableMemory<TValue>? newValuesBuffer = null;
            RetainedVec newValues = default;

            try
            {
                if (typeof(TRowKey) != typeof(Index))
                {
                    newRowKeysBuffer = BufferPool<TRowKey>.MemoryPool.RentMemory(newRowCapacity, exactBucket: true);
                    ThrowHelper.DebugAssert(newRowKeysBuffer.Length == newRowCapacity);
                    newRowKeys = RetainedVec.Create(newRowKeysBuffer, start: 0, newRowKeysBuffer.Length); // new buffer could be larger
                }

                newValuesBuffer = BufferPool<TValue>.MemoryPool.RentMemory(newRowCapacity * ColumnCount, exactBucket: true);
                ThrowHelper.DebugAssert(newValuesBuffer.Length == newRowCapacity * ColumnCount);
                newValues = RetainedVec.Create(newValuesBuffer, start: 0, newValuesBuffer.Length);

                if (RowCount > 0)
                {
                    if (typeof(TRowKey) != typeof(Index))
                        rowKeys.GetSpan<TRowKey>().Slice(0, RowCount).CopyTo(newRowKeys.GetSpan<TRowKey>());
                    values.GetSpan<TValue>().Slice(0, RowCount * ColumnCount).CopyTo(newValues.GetSpan<TValue>());
                }
            }
            catch (OutOfMemoryException)
            {
                if (typeof(TRowKey) != typeof(Index))
                {
                    if (newRowKeys != default)
                        newRowKeys.Dispose();
                    else
                        newRowKeysBuffer?.DecrementIfOne();
                }

                if (newValues != default)
                    newValues.Dispose();
                else
                    newValuesBuffer?.DecrementIfOne();

                // TODO log this event
                return -1;
            }

            try
            {
                try
                {
                }
                finally
                {
                    // we have all needed buffers, must switch in one operation

                    if (typeof(TRowKey) != typeof(Index))
                    {
                        _rowKeys = newRowKeys;
                        rowKeys.Dispose();
                    }

                    _values = newValues;
                    values.Dispose();

                    _rowCapacity = newRowCapacity;
                }

                return _rowCapacity;
            }
            catch
            {
                return -1;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining
#if HAS_AGGR_OPT
                    | MethodImplOptions.AggressiveOptimization
#endif
        )]
        internal void AppendBlock<TKey, TValue>(TKey key, TValue value)
        {
            if (AdditionalCorrectnessChecks.Enabled)
            {
                EnsurePanelLayout();
                EnsureNotSentinel();
                ThrowHelper.Assert(RowCount < _rowKeys.Length, "_rowCount < _rowKeys.Length");
                ThrowHelper.Assert(_rowKeys._runtimeTypeId == VecTypeHelper<TKey>.RuntimeTypeId, "_rowKeys._runtimeTypeId == VecTypeHelper<TKey>.RuntimeTypeId");
                ThrowHelper.Assert(_values._runtimeTypeId == VecTypeHelper<TValue>.RuntimeTypeId, "_values._runtimeTypeId == VecTypeHelper<TValue>.RuntimeTypeId");
            }

            var offset = RowCount;

            _rowKeys.UnsafeWriteUnaligned(offset, key);
            _values.UnsafeWriteUnaligned(offset, value);

            // volatile increment goes last
            RowCount++;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining
#if HAS_AGGR_OPT
                    | MethodImplOptions.AggressiveOptimization
#endif
        )]
        internal void AppendBlock<TKey, TValue>(TKey key, ReadOnlySpan<TValue> values)
        {
            if (AdditionalCorrectnessChecks.Enabled)
            {
                EnsurePanelLayout(); // this guaranties that values have capacity for _rowKeys.Length * _columnCount
                EnsureNotSentinel();
                ThrowHelper.Assert(RowCount < _rowKeys.Length, "_rowCount < _rowKeys.Length");
                ThrowHelper.Assert(_rowKeys._runtimeTypeId == VecTypeHelper<TKey>.RuntimeTypeId, "_rowKeys._runtimeTypeId == VecTypeHelper<TKey>.RuntimeTypeId");
                ThrowHelper.Assert(_values._runtimeTypeId == VecTypeHelper<TValue>.RuntimeTypeId, "_values._runtimeTypeId == VecTypeHelper<TValue>.RuntimeTypeId");
                ThrowHelper.Assert(values.Length == ColumnCount);
            }

            var offset = RowCount;

            _rowKeys.UnsafeWriteUnaligned(offset, key);
            _values.UnsafeWriteUnaligned(offset, values);

            // volatile increment goes last
            RowCount++;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining
#if HAS_AGGR_OPT
                    | MethodImplOptions.AggressiveOptimization
#endif
        )]
        internal bool TryAppendToBlock<TKey, TValue>(TKey key, TValue value)
        {
            if (RowCount >= _rowKeys.Length)
                return false;

            AppendBlock(key, value);
            return true;
        }

        // There was trivial insert method, see git history. Nothing special, just copying Spans
    }
}