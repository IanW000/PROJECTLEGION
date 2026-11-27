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
