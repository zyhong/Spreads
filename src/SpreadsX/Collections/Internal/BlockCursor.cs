// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at http://mozilla.org/MPL/2.0/.

using Spreads.Threading;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Spreads.Collections.Internal
{
    // TODO maybe it's possible to create a single cursor for all containers?

    // TODO problem: with synced moves we must not update cursor state unless succeeded
    // therefore we cannot use it as internal cursor of other cursors - CV access needs
    // to be synced. If we compare order version then we throw on CV getter, otherwise
    // we will throw on next move but CV could be stale.

    /// <summary>
    /// <see cref="Series{TKey,TValue}"/> cursor implementation.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct BlockCursor<TKey, TValue, TContainer> : ICursor<TKey, DataBlock, BlockCursor<TKey, TValue, TContainer>>
        where TContainer : BaseContainer<TKey>
    {
        internal TContainer _source;

        /// <summary>
        /// Backing storage for <see cref="CurrentBlock"/>. Must never be used directly.
        /// </summary>
        [Obsolete("Use CurrentBlock")]
        private DataBlock _currentBlockStorage;

        internal DataBlock CurrentBlock
        {
#pragma warning disable 618
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _currentBlockStorage;
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set
            {
                if (_currentBlockStorage != DataBlock.Empty)
                {
                    _currentBlockStorage.Decrement();
                }
                _currentBlockStorage = value;
                if (value != DataBlock.Empty)
                {
                    value?.Increment();
                }
            }
#pragma warning restore 618
        }

        internal int BlockIndex;

        // TODO offtop, from empty to non-empty changes order from 0 to 1

        // TODO review/test order version overflow in AC

        /// <summary>
        /// Series order version saved at cursor creation to detect changes in series.
        /// Should only be checked for <see cref="Mutability.Mutable"/>, append-only does not change order.
        /// </summary>
        internal int _orderVersion;

        // Note: We need to cache CurrentKey:
        // * in most cases it is <= 8 bytes so the entire struct should be <= 32 bytes or 1/2 cache line;
        // * we use it to recover from OOO exceptions;
        // * it does not affect performance too much and evaluation will be needed anyways in most cases.
        internal TKey _currentKey;

        internal TValue _currentValue;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public BlockCursor(TContainer source)
        {
            _source = source;
            BlockIndex = -1;
#pragma warning disable 618
            _currentBlockStorage = DataBlock.Empty;
#pragma warning restore 618
            _orderVersion = AtomicCounter.GetCount(ref _source.OrderVersion); // TODO
            _currentKey = default!;
            _currentValue = default!;
            Debug.Assert(source.Data != null, "source.Data != null: must be DataBlock.Empty instead of null");
            if (source.IsDataBlock(out var db, out _))
            {
                CurrentBlock = db;
            }
        }

        public CursorState State
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                if (_source == null)
                {
                    return CursorState.None;
                }

                return BlockIndex >= 0 ? CursorState.Moving : CursorState.Initialized;
            }
        }

        public KeyComparer<TKey> Comparer
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _source._comparer;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MoveAt(TKey key, Lookup direction)
        {
            ThrowHelper.DebugAssert(!CurrentBlock.IsDisposed || ReferenceEquals(CurrentBlock, DataBlock.Empty), "!CurrentBlock.IsDisposed || ReferenceEquals(CurrentBlock, DataBlock.Empty)");

            bool found;
            int nextPosition;
            DataBlock nextBlock;
            TValue v = default!;
            var sw = new SpinWait();

        RETRY:

            var version = _source.Version;
            {
                found = _source.TryFindBlockAt(ref key, direction, out nextBlock, out nextPosition);
                if (found)
                {
                    v = GetCurrentValue(nextPosition);
                }
            }

            if (_source.NextVersion != version)
            {
                // See Move comments
                EnsureSourceNotDisposed();
                EnsureOrder();
                sw.SpinOnce();
                goto RETRY;
            }

            if (found)
            {
                BlockIndex = nextPosition;
                _currentKey = key;
                _currentValue = v;
                if (nextBlock != null)
                {
                    CurrentBlock = nextBlock;
                }
            }

            return found;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private TValue GetCurrentValue(int currentBlockIndex)
        {
            if (typeof(TContainer) == typeof(Series<TKey, TValue>))
            {
                return CurrentBlock.DangerousValueRef<TValue>(currentBlockIndex);
            }
            if (typeof(TContainer) == typeof(BaseContainer<TKey>))
            {
                return default;
            }
            ThrowHelper.ThrowNotImplementedException();
            return default;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void GetCurrentKeyValue(int currentBlockIndex, out TKey key, out TValue value)
        {
            if (typeof(TContainer) == typeof(Series<TKey, TValue>))
            {
                CurrentBlock.DangerousGetRowKeyValueRef(currentBlockIndex, out key, out value);
                return;
            }
            if (typeof(TContainer) == typeof(BaseContainer<TKey>))
            {
                key = CurrentBlock.DangerousRowKeyRef<TKey>(currentBlockIndex);
                value = default;
                return;
            }
            ThrowHelper.ThrowNotImplementedException();
            key = default;
            value = default;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining
#if HAS_AGGR_OPT
                    | MethodImplOptions.AggressiveOptimization
#endif
        )]
        public long Move(long stride, bool allowPartial)
        {
            if (TryMove(stride, allowPartial, out var mc))
            {
                return mc;
            }
            ThrowHelper.ThrowOutOfOrderKeyException(_currentKey);
            return 0;
        }

        // TODO docs on handling OOO
        /// <summary>
        /// Returns true if a move is valid or false if the source order changed
        /// since this cursor construction or last <see cref="MoveAt"/> move.
        /// </summary>
        /// <seealso cref="OutOfOrderKeyException{TKey}"/>
        [MethodImpl(MethodImplOptions.AggressiveInlining
#if HAS_AGGR_OPT
                    | MethodImplOptions.AggressiveOptimization
#endif
        )]
        public bool TryMove(long stride, bool allowPartial, out long moveCount)
        {
            // In this top part of Move we touch CurrentBlock.RowCount only once,
            // hope that we are still in the current block and moving forward
            // and handle all other scenarios in the MoveRare part

            ThrowHelper.DebugAssert(!CurrentBlock.IsDisposed || ReferenceEquals(CurrentBlock, DataBlock.Empty), "!CurrentBlock.IsDisposed || ReferenceEquals(CurrentBlock, DataBlock.Empty)");

            ulong newBlockIndex; // we need a local var
            TKey k = default!;
            TValue v = default!;
            var sw = new SpinWait();

        SYNC:
            var version = _source.Version;
            {
                // Note: this does not handle MP from uninitialized state (_blockPosition == -1, stride <= 0). This case is rare.
                // Uninitialized multi-block case goes to rare as well as uninitialized MP
                newBlockIndex = unchecked((ulong)(BlockIndex + stride)); // int.Max + long.Max < ulong.Max

                var rowCount = CurrentBlock.RowCount;
                if (AdditionalCorrectnessChecks.Enabled)
                { ThrowHelper.Assert(rowCount >= 0, "rowCount >= 0 for all CurrentBlocks, empty sentinel has zero length specifically for this case"); }

                if (newBlockIndex < (ulong)rowCount)
                {
                    moveCount = stride;
                }
                else
                {
                    moveCount = MoveRare(stride, allowPartial, ref newBlockIndex);
                    if (AdditionalCorrectnessChecks.Enabled && moveCount != 0)
                    {
                        if (newBlockIndex >= (ulong)CurrentBlock.RowCount)
                        {
                            ThrowBadNewBlockIndex(newBlockIndex, moveCount);
                        }
                    }
                }

                if (moveCount != 0)
                {
                    // ThrowHelper.Assert((long)newBlockIndex <= CurrentBlock.RowCount);
                    // Note: do not use _blockPosition, it's 20% slower than second cast to int
                    GetCurrentKeyValue((int)newBlockIndex, out k, out v);
                }
            }
            if (_source.NextVersion != version)
            {
                sw.SpinOnce();
                goto SYNC;
            }

            if (_orderVersion != AtomicCounter.GetCount(ref _source.OrderVersion))
            {
                moveCount = 0;
                return false;
            }

            if (moveCount != 0)
            {
                BlockIndex = (int)newBlockIndex;
                _currentKey = k;
                _currentValue = v;
            }

            if (AdditionalCorrectnessChecks.Enabled)
            { if (moveCount != 0 && moveCount != stride && !allowPartial) { ThrowBadReturnValue(stride, moveCount); } }

            return true;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void ThrowBadNewBlockIndex(ulong newBlockIndex, long mc)
        {
            ThrowHelper.ThrowInvalidOperationException(
                $"newBlockIndex [{(long)newBlockIndex}] >= (ulong)CurrentBlock.RowCount [{CurrentBlock.RowCount}], mc={mc}");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void ThrowBadReturnValue(long stride, long mc)
        {
            ThrowHelper.ThrowInvalidOperationException(
                $"Return value mc={mc} does not equal to stride=[{stride}]");
        }

        /// <summary>
        /// Called when next position is outside current block. Must be pure and do not change state.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining
#if HAS_AGGR_OPT
                    | MethodImplOptions.AggressiveOptimization
#endif
        )]
        public long MoveRare(long stride, bool allowPartial, ref ulong newBlockIndex)
        {
            if (stride == 0)
            {
                return 0;
            }

            if (_source.IsDataBlock(out var localBlock, out var ds))
            {
                if (CurrentBlock != localBlock && ReferenceEquals(CurrentBlock, DataBlock.Empty))
                {
                    // initialized cursor before adding first value
                    CurrentBlock = localBlock;
                    return Move(stride, allowPartial);
                }

                ThrowHelper.DebugAssert(CurrentBlock == localBlock, "CurrentBlock == localBlock");

                if (BlockIndex < 0 & stride < 0) // not &&
                {
                    ThrowHelper.DebugAssert(State == CursorState.Initialized, "State == CursorState.Initialized");
                    var nextPosition = unchecked((localBlock.RowCount + stride));
                    if (nextPosition >= 0)
                    {
                        newBlockIndex = (ulong)nextPosition;
                        return stride;
                    }

                    if (allowPartial)
                    {
                        newBlockIndex = 0;
                        return -localBlock.RowCount;
                    }
                }

                if (allowPartial)
                {
                    // TODO test for edge cases
                    if (BlockIndex + stride >= localBlock.RowCount)
                    {
                        var mc = (localBlock.RowCount - 1) - BlockIndex;
                        newBlockIndex = (ulong)(BlockIndex + mc);
                        return mc;
                    }
                    if (stride < 0) // cannot just use else without checks before, e.g. what if _blockPosition == -1 and stride == 0
                    {
                        newBlockIndex = 0;
                        return -BlockIndex;
                    }
                }

                newBlockIndex = 0;
                return 0;
            }

            if (CurrentBlock.IsFull)
            {
                return MoveBlock(ds, stride, allowPartial, ref newBlockIndex);
            }

            return 0;
        }

        [MethodImpl(MethodImplOptions.NoInlining
#if HAS_AGGR_OPT
                    | MethodImplOptions.AggressiveOptimization
#endif
        )]
        private long MoveBlock(DataBlockSource<TKey> ds, long stride, bool allowPartial, ref ulong nextBlockIndex)
        {
            var cb = CurrentBlock;
            int mc;

            if (stride > 0)
            {
                mc = BlockIndex == -1 ? 1 : cb.RowCount - BlockIndex; // we left CB, at first pos of NB
                // TODO this works now in tests because it doesn't work
                //ThrowHelper.Assert(mc == 1 | mc == 0, $"mc={mc} rc={cb.RowCount} bi={BlockIndex}");
                while (true)
                {
                    if (ds.TryGetNextBlock(cb, out var nb))
                    {
                        var rowCount = nb.RowCount;
                        ThrowHelper.DebugAssert(rowCount > 0, $"nb.RowCount [{rowCount}] > 0, is DataBlock.Empty = {ReferenceEquals(nb, DataBlock.Empty)}");
                        var idx = stride - mc;
                        if ((ulong)idx < (ulong)rowCount)
                        {
                            // this block
                            nextBlockIndex = (ulong)idx;
                            CurrentBlock = nb;
                            return mc + (int)nextBlockIndex;
                        }

                        cb = nb;
                        mc += rowCount;
                        Console.WriteLine($"mc [{mc}] == 1");
                        //ThrowHelper.Assert(mc == 1, $"mc [{mc}] == 1");
                    }
                    else
                    {
                        if (allowPartial)
                        {
                            // last in cb
                            nextBlockIndex = (ulong)(cb.RowCount - 1);
                            CurrentBlock = cb;
                            return mc - 1;
                        }

                        break;
                    }
                }
            }
            else
            {
                ThrowHelper.DebugAssert(stride < 0, "stride < 0");

                mc = BlockIndex == -1 ? -1 : -(BlockIndex + 1); // at last pos of PB

                while (true)
                {
                    if (ds.TryGetPreviousBlock(cb, out var pb))
                    {
                        ThrowHelper.DebugAssert(pb.RowCount > 0, "pb.RowCount > 0");
                        ThrowHelper.DebugAssert(stride - mc <= 0, "stride - mc < 0");
                        var idx = pb.RowCount - 1 + (stride - mc);
                        if (idx >= 0)
                        {
                            // this block
                            nextBlockIndex = (ulong)idx;
                            CurrentBlock = pb;
                            return mc + idx;
                        }

                        cb = pb;
                        mc -= pb.RowCount;
                    }
                    else
                    {
                        if (allowPartial)
                        {
                            // first in cb
                            nextBlockIndex = 0;
                            CurrentBlock = cb;
                            return mc + 1;
                        }

                        break;
                    }
                }
            }
            return 0;
        }

        [Obsolete("TODO cursor counter, throw on dispose if there are undisposed cursors")]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void EnsureSourceNotDisposed()
        {
            if (_source.IsDisposed)
            {
                ThrowCursorSourceDisposed();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void EnsureOrder()
        {
            // this should be false for all cases

            if (_orderVersion != AtomicCounter.GetCount(ref _source.OrderVersion))
            {
                ThrowHelper.ThrowOutOfOrderKeyException(_currentKey);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowCursorSourceDisposed()
        {
            throw new ObjectDisposedException("Cursor.Source");
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MoveFirst()
        {
            // TODO sync
            if (_source.IsDataBlock(out var db, out var ds))
            {
                if (CurrentBlock.RowCount > 0)
                {
                    BlockIndex = 0;
                    return true;
                }

                return false;
            }
            throw new NotImplementedException();
        }

        public bool MoveLast()
        {
            // TODO sync
            if (_source.IsDataBlock(out var db, out var ds))
            {
                if (CurrentBlock.RowCount > 0)
                {
                    BlockIndex = CurrentBlock.RowCount - 1;
                    return true;
                }

                return false;
            }
            throw new NotImplementedException();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MoveNext()
        {
            // TODO Optimize MN fast path (current block), then go via MV but non-inlined
            // Impl via MV is not a deal breaker or not at all a difference, quick initial tests ~margin of error.
            // Need to spend time on proper MV implementation (that of cause favors MN if).
            return Move(stride: 1, allowPartial: false) != 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MovePrevious()
        {
            return Move(stride: -1, allowPartial: false) != 0;
        }

        public TKey CurrentKey
        {
            // No need to sync this access
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _currentKey;
        }

        // Container cursors must check order before getting CV from current block
        // but after getting the value. It helps that DataBlock cannot shrink in size,
        // only RowLength could, so we will not overrun even if order changed.

        public DataBlock CurrentValue
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => CurrentBlock;
        }

        //[MethodImpl(MethodImplOptions.AggressiveInlining)]
        //public ref T GetValue<T>()
        //{
        //    ref var v = ref _currentBlock.Values._vec.DangerousGetRef<T>(_blockPosition);
        //    EnsureOrder();
        //    return ref v;
        //}

        public int CurrentBlockPosition
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => BlockIndex;
        }

        public Series<TKey, DataBlock, BlockCursor<TKey, TValue, TContainer>> Source
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => new Series<TKey, DataBlock, BlockCursor<TKey, TValue, TContainer>>(Initialize());
        }

        public ValueTask<bool> MoveNextAsync()
        {
            throw new NotSupportedException("This cursor is only used as a building block of other cursors.");
        }

        public IAsyncCompleter AsyncCompleter => throw new NotSupportedException("This cursor is only used as a building block of other cursors.");

        ISeries<TKey, DataBlock> ICursor<TKey, DataBlock>.Source => Source;

        public bool IsContinuous
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public BlockCursor<TKey, TValue, TContainer> Initialize()
        {
            var c = this;
            c.Reset();
            return c;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public BlockCursor<TKey, TValue, TContainer> Clone()
        {
            var c = this;
            c.CurrentBlock?.Increment();
            return c;
        }

        ICursor<TKey, DataBlock> ICursor<TKey, DataBlock>.Clone()
        {
            return Clone();
        }

        public bool TryGetValue(TKey key, out DataBlock value)
        {
            ThrowHelper.DebugAssert(!CurrentBlock.IsDisposed || ReferenceEquals(CurrentBlock, DataBlock.Empty), "!CurrentBlock.IsDisposed || ReferenceEquals(CurrentBlock, DataBlock.Empty)");

            throw new NotImplementedException();
        }

        public void Reset()
        {
            BlockIndex = -1;
            _currentKey = default;
            _orderVersion = AtomicCounter.GetCount(ref _source.OrderVersion);

            if (!_source.IsDataBlock(out _, out _))
            {
                CurrentBlock = DataBlock.Empty;
            }
        }

        public KeyValuePair<TKey, DataBlock> Current
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => new KeyValuePair<TKey, DataBlock>(CurrentKey, CurrentValue);
        }

        object IEnumerator.Current => Current;

        public void Dispose()
        {
            BlockIndex = -1;
            _currentKey = default;
            _orderVersion = AtomicCounter.GetCount(ref _source.OrderVersion);
            CurrentBlock = DataBlock.Empty;
            _source = null;
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return new ValueTask();
        }

        #region Obsolete members

        public bool IsIndexed
        {
            get { throw new NotImplementedException(); }
        }

        public bool IsCompleted
        {
            get => _source.Flags.Mutability == Mutability.ReadOnly;
        }

        #endregion Obsolete members
    }
}
