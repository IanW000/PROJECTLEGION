# PROJECTLEGION Agent Guide

## Mission

PROJECTLEGION is a high-performance RTS and a learning project for large-scale Unity simulation. Support hundreds to thousands of active units, individual selection, box selection, multi-selection, and control groups. Let players issue move, attack, stop, and other gameplay commands to large groups. Develop scalable global pathfinding, local avoidance, and formation movement, and measure CPU and GPU costs.

## Repository and Technology

- The Git root contains the Unity project in `PROJECTLEGION/`. Treat Unity paths below as relative to that directory.
- The verified baseline is Unity `6000.0.59f2`, URP `17.0.4`, Input System `1.14.2`, and AI Navigation `2.0.9`. URP has PC and Mobile presets. Active Input Handling is Both. The project-wide action asset is `Assets/InputSystem_Actions.inputactions`. `Assets/Scenes/SampleScene.unity` is the only enabled build scene in this baseline.
- Before making technical decisions, verify `ProjectSettings/ProjectVersion.txt`, `Packages/manifest.json`, and `Packages/packages-lock.json`. Check relevant Player, Build, Graphics, and Quality settings when changing input or rendering.
- The baseline lockfile resolves Burst `1.8.25`, Collections `2.5.1`, and Mathematics `1.3.2` as transitive dependencies. Entities and Entities Graphics are absent from that baseline. Verify availability and compatibility before using any package API. Get explicit approval before installing or upgrading packages.
- Evaluate DOTS, ECS, the C# Job System, Burst, Unity Mathematics, Native Collections, Entities Graphics, and GPU instancing against the needs of each system.

## Architecture

- Favor data-oriented, batched processing for high-frequency unit simulation. Use ECS where it provides measurable benefits. MonoBehaviours are suitable for camera, input, UI, and similar systems.
- Separate simulation from presentation and configuration from runtime state. Input and UI submit selections and commands, simulation resolves them, and presentation reads the resulting state.
- Prefer composition and clear dependencies. Reuse existing systems where they fit. Introduce abstractions when concrete needs justify them, and explain trade-offs in performance, complexity, and maintenance.

## CPU and Simulation

- Evaluate time and space complexity before parallelizing or tuning implementation details. Avoid unnecessary O(N²) work and favor contiguous, cache-friendly data.
- Avoid LINQ, boxing, closures, and unnecessary managed allocations in hot loops. Keep managed objects out of Burst-compatible paths. Use Native Collections where appropriate and define ownership and disposal.
- Use Burst-compatible parallel jobs for independent, high-volume calculations. Keep unnecessary work off the main thread, minimize completion and synchronization points, and preserve correct job dependencies and thread safety.
- Minimize ECS structural changes. Batch them with appropriate EntityCommandBuffer workflows and consider enableable components for frequently toggled states. Keep safety checks enabled when benchmarking.
- Avoid per-unit MonoBehaviour updates and unnecessary per-unit coroutines. Consider staggered updates and configurable simulation tick rates for non-critical work.

## Navigation and Rendering

- Separate global pathfinding, local avoidance, and formation management. Reuse navigation data and evaluate shared routes or cached paths for units with common destinations. Consider spatial partitioning for neighbor queries.
- Evaluate A*, flow fields, hierarchical pathfinding, and hybrid approaches against representative scenarios. Cover dynamic obstacles, congestion, unreachable destinations, and narrow passages. Benchmark latency, throughput, and memory before committing to a navigation architecture.
- Evaluate Entities Graphics and compatible GPU instancing paths. Share meshes and materials, and avoid unnecessary material instances. Evaluate frustum and distance culling, LOD, and scalable animation. Profile draw calls, overdraw, rendering submission overhead, and GPU cost.

## Performance Targets and Evidence

- Initial design target is 60 FPS at 1080p with 1,000 active units. The stretch goal is 10,000 active units. These targets are unverified. Benchmark hardware is a 12th Gen Intel Core i7-12700K (3.60 GHz), RTX 3060, and 32 GB RAM.
- Build reproducible scenarios for 100, 500, 1,000, 2,500, 5,000, and 10,000 active units. Include representative group commands. Record hardware, build type, resolution, quality preset, and workload so results are comparable.
- Measure main-thread, render-thread, and GPU frame times, frame-time distribution, job execution and synchronization costs, managed allocations and GC, memory usage, and pathfinding latency and throughput.
- Establish a baseline and profile the bottleneck before optimizing. Use Unity Profiler, Frame Debugger, Burst Inspector, and other available tools as appropriate. Label estimates and measured results explicitly. Claim a performance improvement only when comparable measurements support it.

## Code and Validation

- Write readable, maintainable C#. Use PascalCase for types and methods, camelCase for locals and parameters, and _camelCase for private fields. Use meaningful names and named constants or configuration values in place of magic numbers.
- Write source code and comments in English. Explain non-obvious decisions. Preserve asset GUIDs and move or rename assets with their original .meta files.
- Validate correctness before optimization. Use EditMode and PlayMode tests where appropriate, including edge cases, regressions, and large-scale stress scenarios. Report compilation and test outcomes only when actually verified.

## Workflow and Safety

1. Before implementation, inspect relevant files, check Git status, and identify existing systems and dependencies. Present a plan, explain architecture and performance trade-offs, and list the files to create or modify. Wait for explicit developer approval.
2. After approval, work in small, independently verifiable steps. Test the highest-impact assumptions early and preserve unrelated developer changes.
3. Get explicit approval before committing, pushing, discarding existing changes, deleting assets, installing or upgrading packages, or changing scenes, prefabs, or project settings.
4. Keep modifications inside the repository. Protect secrets and credentials. Exclude generated Unity paths such as Library, Temp, Logs, obj, and build output from scans.
5. After implementation, review the diff and run available checks. Report actual compilation and test results, required Unity Editor setup, known limitations, untested behavior, and relevant performance tests.

## Communication and Learning

Assume familiarity with traditional Unity development and explain new DOTS, ECS, Jobs, or Burst concepts when introducing them. Cover how they work, why they fit the task, how they compare with traditional Unity approaches, and the performance and maintenance trade-offs. Keep detailed designs, benchmark procedures, and measurement records in separate documents when warranted.
