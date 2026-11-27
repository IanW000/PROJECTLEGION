using System;
using Unity.Collections;

namespace ProjectLegion.Gameplay
{
    // Each chain is reserved in full before any path nodes are copied.
    internal sealed class NavigationPathResultPool : IDisposable
    {
        internal const int BlockSize = 32;
        private NativeArray<int> _nodes, _next;
        private int _free;
        internal int FreeBlocks { get; private set; }
        internal int BlockCount => _next.Length;
        internal long PayloadBytes => (long)BlockCount * (BlockSize + 1) * sizeof(int);

        internal NavigationPathResultPool(int blocks)
        {
            _nodes = new NativeArray<int>(checked(blocks * BlockSize), Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory);
            try
            {
                _next = new NativeArray<int>(blocks, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                for (int i = 0; i < blocks; i++)
                    _next[i] = i + 1 < blocks ? i + 1 : -1;
                FreeBlocks = blocks;
                _free = 0;
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        internal static int BlocksFor(int nodes) => (nodes - 1) / BlockSize + 1;

        internal bool TryReserve(int blocks, out int head)
        {
            head = -1;
            if (blocks > FreeBlocks)
                return false;
            head = _free;
            int last = head;
            for (int i = 1; i < blocks; i++)
                last = _next[last];
            _free = _next[last];
            _next[last] = -1;
            FreeBlocks -= blocks;
            return true;
        }

        internal void Release(int head)
        {
            while (head >= 0)
            {
                int next = _next[head];
                _next[head] = _free;
                _free = head;
                FreeBlocks++;
                head = next;
            }
        }

        internal int Next(int block) => _next[block];
        internal void Write(int block, int offset, int node) => _nodes[block * BlockSize + offset] = node;
        internal int Read(int block, int offset) => _nodes[block * BlockSize + offset];

        public void Dispose()
        {
            if (_nodes.IsCreated) _nodes.Dispose();
            if (_next.IsCreated) _next.Dispose();
        }
    }
}
