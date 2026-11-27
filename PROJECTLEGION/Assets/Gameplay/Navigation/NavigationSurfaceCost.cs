using UnityEngine;

namespace ProjectLegion.Gameplay
{
    [DisallowMultipleComponent]
    public sealed class NavigationSurfaceCost : MonoBehaviour
    {
        [SerializeField, Range(NavigationGrid.DefaultTraversalCost, NavigationGrid.MaxTraversalCost)]
        private float _traversalCost = NavigationGrid.DefaultTraversalCost;

        public float TraversalCost
        {
            get => NavigationGrid.ValidateCost(_traversalCost);
            set => _traversalCost = NavigationGrid.ValidateCost(value);
        }
    }
}
