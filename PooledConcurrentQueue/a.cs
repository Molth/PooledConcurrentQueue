using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

#pragma warning disable CA2208
#pragma warning disable CS0169
#pragma warning disable CS8602
#pragma warning disable CS8632
#pragma warning disable CS8762

namespace AQueue
{
    /// <summary>
    ///     ConcurrentQueue
    /// </summary>
    /// <typeparam name="T">Type</typeparam>
    [StructLayout(LayoutKind.Sequential, Pack = ConcurrentQueue.CACHE_LINE_SIZE)]
    public sealed class PooledConcurrentQueue<T>
    {
        /// <summary>
        ///     Cross segment lock
        /// </summary>
        private object _crossSegmentLock;

        /// <summary>
        ///     Segment pool
        /// </summary>
        private Stack<ConcurrentQueue.ConcurrentQueueSegmentArm64<T>> _segmentPool;

        /// <summary>
        ///     Tail
        /// </summary>
        private volatile ConcurrentQueue.ConcurrentQueueSegmentArm64<T> _tail;

        /// <summary>
        ///     Head
        /// </summary>
        private volatile ConcurrentQueue.ConcurrentQueueSegmentArm64<T> _head;

        /// <summary>
        ///     Structure
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public PooledConcurrentQueue()
        {
            _crossSegmentLock = new object();
            _segmentPool = new Stack<ConcurrentQueue.ConcurrentQueueSegmentArm64<T>>();
            var segment = new ConcurrentQueue.ConcurrentQueueSegmentArm64<T>();
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
                                        count += s.HeadAndTail.Tail - ConcurrentQueue.FREEZE_OFFSET;
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
                    segment = new ConcurrentQueue.ConcurrentQueueSegmentArm64<T>();
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
                                newTail = new ConcurrentQueue.ConcurrentQueueSegmentArm64<T>();
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
        public bool TryDequeue([NotNullWhen(true)] out T? result)
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
            if (head != tail && head != tail - ConcurrentQueue.FREEZE_OFFSET)
            {
                head &= ConcurrentQueue.SLOTS_MASK;
                tail &= ConcurrentQueue.SLOTS_MASK;
                return head < tail ? tail - head : ConcurrentQueue.SLOTS_LENGTH - head + tail;
            }

            return 0;
        }
    }
}