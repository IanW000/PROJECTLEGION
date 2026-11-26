using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.TestTools;

namespace ProjectLegion.RenderingBenchmark.Tests
{
    public sealed class RenderingBenchmarkLifecycleTests
    {
#if UNITY_EDITOR
        [UnityTest]
        public IEnumerator CancelledSamplingCanRestartAndExportFinalFrame()
        {
            const string assets = "Assets/Benchmarks/Rendering/Settings/";
            string output = System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.Guid.NewGuid().ToString("N"));
            var root = new GameObject("Rendering benchmark suite test");
            var runner = root.AddComponent<RenderBenchmarkRunner>();
            Assign(runner, "_g1Pipeline", UnityEditor.AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(assets + "BenchmarkG1.asset"));
            Assign(runner, "_g2Pipeline", UnityEditor.AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(assets + "BenchmarkG2.asset"));
            Assign(runner, "_material", UnityEditor.AssetDatabase.LoadAssetAtPath<Material>(assets + "BenchmarkUnits.mat"));
            int fps = Application.targetFrameRate;
            try
            {
                var plan = new System.Collections.Generic.List<RenderBenchmarkConfig>
                {
                    new RenderBenchmarkConfig(RenderMode.G1, 0, 1, 3, 0)
                };
                var active = runner.StartCoroutine(runner.Run(plan, output, false));
                runner.Cancel();
                yield return active;
                Assert.That(Application.targetFrameRate, Is.EqualTo(fps));
                Assert.That(GameObject.Find("Rendering benchmark workload"), Is.Null);

                yield return runner.Run(plan, output, false);
                string[] reports = System.IO.Directory.GetDirectories(output);
                Assert.That(reports.Length, Is.EqualTo(2));
                foreach (string report in reports)
                {
                    string run = System.IO.File.ReadAllText(System.IO.Path.Combine(report, "runs.csv"));
                    string[] frames = System.IO.File.ReadAllLines(System.IO.Path.Combine(report, "frames.csv"));
                    Assert.That(frames.Length, Is.EqualTo(run.Contains(",Cancelled,") ? 1 : 4), run);
                }
                Assert.That(Application.targetFrameRate, Is.EqualTo(fps));
            }
            finally
            {
                runner.End();
                Object.Destroy(root);
                if (System.IO.Directory.Exists(output))
                    System.IO.Directory.Delete(output, true);
            }
        }
#endif

        [UnityTest]
        public IEnumerator BothModesShareTheBackendAndRestoreSettingsAfterCancellationOrEnd()
        {
            RenderPipelineAsset pipeline = QualitySettings.renderPipeline;
            bool batcher = GraphicsSettings.useScriptableRenderPipelineBatching;
            int vSync = QualitySettings.vSyncCount;
            int fps = Application.targetFrameRate;
            bool background = Application.runInBackground;
            var root = new GameObject("Rendering benchmark test");
            var runner = root.AddComponent<RenderBenchmarkRunner>();
            var g1 = ScriptableObject.CreateInstance<UniversalRenderPipelineAsset>();
            var g2 = ScriptableObject.CreateInstance<UniversalRenderPipelineAsset>();
            g1.useSRPBatcher = false;
            g2.useSRPBatcher = true;
            var material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            Assign(runner, "_g1Pipeline", g1);
            Assign(runner, "_g2Pipeline", g2);
            Assign(runner, "_material", material);
            try
            {
                runner.Begin(new RenderBenchmarkConfig(RenderMode.G1, 100, 0, 1, 0));
                var backend = (MeshRendererBackend)Read(runner, "_backend");
                var renderers = backend.Camera.transform.parent.GetComponentsInChildren<MeshRenderer>();
                Assert.That(renderers.Length, Is.EqualTo(100));
                Assert.That(renderers[0].sharedMaterial, Is.SameAs(material));
                Assert.That(renderers[0].GetComponent<MeshFilter>().sharedMesh, Is.SameAs(renderers[99].GetComponent<MeshFilter>().sharedMesh));
                Assert.That(backend.Camera.targetTexture.width, Is.EqualTo(1920));
                Assert.That(backend.Camera.targetTexture.height, Is.EqualTo(1080));
                runner.End();
                runner.Begin(new RenderBenchmarkConfig(RenderMode.G2, 0, 0, 1, 0));
                runner.Cancel();
                runner.enabled = false;
                Assert.That(QualitySettings.renderPipeline, Is.SameAs(pipeline));
                Assert.That(GraphicsSettings.useScriptableRenderPipelineBatching, Is.EqualTo(batcher));
                Assert.That(QualitySettings.vSyncCount, Is.EqualTo(vSync));
                Assert.That(Application.targetFrameRate, Is.EqualTo(fps));
                Assert.That(Application.runInBackground, Is.EqualTo(background));
            }
            finally
            {
                runner.End();
                Object.Destroy(root);
                Object.Destroy(g1);
                Object.Destroy(g2);
                Object.Destroy(material);
            }
            yield return null;
            Assert.That(GameObject.Find("Rendering benchmark workload"), Is.Null);
        }

        private static void Assign(RenderBenchmarkRunner runner, string field, object value) =>
            typeof(RenderBenchmarkRunner).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(runner, value);
        private static object Read(RenderBenchmarkRunner runner, string field) =>
            typeof(RenderBenchmarkRunner).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(runner);
    }
}
