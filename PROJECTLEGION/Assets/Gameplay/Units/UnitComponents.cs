using Unity.Entities;
using Unity.Mathematics;

namespace ProjectLegion.Gameplay
{
    public struct UnitTag : IComponentData { }

    public struct UnitSelected : IComponentData, IEnableableComponent { }

    public struct UnitMoveTarget : IComponentData, IEnableableComponent
    {
        public float2 DestinationXZ;
    }

    public struct UnitId : IComponentData
    {
        public ulong Value;
    }

    public struct UnitStats : IComponentData
    {
        public float MovementSpeed;
    }

    internal struct UnitAvoidanceVelocity : IComponentData
    {
        internal float2 Velocity;
        internal AvoidanceMove Mode;
    }

    internal struct UnitOverlapCorrection : IComponentData
    {
        internal float2 Displacement;
    }

    internal enum UnitMovementState : byte { Moving, Waiting, Retrying }
    internal enum UnitBlockReason : byte { None, Congestion, ArrivalHold }

    internal enum ArrivalStatus : byte
    {
        Approaching, TemporarilyOccupied, PersistentlyOccupied, RouteBlocked, NoAlternative,
        Alternative, OriginalCompleted, AlternativeCompleted
    }

    internal struct UnitArrivalState : IComponentData
    {
        internal float2 OriginalSlot;
        internal float2 FormationForward;
        internal float Spacing;
        internal double OccupiedSince;
        internal double QueuedSince;
        internal double NextRequest;
        internal double LastProgressTime;
        internal float BestDistance;
        internal uint CommandVersion;
        internal uint FailedCandidates;
        internal byte AlternativeIndex;
        internal byte FailedAttempts;
        internal ArrivalStatus Status;
        internal bool HasCommand => CommandVersion != 0;
    }

    internal struct UnitMovementProgress : IComponentData
    {
        internal float2 WindowStart;
        internal float2 PreviousPosition;
        internal float StartDistance;
        internal float BestDistance;
        internal float WindowSeconds;
        internal float Travel;
        internal float BlockedSeconds;
        internal float RetryDelay;
        internal double NextProbe;
        internal uint CommandVersion;
        internal uint Epoch;
        internal uint ProbeCount;
        internal uint ProbeCandidates;
        internal byte FailedWindows;
        internal byte DetourWindows;
        internal bool Initialized;
        internal UnitMovementState State;
        internal UnitBlockReason Reason;

        internal void Reset(uint epoch)
        {
            uint version = CommandVersion;
            uint probes = ProbeCount;
            uint candidates = ProbeCandidates;
            this = default;
            CommandVersion = version;
            ProbeCount = probes;
            ProbeCandidates = candidates;
            Epoch = epoch;
        }
    }

    internal struct UnitSpawnRequest : IComponentData
    {
        public Entity Prefab;
        public int Count;
        public float Spacing;
        public float3 Origin;
    }

    internal struct PrototypeSpawnSource : IComponentData
    {
        internal UnitSpawnRequest Request;
    }

    internal struct PrototypeUnit : IComponentData { }
}
