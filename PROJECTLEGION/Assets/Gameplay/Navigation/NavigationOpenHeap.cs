using Unity.Collections;

namespace ProjectLegion.Gameplay
{
    internal struct NavigationHeapEntry
    {
        internal int Node;
        internal double F, H;

        internal NavigationHeapEntry(int node, double g, double h)
        {
            Node = node;
            F = g + h;
            H = h;
        }

        internal bool ComesBefore(NavigationHeapEntry other) =>
            F < other.F || (F == other.F && (H < other.H || (H == other.H && Node < other.Node)));
    }

    internal struct NavigationOpenHeap
    {
        private NativeArray<NavigationHeapEntry> _entries;
        private NativeArray<int> _positions;
        internal int Count { get; private set; }

        internal NavigationOpenHeap(NativeArray<NavigationHeapEntry> entries, NativeArray<int> positions)
        {
            _entries = entries;
            _positions = positions;
            Count = 0;
        }

        internal void Push(NavigationHeapEntry entry)
        {
            int index = Count++;
            MoveUp(index, entry);
        }

        internal void DecreaseKey(NavigationHeapEntry entry) => MoveUp(_positions[entry.Node], entry);

        internal NavigationHeapEntry Pop()
        {
            var first = _entries[0];
            var last = _entries[--Count];
            _positions[first.Node] = -1;
            if (Count == 0)
                return first;

            int index = 0;
            while (index < Count / 2)
            {
                int child = index * 2 + 1;
                if (child + 1 < Count && _entries[child + 1].ComesBefore(_entries[child]))
                    child++;
                if (!_entries[child].ComesBefore(last))
                    break;
                Set(index, _entries[child]);
                index = child;
            }
            Set(index, last);
            return first;
        }

        private void MoveUp(int index, NavigationHeapEntry entry)
        {
            while (index > 0)
            {
                int parent = (index - 1) / 2;
                if (!entry.ComesBefore(_entries[parent]))
                    break;
                Set(index, _entries[parent]);
                index = parent;
            }
            Set(index, entry);
        }

        private void Set(int index, NavigationHeapEntry entry)
        {
            _entries[index] = entry;
            _positions[entry.Node] = index;
        }
    }
}
