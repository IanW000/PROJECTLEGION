using Unity.Entities;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ProjectLegion.Gameplay
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Camera))]
    [DefaultExecutionOrder(110)]
    public sealed class RtsMoveCommandInput : MonoBehaviour
    {
        [SerializeField] private MeshFilter _ground;
        [SerializeField, Min(RectangularFormation.MinimumSpacing)]
        private float _formationSpacing = RectangularFormation.DefaultSpacing;
        [SerializeField] private LocalAvoidanceSettings _localAvoidance = LocalAvoidanceSettings.Default;
        [SerializeField] private OverlapResolutionSettings _overlapResolution = OverlapResolutionSettings.Default;
        [SerializeField] private StuckDetectionSettings _stuckDetection = StuckDetectionSettings.Default;
        [SerializeField] private ArrivalManagementSettings _arrivalManagement = ArrivalManagementSettings.Default;
        private Camera _camera;
        private InputAction _move;
        private Vector2 _pointer;
        private bool _pending;
        private bool _avoidanceSettingsDirty = true;
        private World _configuredWorld;

        private void OnValidate() => _avoidanceSettingsDirty = true;

        private void Awake()
        {
            _camera = GetComponent<Camera>();
            _move = new InputAction("Move selected units", InputActionType.Button, "<Mouse>/rightButton");
            _move.performed += OnMovePressed;
        }

        private void OnEnable() => _move.Enable();

        private void OnDisable()
        {
            _move.Disable();
            _pending = false;
        }

        private void OnDestroy() => _move.Dispose();

        private void OnApplicationFocus(bool focused)
        {
            if (!focused)
                _pending = false;
        }

        private void OnMovePressed(InputAction.CallbackContext context)
        {
            if (Application.isFocused && Cursor.lockState != CursorLockMode.Locked &&
                context.control?.device is Mouse mouse)
            {
                _pointer = mouse.position.ReadValue();
                _pending = _camera.pixelRect.Contains(_pointer);
            }
        }

        private void LateUpdate()
        {
            var world = World.DefaultGameObjectInjectionWorld;
            if (world is { IsCreated: true } && (_avoidanceSettingsDirty || world != _configuredWorld) &&
                world.GetExistingSystemManaged<UnitMovementSystem>() is { } movement)
            {
                movement.ConfigureAvoidance(_localAvoidance);
                world.GetExistingSystemManaged<UnitOverlapResolutionSystem>()?.Configure(_overlapResolution);
                world.GetExistingSystemManaged<UnitStuckDetectionSystem>()?.Configure(_stuckDetection);
                world.GetExistingSystemManaged<UnitArrivalSystem>()?.Configure(_arrivalManagement);
                _configuredWorld = world;
                _avoidanceSettingsDirty = false;
            }

            if (!_pending)
                return;

            _pending = false;
            if (!Application.isFocused || world is not { IsCreated: true })
                return;

            Ray ray = _camera.ScreenPointToRay(_pointer);
            float maxDistance = (_camera.farClipPlane - _camera.nearClipPlane) /
                Vector3.Dot(ray.direction, _camera.transform.forward);
            if (TryGetGroundPoint(_ground, ray, maxDistance, out Vector3 destination))
                world.GetExistingSystemManaged<UnitMoveCommandSystem>()?.IssueMove(
                    new Unity.Mathematics.float2(destination.x, destination.z), _formationSpacing);
        }

        internal static bool TryGetGroundPoint(MeshFilter ground, Ray ray, float maxDistance, out Vector3 point)
        {
            point = default;
            if (ground == null || !ground.gameObject.activeInHierarchy || ground.sharedMesh == null)
                return false;

            // Keep the transformed direction unnormalized so t remains a world-space ray distance.
            Matrix4x4 toLocal = ground.transform.worldToLocalMatrix;
            Vector3 origin = toLocal.MultiplyPoint3x4(ray.origin);
            Vector3 direction = toLocal.MultiplyVector(ray.direction);
            Bounds bounds = ground.sharedMesh.bounds;
            if (direction.y >= -0.00001f)
                return false;

            float distance = (bounds.center.y - origin.y) / direction.y;
            if (distance < 0f || distance > maxDistance)
                return false;

            Vector3 localPoint = origin + direction * distance;
            if (localPoint.x < bounds.min.x || localPoint.x > bounds.max.x ||
                localPoint.z < bounds.min.z || localPoint.z > bounds.max.z)
                return false;

            point = ray.GetPoint(distance);
            return true;
        }
    }
}
