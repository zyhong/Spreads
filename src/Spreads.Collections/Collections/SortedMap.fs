﻿// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at http://mozilla.org/MPL/2.0/.


namespace rec Spreads.Collections

// TODO [x] Simplify monster method TryFindWithIndex (done)
// TODO Test TryFindWithIndex
// TODO Finalize new interfaces, strides are needed in containers most of all
// TODO Reimplement (review + fix) Append
// TODO (low) Pooling and cursor counter to avoid disposing of a SM with outstanding cursors
// TODO Remove try..finally in simple methods such as Add/Set and use some circuit braker in enterLock with Env.FailFast. Methods should bever throw.

open System
open System.Linq
open System.Diagnostics
open System.Collections
open System.Collections.Generic
open System.Runtime.InteropServices
open System.Runtime.CompilerServices
open System.Threading
open System.Threading.Tasks
open System.Runtime.CompilerServices
open System.Reflection

open Spreads
open Spreads
open Spreads.Buffers
open Spreads.Serialization
open Spreads.Collections
open Spreads.Utils
open Spreads
open Spreads.Collections.Concurrent


// NB: Why regular keys? Because we do not care about daily or hourly data, but there are 1440 (480) minutes in a day (trading hours)
// with the same diff between each consequitive minute. The number is much bigger with seconds and msecs so that
// memory saving is meaningful, while vectorized calculations on values benefit from fast comparison of regular keys.
// Ticks (and seconds for illiquid instruments) are not regular, but they are never equal among different instruments.

// NB (update 2018) Regular keys are useful for DataStreams (WIP), but real data is almost never regular. 
// Keep it at least for the blood wasted on making it right. 

/// Mutable sorted thread-safe IMutableSeries<'K,'V> implementation similar to SCG.SortedList<'K,'V>
[<AllowNullLiteral>]
[<Sealed>]
[<DebuggerTypeProxy(typeof<IDictionaryDebugView<_,_>>)>]
[<DebuggerDisplay("SortedMap: Count = {Count}")>]
type SortedMap<'K,'V>
  internal(dictionary:Opt<IDictionary<'K,'V>>, capacity:Opt<int>, comparerOpt:Opt<KeyComparer<'K>>) as this =
  inherit ContainerSeries<'K,'V, SortedMapCursor<'K,'V>>()
  static do
    SortedMap<'K,'V>.Init()

  static let smallArrayPool : ObjectPool<'K[]> = ObjectPool(fun _ -> Array.zeroCreate 2)
  static let empty = lazy (let sm = new SortedMap<'K,'V>() in sm.Complete().Wait();sm)

  // data fields
  [<DefaultValueAttribute>]
  val mutable internal size : int

  [<DefaultValueAttribute>]
  val mutable internal keys : 'K array

  [<DefaultValueAttribute>]
  val mutable internal regKeys : 'K array

  [<DefaultValueAttribute>]
  val mutable internal values : 'V array

  [<DefaultValueAttribute>] 
  val mutable internal orderVersion : int64

  [<DefaultValueAttribute>] 
  val mutable internal comparer : KeyComparer<'K>

  do
    if comparerOpt.IsMissing || KeyComparer<'K>.Default.Equals(comparerOpt.Present) then
      this.comparer <- KeyComparer<'K>.Default
    else 
      this.comparer <- comparerOpt.Present // do not try to replace with KeyComparer if a this.comparer was given

  
  
  [<DefaultValueAttribute>] 
  val mutable private rkStep_ : int64

  [<DefaultValueAttribute>] 
  val mutable private rkLast: 'K

  [<DefaultValueAttribute>] 
  val mutable internal isReadOnly : bool

  [<DefaultValueAttribute>] 
  val mutable private mapKey: string

  // TODO buffer management similar to OwnedArray
  [<DefaultValueAttribute>] 
  val mutable internal subscriberCount : int
  [<DefaultValueAttribute>] 
  val mutable internal isReadyToDispose : int

  let mutable couldHaveRegularKeys : bool = this.comparer.IsDiffable

  do
    this._isSynchronized <- true
    let tempCap = 
      if capacity.IsPresent && capacity.Present > 16 then
        BitUtil.FindNextPositivePowerOfTwo(capacity.Present) 
      else 16
    this.keys <-
      if couldHaveRegularKeys then 
        // regular keys are the first and the second value, their diff is the step
        // NB: Buffer pools could return a buffer greater than the requested length,
        // but for regular keys we always need a fixed-length array of size 2, so we allocate a new one.
        // TODO wrap the corefx buffer and for len = 2 use a special self-adjusting ObjectPool, because these 
        // arrays are not short-lived and could accumulate in gen 1+ easily.
        smallArrayPool.Allocate() // Array.zeroCreate 2
      else BufferPool<'K>.Rent(tempCap) 
    this.values <- BufferPool<'V>.Rent(tempCap)

    if dictionary.IsPresent && dictionary.Present.Count > 0 then
      match dictionary.Present with
      // TODO SCM
      | :? SortedMap<'K,'V> as map ->
        if map.IsCompleted then Trace.TraceWarning("TODO: reuse arrays of immutable map")
        let mutable entered = false
        try
          entered <- enterWriteLockIf &map.Locker true
          couldHaveRegularKeys <- map.IsRegular
          this.SetCapacity(map.size)
          this.size <- map.size
          Array.Copy(map.keys, 0, this.keys, 0, map.size)
          Array.Copy(map.values, 0, this.values, 0, map.size)
        finally
          exitWriteLockIf &map.Locker entered
      | _ ->
        // TODO ICollection interface to IMutableSeries
        let locked, sr = match dictionary.Present with | :? ICollection as col -> col.IsSynchronized, col.SyncRoot | _ -> false, null
        let entered = enterLockIf sr locked
        try
          if capacity.IsPresent && capacity.Present < dictionary.Present.Count then 
            raise (ArgumentException("capacity is less then dictionary this.size"))
          else
            this.SetCapacity(dictionary.Present.Count)
        
          let tempKeys = BufferPool<_>.Rent(dictionary.Present.Keys.Count)
          dictionary.Present.Keys.CopyTo(tempKeys, 0)
          dictionary.Present.Values.CopyTo(this.values, 0)
          // NB IDictionary guarantees there is no duplicates
          Array.Sort(tempKeys, this.values, 0, dictionary.Present.Keys.Count, this.comparer)
          this.size <- dictionary.Present.Count

          if couldHaveRegularKeys && this.size > 1 then // if could be regular based on initial check of this.comparer type
            let isReg, step, regularKeys = this.rkCheckArray tempKeys this.size (this.comparer)
            couldHaveRegularKeys <- isReg
            if couldHaveRegularKeys then 
              this.keys <- regularKeys
              BufferPool<_>.Return(tempKeys, true) |> ignore
              this.rkLast <- this.rkKeyAtIndex (this.size - 1)
            else
              this.keys <- tempKeys
          else
            this.keys <- tempKeys
        finally
          exitLockIf sr entered
        

  static member internal Init() =
    let converter = {
      new ArrayBasedMapConverter<'K,'V, SortedMap<'K,'V>>() with
        member __.Read(ptr : IntPtr, [<Out>]value: byref<SortedMap<'K,'V>> ) = 
          let totalSize = Marshal.ReadInt32(ptr)
          let version = Marshal.ReadByte(new IntPtr(ptr.ToInt64() + 4L))
          let mapSize = Marshal.ReadInt32(new IntPtr(ptr.ToInt64() + 8L))
          let mapVersion = Marshal.ReadInt64(new IntPtr(ptr.ToInt64() + 12L))
          let isRegular = Marshal.ReadByte(new IntPtr(ptr.ToInt64() + 12L + 8L)) > 0uy
          let isReadOnly = Marshal.ReadByte(new IntPtr(ptr.ToInt64() + 12L + 8L + 1L)) > 0uy
          value <- 
            if mapSize > 0 then
              let ptr = new IntPtr(ptr.ToInt64() + 8L + 14L)
              let mutable keysArray = Unchecked.defaultof<'K[]>
              let mutable keysCount = 0
              let keysLen = CompressedArrayBinaryConverter<'K>.Instance.Read(ptr, &keysArray, &keysCount, false)
              let keys = 
                if isRegular then
                  let arr = Array.sub keysArray 0 2
                  try
                    BufferPool<_>.Return keysArray |> ignore
                  with | _ -> () // TODO this is temp solution for arrays that are not from pool
                  arr
                else keysArray
              let ptr = new IntPtr(ptr.ToInt64() + (int64 keysLen))
              let mutable valuesArray = Unchecked.defaultof<'V[]>
              let mutable valuesCount = 0
              let valuesLen = CompressedArrayBinaryConverter<'V>.Instance.Read(ptr, &valuesArray, &valuesCount, false)
              Debug.Assert((totalSize = 8 + 14 + keysLen + valuesLen))
              let sm : SortedMap<'K,'V> = SortedMap.OfSortedKeysAndValues(keys, valuesArray, mapSize, KeyComparer<'K>.Default, false, isRegular)
              sm._version <- mapVersion
              sm._nextVersion <- mapVersion
              sm.orderVersion <- mapVersion
              sm.isReadOnly <- isReadOnly
              sm
            else 
              let sm = new SortedMap<'K,'V> ()
              sm._version <- mapVersion
              sm._nextVersion <- mapVersion
              sm.orderVersion <- mapVersion
              sm.isReadOnly <- isReadOnly
              sm
          totalSize
    }
    TypeHelper<SortedMap<'K,'V>>.RegisterConverter(converter, true);
  //#region Private & Internal members

  [<MethodImplAttribute(MethodImplOptions.AggressiveInlining);RewriteAIL>]
  member inline private this.EnterWriteLock() : bool =
    enterWriteLockIf &this.Locker this._isSynchronized

  [<MethodImplAttribute(MethodImplOptions.AggressiveInlining);RewriteAIL>]
  member inline private this.rkGetStep() =
    #if DEBUG
    Trace.Assert(this.size > 1)
    #endif
    if this.rkStep_ > 0L then this.rkStep_
    elif this.size > 1 then
      this.rkStep_ <- this.comparer.Diff(this.keys.[1], this.keys.[0])
      this.rkStep_
    else
      ThrowHelper.ThrowInvalidOperationException("Cannot calculate regular keys step for a single element in a map or an empty map")
      0L
  
  [<MethodImplAttribute(MethodImplOptions.AggressiveInlining);RewriteAIL>]
  member private this.rkKeyAtIndex (idx:int) : 'K =
    let step = this.rkGetStep()
    this.comparer.Add(this.keys.[0], (int64 idx) * step)

  [<MethodImplAttribute(MethodImplOptions.AggressiveInlining);RewriteAIL>]
  member private this.rkIndexOfKey (key:'K) : int =
    #if DEBUG
    Trace.Assert(this.size > 1)
    #endif

    let diff = this.comparer.Diff(key, this.keys.[0])
    let step = this.rkGetStep()
    let idxL : int64 = (diff / step)
    let modulo = (diff - step * idxL)
    let idx = idxL

//    https://msdn.microsoft.com/en-us/library/2cy9f6wb(v=vs.110).aspx
//    The index of the specified value in the specified array, if value is found.
//    If value is not found and value is less than one or more elements in array, 
//    a negative number which is the bitwise complement of the index of the first 
//    element that is larger than value. If value is not found and value is greater 
//    than any of the elements in array, a negative number which is the bitwise 
//    complement of (the index of the last element plus 1).

    // TODO test it for diff <> step, bug-prone stuff here
    if modulo = 0L then
      if idx < 0L then
        ~~~0 // -1 for searches, insert will take ~~~
      elif idx >= int64 this.size then
        ~~~this.size
      else int idx
    else
      if idx <= 0L && diff < 0L then
        ~~~0 // -1 for searches, insert will take ~~~
      elif idx >= int64 this.size then
        ~~~this.size
      else
        ~~~((int idx)+1)

  [<MethodImplAttribute(MethodImplOptions.AggressiveInlining);RewriteAIL>]
  member private this.rkMaterialize () =
    let step = this.rkGetStep()
    let arr = BufferPool<'K>.Rent(this.values.Length)
    for i = 0 to this.values.Length - 1 do
      arr.[i] <- this.comparer.Add(this.keys.[0], (int64 i)*step)
    arr

  [<MethodImplAttribute(MethodImplOptions.AggressiveInlining);RewriteAIL>]
  member private this.rkCheckArray (sortedArray:'K[]) (size:int) (dc:IKeyComparer<'K>) : bool * int * 'K array = 
    if size > sortedArray.Length then raise (ArgumentException("size is greater than sortedArray length"))
    if size < 1 then
      true, 0, [|Unchecked.defaultof<'K>;Unchecked.defaultof<'K>|]
    elif size < 2 then
      true, 0, [|sortedArray.[0];Unchecked.defaultof<'K>|]
    elif size < 3 then 
      true, int <| dc.Diff(sortedArray.[1], sortedArray.[0]), [|sortedArray.[0];sortedArray.[1]|]
    else
      let firstDiff = dc.Diff(sortedArray.[1], sortedArray.[0])
      let mutable isReg = true
      let mutable n = 2
      while isReg && n < size do
        let newDiff = dc.Diff(sortedArray.[n], sortedArray.[n-1])
        if newDiff <> firstDiff then
          isReg <- false
        n <- n + 1
      if isReg then
        true, int firstDiff, [|sortedArray.[0];sortedArray.[1]|]
      else
        false, 0, Unchecked.defaultof<'K[]>

  // need this for the SortedMapCursor
  member inline private this.SetRkLast(rkl) = this.rkLast <- rkl
  
  member private this.Clone() = new SortedMap<'K,'V>(Opt.Present(this :> IDictionary<'K,'V>), Opt<_>.Missing, Opt.Present(this.comparer))

  [<MethodImplAttribute(MethodImplOptions.AggressiveInlining);RewriteAIL>]
  member inline internal this.CheckNull(key) =
    if not TypeHelper<'K>.IsValueType && EqualityComparer<'K>.Default.Equals(key, Unchecked.defaultof<'K>) then 
      ThrowHelper.ThrowArgumentNullException("key")
    
  [<MethodImplAttribute(MethodImplOptions.AggressiveInlining);RewriteAIL>]
  member inline internal this.GetKeyByIndexUnchecked(index) =
    if couldHaveRegularKeys && this.size > 1 then this.rkKeyAtIndex index
    else this.keys.[index]

  [<MethodImplAttribute(MethodImplOptions.AggressiveInlining);RewriteAIL>]
  member inline internal this.GetKeyByIndex(index) =
    if uint32 index >= uint32 this.size then raise (ArgumentOutOfRangeException("index"))
    this.GetKeyByIndexUnchecked(index)
  
  [<MethodImplAttribute(MethodImplOptions.AggressiveInlining);RewriteAIL>]
  member inline internal this.GetPairByIndexUnchecked(index) =
    if couldHaveRegularKeys && this.size > 1 then
      Debug.Assert(uint32 index < uint32 this.size, "Index must be checked before calling GetPairByIndexUnchecked")
      KeyValuePair(this.rkKeyAtIndex index, this.values.[index])
    else KeyValuePair(this.keys.[index], this.values.[index]) 
  
  [<MethodImplAttribute(MethodImplOptions.AggressiveInlining);RewriteAIL>]
  member inline private this.CompareToFirst (k:'K) =
    Debug.Assert(this.size > 0)
    this.comparer.Compare(k, this.keys.[0]) // keys.[0] is always the first key even for regular keys
  
  [<MethodImplAttribute(MethodImplOptions.AggressiveInlining);RewriteAIL>]
  member inline internal this.CompareToLast (k:'K) =
    if couldHaveRegularKeys && this.size > 1 then
      Debug.Assert(not <| Unchecked.equals this.rkLast Unchecked.defaultof<'K>)
      this.comparer.Compare(k, this.rkLast)
    else
      Debug.Assert(this.size > 0)
      this.comparer.Compare(k, this.keys.[this.size-1])

  [<MethodImplAttribute(MethodImplOptions.AggressiveInlining);RewriteAIL>]
  member private this.EnsureCapacity(min) = 
    let mutable num = this.values.Length * 2 
    if num > 2146435071 then num <- 2146435071
    if num < min then num <- min // either double or min if min > 2xprevious
    this.SetCapacity(num)

  [<MethodImplAttribute(MethodImplOptions.AggressiveInlining);RewriteAIL>]
  //[<ReliabilityContractAttribute(Consistency.MayCorruptInstance, Cer.MayFail)>]
  member private this.Insert(index:int, k, v) =
    if this.isReadOnly then ThrowHelper.ThrowInvalidOperationException("SortedMap is read-only")
    // key is always new, checks are before this method
    // already inside a lock statement in a caller method if synchronized
   
    if this.size = this.values.Length then this.EnsureCapacity(this.size + 1)
    Debug.Assert(index <= this.size, "index must be <= this.size")
    Debug.Assert(couldHaveRegularKeys || (this.values.Length = this.keys.Length), "keys and values must have equal length for non-regular case")
    // for values it is alway the same operation
    if index < this.size then Array.Copy(this.values, index, this.values, index + 1, this.size - index);
    this.values.[index] <- v
    // for regular keys must do some math to check if they will remain regular after the insertion
    // treat sizes 1,2(after insertion) as non-regular because they are always both regular and not 
    if couldHaveRegularKeys then
      if this.size > 1 then
        let step = this.rkGetStep()
        if this.comparer.Compare(this.comparer.Add(this.rkLast, step), k) = 0 then
          // adding next regular, only rkLast changes
          this.rkLast <- k
        elif this.comparer.Compare(this.comparer.Add(this.keys.[0], -step), k) = 0 then
          this.keys.[1] <- this.keys.[0]
          this.keys.[0] <- k // change first key and size++ at the bottom
          //rkLast is unchanged
        else
          let diff = this.comparer.Diff(k, this.keys.[0])
          let idxL : int64 = (diff / step)
          let modIsOk = (diff - step * idxL) = 0L // gives 13% boost for add compared to diff % step
          let idx = int idxL 
          if modIsOk && idx > -1 && idx < this.size then
            // error for regular keys, this means we insert existing key
            let msg = "Existing key check must be done before insert. SortedMap code is wrong."
            Environment.FailFast(msg, new ApplicationException(msg))            
          else
            // insertting more than 1 away from end or before start, with a hole
            let toFree = this.keys
            this.keys <- this.rkMaterialize()
            smallArrayPool.Free(toFree)
            couldHaveRegularKeys <- false
      else
        if index < this.size then
          Array.Copy(this.keys, index, this.keys, index + 1, this.size - index);
        else 
          this.rkLast <- k
        this.keys.[index] <- k
        
    // couldHaveRegularKeys could be set to false inside the previous block even if it was true before
    if not couldHaveRegularKeys then
      if index < this.size then
        Array.Copy(this.keys, index, this.keys, index + 1, this.size - index);
      this.keys.[index] <- k
      // do not check if could regularize back, it is very rare 
      // that an irregular becomes a regular one, and such check is always done on
      // bucket switch in SHM (TODO really? check) and before serialization
      // the 99% use case is when we load data from a sequential stream or deserialize a map with already regularized keys
    this.size <- this.size + 1
    this.NotifyUpdate(true)

  member this.Complete() : Task =
    this.BeforeWrite()
    try
      if not this.isReadOnly then 
          this.isReadOnly <- true
          this._isSynchronized <- false
          this.NotifyUpdate(false)
      Task.CompletedTask
    finally
      this.AfterWrite(false)

  // override this.IsCompleted with get() = readLockIf &this._nextVersion &this._version this._isSynchronized (fun _ -> this.isReadOnly)
  override this.IsCompleted with get() = readLockIf &this._nextVersion &this._version this._isSynchronized (fun _ -> this.isReadOnly)
  
  override this.IsIndexed with get() = false

  member this.IsSynchronized with get() = this._isSynchronized
    

  member internal this.MapKey with get() = this.mapKey and set(key:string) = this.mapKey <- key

  member this.IsRegular
    with get() = readLockIf &this._nextVersion &this._version this._isSynchronized (fun _ -> couldHaveRegularKeys) 
    and private set (v) = couldHaveRegularKeys <- v

  member this.RegularStep 
    with get() = 
      readLockIf &this._nextVersion &this._version this._isSynchronized (fun _ -> try this.rkGetStep() with | _ -> 0L)

  member this.Version 
    with get() = readLockIf &this._nextVersion &this._version this._isSynchronized (fun _ -> this._version)
    and internal set v = 
      let mutable entered = false
      try
        entered <- this.EnterWriteLock()
        this._version <- v // NB setter only for deserializer
        this._nextVersion <- v
      finally
        exitWriteLockIf &this.Locker entered

  //#endregion

  //#region Public members

  [<MethodImplAttribute(MethodImplOptions.AggressiveInlining);RewriteAIL>]
  member private this.SetCapacity(value) =
    match value with
    | c when c = this.values.Length -> ()
    | c when c < this.size -> ThrowHelper.ThrowArgumentOutOfRangeException("capacity")
    | c when c > 0 -> 
      if this.isReadOnly then ThrowHelper.ThrowInvalidOperationException("SortedMap is read-only")
      
      // first, take new buffers. this could cause out-of-memory
      let kArr : 'K array = 
        if couldHaveRegularKeys then
          // Trace.Assert(this.keys.Length = 2)
          Unchecked.defaultof<_>
        else
          BufferPool<_>.Rent(c)
      let vArr : 'V array = BufferPool<_>.Rent(c)

      try
        if not couldHaveRegularKeys then
          Array.Copy(this.keys, 0, kArr, 0, this.size)
          let toReturn = this.keys
          this.keys <- kArr
          BufferPool<_>.Return(toReturn, true) |> ignore
        Array.Copy(this.values, 0, vArr, 0, this.size)
        let toReturn = this.values
        this.values <- vArr
        BufferPool<_>.Return(toReturn, true) |> ignore
      with
      // NB see enterWriteLockIf comment and https://github.com/dotnet/corefx/issues/1345#issuecomment-147569967
      // If we were able to get new arrays without OOM but got some out-of-band exception during
      // copying, then we corrupted state and should die
      | _ as ex -> Environment.FailFast(ex.Message, ex)
    | _ -> ()

  member this.Capacity
    with get() = 
      readLockIf &this._nextVersion &this._version this._isSynchronized (fun _ ->
        this.values.Length
      )
    and set(value) =
      let mutable entered = false
      try
        entered <- this.EnterWriteLock()
        this.SetCapacity(value)
      finally
        exitWriteLockIf &this.Locker entered

  override this.Comparer with get() = this.comparer

  member this.Clear() =
    let entered = this.EnterWriteLock()
    if entered then Interlocked.Increment(&this._nextVersion) |> ignore
    try
      if couldHaveRegularKeys then
        // Trace.Assert(this.keys.Length = 2)
        Array.Clear(this.keys, 0, 2)
      else
        Array.Clear(this.keys, 0, this.size)
      Array.Clear(this.values, 0, this.size)
      this.size <- 0
      increment &this.orderVersion
    finally
      Interlocked.Increment(&this._version) |> ignore
      exitWriteLockIf &this.Locker entered

  member this.Count
    with [<MethodImplAttribute(MethodImplOptions.AggressiveInlining);RewriteAIL>] get() = this.size

  override this.Keys 
    with get() =
      {new IList<'K> with
        member x.Count with get() = readLockIf &this._nextVersion &this._version this._isSynchronized (fun _ -> this.size)
        member x.IsReadOnly with get() = true
        member x.Item 
          with get index : 'K = readLockIf &this._nextVersion &this._version this._isSynchronized (fun _ -> this.GetKeyByIndex(index))
          and set index value = raise (NotSupportedException("Keys collection is read-only"))
        member x.Add(k) = raise (NotSupportedException("Keys collection is read-only"))
        member x.Clear() = raise (NotSupportedException("Keys collection is read-only"))
        member x.Contains(key) = this.ContainsKey(key)
        member x.CopyTo(array, arrayIndex) =
          let mutable entered = false
          try
            entered <- this.EnterWriteLock()
            if couldHaveRegularKeys && this.size > 2 then
              Array.Copy(this.rkMaterialize(), 0, array, arrayIndex, this.size)
            else
              Array.Copy(this.keys, 0, array, arrayIndex, this.size)
          finally
            exitWriteLockIf &this.Locker entered

        member x.IndexOf(key:'K) = this.IndexOfKey(key)
        member x.Insert(index, value) = raise (NotSupportedException("Keys collection is read-only"))
        member x.Remove(key:'K) = raise (NotSupportedException("Keys collection is read-only"))
        member x.RemoveAt(index:int) = raise (NotSupportedException("Keys collection is read-only"))
        member x.GetEnumerator() = x.GetEnumerator() :> IEnumerator
        member x.GetEnumerator() : IEnumerator<'K> = 
          let index = ref 0
          let eVersion = ref this._version
          let currentKey : 'K ref = ref Unchecked.defaultof<'K>
          { new IEnumerator<'K> with
            member e.Current with get() = currentKey.Value
            member e.Current with get() = box e.Current
            member e.MoveNext() =
              let nextIndex = index.Value + 1
              readLockIf &this._nextVersion &this._version this._isSynchronized (fun _ ->
                if eVersion.Value <> this._version then
                  raise (InvalidOperationException("Collection changed during enumeration"))
                if index.Value < this.size then
                  currentKey := 
                    if couldHaveRegularKeys && this.size > 1 then this.comparer.Add(this.keys.[0], (int64 !index)*this.rkGetStep()) 
                    else this.keys.[!index]
                  index := nextIndex
                  true
                else
                  index := this.size + 1
                  currentKey := Unchecked.defaultof<'K>
                  false
              )
            member e.Reset() =
              readLockIf &this._nextVersion &this._version this._isSynchronized (fun _ ->
                if eVersion.Value <> this._version then
                  raise (InvalidOperationException("Collection changed during enumeration"))
                index := 0
                currentKey := Unchecked.defaultof<'K>
              )
            member e.Dispose() = 
              index := 0
              currentKey := Unchecked.defaultof<'K>
          }
      } :> IEnumerable<_>

  override this.Values
    with get() =
      { new IList<'V> with
        member x.Count with get() = readLockIf &this._nextVersion &this._version this._isSynchronized (fun _ -> this.size)
        member x.IsReadOnly with get() = true
        member x.Item 
          with get index : 'V = readLockIf &this._nextVersion &this._version this._isSynchronized (fun _ -> this.values.[index])
          and set index value = raise (NotSupportedException("Values collection is read-only"))
        member x.Add(k) = raise (NotSupportedException("Values colelction is read-only"))
        member x.Clear() = raise (NotSupportedException("Values colelction is read-only"))
        member x.Contains(value) = this.ContainsValue(value)
        member x.CopyTo(array, arrayIndex) =
          let mutable entered = false
          try
            entered <- this.EnterWriteLock()
            Array.Copy(this.values, 0, array, arrayIndex, this.size)
          finally
            exitWriteLockIf &this.Locker entered
          
        member x.IndexOf(value:'V) = this.IndexOfValue(value)
        member x.Insert(index, value) = raise (NotSupportedException("Values collection is read-only"))
        member x.Remove(value:'V) = raise (NotSupportedException("Values collection is read-only"))
        member x.RemoveAt(index:int) = raise (NotSupportedException("Values collection is read-only"))
        member x.GetEnumerator() = x.GetEnumerator() :> IEnumerator
        member x.GetEnumerator() : IEnumerator<'V> = 
          let index = ref 0
          let eVersion = ref this._version
          let currentValue : 'V ref = ref Unchecked.defaultof<'V>
          { new IEnumerator<'V> with
            member e.Current with get() = currentValue.Value
            member e.Current with get() = box e.Current
            member e.MoveNext() =
              let nextIndex = index.Value + 1
              readLockIf &this._nextVersion &this._version this._isSynchronized (fun _ ->
                if eVersion.Value <> this._version then
                  raise (InvalidOperationException("Collection changed during enumeration"))
                if index.Value < this.size then
                  currentValue := this.values.[index.Value]
                  index := nextIndex
                  true
                else
                  index := this.size + 1
                  currentValue := Unchecked.defaultof<'V>
                  false
              )
            member e.Reset() =
              readLockIf &this._nextVersion &this._version this._isSynchronized (fun _ ->
                if eVersion.Value <> this._version then
                  raise (InvalidOperationException("Collection changed during enumeration"))
                index := 0
                currentValue := Unchecked.defaultof<'V>
              )
            member e.Dispose() = 
              index := 0
              currentValue := Unchecked.defaultof<'V>
          }
        }  :> IEnumerable<_>

  [<MethodImplAttribute(MethodImplOptions.AggressiveInlining);RewriteAIL>]
  member this.ContainsKey(key) = this.IndexOfKey(key) >= 0

  member this.ContainsValue(value) = this.IndexOfValue(value) >= 0

  [<MethodImplAttribute(MethodImplOptions.AggressiveInlining);RewriteAIL>]
  member inline internal this.IndexOfKeyUnchecked(key:'K) : int =
    if couldHaveRegularKeys && this.size > 1 then this.rkIndexOfKey key
    else this.comparer.BinarySearch(this.keys, 0, this.size, key)

  [<MethodImplAttribute(MethodImplOptions.AggressiveInlining);RewriteAIL>]
  member this.IndexOfKey(key:'K) : int =
    this.CheckNull(key)
    readLockIf &this._nextVersion &this._version this._isSynchronized (fun _ ->
      this.IndexOfKeyUnchecked(key)
    )

  [<MethodImplAttribute(MethodImplOptions.AggressiveInlining);RewriteAIL>]
  member this.IndexOfValue(value:'V) : int =
    readLockIf &this._nextVersion &this._version this._isSynchronized (fun _ ->
      let mutable res = 0
      let mutable found = false
      let valueComparer = Comparer<'V>.Default;
      while not found do
          if valueComparer.Compare(value,this.values.[res]) = 0 then
              found <- true
          else res <- res + 1
      if found then res else -1
    )


  override this.First
    with [<MethodImplAttribute(MethodImplOptions.AggressiveInlining);RewriteAIL>] get() =
      readLockIf &this._nextVersion &this._version this._isSynchronized (fun _ ->
        this.FirstUnchecked
      )

  member private this.FirstUnchecked
    with [<MethodImplAttribute(MethodImplOptions.AggressiveInlining);RewriteAIL>] get() =
      if this.size > 0 then 
        Opt.Present(KeyValuePair(this.keys.[0], this.values.[0]))
      else Opt.Missing
      
  override this.Last
    with [<MethodImplAttribute(MethodImplOptions.AggressiveInlining);RewriteAIL>] get() =
      readLockIf &this._nextVersion &this._version this._isSynchronized (fun _ ->
        this.LastUnchecked
      )

  member internal this.LastUnchecked
    with [<MethodImplAttribute(MethodImplOptions.AggressiveInlining);RewriteAIL>] get() =
      if couldHaveRegularKeys && this.size > 1 then
        Trace.Assert(this.comparer.Compare(this.rkLast, this.comparer.Add(this.keys.[0], (int64 (this.size-1))*this.rkGetStep())) = 0)
        Opt.Present(KeyValuePair(this.rkLast, this.values.[this.size - 1]))
      elif this.size > 0 then Opt.Present(KeyValuePair(this.keys.[this.size - 1], this.values.[this.size - 1]))
      else Opt.Missing

  [<MethodImplAttribute(MethodImplOptions.AggressiveInlining);RewriteAIL>]
  member internal this.TryGetValueUnchecked(key, [<Out>]value: byref<'V>) : bool =
    let index = this.IndexOfKeyUnchecked(key)
    if index >= 0 then
      value <- this.values.[index]
      true
    else false
    
  [<MethodImplAttribute(MethodImplOptions.AggressiveInlining);RewriteAIL>]
  override this.TryGetValue(key, [<Out>]value: byref<'V>) : bool =
    this.CheckNull(key)
    let mutable value' = Unchecked.defaultof<_>
    let inline res() = this.TryGetValueUnchecked(key, &value')
    if readLockIf &this._nextVersion &this._version this._isSynchronized res then
      value <- value'; true
    else false
      
  [<MethodImplAttribute(MethodImplOptions.AggressiveInlining);RewriteAIL>]
  member internal this.SetOrAddUnchecked(k, v, overwrite:bool) : ValueTuple<bool, bool> =
    #if DEBUG
    try
    #endif
      let mutable keepOrderVersion = false
      let mutable added = false
    
      if this.size = 0 then
        this.Insert(0, k, v)
        keepOrderVersion <- true
        added <- true
      else
        if this.CompareToLast(k) > 0 then // adding last value, Insert won't copy arrays if enough capacity
          this.Insert(this.size, k, v)
          keepOrderVersion <- true
          added <- true
        else
          let index = this.IndexOfKeyUnchecked(k)
          if index >= 0 && overwrite then // contains key
            this.values.[index] <- v
            this.NotifyUpdate(true) // Insert has it in other branches
          else
            this.Insert(~~~index, k, v)
            added <- true
      struct(added, keepOrderVersion)
    #if DEBUG
    with | _ -> ThrowHelper.FailFast("SM.SetOrAddUnchecked should never throw"); Unchecked.defaultof<_>
    #endif


  [<MethodImplAttribute(MethodImplOptions.AggressiveInlining);RewriteAIL>]
  member internal this.SetOrAdd(key, value, overwrite: bool) : Task<bool> =
    this.CheckNull(key)
    if this.isReadOnly then ThrowHelper.ThrowInvalidOperationException("SortedMap is read-only")
    
    let entered = this.EnterWriteLock()
    if entered then Interlocked.Increment(&this._nextVersion) |> ignore
    
    let struct(added, keepOrderVersion) = this.SetOrAddUnchecked(key, value, overwrite)
    
    if entered && not overwrite && not added then 
      // did not added a value, decrement next and do not increment version
      Interlocked.Decrement(&this._nextVersion) |> ignore
    else 
      Interlocked.Increment(&this._version) |> ignore
    if not keepOrderVersion then increment(&this.orderVersion)
    exitWriteLockIf &this.Locker entered
    #if DEBUG
    if entered && this._version <> this._nextVersion then raise (ApplicationException("this._version <> this._nextVersion"))
    #endif
    if added then TaskUtil.TrueTask else TaskUtil.FalseTask

  [<MethodImplAttribute(MethodImplOptions.AggressiveInlining);RewriteAIL>]
  member this.Set(key, value) : Task<bool> = this.SetOrAdd(key, value, true)

  [<MethodImplAttribute(MethodImplOptions.AggressiveInlining);RewriteAIL>]
  member this.TryAdd(key, value) : Task<bool> = this.SetOrAdd(key, value, false)

  // NB this is for ctor pattern with IEnumerable
  [<MethodImplAttribute(MethodImplOptions.AggressiveInlining);RewriteAIL>]
  member this.Add(key, value) : unit =
    let added = this.TryAdd(key, value).Result
    if not added then ThrowHelper.ThrowArgumentException("Key already exists");
      
  [<MethodImplAttribute(MethodImplOptions.AggressiveInlining);RewriteAIL>]
  member this.TryAddLast(key, value) : Task<bool> =
    this.CheckNull(key)
    if this.isReadOnly then ThrowHelper.ThrowInvalidOperationException("SortedMap is read-only")
        
    let entered = this.EnterWriteLock()
    if entered then Interlocked.Increment(&this._nextVersion) |> ignore
    
    let mutable added = false
    
    if this.size = 0 then
      this.Insert(0, key, value)
      added <- true
    else
      let c = this.CompareToLast key
      if c > 0 then 
        this.Insert(this.size, key, value)
        added <- true
      
    exitWriteLockIf &this.Locker entered
    if added then Interlocked.Increment(&this._version) |> ignore elif entered then Interlocked.Decrement(&this._nextVersion) |> ignore
    #if DEBUG
    if entered && this._version <> this._nextVersion then raise (ApplicationException("this._version <> this._nextVersion"))
    #endif
    if added then TaskUtil.TrueTask else TaskUtil.FalseTask

//  // TODO lockless AddLast for temporary Append implementation
//  [<MethodImplAttribute(MethodImplOptions.AggressiveInlining);RewriteAIL>]
//  member private this.AddLastUnchecked(key, value) : unit =
//      if this.size = 0 then
//        this.Insert(0, key, value)
//      else
//        let c = this.CompareToLast key
//        if c > 0 then 
//          this.Insert(this.size, key, value)
//        else
//          ThrowHelper.ThrowOutOfOrderKeyException(this.LastUnchecked.Present.Key, "SortedMap.AddLast: New key is smaller or equal to the largest existing key")
//
//
//  member this.TryAppend(appendMap:ISeries<'K,'V>, [<Out>] result: byref<int64>, option:AppendOption) =
//      let hasEqOverlap (old:ISeries<'K,'V>) (append:ISeries<'K,'V>) : bool =
//        if this.comparer.Compare(append.First.Present.Key, old.Last.Present.Key) > 0 then false
//        else
//          let oldC = old.GetCursor()
//          let appC = append.GetCursor();
//          let mutable cont = true
//          let mutable overlapOk = 
//            oldC.MoveAt(append.First.Present.Key, Lookup.EQ) 
//              && appC.MoveFirst() 
//              && this.comparer.Compare(oldC.CurrentKey, appC.CurrentKey) = 0
//              && Unchecked.equals oldC.CurrentValue appC.CurrentValue
//          while overlapOk && cont do
//            if oldC.MoveNext() then
//              overlapOk <-
//                appC.MoveNext() 
//                && this.comparer.Compare(oldC.CurrentKey, appC.CurrentKey) = 0
//                && Unchecked.equals oldC.CurrentValue appC.CurrentValue
//            else cont <- false
//          overlapOk
//      if appendMap.First.IsMissing then
//        0
//      else
//        let mutable entered = false
//        try
//          entered <- enterWriteLockIf &this.Locker this._isSynchronized
//          match option with
//          | AppendOption.RejectOnOverlap ->
//            if this.size = 0 || this.comparer.Compare(appendMap.First.Present.Key, this.LastUnchecked.Present.Key) > 0 then
//              let mutable c = 0
//              for i in appendMap do
//                c <- c + 1
//                this.AddLastUnchecked(i.Key, i.Value) // TODO Add last when fixed flushing
//              c
//            else invalidOp "values overlap with existing"
//          | AppendOption.DropOldOverlap ->
//            if this.size = 0 || this.comparer.Compare(appendMap.First.Present.Key, this.LastUnchecked.Present.Key) > 0 then
//              let mutable c = 0
//              for i in appendMap do
//                c <- c + 1
//                this.AddLastUnchecked(i.Key, i.Value) // TODO Add last when fixed flushing
//              c
//            else
//              let removed = this.RemoveMany(appendMap.First.Key, Lookup.GE)
//              Trace.Assert(removed)
//              let mutable c = 0
//              for i in appendMap do
//                c <- c + 1
//                this.AddLastUnchecked(i.Key, i.Value) // TODO Add last when fixed flushing
//              c
//          | AppendOption.IgnoreEqualOverlap ->
//            if this.IsEmpty || this.comparer.Compare(appendMap.First.Key, this.LastUnchecked.Key) > 0 then
//              let mutable c = 0
//              for i in appendMap do
//                c <- c + 1
//                this.AddLastUnchecked(i.Key, i.Value) // TODO Add last when fixed flushing
//              c
//            else
//              let isEqOverlap = hasEqOverlap this appendMap
//              if isEqOverlap then
//                let appC = appendMap.GetCursor();
//                if appC.MoveAt(this.LastUnchecked.Key, Lookup.GT) then
//                  this.AddLastUnchecked(appC.CurrentKey, appC.CurrentValue) // TODO Add last when fixed flushing
//                  let mutable c = 1
//                  while appC.MoveNext() do
//                    this.AddLastUnchecked(appC.CurrentKey, appC.CurrentValue) // TODO Add last when fixed flushing
//                    c <- c + 1
//                  c
//                else 0
//              else invalidOp "overlapping values are not equal" // TODO unit test
//          | AppendOption.RequireEqualOverlap ->
//            if this.IsEmpty then
//              let mutable c = 0
//              for i in appendMap do
//                c <- c + 1
//                this.AddLastUnchecked(i.Key, i.Value) // TODO Add last when fixed flushing
//              c
//            elif this.comparer.Compare(appendMap.First.Key, this.LastUnchecked.Key) > 0 then
//              invalidOp "values do not overlap with existing"
//            else
//              let isEqOverlap = hasEqOverlap this appendMap
//              if isEqOverlap then
//                let appC = appendMap.GetCursor();
//                if appC.MoveAt(this.LastUnchecked.Key, Lookup.GT) then
//                  this.AddLastUnchecked(appC.CurrentKey, appC.CurrentValue) // TODO Add last when fixed flushing
//                  let mutable c = 1
//                  while appC.MoveNext() do
//                    this.AddLastUnchecked(appC.CurrentKey, appC.CurrentValue) // TODO Add last when fixed flushing
//                    c <- c + 1
//                  c
//                else 0
//              else invalidOp "overlapping values are not equal" // TODO unit test
//          | _ -> failwith "Unknown AppendOption"
//        finally
//          exitWriteLockIf &this.Locker entered

  [<MethodImplAttribute(MethodImplOptions.AggressiveInlining);RewriteAIL>]
  member this.TryAddFirst(key, value) : Task<bool> =
    this.CheckNull(key)
    if this.isReadOnly then ThrowHelper.ThrowInvalidOperationException("SortedMap is read-only")
      
    let entered = this.EnterWriteLock()
    if entered then Interlocked.Increment(&this._nextVersion) |> ignore
    
    let mutable added = false
    let mutable keepOrderVersion = false
    
    try
      if this.size = 0 then
        this.Insert(0, key, value)
        keepOrderVersion <- true
        added <- true
      else
        let c = this.CompareToFirst key
        if c < 0 then
            this.Insert(0, key, value)
            added <- true
      if added then TaskUtil.TrueTask else TaskUtil.FalseTask   
    finally
      if added then Interlocked.Increment(&this._version) |> ignore elif entered then Interlocked.Decrement(&this._nextVersion) |> ignore
      if not keepOrderVersion then increment(&this.orderVersion)
      exitWriteLockIf &this.Locker entered
      #if DEBUG
      if entered && this._version <> this._nextVersion then raise (ApplicationException("this._version <> this._nextVersion"))
      #else
      if entered && this._version <> this._nextVersion then Environment.FailFast("this._version <> this._nextVersion")
      #endif
    
  [<MethodImplAttribute(MethodImplOptions.AggressiveInlining);RewriteAIL>]
  member private this.RemoveAt(index)  =
    let result = this.GetPairByIndexUnchecked(index)
    if uint32 index >= uint32 this.size then ThrowHelper.ThrowArgumentOutOfRangeException("index")
    let newSize = this.size - 1
    // TODO review, check for off by 1 bugs, could had lost focus at 3 AM
    // keys
    if couldHaveRegularKeys && this.size > 2 then // will have >= 2 after removal
      if index = 0 then
        this.keys.[0] <- (this.comparer.Add(this.keys.[0], this.rkGetStep())) // change first key to next and size--
        this.keys.[1] <- (this.comparer.Add(this.keys.[0], this.rkGetStep())) // add step to the new first value
      elif index = newSize then 
        this.rkLast <- this.comparer.Add(this.keys.[0], (int64 (newSize-1))*this.rkGetStep()) // removing last, only size--
      else
        // removing within range,  creating a hole
        this.keys <- this.rkMaterialize()
        couldHaveRegularKeys <- false
    elif couldHaveRegularKeys && this.size = 2 then // will have single value with undefined step
      if index = 0 then
        this.keys.[0] <- this.keys.[1]
        this.keys.[1] <- Unchecked.defaultof<'K>
      elif index = 1 then
        this.rkLast <- this.keys.[0]
      this.rkStep_ <- 0L

    if not couldHaveRegularKeys || this.size = 1 then
      if index < this.size then
        Array.Copy(this.keys, index + 1, this.keys, index, newSize - index) // this.size
      this.keys.[newSize] <- Unchecked.defaultof<'K>
      
    // values
    if index < newSize then
      Array.Copy(this.values, index + 1, this.values, index, newSize - index) //this.size
      
    this.values.[newSize] <- Unchecked.defaultof<'V>
    this.size <- newSize
    this.NotifyUpdate(true)
    result
    
  [<MethodImplAttribute(MethodImplOptions.AggressiveInlining);RewriteAIL>]
  member this.TryRemove(key) : ValueTask<Opt<'V>>  =
    this.CheckNull(key)
    if this.isReadOnly then ThrowHelper.ThrowInvalidOperationException("SortedMap is read-only")
    
    let mutable removed = false
    let entered = this.EnterWriteLock()
    if entered then Interlocked.Increment(&this._nextVersion) |> ignore
    
    try
      let index = this.IndexOfKeyUnchecked(key)
      if index >= 0 then 
        let result = this.RemoveAt(index).Value
        increment &this.orderVersion
        removed <- true
        ValueTask<_>(Opt.Present(result))
      else ValueTask<_>(Opt<_>.Missing)
    finally
      if removed then Interlocked.Increment(&this._version) |> ignore elif entered then Interlocked.Decrement(&this._nextVersion) |> ignore
      exitWriteLockIf &this.Locker entered
      #if DEBUG
      if entered && this._version <> this._nextVersion then raise (ApplicationException("this._version <> this._nextVersion"))
      #else
      if entered && this._version <> this._nextVersion then Environment.FailFast("this._version <> this._nextVersion")
      #endif


  member this.TryRemoveFirst() : ValueTask<Opt<KVP<'K,'V>>>  =
    if this.isReadOnly then ThrowHelper.ThrowInvalidOperationException("SortedMap is read-only")
    let mutable removed = false
    let entered = this.EnterWriteLock()
    if entered then Interlocked.Increment(&this._nextVersion) |> ignore
    try
      if this.size > 0 then
        let result = this.RemoveAt(0)
        increment &this.orderVersion
        removed <- true
        ValueTask<_>(Opt.Present(result))
      else ValueTask<_>(Opt<_>.Missing)
    finally
      if removed then Interlocked.Increment(&this._version) |> ignore elif entered then Interlocked.Decrement(&this._nextVersion) |> ignore
      exitWriteLockIf &this.Locker entered
      #if DEBUG
      if entered && this._version <> this._nextVersion then raise (ApplicationException("this._version <> this._nextVersion"))
      #else
      if entered && this._version <> this._nextVersion then Environment.FailFast("this._version <> this._nextVersion")
      #endif


  member this.TryRemoveLast() : ValueTask<Opt<KVP<'K,'V>>> =
    if this.isReadOnly then ThrowHelper.ThrowInvalidOperationException("SortedMap is read-only")
    let mutable removed = false
    let entered = this.EnterWriteLock()
    if entered then Interlocked.Increment(&this._nextVersion) |> ignore
    try
      if this.size > 0 then
        let result = this.RemoveAt(this.size - 1)
        increment &this.orderVersion
        removed <- true
        ValueTask<_>(Opt.Present(result))
      else ValueTask<_>(Opt<_>.Missing)
    finally
      if removed then Interlocked.Increment(&this._version) |> ignore elif entered then Interlocked.Decrement(&this._nextVersion) |> ignore
      exitWriteLockIf &this.Locker entered
      #if DEBUG
      if entered && this._version <> this._nextVersion then raise (ApplicationException("this._version <> this._nextVersion"))
      #else
      if entered && this._version <> this._nextVersion then Environment.FailFast("this._version <> this._nextVersion")
      #endif

  /// Removes all elements that are to `direction` from `key`
  member this.TryRemoveMany(key:'K, direction:Lookup) : ValueTask<Opt<KVP<'K,'V>>> =
    this.CheckNull(key)
    if this.isReadOnly then ThrowHelper.ThrowInvalidOperationException("SortedMap is read-only")

    let mutable removed = false
    let entered = this.EnterWriteLock()
    try
      if entered then Interlocked.Increment(&this._nextVersion) |> ignore
      if this.size = 0 then ValueTask<_>(Opt<_>.Missing)
      else
        let mutable result = Unchecked.defaultof<_>
        let pivotIndex = this.FindIndexAt(key, direction)
        let mutable result =
          if pivotIndex >= 0 && pivotIndex < this.size then
            this.GetPairByIndexUnchecked(pivotIndex)
          else Unchecked.defaultof<_>
        // pivot should be removed, after calling TFWI pivot is always inclusive
        match direction with
        | Lookup.EQ -> 
          let index = this.IndexOfKeyUnchecked(key)
          if index >= 0 then 
            result <- this.RemoveAt(index)
            increment &this.orderVersion
            removed <- true
            ValueTask<_>(Opt.Present(result))
          else ValueTask<_>(Opt<_>.Missing)
        | Lookup.LT | Lookup.LE ->
          if pivotIndex = -1 then // pivot is not here but to the left, keep all elements
            ValueTask<_>(Opt<_>.Missing)
          elif pivotIndex >=0 then // remove elements below pivot and pivot
            this.size <- this.size - (pivotIndex + 1)
            
            if couldHaveRegularKeys then
              this.keys.[0] <- (this.comparer.Add(this.keys.[0], int64 (pivotIndex+1)))
              if this.size > 1 then 
                this.keys.[1] <- (this.comparer.Add(this.keys.[0], this.rkGetStep())) 
              else
                this.keys.[1] <- Unchecked.defaultof<'K>
                this.rkStep_ <- 0L
            else
              Array.Copy(this.keys, pivotIndex + 1, this.keys, 0, this.size) // move this.values to 
              Array.fill this.keys this.size (this.values.Length - this.size) Unchecked.defaultof<'K>

            Array.Copy(this.values, pivotIndex + 1, this.values, 0, this.size)
            Array.fill this.values this.size (this.values.Length - this.size) Unchecked.defaultof<'V>
            
            increment &this.orderVersion
            removed <- true
            ValueTask<_>(Opt.Present(result))
          else
            ThrowHelper.ThrowInvalidOperationException("wrong result of TryFindWithIndex with LT/LE direction");Unchecked.defaultof<_>
        | Lookup.GT | Lookup.GE ->
          if pivotIndex = -2 then // pivot is not here but to the right, keep all elements
            ValueTask<_>(Opt<_>.Missing)
          elif pivotIndex >= 0 then // remove elements above and including pivot
            this.size <- pivotIndex
            if couldHaveRegularKeys then
              if this.size > 1 then
                this.rkLast <- this.comparer.Add(this.keys.[0], (int64 (this.size-1))*this.rkGetStep()) // -1 is correct, the size is updated on the previous line
              else
                this.keys.[1] <- Unchecked.defaultof<'K>
                this.rkStep_ <- 0L
                if this.size = 1 then this.rkLast <- this.keys.[0] 
                else this.rkLast <- Unchecked.defaultof<_>
            if not couldHaveRegularKeys then
              Array.fill this.keys pivotIndex (this.values.Length - pivotIndex) Unchecked.defaultof<'K>
            Array.fill this.values pivotIndex (this.values.Length - pivotIndex) Unchecked.defaultof<'V>
            this.SetCapacity(this.size)
            increment &this.orderVersion
            removed <- true
            ValueTask<_>(Opt.Present(result))
          else
            ThrowHelper.ThrowInvalidOperationException("Wrong result of TryFindWithIndex with GT/GE direction"); Unchecked.defaultof<_>
        | _ -> ThrowHelper.ThrowInvalidOperationException("Invalid direction"); Unchecked.defaultof<_> //
    finally
      this.NotifyUpdate(true)
      if removed then Interlocked.Increment(&this._version) |> ignore elif entered then Interlocked.Decrement(&this._nextVersion) |> ignore
      exitWriteLockIf &this.Locker entered
      #if DEBUG
      if entered && this._version <> this._nextVersion then raise (ApplicationException("this._version <> this._nextVersion"))
      #else
      if entered && this._version <> this._nextVersion then Environment.FailFast("this._version <> this._nextVersion")
      #endif
    
  /// Returns the index of found KeyValuePair or a negative value:
  /// -1 if the non-found key is smaller than the first key
  /// -2 if the non-found key is larger than the last key
  /// -3 if the non-found key is within the key range (for EQ direction only)
  /// -4 empty
  /// Example: (-1) [...current...(-3)...map ...] (-2)
  [<MethodImplAttribute(MethodImplOptions.AggressiveInlining);RewriteAIL>]
  member internal this.FindIndexAt(key:'K, direction:Lookup) : int32 =
    if this.size = 0 then -4
    // the only optimization: return last item if requesting LE with key above the last one
    elif direction = Lookup.LE && this.CompareToLast(key) >= 0 then 
      this.size - 1
    else
      let mutable idx = this.IndexOfKeyUnchecked(key);
      // adjust idx
      if idx >= 0 then
        if direction = Lookup.LT then 
          idx <- idx - 1
        elif direction = Lookup.GT then
          idx <- idx + 1
        else () // for LE/EQ/GE we are fine with existing key
      else
        idx <- ~~~idx
        if direction = Lookup.EQ then
          idx <- -3
        elif direction = Lookup.LE || direction = Lookup.LT then
          idx <- idx - 1
        else () // for GE/GT ~~~ is already right
      if idx >= this.size then idx <- -2
      idx


  [<MethodImplAttribute(MethodImplOptions.AggressiveInlining);RewriteAIL>]
  member this.TryFindAtUnchecked(key:'K, direction:Lookup, [<Out>]value: byref<KeyValuePair<'K, 'V>>) : bool =
    let idx = this.FindIndexAt(key, direction)
    if idx >= 0 && idx < this.size then
      value <- this.GetPairByIndexUnchecked(idx)
      true
    else false

  [<MethodImplAttribute(MethodImplOptions.AggressiveInlining);RewriteAIL>]
  override this.TryFindAt(key:'K, direction:Lookup, [<Out>]value: byref<KeyValuePair<'K, 'V>>) : bool =
    // NB *very* hot path, manual inlining of read lock logic
    let mutable result = Unchecked.defaultof<_>
    let mutable doSpin = true
    let sw = new SpinWait()
    while doSpin do
      doSpin <- this._isSynchronized
      let version = if doSpin then Volatile.Read(&this._version) else 0L
      result <- 
        let idx = this.FindIndexAt(key, direction)
        if idx >= 0 && idx < this.size then
          value <- this.GetPairByIndexUnchecked(idx)
          true
        else false
      if doSpin then
        let nextVersion = Volatile.Read(&this._nextVersion)
        if version = nextVersion then doSpin <- false
        else sw.SpinOnce()
      else doSpin <- false
    result

  override this.GetCursor() =
    let entered = this.EnterWriteLock()
    try
      // if source is already read-only, MNA will always return false
      if this.isReadOnly then new SortedMapCursor<'K,'V>(this) :> ICursor<'K,'V>
      else 
        let c = new BaseCursorAsync<'K,'V,_>(Func<_>(this.GetEnumerator))
        //let c = new SortedMapCursor<'K,'V>(this)
        c :> ICursor<'K,'V>
    finally
      exitWriteLockIf &this.Locker entered

  override this.GetContainerCursor() = this.GetEnumerator()

  // .NETs foreach pattern optimization must return struct
  [<MethodImplAttribute(MethodImplOptions.AggressiveInlining);RewriteAIL>]
  member this.GetEnumerator() : SortedMapCursor<_,_> = new SortedMapCursor<'K,'V>(this)

  // .NETs foreach pattern optimization must return struct
  [<MethodImplAttribute(MethodImplOptions.AggressiveInlining);RewriteAIL>]
  member this.GetAsyncEnumerator() : SortedMapCursor<_,_> = new SortedMapCursor<'K,'V>(this)
  
  [<MethodImplAttribute(MethodImplOptions.AggressiveInlining);RewriteAIL>]
  member internal this.GetSMCursor() = new SortedMapCursor<'K,'V>(this)

  [<MethodImplAttribute(MethodImplOptions.AggressiveInlining);RewriteAIL>]
  override this.TryGetAt(idx:int64, [<Out>]value: byref<KeyValuePair<'K, 'V>>) : bool =
    let mutable kvp: KeyValuePair<'K, 'V> = Unchecked.defaultof<_>
    let result = readLockIf &this._nextVersion &this._version this._isSynchronized (fun _ ->
      if idx >= 0L && idx < int64(this.size) then 
       kvp <- KVP( this.GetKeyByIndexUnchecked(int32(idx)), this.values.[int32(idx)])
       true 
      else false
    )
    value <- kvp
    result

  /// Make the capacity equal to the size
  member this.TrimExcess() = this.Capacity <- this.size

  member private this.Dispose(disposing:bool) =
    if BufferPoolRetainedMemoryHelper<'V>.IsRetainedMemory then BufferPoolRetainedMemoryHelper<'V>.DisposeRetainedMemory(this.values, 0, this.size)

    if not couldHaveRegularKeys then BufferPool<_>.Return(this.keys, true) |> ignore
    BufferPool<_>.Return(this.values, true) |> ignore
    if disposing then GC.SuppressFinalize(this)
  
  member this.Dispose() = this.Dispose(true)
  override this.Finalize() = this.Dispose(false)

  //#endregion


  //#region Interfaces

  interface IArrayBasedMap<'K,'V> with
    member this.Length with get() = this.size
    member this.Version with get() = this._version
    member this.IsRegular with get() = this.IsRegular
    member this.IsCompleted with get() = this.IsCompleted
    member this.Keys with get() = this.keys
    member this.Values with get() = this.values

  interface IDisposable with
    member this.Dispose() = this.Dispose(true)

  interface IEnumerable with
    member this.GetEnumerator() = this.GetEnumerator() :> IEnumerator

  interface IEnumerable<KeyValuePair<'K,'V>> with
    member this.GetEnumerator() : IEnumerator<KeyValuePair<'K,'V>> = 
      this.GetEnumerator() :> IEnumerator<KeyValuePair<'K,'V>>

  interface ICollection  with
    member this.SyncRoot = this.SyncRoot
    member this.CopyTo(array, arrayIndex) =
      let entered = this.EnterWriteLock()
      try
        if array = null then raise (ArgumentNullException("array"))
        if arrayIndex < 0 || arrayIndex > array.Length then raise (ArgumentOutOfRangeException("arrayIndex"))
        if array.Length - arrayIndex < this.Count then raise (ArgumentException("ArrayPlusOffTooSmall"))
        for index in 0..this.size do
          let kvp = KeyValuePair(this.GetKeyByIndexUnchecked(index), this.values.[index])
          array.SetValue(kvp, arrayIndex + index)
      finally
        exitWriteLockIf &this.Locker entered
    member this.Count = this.Count
    member this.IsSynchronized with get() =  this._isSynchronized

  interface IDictionary<'K,'V> with
    member this.Count = this.Count
    member this.IsReadOnly with get() = this.IsCompleted
    member this.Item 
      with get key = this.Item(key)
      and set key value = this.Set(key, value) |> ignore
    member this.Keys with get() = this.Keys :?> ICollection<'K>
    member this.Values with get() = this.Values :?> ICollection<'V>
    member this.Clear() = this.Clear()
    member this.ContainsKey(key) = this.ContainsKey(key)
    member this.Contains(kvp:KeyValuePair<'K,'V>) = this.ContainsKey(kvp.Key)
    member this.CopyTo(array, arrayIndex) =
      let entered = this.EnterWriteLock()
      try
        if array = null then raise (ArgumentNullException("array"))
        if arrayIndex < 0 || arrayIndex > array.Length then raise (ArgumentOutOfRangeException("arrayIndex"))
        if array.Length - arrayIndex < this.Count then raise (ArgumentException("ArrayPlusOffTooSmall"))
        for index in 0..this.Count do
          let kvp = KeyValuePair(this.keys.[index], this.values.[index])
          array.[arrayIndex + index] <- kvp
      finally
        exitWriteLockIf &this.Locker entered
    member this.Add(key, value) = 
      if not (this.TryAdd(key, value).Result) then ThrowHelper.ThrowInvalidOperationException("Key already exists") 
    member this.Add(kvp:KeyValuePair<'K,'V>) = 
      if not (this.TryAdd(kvp.Key, kvp.Value).Result) then ThrowHelper.ThrowInvalidOperationException("Key already exists") 
    member this.Remove(key) = let opt = this.TryRemove(key).Result in opt.IsPresent
    member this.Remove(kvp:KeyValuePair<'K,'V>) = let opt = this.TryRemove(kvp.Key).Result in opt.IsPresent
    member this.TryGetValue(key, [<Out>]value: byref<'V>) : bool = this.TryGetValue(key, &value)
    
  interface IMutableSeries<'K,'V> with
    member this.Complete() = this.Complete()
    member this.Version with get() = this.Version
    member this.Count with get() = int64(this.size)
    member this.IsAppendOnly with get() = false
    member this.Set(k, v) = this.Set(k,v)
    member this.TryAdd(k, v) = this.TryAdd(k,v)
    member this.TryAddLast(k, v) = this.TryAddLast(k, v)
    member this.TryAddFirst(k, v) = this.TryAddFirst(k, v)
    member this.TryRemove(k) = this.TryRemove(k)
    member this.TryRemoveFirst() = this.TryRemoveFirst()
    member this.TryRemoveLast() = this.TryRemoveLast()
    member this.TryRemoveMany(key:'K, direction:Lookup) = this.TryRemoveMany(key, direction)
    member this.TryRemoveMany(key, keyChunk, direction) =
      match direction with
      | Lookup.EQ -> 
        this.Set(key, keyChunk) |> ignore
        TaskUtil.TrueTask
      | Lookup.LT | Lookup.LE -> 
        let r = this.TryRemoveMany(key, Lookup.LT).Result
        this.Set(key, keyChunk) |> ignore
        if r.IsPresent then TaskUtil.TrueTask else TaskUtil.FalseTask
      | Lookup.GT | Lookup.GE -> 
        let r = this.TryRemoveMany(key, Lookup.GT).Result
        this.Set(key, keyChunk) |> ignore
        if r.IsPresent then TaskUtil.TrueTask else TaskUtil.FalseTask
      | _ -> Unchecked.defaultof<_>

    // TODO move to type memeber, check if ISeries is SM and copy arrays in one go
    // TODO atomic append with single version increase, now it is a sequence of remove/add mutations
    member this.TryAppend(appendMap:ISeries<'K,'V>, option:AppendOption) =
      raise (NotImplementedException())

  //#endregion

  //#region Constructors


  new() = new SortedMap<_,_>(Opt<_>.Missing, Opt<_>.Missing, Opt<_>.Missing)
  new(dictionary:IDictionary<'K,'V>) = new SortedMap<_,_>(Opt.Present(dictionary), Opt.Present(dictionary.Count), Opt<_>.Missing)
  new(minimumCapacity:int) = new SortedMap<_,_>(Opt<_>.Missing, Opt.Present(minimumCapacity), Opt<_>.Missing)

  // do not expose ctors with this.comparer to public
  internal new(comparer:IComparer<'K>) = new SortedMap<_,_>(Opt<_>.Missing, Opt<_>.Missing, Opt.Present(KeyComparer<'K>.Create(comparer)))
  internal new(dictionary:IDictionary<'K,'V>,comparer:IComparer<'K>) = new SortedMap<_,_>(Opt.Present(dictionary), Opt.Present(dictionary.Count), Opt.Present(KeyComparer<'K>.Create(comparer)))
  internal new(minimumCapacity:int,comparer:IComparer<'K>) = new SortedMap<_,_>(Opt<_>.Missing, Opt.Present(minimumCapacity), Opt.Present(KeyComparer<'K>.Create(comparer)))

  internal new(comparer:KeyComparer<'K>) = new SortedMap<_,_>(Opt<_>.Missing, Opt<_>.Missing, Opt.Present(comparer))
  internal new(dictionary:IDictionary<'K,'V>,comparer:KeyComparer<'K>) = new SortedMap<_,_>(Opt.Present(dictionary), Opt.Present(dictionary.Count), Opt.Present(comparer))
  internal new(minimumCapacity:int,comparer:KeyComparer<'K>) = new SortedMap<_,_>(Opt<_>.Missing, Opt.Present(minimumCapacity), Opt.Present(comparer))

  static member internal OfSortedKeysAndValues(keys:'K[], values:'V[], size:int, comparer:IComparer<'K>, doCheckSorted:bool, isAlreadyRegular) =
    if keys.Length < size && not isAlreadyRegular then raise (new ArgumentException("Keys array is smaller than provided size"))
    if values.Length < size then raise (new ArgumentException("Values array is smaller than provided size"))
    let sm = new SortedMap<'K,'V>(comparer)
    if doCheckSorted then
      for i in 1..size-1 do
        if comparer.Compare(keys.[i-1], keys.[i]) >= 0 then raise (new ArgumentException("Keys are not sorted"))

    // at this point IsRegular means could be regular
    if sm.IsRegular && not isAlreadyRegular then
      let isReg, step, firstArr = sm.rkCheckArray keys size sm.Comparer
      if isReg then
        sm.keys <- firstArr
      else 
        sm.IsRegular <- false
        sm.keys <- keys
    elif sm.IsRegular && isAlreadyRegular then
      Trace.Assert(keys.Length >= 2)
      sm.keys <- keys
    elif not sm.IsRegular && not isAlreadyRegular then
      sm.IsRegular <- false
      sm.keys <- keys
    else raise (InvalidOperationException("Keys are marked as already regular, but this.comparer doesn't cupport regular keys"))
    
    sm.size <- size
    sm.values <- values
    if sm.size > 0 then sm.SetRkLast(sm.GetKeyByIndexUnchecked(sm.size - 1))
    sm

  /// Create a SortedMap using the first `size` elements of the provided keys and values.
  static member OfSortedKeysAndValues(keys:'K[], values:'V[], size:int, comparer:IComparer<'K>) =
    if comparer = Unchecked.defaultof<_> then raise (ArgumentNullException("this.comparer"))
    else SortedMap.OfSortedKeysAndValues(keys, values, size, comparer, true, false)

  /// Create a SortedMap using the first `size` elements of the provided keys and values, with default this.comparer.
  static member OfSortedKeysAndValues(keys:'K[], values:'V[], size:int) =
    let comparer =
      KeyComparer<'K>.Default
    SortedMap.OfSortedKeysAndValues(keys, values, size, comparer, true, false)

  static member OfSortedKeysAndValues(keys:'K[], values:'V[]) =
    if keys.Length <> values.Length then raise (new ArgumentException("Keys and values arrays are of different sizes"))
    SortedMap.OfSortedKeysAndValues(keys, values, values.Length)

  //#endregion


  /// Checks if keys of two maps are equal
  static member internal KeysAreEqual(smA:SortedMap<'K,_>,smB:SortedMap<'K,_>) : bool =
    if not (smA.size = smB.size) then false
    elif smA.IsRegular && smB.IsRegular 
      && smA.RegularStep = smB.RegularStep
      && smA.Comparer.Equals(smB.Comparer) // TODO test custom this.comparer equality, custom comparers must implement equality
      && smA.Comparer.Compare(smA.keys.[0], smB.keys.[0]) = 0 then // if steps are equal we could skip checking second elements in keys
      true
    else
      // this is very slow to be used in any "optimization", should use BytesExtensions.UnsafeCompare
      System.Linq.Enumerable.SequenceEqual(smA.keys, smB.keys)

  static member Empty = empty.Value


type public SortedMapCursor<'K,'V> =
    struct
      val internal source : SortedMap<'K,'V>
      val mutable internal index : int
      val mutable internal current : KVP<'K,'V>
      //val mutable internal currentKey : 'K
      //val mutable internal currentValue: 'V
      val mutable internal cursorVersion : int64
      val mutable internal isBatch : bool
      val mutable internal doSpin : bool
      [<MethodImplAttribute(MethodImplOptions.AggressiveInlining)>]
      new(source:SortedMap<'K,'V>) = 
        { source = source;
          index = -1;
          current = Unchecked.defaultof<_>;
          //currentKey = Unchecked.defaultof<_>;
          //currentValue = Unchecked.defaultof<_>;
          cursorVersion = source.orderVersion;
          isBatch = false;
          doSpin = source._isSynchronized
        }
    end

    member this.CurrentKey 
      with [<MethodImplAttribute(MethodImplOptions.AggressiveInlining);RewriteAIL>] get() = this.current.Key

    member this.CurrentValue 
      with [<MethodImplAttribute(MethodImplOptions.AggressiveInlining);RewriteAIL>] get() = this.current.Value

    member this.Current 
      with [<MethodImplAttribute(MethodImplOptions.AggressiveInlining);RewriteAIL>] get() : KVP<'K,'V> = this.current // KeyValuePair(this.currentKey, this.currentValue)

    member this.Source 
      with [<MethodImplAttribute(MethodImplOptions.AggressiveInlining);RewriteAIL>] get(): ISeries<'K,'V> = this.source :> ISeries<'K,'V>    
    
    member this.IsContinuous with get() = false

    member this.Comparer
      with [<MethodImplAttribute(MethodImplOptions.AggressiveInlining);RewriteAIL>] get() : KeyComparer<'K> = this.source.comparer
    
    [<MethodImplAttribute(MethodImplOptions.AggressiveInlining);RewriteAIL>]
    member this.TryGetValue(key: 'K, value: byref<'V>): bool = this.source.TryGetValue(key, &value)
        
    [<MethodImplAttribute(MethodImplOptions.AggressiveInlining);RewriteAIL>]
    member this.MoveNext() =
      this.index <- this.index + 1
      let mutable doSpin = this.doSpin;
      let source = this.source
      let mutable version = if doSpin then Volatile.Read(&source._version) else 0L
      
      if this.index < source.size then
        let mutable newC = source.GetPairByIndexUnchecked(this.index)
      
        while doSpin do
          let nextVersion = Volatile.Read(&source._nextVersion)
          if version = nextVersion then 
            doSpin <- false
          else
            let mutable sw = new SpinWait()
            sw.SpinOnce()
            version <- Volatile.Read(&source._version)
            if this.index < source.size then
              newC <- source.GetPairByIndexUnchecked(this.index)
            else
              // NB this is only possible if size decreased but that changes 
              // this.source.orderVersion and operation will throw on the next section below
              doSpin <- false 

        if this.cursorVersion <> source.orderVersion then
          // source order change
          this.index <- -1
          let key = this.current.Key
          this.current <- Unchecked.defaultof<_>
          ThrowHelper.ThrowOutOfOrderKeyException(key, "SortedMap order was changed since last move. Catch OutOfOrderKeyException and use its CurrentKey property together with MoveAt(key, Lookup.GT) to recover.")
        this.current <- newC
        true
      else
        // NB It's slightly chaper to set index on the happy path and recover here
        // If happy path throws than the cursor is in invalid state (calling Current/Key/Value is undefined)
        // but we have CurrentKey stored inside the exception and could recover with MoveAt
        this.index <- this.index - 1
        false


    member this.CurrentBatch: ISeries<'K,'V> = 
      let mutable result = Unchecked.defaultof<_>
      let mutable doSpin = true
      let sw = new SpinWait()
      while doSpin do
        doSpin <- this.source._isSynchronized
        let version = if doSpin then Volatile.Read(&this.source._version) else 0L
        result <-
        /////////// Start read-locked code /////////////

          if this.isBatch then
            Trace.Assert(this.index = this.source.size - 1)
            Trace.Assert(this.source.isReadOnly)
            this.source :> ISeries<'K,'V>
          else raise (InvalidOperationException("SortedMap cursor is not at a batch position"))

        /////////// End read-locked code /////////////
        if doSpin then
          let nextVersion = Volatile.Read(&this.source._nextVersion)
          if version = nextVersion then doSpin <- false
          else sw.SpinOnce()
      result

    member this.MoveNextBatch(cancellationToken: CancellationToken): Task<bool> =
      let mutable newIndex = this.index
      let mutable newC = Unchecked.defaultof<_>
      //let mutable newKey = this.currentKey
      //let mutable newValue = this.currentValue
      let mutable newIsBatch = this.isBatch

      let mutable result = Unchecked.defaultof<_>
      let mutable doSpin = true
      let sw = new SpinWait()
      while doSpin do
        doSpin <- this.source._isSynchronized
        let version = if doSpin then Volatile.Read(&this.source._version) else 0L
        result <-
        /////////// Start read-locked code /////////////

          if (this.source.isReadOnly) && (this.index = -1) && this.source.size > 0 then
            this.cursorVersion <- this.source.orderVersion
            newIndex <- this.source.size - 1 // at the last element of the batch
            newC <- this.source.GetPairByIndexUnchecked(newIndex)
            //newKey <- this.source.GetKeyByIndexUnchecked(newIndex)
            //newValue <- this.source.values.[newIndex]
            newIsBatch <- true
            TaskUtil.TrueTask
          else TaskUtil.FalseTask

        /////////// End read-locked code /////////////
        if doSpin then
          let nextVersion = Volatile.Read(&this.source._nextVersion)
          if version = nextVersion then doSpin <- false
          else sw.SpinOnce()
      if result.Result then
        this.index <- newIndex
        this.current <- newC
        //this.currentKey <- newKey
        //this.currentValue <- newValue
        this.isBatch <- newIsBatch
      result

    [<MethodImplAttribute(MethodImplOptions.AggressiveInlining);RewriteAIL>]
    member this.MovePrevious() = 
      let mutable newIndex = this.index
      let mutable newC = Unchecked.defaultof<_>
      //let mutable newKey = this.currentKey
      //let mutable newValue = this.currentValue

      let mutable result = Unchecked.defaultof<_>
      let mutable doSpin = true
      let sw = new SpinWait()
      while doSpin do
        doSpin <- this.source._isSynchronized
        let version = if doSpin then Volatile.Read(&this.source._version) else 0L
        result <-
        /////////// Start read-locked code /////////////
          if this.index = -1 then
            if this.source.size > 0 then
              this.cursorVersion <- this.source.orderVersion
              newIndex <- this.source.size - 1
              newC <- this.source.GetPairByIndexUnchecked(newIndex)
              //newKey <- this.source.GetKeyByIndexUnchecked(newIndex)
              //newValue <- this.source.values.[newIndex]
              true
            else
              false
          elif this.cursorVersion = this.source.orderVersion then
            if this.index > 0 && this.index < this.source.size then
              newIndex <- this.index - 1
              newC <- this.source.GetPairByIndexUnchecked(newIndex)
              //newKey <- this.source.GetKeyByIndexUnchecked(newIndex)
              //newValue <- this.source.values.[newIndex]
              true
            else
              false
          else
            ThrowHelper.ThrowOutOfOrderKeyException(this.current.Key, "SortedMap order was changed since last move. Catch OutOfOrderKeyException and use its CurrentKey property together with MoveAt(key, Lookup.LT) to recover.")
            false
        /////////// End read-locked code /////////////
        if doSpin then
          let nextVersion = Volatile.Read(&this.source._nextVersion)
          if version = nextVersion then doSpin <- false
          else sw.SpinOnce()
      if result then
        this.index <- newIndex
        this.current <- newC
        //this.currentKey <- newKey
        //this.currentValue <- newValue
      result

    [<MethodImplAttribute(MethodImplOptions.AggressiveInlining);RewriteAIL>]
    member this.MoveAt(key:'K, lookup:Lookup) =
      let mutable newIndex = this.index
      let mutable newC = Unchecked.defaultof<_>
      //let mutable newKey = this.currentKey
      //let mutable newValue = this.currentValue
      let mutable result = Unchecked.defaultof<_>
      let mutable doSpin = true
      let sw = new SpinWait()
      while doSpin do
        doSpin <- this.source._isSynchronized
        let version = if doSpin then Volatile.Read(&this.source._version) else 0L
        result <-
        /////////// Start read-locked code /////////////
          // leave it for mutations, optimize TryFindAt
          let position = this.source.FindIndexAt(key, lookup)
          if position >= 0 then
            newC <- this.source.GetPairByIndexUnchecked(position)
            this.cursorVersion <- this.source.orderVersion
            newIndex <- position
            //newKey <- kvp.Key
            //newValue <- kvp.Value
            true
          else
            false
      /////////// End read-locked code /////////////
        if doSpin then
          let nextVersion = Volatile.Read(&this.source._nextVersion)
          if version = nextVersion then doSpin <- false
          else sw.SpinOnce()
      if result then
        this.index <- newIndex
        this.current <- newC
        //this.currentKey <- newKey
        //this.currentValue <- newValue
      result

    [<MethodImplAttribute(MethodImplOptions.AggressiveInlining);RewriteAIL>]
    member this.MoveFirst() =
      let mutable newIndex = this.index
      let mutable newC = Unchecked.defaultof<_>
      //let mutable newKey = this.currentKey
      //let mutable newValue = this.currentValue
      let mutable result = Unchecked.defaultof<_>
      let mutable doSpin = true
      let sw = new SpinWait()
      while doSpin do
        doSpin <- this.source._isSynchronized
        let version = if doSpin then Volatile.Read(&this.source._version) else 0L
        result <-
        /////////// Start read-locked code /////////////
          if this.source.size > 0 then
            this.cursorVersion <- this.source.orderVersion
            newIndex <- 0
            newC <- this.source.GetPairByIndexUnchecked(newIndex)
            //newKey <- this.source.GetKeyByIndexUnchecked(newIndex)
            //newValue <- this.source.values.[newIndex]
            true
          else
            false
        /////////// End read-locked code /////////////
        if doSpin then
          let nextVersion = Volatile.Read(&this.source._nextVersion)
          if version = nextVersion then doSpin <- false
          else sw.SpinOnce()
      if result then
        this.index <- newIndex
        this.current <- newC
        //this.currentKey <- newKey
        //this.currentValue <- newValue
      result

    [<MethodImplAttribute(MethodImplOptions.AggressiveInlining);RewriteAIL>]
    member this.MoveLast() =
      let mutable newIndex = this.index
      let mutable newC = Unchecked.defaultof<_>
      //let mutable newKey = this.currentKey
      //let mutable newValue = this.currentValue
      let mutable result = Unchecked.defaultof<_>
      let mutable doSpin = true
      let sw = new SpinWait()
      while doSpin do
        doSpin <- this.source._isSynchronized
        let version = if doSpin then Volatile.Read(&this.source._version) else 0L
        result <-
        /////////// Start read-locked code /////////////
          if this.source.size > 0 then
            this.cursorVersion <- this.source.orderVersion
            newIndex <- this.source.size - 1
            newC <- this.source.GetPairByIndexUnchecked(newIndex)
            //newKey <- this.source.GetKeyByIndexUnchecked(newIndex)
            //newValue <- this.source.values.[newIndex]
            true
          else
            false
        /////////// End read-locked code /////////////
        if doSpin then
          let nextVersion = Volatile.Read(&this.source._nextVersion)
          if version = nextVersion then doSpin <- false
          else sw.SpinOnce()
      if result then
        this.index <- newIndex
        this.current <- newC
        //this.currentKey <- newKey
        //this.currentValue <- newValue
      result

    member this.MoveNextAsync(cancellationToken:CancellationToken): Task<bool> =
      if this.source.isReadOnly then
        if this.MoveNext() then TaskUtil.TrueTask else TaskUtil.FalseTask
      else ThrowHelper.ThrowNotSupportedException("Use an async cursor wrapper instead");TaskUtil.FalseTask

    member this.Clone() = 
      let mutable copy = this
      copy

    member this.Reset() = 
      this.cursorVersion <- this.source.orderVersion
      this.current <- Unchecked.defaultof<_>
      //this.currentKey <- Unchecked.defaultof<'K>
      //this.currentValue <- Unchecked.defaultof<'V>
      this.index <- -1

    member this.Dispose() = this.Reset()

    interface IDisposable with
      member this.Dispose() = this.Dispose()

    interface IEnumerator<KVP<'K,'V>> with    
      member this.Reset() = this.Reset()
      member this.MoveNext():bool = this.MoveNext()
      member this.Current with get(): KVP<'K, 'V> = this.Current
      member this.Current with get(): obj = this.Current :> obj
    
    interface IAsyncEnumerator<KVP<'K,'V>> with
      member this.MoveNextAsync(cancellationToken:CancellationToken): Task<bool> = this.MoveNextAsync(cancellationToken)
      member this.MoveNextAsync(): Task<bool> = this.MoveNextAsync(CancellationToken.None)
      member this.DisposeAsync() = this.Dispose();Task.CompletedTask
      
    interface ICursor<'K,'V> with
      member this.Comparer with get() = this.Comparer
      member this.CurrentBatch = this.CurrentBatch
      member this.MoveNextBatch(cancellationToken: CancellationToken): Task<bool> = this.MoveNextBatch(cancellationToken)
      member this.MoveAt(index:'K, lookup:Lookup) = this.MoveAt(index, lookup)
      member this.MoveFirst():bool = this.MoveFirst()
      member this.MoveLast():bool =  this.MoveLast()
      member this.MovePrevious():bool = this.MovePrevious()
      member this.CurrentKey with get():'K = this.CurrentKey
      member this.CurrentValue with get():'V = this.CurrentValue
      member this.Source with get() = this.source :> ISeries<'K,'V>
      member this.Clone() = this.Clone() :> ICursor<'K,'V>
      member this.IsContinuous with get() = false
      member this.TryGetValue(key, [<Out>] value: byref<'V>) = this.TryGetValue(key, &value)
      // TODO
      member this.State with get() = raise (NotImplementedException())
      member this.MoveNext(stride, allowPartial) = raise (NotImplementedException())
      member this.MovePrevious(stride, allowPartial) = raise (NotImplementedException())
      
    interface ISpecializedCursor<'K,'V, SortedMapCursor<'K,'V>> with
      member this.Initialize() = 
        let c = this.Clone()
        c.Reset()
        c
      member this.Clone() = this.Clone()

//type internal ChunksContainer<'K,'V>
//  (comparer : IComparer<'K>)  =
//  let t = new SortedMap<_,_>(comparer)
//  interface ISeries<'K,SortedMap<'K,'V>> with
//    member x.Updated = t.Updated
//    member x.Comparer = t.Comparer
//    member x.First = t.First
//    member x.TryGetAt(idx, value) = t.TryGetAt(idx, &value)
//    member x.GetCursor() = t.GetCursor()
//    member x.GetAsyncEnumerator(): IAsyncEnumerator<KeyValuePair<'K,SortedMap<'K, 'V>>> = 
//      t.GetEnumerator() :> IAsyncEnumerator<KeyValuePair<'K,SortedMap<'K, 'V>>>
//    member x.GetEnumerator(): IEnumerator = 
//      t.GetEnumerator() :> IEnumerator
//    member x.GetEnumerator(): IEnumerator<KeyValuePair<'K,SortedMap<'K, 'V>>> = 
//      t.GetEnumerator() :> IEnumerator<KeyValuePair<'K,SortedMap<'K, 'V>>>
//    member x.IsIndexed = false
//    member x.IsCompleted = t.IsCompleted
//    member x.Item
//      with get (key) = t.Item(key)
//    member x.Keys = t.Keys
//    member x.Last = t.Last 
//    member x.TryFindAt(key, direction, value) = t.TryFindAt(key, direction, &value)
//    member x.Values = t.Values 
    
    
//  interface IMutableChunksSeries<'K,'V,SortedMap<'K,'V>> with
//    member x.Dispose() = ()
//    member x.Flush() = Task.CompletedTask
//    member x.Id = ""
    
//    member x.Set(key, v) =
//        if v.size > 0 then 
//          let ov = t.orderVersion
//          t.Set(key, v) |> ignore
//          t.orderVersion <- ov
//        else t.TryRemove(key) |> ignore
//        t._version <- v._version
//        TaskUtil.FalseTask

//    member x.RemoveMany(key, keyChunk, direction) =
//      match direction with
//      | Lookup.EQ -> (x :> IMutableChunksSeries<'K,'V,SortedMap<'K,'V>>).Set(key, keyChunk)
//      | Lookup.LT | Lookup.LE -> 
//        t.TryRemoveMany(key, Lookup.LT).Result |> ignore
//        (x :> IMutableChunksSeries<'K,'V,SortedMap<'K,'V>>).Set(key, keyChunk)
//      | Lookup.GT | Lookup.GE -> 
//        t.TryRemoveMany(key, Lookup.GT).Result |> ignore
//        (x :> IMutableChunksSeries<'K,'V,SortedMap<'K,'V>>).Set(key, keyChunk)
//      | _ -> failwith "mute F# warning"

//    member x.Version = t._version // small v, field
   