using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;

namespace ProjectLegion.Gameplay.Tests
{
    public sealed class RtsFormationTests
    {
        [Test]
        public void SingleUnitUsesClickPosition()
        {
            var center = new float2(3f, -7f);
            var formation = new RectangularFormation(1, center, float2.zero, 1f);
            Assert.That(formation.GetSlot(0), Is.EqualTo(center));
            Assert.That(formation.Forward, Is.EqualTo(new float2(0f, 1f)));
        }

        [TestCase(5)]
        [TestCase(7)]
        [TestCase(10)]
        public void IncompleteRowsKeepSpacingAndActualCentroid(int count)
        {
            var center = new float2(-10f, 22f);
            var direction = new float2(3f, 4f);
            const float spacing = 1.75f;
            var formation = new RectangularFormation(count, center, direction, spacing);
            float2 sum = default;
            for (int index = 0; index < count; index++)
            {
                float2 slot = formation.GetSlot(index);
                sum += slot;
                if (index % formation.Columns != 0)
                    Assert.That(math.distance(slot - formation.GetSlot(index - 1), formation.Right * spacing),
                        Is.LessThan(0.0001f));
                if (index >= formation.Columns)
                    Assert.That(math.dot(formation.GetSlot(index - formation.Columns) - slot, formation.Forward),
                        Is.EqualTo(spacing).Within(0.0001f));
            }

            Assert.That(math.distance(sum / count, center), Is.LessThan(0.0001f));
            Assert.That(math.distance(formation.Forward, direction / 5f), Is.LessThan(0.0001f));
        }

        [Test]
        public void AssignmentUsesSpatialAxesAndBreaksTiesByUnitId()
        {
            using var units = new NativeArray<FormationSlotAssignment.Unit>(new[]
            {
                new FormationSlotAssignment.Unit { Id = 4, Position = new float2(2, 1) },
                new FormationSlotAssignment.Unit { Id = 3, Position = new float2(2, -1) },
                new FormationSlotAssignment.Unit { Id = 2, Position = new float2(-2, 0) },
                new FormationSlotAssignment.Unit { Id = 1, Position = new float2(-2, 0) }
            }, Allocator.Temp);
            using var reversed = new NativeArray<FormationSlotAssignment.Unit>(4, Allocator.Temp);
            for (int i = 0; i < units.Length; i++)
                NativeArray<FormationSlotAssignment.Unit>.Copy(units, units.Length - i - 1, reversed, i, 1);

            var formation = FormationSlotAssignment.Sort(units, new float2(10, 0), 1f);
            var second = FormationSlotAssignment.Sort(reversed, new float2(10, 0), 1f);
            Assert.That(units[0].Id, Is.EqualTo(4ul));
            Assert.That(units[1].Id, Is.EqualTo(3ul));
            Assert.That(units[2].Id, Is.EqualTo(1ul));
            Assert.That(units[3].Id, Is.EqualTo(2ul));
            for (int i = 0; i < units.Length; i++)
            {
                Assert.That(reversed[i].Id, Is.EqualTo(units[i].Id));
                Assert.That(second.GetSlot(i), Is.EqualTo(formation.GetSlot(i)));
            }
        }
    }
}
