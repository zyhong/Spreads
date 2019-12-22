﻿using Spreads.Native;
using Spreads.Threading;
using Spreads.Utils;
using System;
using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using static Spreads.Buffers.BuffersThrowHelper;

namespace Spreads.Buffers
{
    internal static class RetainableMemoryTracker
    {
        internal static ConditionalWeakTable<object, string> Tags = new ConditionalWeakTable<object, string>();
    }

    /// <summary>
    /// Base class for retainable memory from a pool of arrays or from native memory.
    /// </summary>
    public abstract unsafe class RetainableMemory<T> : MemoryManager<T>, IRefCounted
    {
        protected RetainableMemory()
        {
#if SPREADS
            if (LeaksDetection.Enabled)
            {
                Tag = Environment.StackTrace;
            }
#endif
        }


        
        // [p*<-len---------------->] we must only check capacity at construction and then work from pointer
        // [p*<-len-[<--lenPow2-->]>] buffer could be larger, pooling always by max pow2 we could store

        protected void* _pointer;
        protected int _length;

        [Obsolete("Must be used only from CounterRef or for custom storage when _isNativeWithHeader == true")]
        internal int _counterOrReserved;

        /// <summary>
        /// 0 - externally owned;
        /// 1 - default array pool (no RM pool);
        /// 2+ - custom pool.
        /// </summary>
        internal byte PoolIndex;

        /// <summary>
        /// A pool sets this value atomically from inside a lock.
        /// </summary>
        internal volatile bool IsPooled;

        /// <summary>
        /// True if the memory is already clean (all zeros) on return. Useful for the case when
        /// the pool has <see cref="RetainableMemoryPool{T}.IsRentAlwaysClean"/> set to true
        /// but we know that the buffer is already clean. Use with caution only when cleanliness
        /// is obvious and when cost of cleaning could be high (larger buffers).
        /// </summary>
        internal bool SkipCleaning;

        /// <summary>
        /// True if there is a header at <see cref="NativeHeaderSize"/> before the <see cref="Pointer"/>.
        /// Special case for DataSpreads SM.
        /// </summary>
        internal bool IsNativeWithHeader;

        // One byte slot is padded anyway, so _isNativeWithHeader takes no space.
        // Storing offset as int will increase object size by 4 bytes.
        // (actually in this class 4 bytes are padded as well to 24, but ArrayMemory
        //  uses that and adding a new field will increase AM size by 8 bytes)
        /// <summary>
        /// DataSpreads shared memory header before <see cref="Pointer"/>.
        /// </summary>
        internal const int NativeHeaderSize = 8;

        internal ref int CounterRef
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                if (IsNativeWithHeader)
                {
                    return ref Unsafe.AsRef<int>((byte*)_pointer - NativeHeaderSize);
                }

#pragma warning disable 618
                return ref _counterOrReserved;
#pragma warning restore 618
            }
        }

        // Whenever a memory becomes a storage of app data and not a temp buffer
        // this must be cleared. Decrement to zero causes pooling before checks
        // and we need to somehow refactor logic without introducing another
        // virtual method and just follow the rule that app data buffers are not
        // poolable in this context. When app finishes working with the buffer
        // it could set this field back to original value.
        internal RetainableMemoryPool<T> Pool
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => PoolIndex >= 2 ? RetainableMemoryPool<T>.KnownPools[PoolIndex] : null;
        }

        /// <summary>
        /// An array was allocated manually. Otherwise even if _pool == null we return the array to default array pool on Dispose.
        /// </summary>
        protected bool ExternallyOwned
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => PoolIndex == 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Increment()
        {
            return AtomicCounter.Increment(ref CounterRef);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal int IncrementIfRetained()
        {
            return AtomicCounter.IncrementIfRetained(ref CounterRef);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Decrement()
        {
            var newRefCount = AtomicCounter.Decrement(ref CounterRef);
            if (newRefCount == 0)
            {
                Dispose(true);
            }
            return newRefCount;
        }

        // TODO check usages, they must use the return value
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal int DecrementIfOne()
        {
            var newRefCount = AtomicCounter.DecrementIfOne(ref CounterRef);
            if (newRefCount == 0)
            {
                Dispose(true);
            }
            return newRefCount;
        }

        internal void* Pointer
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _pointer;
        }

        /// <summary>
        /// Extra space (if any) is at the beginning.
        /// </summary>
        internal void* PointerPow2
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Unsafe.Add<T>(_pointer, _length - LengthPow2);
        }

        /// <summary>
        /// The underlying memory is a pinned array or native memory.
        /// </summary>
        public bool IsPinned
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _pointer != null;
        }

        /// <summary>
        /// <see cref="ReferenceCount"/> is positive, i.e. the memory is retained (borrowed).
        /// </summary>
        public bool IsRetained
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => AtomicCounter.GetIsRetained(ref CounterRef);
        }

        internal bool IsPoolable
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => PoolIndex > 1;
        }

        public int ReferenceCount
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => AtomicCounter.GetCount(ref CounterRef);
        }

        public bool IsDisposed
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => AtomicCounter.GetIsDisposed(ref CounterRef);
        }

        public int Length
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _length;
        }

        /// <summary>
        /// Length for pool buckets. To simplify and speedup implementation we just
        /// use default pow2 pool logic without virtual methods and complexity of
        /// calculating lengths. A buffer is pooled by max pow2 it could fit into.
        /// </summary>
        internal int LengthPow2
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => BitUtil.FindPreviousPositivePowerOfTwo(_length);
        }

        /// <summary>
        /// Returns <see cref="Vec{T}"/> backed by the memory of this instance.
        /// </summary>
        public virtual Vec<T> Vec
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                if (IsPooled)
                {
                    ThrowDisposed<RetainableMemory<T>>();
                }

                var vec = new Vec<T>(_pointer, _length);
#if SPREADS
                ThrowHelper.DebugAssert(vec.AsVec().ItemType == typeof(T));
#endif
                return vec;
            }
        }

        public override Span<T> GetSpan()
        {
            if (IsPooled)
            {
                ThrowDisposed<RetainableMemory<T>>();
            }

            ThrowHelper.DebugAssert(_pointer != null && _length > 0, "Pointer != null && _length > 0");

            // if disposed Pointer & _len are null/0, no way to corrupt data, will just throw
            return new Span<T>(_pointer, _length);
        }

        internal DirectBuffer DirectBuffer
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                if (IsPooled)
                {
                    ThrowDisposed<RetainableMemory<T>>();
                }

                ThrowHelper.DebugAssert(_pointer != null && _length > 0, "Pointer != null && _length > 0");

                return new DirectBuffer(_length * Unsafe.SizeOf<T>(), (byte*)_pointer);
            }
        }

        [Obsolete("Prefer fixed statements on a pinnable reference for short-lived pinning")]
#pragma warning disable CS0809 // Obsolete member overrides non-obsolete member
        public override MemoryHandle Pin(int elementIndex = 0)
#pragma warning restore CS0809 // Obsolete member overrides non-obsolete member
        {
            Increment();
            if (unchecked((uint)elementIndex) >= _length)
            {
                ThrowIndexOutOfRange();
            }

            if (_pointer == null)
            {
                ThrowHelper.ThrowInvalidOperationException("RetainableMemory that is not pinned must have it's own implementation (override) of Pin method.");
            }

            return new MemoryHandle(Unsafe.Add<T>(_pointer, elementIndex), handle: default, this);
        }

        [Obsolete("Unpin should never be called directly, it is called during disposal of MemoryHandle returned by Pin.")]
#pragma warning disable CS0809 // Obsolete member overrides non-obsolete member
        public override void Unpin()
#pragma warning restore CS0809 // Obsolete member overrides non-obsolete member

        {
            Decrement();
        }

        /// <summary>
        /// Retain buffer memory.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public RetainedMemory<T> Retain()
        {
            return Retain(0, _length);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal RetainedMemory<T> RetainPow2()
        {
            var lengthPow2 = LengthPow2;
            return Retain(_length - lengthPow2, lengthPow2);
        }

        /// <summary>
        /// Retain buffer memory without pinning it.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public RetainedMemory<T> Retain(int start, int length, bool borrow = true) // TODO remove borrow param, Retain == borrow
        {
            if ((uint)start + (uint)length > (uint)_length)
            {
                ThrowBadLength();
            }
            return new RetainedMemory<T>(this, start, length, borrow: borrow);
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void DisposeFinalize()
        {
            Dispose(false);
            GC.SuppressFinalize(this);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void ClearAfterDispose()
        {
            // Do not clear counter, it is in disposed state in the pool or it will be GCed.
            ThrowHelper.DebugAssert(AtomicCounter.GetIsDisposed(ref CounterRef));
            ThrowHelper.DebugAssert(!IsPooled);
            _pointer = null;
            _length = default; // not -1, we have uint cast. Also len = 0 should not corrupt existing data
        }

        internal string Tag
        {
            get => RetainableMemoryTracker.Tags.TryGetValue(this, out var tag) ? tag : null;

            set
            {
                RetainableMemoryTracker.Tags.Remove(this);
                RetainableMemoryTracker.Tags.Add(this, value);
            }
        }

        /// <summary>
        /// We need a finalizer because reference count and backing memory could be a native resource.
        /// If object dies without releasing a reference then it is an error.
        /// Current code kills application by throwing in finalizer and this is what we want
        /// for DS - ensure correct memory management.
        /// </summary>
        ~RetainableMemory()
        {
            if (Tag != null)
            {
                // in general we do not know that Dispose(false) will throw/fail, so just print it here
                Console.WriteLine("Finalizing RetainableMemory: " + Tag);
            }

            // always dies in Debug
            if (IsRetained)
            {
                Trace.TraceWarning("Finalizing retained RM");
            }

            // TODO review current logic, we throw when finalizing dropped retained object
            // If it is safe enough to tell that when finalized it always dropped then we
            // could ignore IsRetained when finalizing. Before that failing is better.
            // https://docs.microsoft.com/en-us/dotnet/api/system.object.finalize?redirectedfrom=MSDN&view=netframework-4.7.2#System_Object_Finalize
            // If Finalize or an override of Finalize throws an exception, and
            // the runtime is not hosted by an application that overrides the
            // default policy, the runtime terminates the process and no active
            // try/finally blocks or finalizers are executed. This behavior
            // ensures process integrity if the finalizer cannot free or destroy resources.
            Dispose(false);
        }
    }
}
