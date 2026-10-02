using System;
using System.Collections;
using System.Collections.Generic;

namespace RvtMcp.Plugin.Views.Toast
{
    /// <summary>
    /// Append-only, card-scoped summaries. Snapshots capture a count and an immutable
    /// block table rather than copying the entire log on every counter update.
    /// Written slots never change; new blocks publish a new table. Call under the aggregator lock.
    /// </summary>
    internal sealed class ToastActivityLog
    {
        private const int BlockSize = 64;
        private static readonly ToastActivityEntry[][] EmptyBlocks = new ToastActivityEntry[0][];
        private static readonly IReadOnlyList<ToastActivityEntry> Empty = new SnapshotView(EmptyBlocks, 0);
        private ToastActivityEntry[][] _blocks = EmptyBlocks;
        private int _count;

        internal void Add(ToastActivityEntry entry)
        {
            if (_count % BlockSize == 0)
            {
                var blocks = new ToastActivityEntry[_blocks.Length + 1][];
                Array.Copy(_blocks, blocks, _blocks.Length);
                blocks[blocks.Length - 1] = new ToastActivityEntry[BlockSize];
                _blocks = blocks;
            }
            _blocks[_count / BlockSize][_count % BlockSize] = entry;
            _count++;
        }

        internal IReadOnlyList<ToastActivityEntry> Snapshot() => new SnapshotView(_blocks, _count);

        internal void Clear()
        {
            // Never clear shared slots: a preview may still be reading an older snapshot.
            _blocks = EmptyBlocks;
            _count = 0;
        }

        internal static IReadOnlyList<ToastActivityEntry> Freeze(IReadOnlyList<ToastActivityEntry> entries)
        {
            if (entries == null || entries.Count == 0) return Empty;
            if (entries is SnapshotView) return entries;
            // External constructor callers may supply a mutable list or array.
            return Array.AsReadOnly(new List<ToastActivityEntry>(entries).ToArray());
        }

        // IList lets WPF use indexed access without eagerly enumerating/copying the log.
        private sealed class SnapshotView : IReadOnlyList<ToastActivityEntry>, IList
        {
            private readonly ToastActivityEntry[][] _blocks;
            internal SnapshotView(ToastActivityEntry[][] blocks, int count)
            {
                _blocks = blocks;
                Count = count;
            }
            public int Count { get; }
            public ToastActivityEntry this[int index]
            {
                get
                {
                    if (index < 0 || index >= Count) throw new ArgumentOutOfRangeException(nameof(index));
                    return _blocks[index / BlockSize][index % BlockSize];
                }
            }
            bool IList.IsReadOnly => true;
            bool IList.IsFixedSize => true;
            bool ICollection.IsSynchronized => false;
            object ICollection.SyncRoot => this;
            object IList.this[int index] { get => this[index]; set => throw new NotSupportedException(); }
            int IList.Add(object value) => throw new NotSupportedException();
            void IList.Clear() => throw new NotSupportedException();
            void IList.Insert(int index, object value) => throw new NotSupportedException();
            void IList.Remove(object value) => throw new NotSupportedException();
            void IList.RemoveAt(int index) => throw new NotSupportedException();
            bool IList.Contains(object value) => ((IList)this).IndexOf(value) >= 0;
            int IList.IndexOf(object value)
            {
                for (var i = 0; i < Count; i++) if (ReferenceEquals(this[i], value)) return i;
                return -1;
            }
            void ICollection.CopyTo(Array array, int index)
            {
                if (array == null) throw new ArgumentNullException(nameof(array));
                if (array.Rank != 1 || array.GetLowerBound(0) != 0) throw new ArgumentException("Expected a zero-based vector.", nameof(array));
                if (index < 0) throw new ArgumentOutOfRangeException(nameof(index));
                if (index > array.Length - Count) throw new ArgumentException("Insufficient destination space.", nameof(array));
                for (var i = 0; i < Count; i++) array.SetValue(this[i], index + i);
            }
            public IEnumerator<ToastActivityEntry> GetEnumerator()
            {
                for (var i = 0; i < Count; i++) yield return this[i];
            }
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }
    }
}
