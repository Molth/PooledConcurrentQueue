using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace AQueue
{
    /// <summary>
    ///     ConcurrentQueue
    /// </summary>
    internal static partial class ConcurrentQueue
    {
        /// <summary>
        ///     ConcurrentQueue
        /// </summary>
        /// <typeparam name="T">Type</typeparam>
        [StructLayout(LayoutKind.Sequential)]
        public sealed class ConcurrentQueueNotArm64<T>
        {
            /// <summary>
            ///     Cross segment lock
            /// </summary>
            private object _crossSegmentLock;

            /// <summary>
            ///     Segment pool
            /// </summary>
            private Stack<ConcurrentQueueSegmentNotArm64<T>> _segmentPool;

            /// <summary>
            ///     Tail
            /// </summary>
            private volatile ConcurrentQueueSegmentNotArm64<T> _tail;

            /// <summary>
            ///     Head
            /// </summary>
            private volatile ConcurrentQueueSegmentNotArm64<T> _head;

            /// <summary>
            ///     Structure
            /// </summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public ConcurrentQueueNotArm64()
            {
                _crossSegmentLock = new object();
                _segmentPool = new Stack<ConcurrentQueueSegmentNotArm64<T>>();
                var segment = new ConcurrentQueueSegmentNotArm64<T>();
                segment.Initialize();
                _tail = _head = segment;
            }

            /// <summary>
            ///     IsEmpty
            /// </summary>
            public bool IsEmpty
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get
                {
                    var segment = _head;
                    while (true)
                    {
                        var next = Volatile.Read(ref segment.NextSegment);
                        if (segment.TryPeek())
                            return false;
                        if (next != null)
                            segment = next;
                        else if (Volatile.Read(ref segment.NextSegment) == null)
                            break;
                    }

                    return true;
                }
            }

            /// <summary>
            ///     Count
            /// </summary>
            public int Count
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get
                {
                    var spinWait = new NativeSpinWait();
                    while (true)
                    {
                        var head = _head;
                        var tail = _tail;
                        var headHead = Volatile.Read(ref head.HeadAndTail.Head);
                        var headTail = Volatile.Read(ref head.HeadAndTail.Tail);
                        if (head == tail)
                        {
                            if (head == _head && tail == _tail && headHead == Volatile.Read(ref head.HeadAndTail.Head) && headTail == Volatile.Read(ref head.HeadAndTail.Tail))
                                return GetCount(headHead, headTail);
                        }
                        else if (head.NextSegment! == tail)
                        {
                            var tailHead = Volatile.Read(ref tail.HeadAndTail.Head);
                            var tailTail = Volatile.Read(ref tail.HeadAndTail.Tail);
                            if (head == _head && tail == _tail && headHead == Volatile.Read(ref head.HeadAndTail.Head) && headTail == Volatile.Read(ref head.HeadAndTail.Tail) && tailHead == Volatile.Read(ref tail.HeadAndTail.Head) && tailTail == Volatile.Read(ref tail.HeadAndTail.Tail))
                                return GetCount(headHead, headTail) + GetCount(tailHead, tailTail);
                        }
                        else
                        {
                            lock (_crossSegmentLock)
                            {
                                if (head == _head && tail == _tail)
                                {
                                    var tailHead = Volatile.Read(ref tail.HeadAndTail.Head);
                                    var tailTail = Volatile.Read(ref tail.HeadAndTail.Tail);
                                    if (headHead == Volatile.Read(ref head.HeadAndTail.Head) && headTail == Volatile.Read(ref head.HeadAndTail.Tail) && tailHead == Volatile.Read(ref tail.HeadAndTail.Head) && tailTail == Volatile.Read(ref tail.HeadAndTail.Tail))
                                    {
                                        var count = GetCount(headHead, headTail) + GetCount(tailHead, tailTail);
                                        for (var s = head.NextSegment!; s != tail; s = s.NextSegment!)
                                            count += s.HeadAndTail.Tail - FREEZE_OFFSET;
                                        return count;
                                    }
                                }
                            }
                        }

                        spinWait.SpinOnce(-1);
                    }
                }
            }

            /// <summary>
            ///     Clear
            /// </summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Clear()
            {
                lock (_crossSegmentLock)
                {
                    _tail.EnsureFrozenForEnqueues();
                    var node = _head;
                    while (node != null)
                    {
                        var temp = node;
                        node = node.NextSegment!;
                        _segmentPool.Push(temp);
                    }

                    if (!_segmentPool.TryPop(out var segment))
                        segment = new ConcurrentQueueSegmentNotArm64<T>();
                    segment.Initialize();
                    _tail = _head = segment;
                }
            }

            /// <summary>
            ///     Enqueue
            /// </summary>
            /// <param name="item">Item</param>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Enqueue(in T item)
            {
                if (!_tail.TryEnqueue(item))
                {
                    while (true)
                    {
                        var tail = _tail;
                        if (tail.TryEnqueue(item))
                            return;
                        lock (_crossSegmentLock)
                        {
                            if (tail == _tail)
                            {
                                tail.EnsureFrozenForEnqueues();
                                if (!_segmentPool.TryPop(out var newTail))
                                    newTail = new ConcurrentQueueSegmentNotArm64<T>();
                                newTail.Initialize();
                                tail.NextSegment = newTail;
                                _tail = newTail;
                            }
                        }
                    }
                }
            }

            /// <summary>
            ///     Try dequeue
            /// </summary>
            /// <param name="result">Item</param>
            /// <returns>Dequeued</returns>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool TryDequeue(out T? result)
            {
                var head = _head;
                if (head.TryDequeue(out result))
                    return true;
                if (head.NextSegment == null)
                {
                    result = default;
                    return false;
                }

                while (true)
                {
                    head = _head;
                    if (head.TryDequeue(out result))
                        return true;
                    if (head.NextSegment == null)
                    {
                        result = default;
                        return false;
                    }

                    if (head.TryDequeue(out result))
                        return true;
                    lock (_crossSegmentLock)
                    {
                        if (head == _head)
                        {
                            _head = head.NextSegment;
                            _segmentPool.Push(head);
                        }
                    }
                }
            }

            /// <summary>
            ///     Get count
            /// </summary>
            /// <param name="head">Head</param>
            /// <param name="tail">Tail</param>
            /// <returns>Count</returns>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static int GetCount(int head, int tail)
            {
                if (head != tail && head != tail - FREEZE_OFFSET)
                {
                    head &= SLOTS_MASK;
                    tail &= SLOTS_MASK;
                    return head < tail ? tail - head : LENGTH - head + tail;
                }

                return 0;
            }
        }

        /// <summary>
        ///     ConcurrentQueue segment
        /// </summary>
        /// <typeparam name="T">Type</typeparam>
        [StructLayout(LayoutKind.Sequential)]
        public sealed class ConcurrentQueueSegmentNotArm64<T>
        {
            /// <summary>
            ///     Slots
            /// </summary>
            public ConcurrentQueueSegmentSlots1024<T> Slots;

            /// <summary>
            ///     Head and tail
            /// </summary>
            public ConcurrentQueuePaddedHeadAndTailNotArm64 HeadAndTail;

            /// <summary>
            ///     Frozen for enqueues
            /// </summary>
            public bool FrozenForEnqueues;

            /// <summary>
            ///     Next segment
            /// </summary>
            public ConcurrentQueueSegmentNotArm64<T>? NextSegment;

            /// <summary>
            ///     Initialize
            /// </summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Initialize()
            {
                ref var slot = ref Unsafe.As<ConcurrentQueueSegmentSlots1024<T>, ConcurrentQueueSegmentSlot<T>>(ref Slots);
                for (var i = 0; i < LENGTH; ++i)
                    Unsafe.Add(ref slot, (nint)i).SequenceNumber = i;
                HeadAndTail = new ConcurrentQueuePaddedHeadAndTailNotArm64();
                FrozenForEnqueues = false;
                NextSegment = null;
            }

            /// <summary>
            ///     Ensure frozen for enqueues
            /// </summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void EnsureFrozenForEnqueues()
            {
                if (!FrozenForEnqueues)
                {
                    FrozenForEnqueues = true;
                    Interlocked.Add(ref HeadAndTail.Tail, FREEZE_OFFSET);
                }
            }

            /// <summary>
            ///     Try dequeue
            /// </summary>
            /// <param name="result">Item</param>
            /// <returns>Dequeued</returns>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool TryDequeue(out T? result)
            {
                ref var slot = ref Unsafe.As<ConcurrentQueueSegmentSlots1024<T>, ConcurrentQueueSegmentSlot<T>>(ref Slots);
                var spinWait = new NativeSpinWait();
                while (true)
                {
                    var currentHead = Volatile.Read(ref HeadAndTail.Head);
                    var slotsIndex = currentHead & SLOTS_MASK;
                    var sequenceNumber = Volatile.Read(ref Unsafe.Add(ref slot, (nint)slotsIndex).SequenceNumber);
                    var diff = sequenceNumber - (currentHead + 1);
                    if (diff == 0)
                    {
                        if (Interlocked.CompareExchange(ref HeadAndTail.Head, currentHead + 1, currentHead) == currentHead)
                        {
                            result = Unsafe.Add(ref slot, (nint)slotsIndex).Item;
                            Volatile.Write(ref Unsafe.Add(ref slot, (nint)slotsIndex).SequenceNumber, currentHead + LENGTH);
                            return true;
                        }
                    }
                    else if (diff < 0)
                    {
                        var frozen = FrozenForEnqueues;
                        var currentTail = Volatile.Read(ref HeadAndTail.Tail);
                        if (currentTail - currentHead <= 0 || (frozen && currentTail - FREEZE_OFFSET - currentHead <= 0))
                        {
                            result = default;
                            return false;
                        }

                        spinWait.SpinOnce(-1);
                    }
                }
            }

            /// <summary>
            ///     Try peek
            /// </summary>
            /// <returns>Peeked</returns>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool TryPeek()
            {
                ref var slot = ref Unsafe.As<ConcurrentQueueSegmentSlots1024<T>, ConcurrentQueueSegmentSlot<T>>(ref Slots);
                var spinWait = new NativeSpinWait();
                while (true)
                {
                    var currentHead = Volatile.Read(ref HeadAndTail.Head);
                    var slotsIndex = currentHead & SLOTS_MASK;
                    var sequenceNumber = Volatile.Read(ref Unsafe.Add(ref slot, (nint)slotsIndex).SequenceNumber);
                    var diff = sequenceNumber - (currentHead + 1);
                    if (diff == 0)
                        return true;
                    if (diff < 0)
                    {
                        var frozen = FrozenForEnqueues;
                        var currentTail = Volatile.Read(ref HeadAndTail.Tail);
                        if (currentTail - currentHead <= 0 || (frozen && currentTail - FREEZE_OFFSET - currentHead <= 0))
                            return false;
                        spinWait.SpinOnce(-1);
                    }
                }
            }

            /// <summary>
            ///     Try enqueue
            /// </summary>
            /// <param name="item">Item</param>
            /// <returns>Enqueued</returns>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool TryEnqueue(in T item)
            {
                ref var slot = ref Unsafe.As<ConcurrentQueueSegmentSlots1024<T>, ConcurrentQueueSegmentSlot<T>>(ref Slots);
                while (true)
                {
                    var currentTail = Volatile.Read(ref HeadAndTail.Tail);
                    var slotsIndex = currentTail & SLOTS_MASK;
                    var sequenceNumber = Volatile.Read(ref Unsafe.Add(ref slot, (nint)slotsIndex).SequenceNumber);
                    var diff = sequenceNumber - currentTail;
                    if (diff == 0)
                    {
                        if (Interlocked.CompareExchange(ref HeadAndTail.Tail, currentTail + 1, currentTail) == currentTail)
                        {
                            Unsafe.Add(ref slot, (nint)slotsIndex).Item = item;
                            Volatile.Write(ref Unsafe.Add(ref slot, (nint)slotsIndex).SequenceNumber, currentTail + 1);
                            return true;
                        }
                    }
                    else if (diff < 0)
                    {
                        return false;
                    }
                }
            }
        }

        /// <summary>
        ///     ConcurrentQueue padded head and tail
        /// </summary>
        [StructLayout(LayoutKind.Explicit, Size = 3 * CACHE_LINE_SIZE)]
        public struct ConcurrentQueuePaddedHeadAndTailNotArm64
        {
            /// <summary>
            ///     Head
            /// </summary>
            [FieldOffset(1 * CACHE_LINE_SIZE)] public int Head;

            /// <summary>
            ///     Tail
            /// </summary>
            [FieldOffset(2 * CACHE_LINE_SIZE)] public int Tail;

            /// <summary>
            ///     Catch line size
            /// </summary>
            private const int CACHE_LINE_SIZE = (int)ArchitectureHelpers.CACHE_LINE_SIZE_NOT_ARM64;
        }
    }

    /// <summary>
    ///     ConcurrentQueue
    /// </summary>
    internal static partial class ConcurrentQueue
    {
        /// <summary>
        ///     ConcurrentQueue
        /// </summary>
        /// <typeparam name="T">Type</typeparam>
        [StructLayout(LayoutKind.Sequential)]
        public sealed class ConcurrentQueueArm64<T>
        {
            /// <summary>
            ///     Cross segment lock
            /// </summary>
            private object _crossSegmentLock;

            /// <summary>
            ///     Segment pool
            /// </summary>
            private Stack<ConcurrentQueueSegmentArm64<T>> _segmentPool;

            /// <summary>
            ///     Tail
            /// </summary>
            private volatile ConcurrentQueueSegmentArm64<T> _tail;

            /// <summary>
            ///     Head
            /// </summary>
            private volatile ConcurrentQueueSegmentArm64<T> _head;

            /// <summary>
            ///     Structure
            /// </summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public ConcurrentQueueArm64()
            {
                _crossSegmentLock = new object();
                _segmentPool = new Stack<ConcurrentQueueSegmentArm64<T>>();
                var segment = new ConcurrentQueueSegmentArm64<T>();
                segment.Initialize();
                _tail = _head = segment;
            }

            /// <summary>
            ///     IsEmpty
            /// </summary>
            public bool IsEmpty
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get
                {
                    var segment = _head;
                    while (true)
                    {
                        var next = Volatile.Read(ref segment.NextSegment);
                        if (segment.TryPeek())
                            return false;
                        if (next != null)
                            segment = next;
                        else if (Volatile.Read(ref segment.NextSegment) == null)
                            break;
                    }

                    return true;
                }
            }

            /// <summary>
            ///     Count
            /// </summary>
            public int Count
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get
                {
                    var spinWait = new NativeSpinWait();
                    while (true)
                    {
                        var head = _head;
                        var tail = _tail;
                        var headHead = Volatile.Read(ref head.HeadAndTail.Head);
                        var headTail = Volatile.Read(ref head.HeadAndTail.Tail);
                        if (head == tail)
                        {
                            if (head == _head && tail == _tail && headHead == Volatile.Read(ref head.HeadAndTail.Head) && headTail == Volatile.Read(ref head.HeadAndTail.Tail))
                                return GetCount(headHead, headTail);
                        }
                        else if (head.NextSegment! == tail)
                        {
                            var tailHead = Volatile.Read(ref tail.HeadAndTail.Head);
                            var tailTail = Volatile.Read(ref tail.HeadAndTail.Tail);
                            if (head == _head && tail == _tail && headHead == Volatile.Read(ref head.HeadAndTail.Head) && headTail == Volatile.Read(ref head.HeadAndTail.Tail) && tailHead == Volatile.Read(ref tail.HeadAndTail.Head) && tailTail == Volatile.Read(ref tail.HeadAndTail.Tail))
                                return GetCount(headHead, headTail) + GetCount(tailHead, tailTail);
                        }
                        else
                        {
                            lock (_crossSegmentLock)
                            {
                                if (head == _head && tail == _tail)
                                {
                                    var tailHead = Volatile.Read(ref tail.HeadAndTail.Head);
                                    var tailTail = Volatile.Read(ref tail.HeadAndTail.Tail);
                                    if (headHead == Volatile.Read(ref head.HeadAndTail.Head) && headTail == Volatile.Read(ref head.HeadAndTail.Tail) && tailHead == Volatile.Read(ref tail.HeadAndTail.Head) && tailTail == Volatile.Read(ref tail.HeadAndTail.Tail))
                                    {
                                        var count = GetCount(headHead, headTail) + GetCount(tailHead, tailTail);
                                        for (var s = head.NextSegment!; s != tail; s = s.NextSegment!)
                                            count += s.HeadAndTail.Tail - FREEZE_OFFSET;
                                        return count;
                                    }
                                }
                            }
                        }

                        spinWait.SpinOnce(-1);
                    }
                }
            }

            /// <summary>
            ///     Clear
            /// </summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Clear()
            {
                lock (_crossSegmentLock)
                {
                    _tail.EnsureFrozenForEnqueues();
                    var node = _head;
                    while (node != null)
                    {
                        var temp = node;
                        node = node.NextSegment!;
                        _segmentPool.Push(temp);
                    }

                    if (!_segmentPool.TryPop(out var segment))
                        segment = new ConcurrentQueueSegmentArm64<T>();
                    segment.Initialize();
                    _tail = _head = segment;
                }
            }

            /// <summary>
            ///     Enqueue
            /// </summary>
            /// <param name="item">Item</param>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Enqueue(in T item)
            {
                if (!_tail.TryEnqueue(item))
                {
                    while (true)
                    {
                        var tail = _tail;
                        if (tail.TryEnqueue(item))
                            return;
                        lock (_crossSegmentLock)
                        {
                            if (tail == _tail)
                            {
                                tail.EnsureFrozenForEnqueues();
                                if (!_segmentPool.TryPop(out var newTail))
                                    newTail = new ConcurrentQueueSegmentArm64<T>();
                                newTail.Initialize();
                                tail.NextSegment = newTail;
                                _tail = newTail;
                            }
                        }
                    }
                }
            }

            /// <summary>
            ///     Try dequeue
            /// </summary>
            /// <param name="result">Item</param>
            /// <returns>Dequeued</returns>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool TryDequeue(out T? result)
            {
                var head = _head;
                if (head.TryDequeue(out result))
                    return true;
                if (head.NextSegment == null)
                {
                    result = default;
                    return false;
                }

                while (true)
                {
                    head = _head;
                    if (head.TryDequeue(out result))
                        return true;
                    if (head.NextSegment == null)
                    {
                        result = default;
                        return false;
                    }

                    if (head.TryDequeue(out result))
                        return true;
                    lock (_crossSegmentLock)
                    {
                        if (head == _head)
                        {
                            _head = head.NextSegment;
                            _segmentPool.Push(head);
                        }
                    }
                }
            }

            /// <summary>
            ///     Get count
            /// </summary>
            /// <param name="head">Head</param>
            /// <param name="tail">Tail</param>
            /// <returns>Count</returns>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static int GetCount(int head, int tail)
            {
                if (head != tail && head != tail - FREEZE_OFFSET)
                {
                    head &= SLOTS_MASK;
                    tail &= SLOTS_MASK;
                    return head < tail ? tail - head : LENGTH - head + tail;
                }

                return 0;
            }
        }

        /// <summary>
        ///     ConcurrentQueue segment
        /// </summary>
        /// <typeparam name="T">Type</typeparam>
        [StructLayout(LayoutKind.Sequential)]
        public sealed class ConcurrentQueueSegmentArm64<T>
        {
            /// <summary>
            ///     Slots
            /// </summary>
            public ConcurrentQueueSegmentSlots1024<T> Slots;

            /// <summary>
            ///     Head and tail
            /// </summary>
            public ConcurrentQueuePaddedHeadAndTailArm64 HeadAndTail;

            /// <summary>
            ///     Frozen for enqueues
            /// </summary>
            public bool FrozenForEnqueues;

            /// <summary>
            ///     Next segment
            /// </summary>
            public ConcurrentQueueSegmentArm64<T>? NextSegment;

            /// <summary>
            ///     Initialize
            /// </summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Initialize()
            {
                ref var slot = ref Unsafe.As<ConcurrentQueueSegmentSlots1024<T>, ConcurrentQueueSegmentSlot<T>>(ref Slots);
                for (var i = 0; i < LENGTH; ++i)
                    Unsafe.Add(ref slot, (nint)i).SequenceNumber = i;
                HeadAndTail = new ConcurrentQueuePaddedHeadAndTailArm64();
                FrozenForEnqueues = false;
                NextSegment = null;
            }

            /// <summary>
            ///     Ensure frozen for enqueues
            /// </summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void EnsureFrozenForEnqueues()
            {
                if (!FrozenForEnqueues)
                {
                    FrozenForEnqueues = true;
                    Interlocked.Add(ref HeadAndTail.Tail, FREEZE_OFFSET);
                }
            }

            /// <summary>
            ///     Try dequeue
            /// </summary>
            /// <param name="result">Item</param>
            /// <returns>Dequeued</returns>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool TryDequeue(out T? result)
            {
                ref var slot = ref Unsafe.As<ConcurrentQueueSegmentSlots1024<T>, ConcurrentQueueSegmentSlot<T>>(ref Slots);
                var spinWait = new NativeSpinWait();
                while (true)
                {
                    var currentHead = Volatile.Read(ref HeadAndTail.Head);
                    var slotsIndex = currentHead & SLOTS_MASK;
                    var sequenceNumber = Volatile.Read(ref Unsafe.Add(ref slot, (nint)slotsIndex).SequenceNumber);
                    var diff = sequenceNumber - (currentHead + 1);
                    if (diff == 0)
                    {
                        if (Interlocked.CompareExchange(ref HeadAndTail.Head, currentHead + 1, currentHead) == currentHead)
                        {
                            result = Unsafe.Add(ref slot, (nint)slotsIndex).Item;
                            Volatile.Write(ref Unsafe.Add(ref slot, (nint)slotsIndex).SequenceNumber, currentHead + LENGTH);
                            return true;
                        }
                    }
                    else if (diff < 0)
                    {
                        var frozen = FrozenForEnqueues;
                        var currentTail = Volatile.Read(ref HeadAndTail.Tail);
                        if (currentTail - currentHead <= 0 || (frozen && currentTail - FREEZE_OFFSET - currentHead <= 0))
                        {
                            result = default;
                            return false;
                        }

                        spinWait.SpinOnce(-1);
                    }
                }
            }

            /// <summary>
            ///     Try peek
            /// </summary>
            /// <returns>Peeked</returns>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool TryPeek()
            {
                ref var slot = ref Unsafe.As<ConcurrentQueueSegmentSlots1024<T>, ConcurrentQueueSegmentSlot<T>>(ref Slots);
                var spinWait = new NativeSpinWait();
                while (true)
                {
                    var currentHead = Volatile.Read(ref HeadAndTail.Head);
                    var slotsIndex = currentHead & SLOTS_MASK;
                    var sequenceNumber = Volatile.Read(ref Unsafe.Add(ref slot, (nint)slotsIndex).SequenceNumber);
                    var diff = sequenceNumber - (currentHead + 1);
                    if (diff == 0)
                        return true;
                    if (diff < 0)
                    {
                        var frozen = FrozenForEnqueues;
                        var currentTail = Volatile.Read(ref HeadAndTail.Tail);
                        if (currentTail - currentHead <= 0 || (frozen && currentTail - FREEZE_OFFSET - currentHead <= 0))
                            return false;
                        spinWait.SpinOnce(-1);
                    }
                }
            }

            /// <summary>
            ///     Try enqueue
            /// </summary>
            /// <param name="item">Item</param>
            /// <returns>Enqueued</returns>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool TryEnqueue(in T item)
            {
                ref var slot = ref Unsafe.As<ConcurrentQueueSegmentSlots1024<T>, ConcurrentQueueSegmentSlot<T>>(ref Slots);
                while (true)
                {
                    var currentTail = Volatile.Read(ref HeadAndTail.Tail);
                    var slotsIndex = currentTail & SLOTS_MASK;
                    var sequenceNumber = Volatile.Read(ref Unsafe.Add(ref slot, (nint)slotsIndex).SequenceNumber);
                    var diff = sequenceNumber - currentTail;
                    if (diff == 0)
                    {
                        if (Interlocked.CompareExchange(ref HeadAndTail.Tail, currentTail + 1, currentTail) == currentTail)
                        {
                            Unsafe.Add(ref slot, (nint)slotsIndex).Item = item;
                            Volatile.Write(ref Unsafe.Add(ref slot, (nint)slotsIndex).SequenceNumber, currentTail + 1);
                            return true;
                        }
                    }
                    else if (diff < 0)
                    {
                        return false;
                    }
                }
            }
        }

        /// <summary>
        ///     ConcurrentQueue padded head and tail
        /// </summary>
        [StructLayout(LayoutKind.Explicit, Size = 3 * CACHE_LINE_SIZE)]
        public struct ConcurrentQueuePaddedHeadAndTailArm64
        {
            /// <summary>
            ///     Head
            /// </summary>
            [FieldOffset(1 * CACHE_LINE_SIZE)] public int Head;

            /// <summary>
            ///     Tail
            /// </summary>
            [FieldOffset(2 * CACHE_LINE_SIZE)] public int Tail;

            /// <summary>
            ///     Catch line size
            /// </summary>
            private const int CACHE_LINE_SIZE = (int)ArchitectureHelpers.CACHE_LINE_SIZE_ARM64;
        }
    }

    /// <summary>
    ///     ConcurrentQueue
    /// </summary>
    internal static partial class ConcurrentQueue
    {
        /// <summary>
        ///     Length
        /// </summary>
        public const int LENGTH = 1024;

        /// <summary>
        ///     Slots mask
        /// </summary>
        public const int SLOTS_MASK = LENGTH - 1;

        /// <summary>
        ///     Freeze offset
        /// </summary>
        public const int FREEZE_OFFSET = LENGTH * 2;
    }

    /// <summary>
    ///     ConcurrentQueue
    /// </summary>
    internal static partial class ConcurrentQueue
    {
#if NET8_0_OR_GREATER
        /// <summary>
        ///     Slots
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        [InlineArray(LENGTH)]
        public struct ConcurrentQueueSegmentSlots1024<T>
        {
            private ConcurrentQueueSegmentSlot<T> _element;
        }
#else
        /// <summary>
        ///     Slots
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct ConcurrentQueueSegmentSlots1024<T> 
        {
            private ConcurrentQueueSegmentSlots512<T> _element0;
            private ConcurrentQueueSegmentSlots512<T> _element1;
        }

        /// <summary>
        ///     Slots
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct ConcurrentQueueSegmentSlots512<T> 
        {
            private ConcurrentQueueSegmentSlots256<T> _element0;
            private ConcurrentQueueSegmentSlots256<T> _element1;
        }

        /// <summary>
        ///     Slots
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct ConcurrentQueueSegmentSlots256<T> 
        {
            private ConcurrentQueueSegmentSlots128<T> _element0;
            private ConcurrentQueueSegmentSlots128<T> _element1;
        }

        /// <summary>
        ///     Slots
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct ConcurrentQueueSegmentSlots128<T> 
        {
            private ConcurrentQueueSegmentSlots64<T> _element0;
            private ConcurrentQueueSegmentSlots64<T> _element1;
        }

        /// <summary>
        ///     Slots
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct ConcurrentQueueSegmentSlots64<T> 
        {
            private ConcurrentQueueSegmentSlots32<T> _element0;
            private ConcurrentQueueSegmentSlots32<T> _element1;
        }

        /// <summary>
        ///     Slots
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct ConcurrentQueueSegmentSlots32<T> 
        {
            private ConcurrentQueueSegmentSlots16<T> _element0;
            private ConcurrentQueueSegmentSlots16<T> _element1;
        }

        /// <summary>
        ///     Slots
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct ConcurrentQueueSegmentSlots16<T> 
        {
            private ConcurrentQueueSegmentSlots8<T> _element0;
            private ConcurrentQueueSegmentSlots8<T> _element1;
        }

        /// <summary>
        ///     Slots
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct ConcurrentQueueSegmentSlots8<T> 
        {
            private ConcurrentQueueSegmentSlots4<T> _element0;
            private ConcurrentQueueSegmentSlots4<T> _element1;
        }

        /// <summary>
        ///     Slots
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct ConcurrentQueueSegmentSlots4<T> 
        {
            private ConcurrentQueueSegmentSlots2<T> _element0;
            private ConcurrentQueueSegmentSlots2<T> _element1;
        }

        /// <summary>
        ///     Slots
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct ConcurrentQueueSegmentSlots2<T> 
        {
            private ConcurrentQueueSegmentSlot<T> _element0;
            private ConcurrentQueueSegmentSlot<T> _element1;
        }
#endif

        [StructLayout(LayoutKind.Sequential)]
        public struct ConcurrentQueueSegmentSlot<T>
        {
            /// <summary>
            ///     Item
            /// </summary>
            public T Item;

            /// <summary>
            ///     Sequence number
            /// </summary>
            public int SequenceNumber;
        }
    }
}