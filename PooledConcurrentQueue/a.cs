using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

#pragma warning disable CA2208
#pragma warning disable CS0169
#pragma warning disable CS8602
#pragma warning disable CS8632

namespace AQueue
{
    /// <summary>
    ///     ConcurrentQueue
    /// </summary>
    /// <typeparam name="T">Type</typeparam>
    public sealed class PooledConcurrentQueue<T>
    {
        /// <summary>
        ///     Handle
        /// </summary>
        private ConcurrentQueue.UnsafeConcurrentQueueHandle _handle;

        /// <summary>
        ///     Structure
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public PooledConcurrentQueue()
        {
            if (ArchitectureHelpers.NotArm64)
                Unsafe.As<ConcurrentQueue.UnsafeConcurrentQueueHandle, ConcurrentQueue.ConcurrentQueueNotArm64<T>>(ref _handle) = new ConcurrentQueue.ConcurrentQueueNotArm64<T>();
            else
                Unsafe.As<ConcurrentQueue.UnsafeConcurrentQueueHandle, ConcurrentQueue.ConcurrentQueueArm64<T>>(ref _handle) = new ConcurrentQueue.ConcurrentQueueArm64<T>();
        }

        /// <summary>
        ///     Not arm64
        /// </summary>
        private ConcurrentQueue.ConcurrentQueueNotArm64<T> NotArm64Handle
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Unsafe.As<ConcurrentQueue.UnsafeConcurrentQueueHandle, ConcurrentQueue.ConcurrentQueueNotArm64<T>>(ref _handle);
        }

        /// <summary>
        ///     Arm64
        /// </summary>
        private ConcurrentQueue.ConcurrentQueueArm64<T> Arm64Handle
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Unsafe.As<ConcurrentQueue.UnsafeConcurrentQueueHandle, ConcurrentQueue.ConcurrentQueueArm64<T>>(ref _handle);
        }

        /// <summary>
        ///     IsEmpty
        /// </summary>
        public bool IsEmpty
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => ArchitectureHelpers.NotArm64 ? NotArm64Handle.IsEmpty : Arm64Handle.IsEmpty;
        }

        /// <summary>
        ///     Count
        /// </summary>
        public int Count
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => ArchitectureHelpers.NotArm64 ? NotArm64Handle.Count : Arm64Handle.Count;
        }

        /// <summary>
        ///     Clear
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Clear()
        {
            if (ArchitectureHelpers.NotArm64)
                NotArm64Handle.Clear();
            else
                Arm64Handle.Clear();
        }

        /// <summary>
        ///     Enqueue
        /// </summary>
        /// <param name="item">Item</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Enqueue(in T item)
        {
            if (ArchitectureHelpers.NotArm64)
                NotArm64Handle.Enqueue(item);
            else
                Arm64Handle.Enqueue(item);
        }

        /// <summary>
        ///     Try dequeue
        /// </summary>
        /// <param name="result">Item</param>
        /// <returns>Dequeued</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryDequeue([NotNullWhen(true)] out T? result) => ArchitectureHelpers.NotArm64 ? NotArm64Handle.TryDequeue(out result) : Arm64Handle.TryDequeue(out result);
    }

    /// <summary>
    ///     ConcurrentQueue
    /// </summary>
    internal static partial class ConcurrentQueue
    {
        /// <summary>
        ///     ConcurrentQueue
        /// </summary>
        public struct UnsafeConcurrentQueueHandle
        {
            /// <summary>
            ///     Cross segment lock
            /// </summary>
            private object _crossSegmentLock;

            /// <summary>
            ///     Segment pool
            /// </summary>
            private Stack<object> _segmentPool;

            /// <summary>
            ///     Tail
            /// </summary>
            private volatile object? _tail;

            /// <summary>
            ///     Head
            /// </summary>
            private volatile object? _head;
        }
    }
}