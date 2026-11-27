#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace ProjectLegion.Gameplay.Tests
{
    public sealed class RtsCameraTests
    {
        [UnityTest]
        public IEnumerator PrototypeRigPansInHeadingAndClampsZoomAndMapBounds()
        {
            Scene previous = SceneManager.GetActiveScene();
            Scene prototype = EditorSceneManager.LoadSceneInPlayMode(
                "Assets/Gameplay/Scenes/RtsPrototype.unity", new LoadSceneParameters(LoadSceneMode.Additive));
            try
            {
                yield return null;
                var controller = GameObject.Find("RTS Camera Rig").GetComponent<RtsCameraController>();
                Camera camera = controller.GetComponentInChildren<Camera>();
                Assert.That(controller.enabled, Is.True);
                Assert.That(camera.orthographic, Is.False);
                Assert.That(camera.transform.parent.name, Is.EqualTo("Pitch Pivot"));
                Assert.That(controller.transform.position.y, Is.Zero);
                controller.enabled = false;

                Vector3 start = controller.transform.position;
                Advance(controller, Vector2.zero, 1f, false, 60, 60);
                Advance(controller, Vector2.zero, 0f, false, 60, 60);
                Assert.That(controller.transform.eulerAngles.y, Is.EqualTo(105f).Within(0.02f));
                Vector3 heading = controller.transform.forward;
                Advance(controller, Vector2.up, 0f, false, 60, 60);
                Advance(controller, Vector2.zero, 0f, false, 60, 60);
                Vector3 displacement = controller.transform.position - start;
                Assert.That(Vector3.Dot(displacement.normalized, heading), Is.GreaterThan(0.999f));
                Assert.That(displacement.magnitude, Is.EqualTo(22f).Within(0.02f));

                float previousDistance = -camera.transform.localPosition.z;
                controller.Step(Vector2.zero, 0f, 1f, false, 1f / 60f);
                Assert.That(-camera.transform.localPosition.z, Is.GreaterThan(previousDistance - 4f).And.LessThan(previousDistance));
                Advance(controller, Vector2.zero, 0f, false, 120, 120);
                Assert.That(-camera.transform.localPosition.z, Is.EqualTo(previousDistance - 4f).Within(0.02f));
                controller.Step(Vector2.zero, 0f, 100f, false, 1f);
                Assert.That(-camera.transform.localPosition.z, Is.EqualTo(12f).Within(0.02f));
                controller.Step(Vector2.zero, 0f, -100f, false, 1f);
                Assert.That(-camera.transform.localPosition.z, Is.EqualTo(85f).Within(0.02f));

                Advance(controller, Vector2.up, 0f, true, 120, 120);
                Advance(controller, Vector2.up, 0f, true, 120, 120);
                Assert.That(controller.transform.position.x, Is.EqualTo(60f).Within(0.02f));
                Assert.That(controller.transform.position.z, Is.InRange(-45f, 45f));
                Assert.That(controller.transform.position.y, Is.Zero);
            }
            finally
            {
                if (previous.IsValid() && previous.isLoaded)
                    SceneManager.SetActiveScene(previous);
                SceneManager.UnloadSceneAsync(prototype);
            }
        }

        [UnityTest]
        public IEnumerator BoostCoversSameDistanceAtDifferentStepRates()
        {
            var root = new GameObject("Camera test rig");
            var pitch = new GameObject("Pitch Pivot").transform;
            pitch.SetParent(root.transform, false);
            var view = new GameObject("Camera");
            view.transform.SetParent(pitch, false);
            view.transform.localPosition = Vector3.back * 40f;
            Camera camera = view.AddComponent<Camera>();
            camera.enabled = false;
            root.SetActive(false);

            try
            {
                var controller = root.AddComponent<RtsCameraController>();
                var options = new UnityEditor.SerializedObject(controller);
                options.FindProperty("_pitchPivot").objectReferenceValue = pitch;
                options.FindProperty("_camera").objectReferenceValue = camera;
                options.ApplyModifiedPropertiesWithoutUndo();

                root.SetActive(true);
                controller.enabled = false;
                Advance(controller, Vector2.up, 0f, true, 15, 30);
                Advance(controller, Vector2.zero, 0f, false, 30, 30);
                float slowRateDistance = root.transform.position.z;

                root.SetActive(false);
                root.transform.position = Vector3.zero;
                controller.enabled = true;
                root.SetActive(true);
                controller.enabled = false;
                Advance(controller, Vector2.up, 0f, true, 60, 120);
                Advance(controller, Vector2.zero, 0f, false, 120, 120);
                Assert.That(slowRateDistance, Is.EqualTo(27.5f).Within(0.02f));
                Assert.That(root.transform.position.z, Is.EqualTo(slowRateDistance).Within(0.02f));
            }
            finally
            {
                Object.Destroy(root);
            }
            yield return null;
        }

        private static void Advance(RtsCameraController controller, Vector2 move, float yaw, bool boost,
            int frames, int rate)
        {
            for (int frame = 0; frame < frames; frame++)
                controller.Step(move, yaw, 0f, boost, 1f / rate);
        }
    }
}
#endif
