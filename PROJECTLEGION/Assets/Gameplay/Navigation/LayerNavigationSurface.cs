using UnityEngine;

namespace ProjectLegion.Gameplay
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Collider))]
    public sealed class LayerNavigationSurface : MonoBehaviour
    {
        [SerializeField, Min(1)] private int _stableKey = 1;
        [SerializeField, Range(1, 64)] private float _cost = 1;
        internal int Key => _stableKey;
        internal float Cost => NavigationGrid.ValidateCost(_cost);
    }
}
