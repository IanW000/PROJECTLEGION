using UnityEngine;
using UnityEngine.InputSystem;

namespace ProjectLegion.Gameplay
{
    [DisallowMultipleComponent]
    public sealed class RtsCameraInput : MonoBehaviour
    {
        [SerializeField] private bool _edgeScrolling;
        [SerializeField, Min(1f)] private float _edgeSizePixels = 24f;

        private InputActionMap _actions;
        private InputAction _move;
        private InputAction _yaw;
        private InputAction _zoom;
        private InputAction _accelerate;
        private InputAction _pointerPosition;

        private void Awake()
        {
            _actions = new InputActionMap("RTS Camera");
            _move = _actions.AddAction("Pan", InputActionType.Value);
            _move.AddCompositeBinding("2DVector")
                .With("Up", "<Keyboard>/w")
                .With("Down", "<Keyboard>/s")
                .With("Left", "<Keyboard>/a")
                .With("Right", "<Keyboard>/d");

            _yaw = _actions.AddAction("Yaw", InputActionType.Value);
            _yaw.AddCompositeBinding("1DAxis")
                .With("Negative", "<Keyboard>/q")
                .With("Positive", "<Keyboard>/e");

            _zoom = _actions.AddAction("Zoom", InputActionType.PassThrough, "<Mouse>/scroll/y");
            _accelerate = _actions.AddAction("Accelerate", InputActionType.Button);
            _accelerate.AddBinding("<Keyboard>/leftShift");
            _accelerate.AddBinding("<Keyboard>/rightShift");
            _pointerPosition = _actions.AddAction("Pointer Position", InputActionType.PassThrough, "<Mouse>/position");
        }

        private void OnEnable() => _actions.Enable();

        private void OnDisable() => _actions.Disable();

        private void OnDestroy() => _actions?.Dispose();

        internal void Read(out Vector2 move, out float yaw, out float zoom, out bool accelerate)
        {
            move = Vector2.zero;
            yaw = 0f;
            zoom = 0f;
            accelerate = false;
            if (!isActiveAndEnabled || !Application.isFocused)
                return;
            if (PrototypeInputGate.ResetPending)
                return;

            if (!PrototypeInputGate.KeyboardBlocked)
            {
                move = _move.ReadValue<Vector2>();
                yaw = _yaw.ReadValue<float>();
                accelerate = _accelerate.IsPressed();
            }
            bool pointerBlocked = PrototypeInputGate.PointerBlocked(_pointerPosition.ReadValue<Vector2>());
            zoom = pointerBlocked ? 0f : _zoom.ReadValue<float>();
            if (Application.platform == RuntimePlatform.WindowsPlayer || Application.platform == RuntimePlatform.WindowsEditor)
            {
                if (InputSystem.settings.scrollDeltaBehavior == InputSettings.ScrollDeltaBehavior.KeepPlatformSpecificInputRange)
                    zoom /= 120f;
            }
            if (!pointerBlocked && _edgeScrolling && Mouse.current != null && Cursor.lockState != CursorLockMode.Locked)
                move = Vector2.ClampMagnitude(move + EdgeDirection(), 1f);
        }

        private Vector2 EdgeDirection()
        {
            Vector2 pointer = _pointerPosition.ReadValue<Vector2>();
            float width = Screen.width;
            float height = Screen.height;
            if (pointer.x < 0 || pointer.x >= width || pointer.y < 0 || pointer.y >= height)
                return Vector2.zero;

            float edge = Mathf.Min(_edgeSizePixels, Mathf.Min(width, height) * 0.5f);
            return new Vector2(
                pointer.x < edge ? -1f : pointer.x >= width - edge ? 1f : 0f,
                pointer.y < edge ? -1f : pointer.y >= height - edge ? 1f : 0f);
        }
    }
}
