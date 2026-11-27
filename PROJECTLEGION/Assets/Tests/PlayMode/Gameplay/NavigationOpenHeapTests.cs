using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;

namespace ProjectLegion.Gameplay.Tests
{
    public sealed class NavigationOpenHeapTests
    {
        [Test]
        public void DecreaseAndTiesRespectBothPrioritiesAndNodeId()
        {
            using var entries = new NativeArray<NavigationHeapEntry>(5, Allocator.Temp);
            using var positions = new NativeArray<int>(5, Allocator.Temp);
            var heap = new NavigationOpenHeap(entries, positions);
            heap.Push(new NavigationHeapEntry(1, 6, 0));
            heap.Push(new NavigationHeapEntry(2, 4, 1));
            heap.Push(new NavigationHeapEntry(3, 5, 0));
            heap.Push(new NavigationHeapEntry(0, 5, 0));
            heap.DecreaseKey(new NavigationHeapEntry(1, 2, 0));
            foreach (int node in new[] { 1, 0, 3, 2 })
            {
                Assert.That(heap.Pop().Node, Is.EqualTo(node));
                Assert.That(positions[node], Is.EqualTo(-1));
                for (int i = 0; i < heap.Count; i++)
                    Assert.That(positions[entries[i].Node], Is.EqualTo(i));
            }
            Assert.That(heap.Count, Is.Zero);
        }

        [Test]
        public void SeededOperationsMatchIndependentSortedOracle()
        {
            const int capacity = 32;
            using var entries = new NativeArray<NavigationHeapEntry>(capacity, Allocator.Temp);
            using var positions = new NativeArray<int>(capacity, Allocator.Temp);
            var heap = new NavigationOpenHeap(entries, positions);
            var random = new Random(8217);
            var live = new Dictionary<int, (int f, int h)>();
            var used = new bool[capacity];

            for (int step = 0; step < 2500; step++)
            {
                int node = random.Next(capacity);
                if (live.TryGetValue(node, out var old))
                {
                    int f = old.f - random.Next(1, 30);
                    live[node] = (f, old.h);
                    heap.DecreaseKey(new NavigationHeapEntry(node, f - old.h, old.h));
                }
                else if (live.Count == 0 || random.Next(2) == 0)
                {
                    int f = random.Next(8), h = random.Next(4);
                    live.Add(node, (f, h));
                    used[node] = true;
                    heap.Push(new NavigationHeapEntry(node, f - h, h));
                }
                else
                {
                    var sorted = new List<int>(live.Keys);
                    sorted.Sort((a, b) => (live[a].f, live[a].h, a).CompareTo((live[b].f, live[b].h, b)));
                    int first = heap.Pop().Node;
                    Assert.That(first, Is.EqualTo(sorted[0]));
                    live.Remove(first);
                }

                Assert.That(heap.Count, Is.EqualTo(live.Count));
                for (int i = 0; i < capacity; i++)
                {
                    if (live.ContainsKey(i))
                    {
                        Assert.That(positions[i], Is.InRange(0, heap.Count - 1));
                        Assert.That(entries[positions[i]].Node, Is.EqualTo(i));
                    }
                    else if (used[i])
                        Assert.That(positions[i], Is.EqualTo(-1));
                }
            }
        }
    }
}
