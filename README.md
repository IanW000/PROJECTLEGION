# PROJECTLEGION

### Large-Scale RTS Simulation | Performance Engineering | AI Agentic Development

## Overview

PROJECTLEGION is an experimental real-time strategy (RTS) project focused on large-scale gameplay simulation, high-performance software architecture, and AI-assisted software engineering.

As a game developer with a strong interest in performance optimization, I started this project to explore advanced technologies, algorithms, and development methodologies beyond my previous professional experience.

Rather than focusing primarily on visual fidelity or commercial game production, PROJECTLEGION prioritizes scalability, computational efficiency, software architecture, and technical experimentation.

This project is both a personal learning journey and a practical exploration of modern game development technologies.

## Project Vision

The primary goal of PROJECTLEGION is to develop a scalable RTS prototype capable of simultaneously selecting and commanding **10,000+ individual units** in real time.

The project aims to explore:

- Large-scale unit movement and combat simulation.
- Efficient pathfinding and navigation algorithms.
- Dynamic formation generation and slot assignment.
- Scalable collision avoidance and crowd simulation.
- Multithreaded gameplay systems.
- Data-oriented software architecture.
- CPU and GPU performance optimization.
- Memory-efficient data structures and algorithms.
- AI agent integration into professional software development workflows.

The long-term objective is to continuously push the performance limits of Unity while maintaining a modular, maintainable, and extensible codebase.

## Technology Stack

| Technology | Purpose |
|---|---|
| Unity 6 | Core game engine |
| C# | Primary programming language |
| Unity DOTS | Data-oriented architecture |
| Entity Component System (ECS) | Large-scale entity simulation |
| Unity Job System | Multithreaded computation |
| Burst Compiler | High-performance native code generation |
| Unity Collections | Native data structures and memory management |
| Unity Mathematics | Performance-oriented mathematical operations |
| Entities Graphics | Large-scale entity rendering |
| Universal Render Pipeline (URP) | Rendering infrastructure |
| Unity Input System | Player input and interaction |
| Unity Test Framework | Automated testing and validation |
| OpenAI Codex | AI agentic software development |
| Git & GitHub | Version control and public development |

## Core Technical Areas

### Data-Oriented Design

Explore the advantages of ECS and data-oriented programming over traditional object-oriented GameObject architectures, particularly when processing thousands of independent units.

### Parallel Computing

Utilize the Unity Job System and Burst Compiler to distribute computational workloads across CPU cores and improve simulation scalability.

### Performance Engineering

Investigate CPU and GPU bottlenecks through controlled experiments, profiling, benchmarking, and architectural optimization.

Key areas of interest include:

- Main-thread workload reduction.
- Multithreaded job scheduling.
- Memory access patterns and cache efficiency.
- Native memory management.
- GPU instancing and batch rendering.
- Rendering scalability.
- Algorithmic complexity.

### Large-Scale Gameplay Algorithms

Research and implement algorithms suitable for large-scale RTS simulations, including:

- Spatial hashing and spatial partitioning.
- Efficient neighbor searching.
- Formation generation and slot assignment.
- Local collision avoidance.
- Crowd simulation.
- A* pathfinding.
- Flow field pathfinding.
- Large-scale combat simulation.

### Developer Tooling

Develop custom Unity Editor extensions and debugging tools to improve development efficiency, particularly when working with ECS-based gameplay systems.

## Current Development Progress

PROJECTLEGION is currently in active development.

### Core Gameplay Systems

- [x] RTS camera controller
- [x] ECS-based unit spawning
- [x] Entities Graphics rendering
- [x] Single-unit selection
- [x] Multi-unit box selection
- [x] Group movement commands
- [x] Parallel ECS unit movement
- [x] Rectangular formation generation
- [x] Spatial sorting-based formation slot assignment
- [ ] Spatial hashing and neighbor searching
- [ ] Local collision avoidance
- [ ] Crowd congestion management
- [ ] Large-scale pathfinding
- [ ] Combat and attack commands

Phase 7.6.3a known limitation: units commanded through a gap narrower than their
collision diameter can keep steering and correcting around the entrance while the
target remains active. Phase 7.6.3b will focus on congestion management and stuck
detection, with route selection handled by pathfinding.

### Phase 8.1 Navigation Grid Validation

In [Navigation Playground](PROJECTLEGION/Assets/Gameplay/Scenes/NavigationPlayground.unity),
manual testing at Sample Density 4 found that previously blocked connections on
the land left of the fissure turned green when Agent Radius was reduced from
0.35 to 0.20 and 0.10.

In the checked-in scene, a batch rebuild at density 4 found that all three
cardinal connections along the west strip stayed open and all four cardinal
connections crossing the fissure stayed blocked at each radius. A separate
fixture with a localized fissure alongside an edge reproduced the reported
pattern: both endpoints stayed walkable at all three radii, while the edge
was blocked at 0.35 and open at 0.20 and 0.10.

Nodes and intermediate edge samples use the same standing checks. In addition
to the center surface, four probes offset by Agent Radius check ground support
and local height transitions. The radius also sets the capsule clearance
against configured blocker layers. An edge requires two walkable endpoints
and valid samples and height transitions between them. Diagonals also require
the surrounding cardinal connections to prevent corner cutting. Two green
nodes can therefore have a red edge if a footprint probe along the edge loses
support. Changing Agent Radius can change both the walkable nodes and their
connections, even with unchanged geometry and Sample Density.

The sampling lattice has an XZ pitch of `Cell Size / (2 × Sample Density)`,
or 0.125 units for Cell Size 1 at density 4. The center and footprint probes
are discrete. Thin fissures or blockers can fall between sampled footprints,
and a different radius can move a support probe into or out of a narrow void.
Higher density improves sampling resolution but provides no continuous
geometry guarantee.

### Phase 8.2 A* Pathfinding

Open [Navigation Playground](PROJECTLEGION/Assets/Gameplay/Scenes/NavigationPlayground.unity)
and enable Scene View Gizmos. Move **Path Start** and **Path Destination** with
the Scene View Move tool. On the navigation grid object, use **Build Navigation
Grid** on the authoring component, then **Run Path Search** on the debugger.
The cyan and magenta markers show the start and destination, the thick gold
line follows the node elevations, and blue cubes mark recorded expansions.
The Inspector shows the result, cost, 3D length, node count, expanded nodes,
reopens, heap operations, and elapsed search time. **Clear Path** removes the
result. Rebuilding or releasing the grid invalidates the result immediately.
Exploration capture and drawing have independent controls. The draw budgets
limit the visible path prefix and exploration markers without limiting search.

The search uses the grid's connectivity and its weighted 3D edge cost with
double-precision arithmetic and Burst strict floating-point mode. Straight-line
3D distance is consistent because each edge costs at least its geometric length,
and the triangle inequality bounds the change in the heuristic across an edge.
The heap compares total estimated cost, then remaining distance, then node ID.
Equal-cost alternatives retain the first parent. The standard
`O((V + E) log V)` bound assumes a consistent heuristic and at most one
expansion per node. The implementation tracks reopens on strict improvement
under floating-point rounding. Reopens add another neighbor scan and heap
work for each affected expansion. Workspace capacity is linear in grid size.
Each node reserves 48 bytes: 4 for the search generation, 8 for its G cost,
12 for its parent, heap index and path output slot, and 24 for the heap entry.
The result uses 56 bytes and optional exploration capture uses 4 bytes per
reserved slot. These figures are native buffer payloads and exclude allocator
metadata. Searches reuse the buffers and initialize a node on its first visit
in each generation. A full stamp clear occurs only on generation wraparound.

| Grid | Grid payload | Search workspace with 512 exploration slots |
| --- | ---: | ---: |
| 64 × 64 | 40,960 B | 198,712 B |
| 128 × 128 | 163,840 B | 788,536 B |
| 256 × 256 | 655,360 B | 3,147,832 B |

Single-query measurements used Windows 11 x64, Unity 6000.0.66f2 Editor
PlayMode batch mode, an i7-12700K and Burst strict floating-point mode.
Each group ran three warmups and 30 timed synchronous searches including path
reconstruction. Exploration capture was capped at 512 expansions. Timings
exclude graph construction, workspace allocation, and Scene View drawing.
The longest request on an `S × S` graph ran from `(0, 0)` to
`(S - 1, floor((S - 1) / 2))`.

| Grid | Layout | Capture off median / P95 (ms) | Capture on median / P95 (ms) | Expanded | Heap push / pop / decrease |
| --- | --- | ---: | ---: | ---: | ---: |
| 64 × 64 | Open | 0.1622 / 0.1979 | 0.1687 / 0.2261 | 1,140 | 1,289 / 1,141 / 792 |
| 64 × 64 | Gate | 0.2354 / 0.3421 | 0.2345 / 0.2697 | 1,713 | 1,856 / 1,714 / 1,556 |
| 64 × 64 | Split | 0.2618 / 0.3312 | 0.2606 / 0.2964 | 2,048 | 2,048 / 2,048 / 1,961 |
| 128 × 128 | Open | 0.7278 / 0.8597 | 0.7339 / 0.8068 | 4,687 | 4,987 / 4,688 / 3,225 |
| 128 × 128 | Gate | 1.0169 / 1.2555 | 1.0209 / 1.1123 | 6,889 | 7,173 / 6,890 / 6,055 |
| 128 × 128 | Split | 1.0973 / 1.3882 | 1.1430 / 1.3088 | 8,192 | 8,192 / 8,192 / 7,727 |
| 256 × 256 | Open | 3.2418 / 3.5478 | 3.1925 / 3.4314 | 18,976 | 19,576 / 18,977 / 13,188 |
| 256 × 256 | Gate | 4.3529 / 4.5262 | 4.5257 / 4.7456 | 27,507 | 28,073 / 27,508 / 23,146 |
| 256 × 256 | Split | 4.9923 / 5.3400 | 4.8826 / 5.0698 | 32,768 | 32,768 / 32,768 / 30,226 |

The split requests above were unreachable. With capture off, the near requests
had medians of 0.0027–0.0029 ms. Mid-distance medians were 0.0157–0.0163 ms,
0.0675–0.0692 ms, and 0.3140–0.3184 ms in size order. All timed groups measured
zero managed allocation on the querying thread and zero node reopens. The
search allocates no temporary native containers per query. Synthetic graph
construction took 0.788–0.866 ms, 3.094–3.288 ms, and 12.521–13.025 ms by
size. Workspace initialization measured 0.005–0.034 ms across these runs.

The 19 × 13 collider-based Navigation Playground built in 9.84 ms on the first
call, then 3.12–3.57 ms on rebuild. Its grid reserved 2,470 B of persistent
native payload and used 83,036 B of temporary sampling and query-buffer payload.
The scene smoke test also verified the default flat route, a route from the
lowland to the plateau via the ramp, an invalid destination, and result
invalidation on grid rebuild, grid release, and path clearing.
In a separate interactive Editor run, the `NavigationPathDebugger.Draw` CPU
marker measured 0.1205 ms median and 0.3066 ms P95 per invocation over 90
Profiler Recorder samples for the 10-node ramp route with 16 exploration
markers. This measures the debugger's draw callback; full Scene View and GPU
timings were outside the captured marker.

### Phase 8.3 Parallel Pathfinding

In Navigation Playground, build the navigation grid and open the **Navigation
Batch Debugger** on the same object. Set the request count, requested
workspaces, queue size, memory and per-update budgets, then select **Start or
Repeat Batch**. The panel reports accepted work, concurrency, throughput,
latency, dispatch and collection times, and payload usage. Its P95 uses the
last 4,096 results, while the mean and maximum cover the batch. Enable **Show
Path Preview** to draw a bounded prefix of one sampled successful route.
**Cancel Batch** invalidates outstanding requests immediately. **Clear Batch
Results** clears the display and releases the batch buffers after active jobs
return. Start a new batch after the cancelled jobs have drained.

The scheduler is owned by the main thread. Each submitted search uses the same
Burst A* kernel as the synchronous debugger and owns one exclusive workspace
until collection. The scheduler polls each job and completes only reported
finished jobs during normal dispatch and collection. The grid tracks reader
dependencies and retires its arrays through dependent disposal jobs. The
owning components may wait during final shutdown or unload. Grid rebuilds
defer while a previous retirement is pending; retry the build after it drains.

Request IDs increase within the scheduler, revisions increase on replacement,
and grid generations increase when the scheduler changes snapshots. Handles
carry all three plus a scheduler identity and slot. Slot reuse never revives
an old handle. Cancellation or replacement invalidates old results immediately.
A submitted worker still runs to completion and its eventual result is
discarded if superseded. A full queue rejects new submissions and replacements
that need a new queue position. Accepted requests dispatch FIFO, and an accepted
replacement joins the tail.

A completed search keeps its workspace until the scheduler reserves all the
result blocks and copies its path under the per-update node budget. Results
own those blocks independently of the workspace. Consumers validate the handle,
copy any needed nodes, then release it. Invalidation or replacement also
reclaims any complete or partial copy. When retained paths fill the arena,
submission and dispatch eventually experience backpressure. A single path
larger than the whole arena returns an explicit capacity failure.

Each workspace reserves `48 × node count + 56` bytes. A result block holds 32
node indices and one link (132 bytes). The navigation budget covers grid
payloads, the new grid and estimated build buffers before a rebuild, request
storage, workspaces, and the result arena. While a grid retires, its payload
remains charged. Workspaces are resized only after the old work drains, and the
old pool is released before allocating its replacement. The budget excludes
allocator metadata, Job System internals, Burst compilation, and managed
objects and diagnostic scratch arrays. It is a buffer budget, so process RSS
and allocator reserved memory can differ. Changing a budget does not reclaim
already-allocated buffers mid-job.
The saved Playground smoke test reached 859,752 B of accounted payload while
rebuilding the 19 × 13 grid with a default four-workspace batch allocated.

The Gameplay PlayMode scheduler tests include a maximum-throughput pump
and separate paced 60/120 Hz Unity frame loops. They report latency and queue
distributions separately for short and expensive requests. The frame tests
use a test world with 1,000 moving units and the existing movement, avoidance,
spatial grid, and overlap systems. Scheduler main-thread timings include
submission, dispatch, collection, and result release. The A* profiler marker
sums worker execution and can exceed batch wall time because searches overlap.
The timed steady-state runs exclude construction and workspace warm-up.

Observed in Unity 6000.0.66f2 Windows x64 Editor PlayMode on an i7-12700K,
with Burst and safety checks enabled. The following batch wall times in
milliseconds used a 64 × 64 graph with 90% short reachable and 10% expensive
unreachable requests.

| Requests | 1 workspace | 2 | 4 | 8 | 16 |
| --- | ---: | ---: | ---: | ---: | ---: |
| 100 | 3.748 | 1.838 | 1.055 | 0.877 | 0.870 |
| 1,000 | 35.464 | 17.325 | 9.178 | 7.406 | 6.196 |
| 10,000 | 351.035 | 171.250 | 86.933 | 58.492 | 53.673 |

On the 256 × 256 split graph, 1,000 requests took 130.049 ms with four
workspaces and 56.399 ms with sixteen. With sixteen, cumulative A* worker
time was 823.045 ms and peak graph plus scheduler payload was 51,287,808 B.
The paced 128 × 128 frame tests used eight workspaces, 600 requests and
96 game updates each. Values in the next table are mean / P95 / max in ms.

| Rate | Short latency | Expensive latency | Short queue | Expensive queue | Main-thread scheduler |
| --- | --- | --- | --- | --- | --- |
| 60 Hz | 233.528 / 266.685 / 266.977 | 231.671 / 266.685 / 266.973 | 216.908 / 250.107 / 250.408 | 215.049 / 250.094 / 250.393 | 0.075 / 0.132 / 0.190 |
| 120 Hz | 116.735 / 133.404 / 133.749 | 115.793 / 133.349 / 133.419 | 108.449 / 125.113 / 125.689 | 107.516 / 125.118 / 125.608 | 0.074 / 0.142 / 0.406 |

Gameplay step P95 without and with scheduling was 0.759 / 0.811 ms at 60 Hz
and 0.570 / 0.852 ms at 120 Hz. These samples include game-system dependency
completion in the test world.
Measured scheduler-thread managed allocations were 0 B in all timed runs.
In the 5,000-replacement case, maximum logical invalidation time was 0.166 ms
and ten detached worker slots returned in 5.463 ms on average, 6.035 ms
maximum. The last valid result published in 0.093 ms with fixed native
payload. A separate final shutdown with eight expensive jobs waiting took
10.122 ms to synchronize.

### Phase 8.4 Flow Fields

Open Navigation Playground, build the navigation grid, and select the navigation
grid object with Scene View Gizmos enabled. Move **Flow Goal** or the **Flow
Start** markers with the Move tool. Use **Build Flow Field** and **Clear Flow
Field** on the Flow Debugger. The heatmap normalizes against the highest finite
integration cost. White arrows show the next legal step, coral crosses mark
blocked nodes, violet wire squares mark unreachable nodes, and a yellow ring
marks the goal. Magenta lines follow the field from the sample starts. Layers,
focus radius, node count, and sample-path steps have independent controls.

The integration job runs reverse Dijkstra. It probes candidate predecessors
using the original outgoing connectivity and charges the forward edge cost.
The direction job selects a legal outgoing edge satisfying the Bellman
equation, with fixed direction order for ties. Every published step strictly
decreases the integration cost. If floating-point precision prevents a valid
descending optimum, construction reports a numerical failure. Queries return
the state and next node without exposing the direction encoding.

A completed field owns its integration and direction arrays independently of
the builder. Each destination uses 9 bytes per grid node. The reusable
construction workspace uses 28 bytes per node plus a small statistics array.
The debugger keeps one published result and charges any in-flight or retiring
buffers against the navigation budget.

Acquire views on the owner thread against the current grid snapshot and
generation. For synchronous work, use the view during that acquisition scope.
For a Job, acquire, schedule and register its Reader before returning control
to the owner. Never cache a view for a future schedule. Grid invalidation
blocks new acquisitions immediately. In-flight readers can finish safely,
and their derived results need a current-generation check before publication.
The grid retires after its build Readers, and the field retires after its
consumer Readers. A completed field no longer needs its build workspace.

Measurements used Unity 6000.0.66f2 Windows x64 Editor PlayMode on an
i7-12700K with Burst and safety checks. Values below are medians of five
warmed asynchronous builds in milliseconds, excluding synthetic graph
construction and visualization.

| Layout | 64² | 128² | 256² | 512² |
| --- | ---: | ---: | ---: | ---: |
| Open | 0.723 | 2.838 | 12.077 | 51.409 |
| Weighted | 0.815 | 3.159 | 12.901 | 55.022 |
| Bottleneck | 0.714 | 2.844 | 11.753 | 51.449 |
| Disconnected | 0.426 | 1.368 | 5.820 | 24.051 |

For 512² weighted terrain, integration took 52.002 ms and direction
generation used 28.025 ms of cumulative worker CPU. Parallel direction
chunks overlap in wall time. Main-thread dispatch and publication took
0.0248 ms and 0.0048 ms. The field used 2,359,296 B and the workspace used
7,340,056 B, including 24 B of build statistics. A second retained
destination adds another 2,359,296 B. Steady builds reuse the workspace
and allocate two new output arrays plus one managed field owner.

| Consumers, 128² bottleneck | One lookup (ms) | Follow to goal (ms) | Materialize path (ms) |
| ---: | ---: | ---: | ---: |
| 100 | 0.0014 | 0.0500 | 0.0561 |
| 1,000 | 0.0074 | 0.5034 | 0.5618 |
| 10,000 | 0.0788 | 5.0574 | 5.5358 |

The 10,000 continuous consumers made 1,395,784 successful next-node lookups.
These are Burst batch timings. Full path output reuses caller-owned storage.
A separate allocation probe detected no `GC.Alloc` activity for 500 warmed
lookup dispatches and detected allocation activity when building new fields.
On this Editor backend, the thread allocation byte counter also returned
zero for a known 16 KiB allocation. Exact managed allocation bytes are
unavailable from that counter.

For a shared goal on a 128² open grid, compare one field build plus the
requested delivery against the same A* delivery. The first sampled
crossovers for next step, continuous traversal and full materialization
fell in the same intervals below. The parallel baseline used four workspaces.

| Starts | Compared with synchronous A* | Compared with parallel A* |
| --- | --- | --- |
| Near goal | (1,024, 4,096] | (256, 1,024] |
| Far from goal | (4, 16] | (16, 64] |

For 64 far starts requesting full paths, Flow Field took 2.849 ms including
construction, synchronous A* took 30.104 ms, and parallel A* took 8.379 ms.
Parallel A* accumulated 31.328 ms of search worker CPU. The crossover depends
on the graph and path lengths, and the samples exclude rendering.
The interactive Playground draw callback, with all four layers enabled and
42 reachable nodes, measured 0.0826 ms median and 0.0997 ms P95 across 89 CPU
samples. This scope excludes the rest of the Scene View and GPU rendering.

### Performance Research

- [x] MonoBehaviour movement baseline
- [x] ECS main-thread execution benchmark
- [x] Burst-compiled execution benchmark
- [x] Parallel Job System benchmark
- [x] Burst and parallel job comparison
- [x] Automated benchmark data collection
- [ ] Comprehensive GPU rendering benchmarks
- [ ] Large-scale crowd simulation benchmarks
- [ ] Pathfinding performance benchmarks
- [ ] Full gameplay performance evaluation

### Developer Tools & Engineering

- [x] Custom ECS entity hierarchy framing tool
- [x] AI agent development guidelines
- [x] Structured Git development workflow
- [x] Automated Edit Mode and Play Mode testing
- [ ] Additional ECS debugging and visualization tools

## Current Milestone

The current prototype supports ECS-based spawning, selection, movement, and formation assignment with **10,000 units**.

Formation slots are assigned using a spatial sorting algorithm with O(n log n) time complexity, providing a scalable alternative to more computationally expensive assignment algorithms.

Independent groups can currently intersect and overlap during movement. Spatial hashing, local avoidance, and congestion management are the next major development milestones.

The current unit count represents functional validation rather than a finalized frame-rate or performance guarantee.

## AI Agentic Development

PROJECTLEGION also serves as an experiment in integrating AI coding agents into a structured software engineering workflow.

Rather than relying on unrestricted AI-generated code, the project follows a developer-directed approach in which AI agents assist with implementation under defined architectural, technical, and quality requirements.

The development workflow includes:

1. Technical research and requirement definition.
2. Architecture and algorithm design.
3. AI-assisted implementation.
4. Code review and technical analysis.
5. Automated testing.
6. Manual functional validation.
7. Performance profiling and benchmarking.
8. Git checkpoints and technical documentation.

Project-specific development guidelines are maintained in `AGENTS.md` to establish architectural constraints, coding standards, performance expectations, testing requirements, and agent permissions.

One of the project's objectives is to investigate how AI agents can improve software development productivity without compromising code quality, maintainability, technical understanding, or developer control.

## Development Philosophy

**Performance is an architectural decision, not an afterthought.**

PROJECTLEGION follows several core engineering principles:

- Choose appropriate algorithms before relying on hardware optimization.
- Design systems with scalability in mind.
- Prefer data-oriented approaches for large-scale simulation.
- Maintain clear separation between gameplay systems.
- Validate performance improvements through measurable experiments.
- Prioritize maintainability alongside computational efficiency.
- Use AI as an engineering tool rather than a replacement for technical understanding.

## Development Logs

This project is being developed publicly.

I will be sharing regular development updates, technical experiments, performance comparisons, implementation challenges, and lessons learned throughout the development process.

Follow the project's progress through this repository and my LinkedIn development logs.

## About the Developer

I am a game and software developer with professional experience in Unity development, industrial simulation, performance optimization, and developer tooling.

PROJECTLEGION represents my continued exploration of advanced game development, high-performance computing, scalable software architecture, and emerging AI-assisted development methodologies.

I welcome technical discussions, feedback, and knowledge sharing from fellow developers interested in these areas.

---

**PROJECTLEGION — Built to explore, engineered to scale.**
