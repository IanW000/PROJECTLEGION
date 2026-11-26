using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace ProjectLegion.RenderingBenchmark
{
    internal sealed class MeshRendererBackend : IDisposable
    {
        private GameObject _root;
        private Mesh _mesh;
        private RenderTexture _target;
        internal Camera Camera { get; }

        internal MeshRendererBackend(Material material, int units, bool usePlayerBackbuffer)
        {
            if (material == null || !material.shader.isSupported || material.enableInstancing)
                throw new InvalidOperationException("An available, non-instanced benchmark material is required.");

            _root = new GameObject("Rendering benchmark workload");
            _root.SetActive(false);
            try
            {
                _mesh = CreateMesh();
                var cameraObject = new GameObject("Benchmark camera");
                cameraObject.transform.SetParent(_root.transform, false);
                cameraObject.transform.position = new Vector3(0, 0, -10);
                Camera = cameraObject.AddComponent<Camera>();
                Camera.orthographic = true;
                Camera.orthographicSize = UnitLayout.CameraSize;
                Camera.aspect = UnitLayout.Aspect;
                Camera.nearClipPlane = 0.1f;
                Camera.farClipPlane = 30;
                Camera.clearFlags = CameraClearFlags.SolidColor;
                Camera.backgroundColor = new Color(0.03f, 0.04f, 0.06f);
                Camera.allowHDR = false;
                Camera.allowMSAA = false;
                Camera.allowDynamicResolution = false;
                Camera.useOcclusionCulling = false;
                if (!usePlayerBackbuffer)
                {
                    // Editor inspection is independent of the Game View size.
                    _target = new RenderTexture(RenderBenchmarkConfig.Width, RenderBenchmarkConfig.Height, 24,
                        RenderTextureFormat.ARGB32) { name = "Benchmark 1920x1080", antiAliasing = 1 };
                    if (!_target.Create())
                        throw new InvalidOperationException("The fixed benchmark render target could not be created.");
                    Camera.targetTexture = _target;
                }
                Camera.GetUniversalAdditionalCameraData().renderPostProcessing = false;

                var lightObject = new GameObject("Benchmark directional light");
                lightObject.transform.SetParent(_root.transform, false);
                lightObject.transform.rotation = Quaternion.Euler(20, 25, 0);
                var light = lightObject.AddComponent<Light>();
                light.type = LightType.Directional;
                light.shadows = LightShadows.None;
                light.intensity = 1;

                for (int id = 0; id < units; id++)
                {
                    var unit = new GameObject("Benchmark unit");
                    unit.transform.SetParent(_root.transform, false);
                    unit.transform.localPosition = UnitLayout.Position(id);
                    unit.AddComponent<MeshFilter>().sharedMesh = _mesh;
                    var renderer = unit.AddComponent<MeshRenderer>();
                    renderer.sharedMaterial = material;
                    renderer.shadowCastingMode = ShadowCastingMode.Off;
                    renderer.receiveShadows = false;
                    renderer.lightProbeUsage = LightProbeUsage.Off;
                    renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
                    renderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
                }
                _root.SetActive(true);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        internal static Mesh CreateMesh()
        {
            float r = UnitLayout.MeshSize * 0.5f;
            var mesh = new Mesh { name = "Benchmark quad" };
            mesh.vertices = new[] { new Vector3(-r,-r,0), new Vector3(-r,r,0), new Vector3(r,r,0), new Vector3(r,-r,0) };
            mesh.normals = new[] { Vector3.back, Vector3.back, Vector3.back, Vector3.back };
            mesh.uv = new[] { Vector2.zero, Vector2.up, Vector2.one, Vector2.right };
            mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            mesh.RecalculateBounds();
            return mesh;
        }

        public void Dispose()
        {
            if (_root != null)
            {
                _root.SetActive(false);
                UnityEngine.Object.Destroy(_root);
                _root = null;
            }
            if (_mesh != null)
            {
                UnityEngine.Object.Destroy(_mesh);
                _mesh = null;
            }
            if (_target != null)
            {
                if (Camera != null)
                    Camera.targetTexture = null;
                // Deferred destruction releases the target after this frame's submissions.
                UnityEngine.Object.Destroy(_target);
                _target = null;
            }
        }
    }
}
