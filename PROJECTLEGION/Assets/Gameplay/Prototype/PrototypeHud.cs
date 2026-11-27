using System.Globalization;
using System.Text;
using Unity.Entities;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

namespace ProjectLegion.Gameplay
{
    internal static class PrototypeInputGate
    {
        internal static RectTransform Panel, Launcher;
        internal static bool ResetPending;
        internal static bool PointerHeldByUI;
        internal static bool PointerBlocked(Vector2 position) => ResetPending || PointerHeldByUI ||
            IsOver(Panel, position) || IsOver(Launcher, position);
        internal static bool KeyboardBlocked => ResetPending ||
            (EventSystem.current != null && EventSystem.current.currentSelectedGameObject != null);
        private static bool IsOver(RectTransform rect, Vector2 position) =>
            rect != null && rect.gameObject.activeInHierarchy &&
            RectTransformUtility.RectangleContainsScreenPoint(rect, position);
    }

    [DisallowMultipleComponent]
    [DefaultExecutionOrder(80)]
    public sealed class PrototypeHud : MonoBehaviour
    {
        private static readonly int[] Presets = { 100, 1000, 5000, 10000 };
        private static readonly string[] PresetLabels = { "100", "1,000", "5,000", "10,000" };
        private static readonly Color Background = new Color32(13, 23, 29, 246);
        private static readonly Color Surface = new Color32(32, 51, 59, 255);
        private static readonly Color Accent = new Color32(90, 220, 180, 255);
        [SerializeField] private RtsMoveCommandInput _moveInput;
        [SerializeField] private RtsSelectionController _selectionInput;
        [SerializeField, Min(0.1f)] private float _statisticsInterval = 0.25f;
        private readonly StringBuilder _format = new StringBuilder(200);
        private readonly Image[] _presets = new Image[4];
        private RectTransform _hudCanvas, _controlCanvas;
        private Text _statistics, _launcherLabel, _status;
        private Button _reset;
        private Font _font;
        private World _world;
        private UnitSpawnSystem _spawner;
        private PrototypeStatisticsSystem _stats;
        private float _elapsed;
        private int _frames;
        private int _preset = 1;
        private bool _shown = true;
        private uint _generation;

        private void Awake()
        {
            _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (EventSystem.current == null)
            {
                var eventObject = new GameObject("Prototype EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));
                eventObject.transform.SetParent(transform, false);
            }

            _hudCanvas = MakeCanvas("Prototype Statistics", 20, false);
            var hud = Place("Statistics", _hudCanvas, 16, 16, 290, 128);
            MakeImage(hud, Background, false);
            MakeText(hud, "SIMULATION", 14, Accent, 14, 9, 250, 22);
            _statistics = MakeText(hud, "Sampling…", 15, Color.white, 14, 33, 262, 81);

            _controlCanvas = MakeCanvas("Prototype Controls", 21, true);
            var panel = Place("Control Panel", _controlCanvas, 16, 154, 290, 274);
            PrototypeInputGate.Panel = panel;
            MakeImage(panel, Background, true);
            MakeText(panel, "RTS PROTOTYPE", 15, Accent, 14, 8, 250, 20);
            MakeText(panel, "Unit count", 14, Color.white, 14, 35, 250, 20);
            for (int i = 0; i < Presets.Length; i++)
            {
                int choice = i;
                _presets[i] = MakeButton(panel, PresetLabels[i], 14 + i * 66, 60, 62, 28, () => SetPreset(choice)).image;
            }
            SetPreset(_preset);
            _reset = MakeButton(panel, "Reset simulation", 14, 96, 262, 31, RequestReset);
            _reset.interactable = false;
            _status = MakeText(panel, "Loading units…", 13, Color.white, 14, 131, 262, 20);
            AddToggle(panel, "Local avoidance", 154, 0);
            AddToggle(panel, "Overlap resolution", 182, 1);
            AddToggle(panel, "Stuck detection", 210, 2);
            AddToggle(panel, "Arrival management", 238, 3);

            var launcher = MakeCanvas("Prototype Interface Toggle", 22, true);
            var button = MakeButton(launcher, "Hide interface", 16, 438, 140, 31, () => SetShown(!_shown));
            PrototypeInputGate.Launcher = (RectTransform)button.transform;
            _launcherLabel = button.GetComponentInChildren<Text>();
            if (PrototypePlayerMeasurements.OutputPath() is { } output)
                StartCoroutine(PrototypePlayerMeasurements.Run(this, output));
        }

        private void AddToggle(RectTransform panel, string label, int y, int index)
        {
            var row = Place(label, panel, 14, y, 262, 24);
            MakeImage(row, Color.clear, true);
            var toggle = row.gameObject.AddComponent<Toggle>();
            var frame = MakeImage(Place("Box", row, 0, 2, 20, 20), Surface, false);
            toggle.targetGraphic = frame;
            toggle.graphic = MakeImage(Place("Check", (RectTransform)frame.transform, 4, 4, 12, 12), Accent, false);
            MakeText(row, label, 14, Color.white, 28, 0, 224, 24);
            toggle.isOn = _moveInput == null || _moveInput.FeatureEnabled(index);
            toggle.onValueChanged.AddListener(enabled => _moveInput?.SetFeature(index, enabled));
        }

        internal void SetShown(bool shown)
        {
            _shown = shown;
            _elapsed = 0f;
            _frames = 0;
            _selectionInput?.CancelGesture();
            _moveInput?.CancelPending();
            EventSystem.current?.SetSelectedGameObject(null);
            _hudCanvas.gameObject.SetActive(shown);
            _controlCanvas.gameObject.SetActive(shown);
            _launcherLabel.text = shown ? "Hide interface" : "Show interface";
            PrototypeInputGate.Launcher.anchoredPosition = new Vector2(16, shown ? -438 : -16);
            if (_stats != null)
                _stats.Collect = shown;
        }

        private void Update()
        {
            var world = World.DefaultGameObjectInjectionWorld;
            if (world != _world)
            {
                _world = world;
                _spawner = world is { IsCreated: true } ? world.GetExistingSystemManaged<UnitSpawnSystem>() : null;
                _stats = world is { IsCreated: true } ? world.GetExistingSystemManaged<PrototypeStatisticsSystem>() : null;
                if (_stats != null)
                {
                    _stats.Interval = _statisticsInterval;
                    _stats.Collect = _shown;
                }
            }

            if (Mouse.current is { } mouse)
            {
                if (mouse.leftButton.wasPressedThisFrame || mouse.rightButton.wasPressedThisFrame)
                    PrototypeInputGate.PointerHeldByUI = PrototypeInputGate.PointerBlocked(mouse.position.ReadValue());
                if (!mouse.leftButton.isPressed && !mouse.rightButton.isPressed)
                    PrototypeInputGate.PointerHeldByUI = false;
                if (mouse.leftButton.wasPressedThisFrame && !PrototypeInputGate.PointerHeldByUI)
                    EventSystem.current?.SetSelectedGameObject(null);
            }

            PrototypeInputGate.ResetPending = _spawner is { ResetPending: true };
            bool ready = _spawner is { CanReset: true } && !PrototypeInputGate.ResetPending;
            if (_reset.interactable != ready)
            {
                _reset.interactable = ready;
                _status.text = ready ? "Ready" : "Resetting simulation…";
            }
            if (_spawner != null && _generation != _spawner.Generation)
            {
                _generation = _spawner.Generation;
                _statistics.text = "Sampling…";
            }
            if (!_shown)
                return;

            _elapsed += Time.unscaledDeltaTime;
            _frames++;
            if (_elapsed < _statisticsInterval)
                return;
            _format.Clear();
            AppendNumber(_frames / _elapsed, "F0").Append(" FPS    ");
            AppendNumber(1000f * _elapsed / _frames, "F2").Append(" ms");
            if (!PrototypeInputGate.ResetPending && _stats != null && _stats.TryGet(_generation, out var counts))
            {
                _format.Append("\nUnits ").Append(counts.Total).Append("    Selected ").Append(counts.Selected);
                _format.Append("\nMoving ").Append(counts.Moving).Append("    Idle ").Append(counts.Idle);
                _format.Append("\nWaiting ").Append(counts.Waiting).Append("    Retrying ").Append(counts.Retrying);
            }
            _statistics.text = _format.ToString();
            _elapsed = 0;
            _frames = 0;
        }

        private StringBuilder AppendNumber(float value, string format)
        {
            System.Span<char> buffer = stackalloc char[32];
            if (value.TryFormat(buffer, out int length, format, CultureInfo.InvariantCulture))
                _format.Append(buffer.Slice(0, length));
            return _format;
        }

        private void RequestReset()
        {
            if (_spawner?.RequestReset(Presets[_preset]) != true)
                return;
            PrototypeInputGate.ResetPending = true;
            _selectionInput?.CancelGesture();
            _moveInput?.CancelPending();
            _reset.interactable = false;
            _status.text = "Resetting simulation…";
            _statistics.text = "Sampling…";
        }

        private void SetPreset(int index)
        {
            _preset = index;
            for (int i = 0; i < _presets.Length; i++)
                _presets[i].color = i == index ? new Color32(29, 109, 91, 255) : Surface;
        }

        private void OnDestroy()
        {
            if (_world is { IsCreated: true } && _stats != null)
                _stats.Collect = false;
            PrototypeInputGate.Panel = PrototypeInputGate.Launcher = null;
            PrototypeInputGate.ResetPending = false;
            PrototypeInputGate.PointerHeldByUI = false;
        }

        private RectTransform MakeCanvas(string name, int sortingOrder, bool interactive)
        {
            var node = new GameObject(name, typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler));
            node.transform.SetParent(transform, false);
            var canvas = node.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = sortingOrder;
            var scaler = node.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1440, 900);
            if (interactive)
                node.AddComponent<GraphicRaycaster>();
            return (RectTransform)node.transform;
        }

        private static RectTransform Place(string name, RectTransform parent, int x, int y, int w, int h)
        {
            var rect = (RectTransform)new GameObject(name, typeof(RectTransform)).transform;
            rect.SetParent(parent, false);
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0, 1);
            rect.anchoredPosition = new Vector2(x, -y);
            rect.sizeDelta = new Vector2(w, h);
            return rect;
        }

        private static Image MakeImage(RectTransform rect, Color color, bool receivesClicks)
        {
            var image = rect.gameObject.AddComponent<Image>();
            image.color = color;
            image.raycastTarget = receivesClicks;
            return image;
        }

        private Text MakeText(RectTransform parent, string value, int size, Color color, int x, int y, int w, int h)
        {
            var text = Place(value, parent, x, y, w, h).gameObject.AddComponent<Text>();
            text.font = _font;
            text.fontSize = size;
            text.color = color;
            text.raycastTarget = false;
            text.text = value;
            return text;
        }

        private Button MakeButton(RectTransform parent, string label, int x, int y, int w, int h, UnityEngine.Events.UnityAction action)
        {
            var rect = Place(label, parent, x, y, w, h);
            var image = MakeImage(rect, Surface, true);
            var button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            button.onClick.AddListener(action);
            var text = MakeText(rect, label, 14, Color.white, 0, 0, w, h);
            text.alignment = TextAnchor.MiddleCenter;
            return button;
        }
    }
}
