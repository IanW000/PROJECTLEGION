using UnityEngine;

namespace ProjectLegion.Gameplay
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(RtsCameraInput))]
    public sealed class RtsCameraController : MonoBehaviour
    {
        [Header("Rig")]
        [SerializeField] private Transform _pitchPivot = null;
        [SerializeField] private Camera _camera = null;
        [SerializeField, Range(20f, 85f)] private float _pitch = 55f;

        [Header("Movement")]
        [SerializeField, Min(0f)] private float _moveSpeed = 22f;
        [SerializeField, Min(1f)] private float _boostMultiplier = 2.5f;
        [SerializeField, Min(0f)] private float _yawSpeed = 105f;
        [SerializeField, Min(0.01f)] private float _moveDamping = 12f;
        [SerializeField, Min(0.01f)] private float _yawDamping = 14f;

        [Header("Zoom")]
        [SerializeField, Min(0.1f)] private float _nearDistance = 12f;
        [SerializeField, Min(0.1f)] private float _farDistance = 85f;
        [SerializeField, Min(0f)] private float _zoomStep = 4f;
        [SerializeField, Min(0.01f)] private float _zoomDamping = 14f;

        [Header("Map Bounds (world XZ)")]
        [SerializeField] private Vector2 _minimumXZ = new Vector2(-60f, -45f);
        [SerializeField] private Vector2 _maximumXZ = new Vector2(60f, 45f);

        private RtsCameraInput _input;
        private Vector3 _targetPosition;
        private float _yaw;
        private float _targetYaw;
        private float _distance;
        private float _targetDistance;

        private void OnEnable()
        {
            if (_pitchPivot == null || _pitchPivot.parent != transform ||
                _camera == null || _camera.transform.parent != _pitchPivot)
            {
                Debug.LogError("Assign a Pitch Pivot and its child Camera to the RTS Camera Rig.", this);
                enabled = false;
                return;
            }

            _input = GetComponent<RtsCameraInput>();
            _targetPosition = ClampToMap(transform.position);
            transform.position = _targetPosition;
            _yaw = _targetYaw = transform.eulerAngles.y;
            _distance = _targetDistance = ClampDistance(-_camera.transform.localPosition.z);
            ApplyPose();
        }

        private void LateUpdate()
        {
            _input.Read(out Vector2 move, out float yaw, out float zoom, out bool accelerate);
            Step(move, yaw, zoom, accelerate, Time.unscaledDeltaTime);
        }

        internal void Step(Vector2 move, float yaw, float scroll, bool accelerate, float deltaTime)
        {
            _targetYaw = Mathf.Repeat(_targetYaw + yaw * _yawSpeed * deltaTime, 360f);
            _yaw = Mathf.LerpAngle(_yaw, _targetYaw, DampingFactor(_yawDamping, deltaTime));
            Quaternion heading = Quaternion.Euler(0f, _yaw, 0f);

            Vector2 direction = Vector2.ClampMagnitude(move, 1f);
            float speed = _moveSpeed * (accelerate ? _boostMultiplier : 1f);
            Vector3 displacement = heading * new Vector3(direction.x, 0f, direction.y) * (speed * deltaTime);
            _targetPosition = ClampToMap(_targetPosition + displacement);
            transform.position = ClampToMap(Vector3.Lerp(transform.position, _targetPosition,
                DampingFactor(_moveDamping, deltaTime)));

            // Scroll is a device delta, so only the smoothing uses deltaTime.
            _targetDistance = ClampDistance(_targetDistance - scroll * _zoomStep);
            _distance = ClampDistance(Mathf.Lerp(_distance, _targetDistance, DampingFactor(_zoomDamping, deltaTime)));
            ApplyPose();
        }

        private void ApplyPose()
        {
            transform.rotation = Quaternion.Euler(0f, _yaw, 0f);
            _pitchPivot.localRotation = Quaternion.Euler(_pitch, 0f, 0f);
            _camera.transform.SetLocalPositionAndRotation(Vector3.back * _distance, Quaternion.identity);
        }

        private Vector3 ClampToMap(Vector3 position)
        {
            Vector2 min = Vector2.Min(_minimumXZ, _maximumXZ);
            Vector2 max = Vector2.Max(_minimumXZ, _maximumXZ);
            position.x = Mathf.Clamp(position.x, min.x, max.x);
            position.z = Mathf.Clamp(position.z, min.y, max.y);
            return position;
        }

        private float ClampDistance(float distance)
        {
            float near = Mathf.Max(0.1f, _nearDistance);
            return Mathf.Clamp(distance, near, Mathf.Max(near, _farDistance));
        }

        private static float DampingFactor(float response, float deltaTime) =>
            1f - Mathf.Exp(-response * deltaTime);
    }
}
