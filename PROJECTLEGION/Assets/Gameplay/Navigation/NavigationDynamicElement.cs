using System;
using UnityEngine;

namespace ProjectLegion.Gameplay
{
    [DisallowMultipleComponent]
    public sealed class NavigationDynamicElement : MonoBehaviour
    {
        [SerializeField, Min(1)] private int _owner = 1;
        [SerializeField] private Collider[] _colliders = Array.Empty<Collider>();
        [SerializeField] private LayerNavigationLink[] _links = Array.Empty<LayerNavigationLink>();
        internal int Owner => _owner;
        internal Collider[] Colliders => _colliders;
        internal LayerNavigationLink[] Links => _links;
    }
}
