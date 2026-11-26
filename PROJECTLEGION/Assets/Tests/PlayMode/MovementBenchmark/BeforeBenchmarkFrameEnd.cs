using System;
using UnityEngine;

namespace ProjectLegion.MovementBenchmark.Tests
{
    [DefaultExecutionOrder(0)]
    public sealed class BeforeBenchmarkFrameEnd : MonoBehaviour
    {
        internal Action Callback;

        private void Update()
        {
            Callback?.Invoke();
        }
    }
}
