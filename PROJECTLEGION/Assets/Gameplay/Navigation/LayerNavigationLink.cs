using UnityEngine;

namespace ProjectLegion.Gameplay
{
    [DisallowMultipleComponent]
    public sealed class LayerNavigationLink : MonoBehaviour
    {
        [SerializeField, Min(1)] private int _stableKey = 1;
        [SerializeField] private Transform _source, _destination;
        [SerializeField] private LayerConnectionType _type = LayerConnectionType.Nonlocal;
        [SerializeField, Min(0.001f)] private float _cost = 1, _returnCost = 1;
        [SerializeField, Min(0)] private int _owner;
        [SerializeField] private bool _bidirectional;

        internal int Key => _stableKey;
        internal Transform Source => _source;
        internal Transform Destination => _destination;
        internal LayerConnectionType Type => _type;
        internal float Cost => _cost;
        internal float ReturnCost => _returnCost;
        internal int Owner => _owner;
        internal bool Bidirectional => _bidirectional;
    }
}
