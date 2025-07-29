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
        ///     ConcurrentQueue segment
        /// </summary>
        /// <typeparam name="T">Type</typeparam>
        [StructLayout(LayoutKind.Sequential, Pack = CACHE_LINE_SIZE)]
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
                for (var i = 0; i < SLOTS_LENGTH; ++i)
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
                            Volatile.Write(ref Unsafe.Add(ref slot, (nint)slotsIndex).SequenceNumber, currentHead + SLOTS_LENGTH);
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
        public const int SLOTS_LENGTH = 1024;

        /// <summary>
        ///     Slots mask
        /// </summary>
        public const int SLOTS_MASK = SLOTS_LENGTH - 1;

        /// <summary>
        ///     Freeze offset
        /// </summary>
        public const int FREEZE_OFFSET = SLOTS_LENGTH * 2;

        /// <summary>
        ///     Catch line size
        /// </summary>
        public const int CACHE_LINE_SIZE = 128;
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
        [InlineArray(SLOTS_LENGTH)]
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