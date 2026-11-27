using System.Collections.Generic;
using Unity.Entities;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;

namespace ProjectLegion.Gameplay
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Camera))]
    [DefaultExecutionOrder(100)]
    public sealed class RtsSelectionController : MonoBehaviour
    {
        private const int RingSegments = 24;
        private const float InnerRadiusRatio = 0.79f;
        private const int MaxInstancesPerDraw = 511;
        private const float RectangleBorderPixels = 2f;

        [SerializeField, Min(1f)] private float _dragThresholdPixels = 6f;
        [SerializeField] private Color _rectangleColor = new Color(0.25f, 1f, 0.65f, 0.9f);
        [SerializeField] private Material _indicatorMaterial;
        [SerializeField, Min(0.01f)] private float _indicatorRadius = 0.33f;
        [SerializeField, Min(0f)] private float _indicatorHeight = 0.025f;

        private readonly List<Matrix4x4> _indicatorMatrices = new(1024);
        private Camera _camera;
        private World _world;
        private UnitSelectionSystem _selection;
        private Mesh _indicatorMesh;
        private InputActionMap _actions;
        private Vector2 _pressPosition;
        private Vector2 _pointerPosition;
        private Vector2 _releasePosition;
        private bool _pressed;
        private bool _dragging;
        private bool _released;
        private bool _additive;
        private bool _clearRequested;

        private void Awake()
        {
            _camera = GetComponent<Camera>();
            _actions = new InputActionMap("RTS Selection");
            var select = _actions.AddAction("Select units", InputActionType.Button, "<Mouse>/leftButton");
            select.performed += OnPressed;
            select.canceled += OnReleased;
            var clear = _actions.AddAction("Clear selection", InputActionType.Button, "<Keyboard>/escape");
            clear.performed += _ => _clearRequested = !PrototypeInputGate.KeyboardBlocked;
            if (_indicatorMaterial != null && _indicatorMaterial.enableInstancing && SystemInfo.supportsInstancing)
                _indicatorMesh = CreateIndicatorMesh();
            else
                Debug.LogWarning("RTS selection indicators require an instancing-enabled material and graphics device.", this);
        }

        private void OnEnable() => _actions.Enable();

        private void OnDisable()
        {
            _actions.Disable();
            CancelGesture();
        }

        private void OnDestroy()
        {
            _actions.Dispose();
            if (_indicatorMesh != null)
                Destroy(_indicatorMesh);
        }

        private void OnApplicationFocus(bool focused)
        {
            if (!focused)
                CancelGesture();
        }

        private void OnPressed(InputAction.CallbackContext context)
        {
            if (!Application.isFocused || Cursor.lockState == CursorLockMode.Locked ||
                context.control?.device is not Mouse mouse)
                return;

            Vector2 position = mouse.position.ReadValue();
            if (!_camera.pixelRect.Contains(position) || PrototypeInputGate.PointerBlocked(position))
                return;

            _pressPosition = position;
            _pointerPosition = position;
            _pressed = true;
            _dragging = false;
            _released = false;
        }

        private void OnReleased(InputAction.CallbackContext context)
        {
            if (!_pressed || context.control?.device is not Mouse mouse)
                return;

            _releasePosition = mouse.position.ReadValue();
            var keyboard = Keyboard.current;
            _additive = keyboard != null && (keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed);
            _released = true;
        }

        private void LateUpdate()
        {
            var world = World.DefaultGameObjectInjectionWorld;
            if (world is not { IsCreated: true })
            {
                _world = null;
                _selection = null;
                CancelGesture();
                return;
            }

            if (_world != world)
            {
                _world = world;
                _selection = world.GetOrCreateSystemManaged<UnitSelectionSystem>();
                CancelGesture();
            }

            ReadInput();
            DrawIndicators();
        }

        private void ReadInput()
        {
            if (!Application.isFocused || PrototypeInputGate.ResetPending || Cursor.lockState == CursorLockMode.Locked)
            {
                CancelGesture();
                return;
            }

            if (_clearRequested)
            {
                _selection.Clear();
                CancelGesture();
                return;
            }

            if (!_pressed)
                return;

            Rect viewport = _camera.pixelRect;
            var mouse = Mouse.current;
            if (!_released && mouse == null)
            {
                CancelGesture();
                return;
            }

            Vector2 position = _released ? _releasePosition : mouse.position.ReadValue();
            if (PrototypeInputGate.PointerBlocked(position))
            {
                CancelGesture();
                return;
            }
            _pointerPosition = Vector2.Max(viewport.min, Vector2.Min(position, viewport.max));
            float threshold = Mathf.Max(1f, _dragThresholdPixels);
            _dragging |= (_pointerPosition - _pressPosition).sqrMagnitude >= threshold * threshold;
            if (!_released)
                return;

            if (_dragging)
                _selection.SelectBox(_camera, RectangleBetween(_pressPosition, _pointerPosition), _additive);
            else if (viewport.Contains(position))
            {
                Ray ray = _camera.ScreenPointToRay(position);
                float maxDistance = (_camera.farClipPlane - _camera.nearClipPlane) /
                    Vector3.Dot(ray.direction, _camera.transform.forward);
                _selection.SelectSingle(ray, maxDistance, _additive);
            }

            CancelGesture();
        }

        internal void CancelGesture()
        {
            _pressed = false;
            _dragging = false;
            _released = false;
            _clearRequested = false;
        }

        internal static Rect RectangleBetween(Vector2 first, Vector2 second)
        {
            Vector2 min = Vector2.Min(first, second);
            Vector2 max = Vector2.Max(first, second);
            return Rect.MinMaxRect(min.x, min.y, max.x, max.y);
        }

        private void OnGUI()
        {
            if (!_dragging || Event.current.type != EventType.Repaint)
                return;

            Rect rect = RectangleBetween(_pressPosition, _pointerPosition);
            rect.y = Screen.height - rect.yMax;
            Color previous = GUI.color;
            GUI.color = new Color(_rectangleColor.r, _rectangleColor.g, _rectangleColor.b, _rectangleColor.a * 0.17f);
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = _rectangleColor;
            float thickness = RectangleBorderPixels;
            GUI.DrawTexture(new Rect(rect.x, rect.y, rect.width, thickness), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(rect.x, rect.yMax - thickness, rect.width, thickness), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(rect.x, rect.y, thickness, rect.height), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(rect.xMax - thickness, rect.y, thickness, rect.height), Texture2D.whiteTexture);
            GUI.color = previous;
        }

        private void DrawIndicators()
        {
            if (_indicatorMesh == null || _indicatorMaterial == null)
                return;

            _selection.CollectIndicatorMatrices(_indicatorMatrices, _indicatorRadius, _indicatorHeight);
            var parameters = new RenderParams(_indicatorMaterial)
            {
                camera = _camera,
                layer = gameObject.layer,
                shadowCastingMode = ShadowCastingMode.Off,
                receiveShadows = false,
                lightProbeUsage = LightProbeUsage.Off
            };
            for (int start = 0; start < _indicatorMatrices.Count; start += MaxInstancesPerDraw)
            {
                int count = Mathf.Min(MaxInstancesPerDraw, _indicatorMatrices.Count - start);
                Graphics.RenderMeshInstanced(parameters, _indicatorMesh, 0, _indicatorMatrices, count, start);
            }
        }

        private static Mesh CreateIndicatorMesh()
        {
            var vertices = new Vector3[RingSegments * 2];
            var indices = new int[RingSegments * 6];
            for (int segment = 0; segment < RingSegments; segment++)
            {
                float angle = segment * (2f * Mathf.PI / RingSegments);
                var outer = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
                int vertex = segment * 2;
                int next = (vertex + 2) % vertices.Length;
                vertices[vertex] = outer;
                vertices[vertex + 1] = outer * InnerRadiusRatio;

                int index = segment * 6;
                indices[index] = vertex;
                indices[index + 1] = vertex + 1;
                indices[index + 2] = next;
                indices[index + 3] = next;
                indices[index + 4] = vertex + 1;
                indices[index + 5] = next + 1;
            }

            var mesh = new Mesh { name = "RTS Selection Ring", vertices = vertices, triangles = indices };
            mesh.UploadMeshData(true);
            return mesh;
        }
    }
}
