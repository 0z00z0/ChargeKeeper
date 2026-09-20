using System;
using System.Collections.Generic;
using ChargeKeeper.Services;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// A network profile may switch the Windows power plan. A plan not put back leaves the machine on
/// somebody else's choice with nothing on screen saying why, so the restore is what these pin. Every
/// case runs against a fake: no test makes a plan active on the machine it runs on.
/// </summary>
public class PowerPlanParkTests
{
    private static readonly Guid Balanced    = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    private static readonly Guid Saver       = new("a1841308-3541-4fab-bc81-f71556f20b4a");
    private static readonly Guid Performance = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    private static readonly Guid Deleted     = new("00000000-0000-0000-0000-0000000000ff");

    private sealed class FakePlans(Guid active) : IPowerPlanSetting
    {
        public Guid? Active { get; set; } = active;
        public List<Guid> Switches { get; } = [];
        public bool RefuseSwitches { get; set; }

        private static readonly Dictionary<Guid, string> Names = new()
        {
            [Balanced] = "Balanced", [Saver] = "Power saver", [Performance] = "High performance",
        };

        public Guid? ReadActive() => Active;

        public bool SetActive(Guid plan)
        {
            Switches.Add(plan);
            if (RefuseSwitches) return false;
            Active = plan;
            return true;
        }

        public string? NameOf(Guid plan) => Names.GetValueOrDefault(plan);
    }

    private sealed class FakeRecord : IPowerPlanRecord
    {
        public Guid? Held { get; set; }
        public int Saves { get; private set; }
        public bool RefuseSaves { get; set; }

        public Guid? Read() => Held;

        public bool Save(Guid plan)
        {
            if (RefuseSaves) return false;
            Saves++;
            Held = plan;
            return true;
        }

        public void Clear() => Held = null;
    }

    private static (PowerPlanPark Park, FakePlans Plans, FakeRecord Record) Build(Guid active)
    {
        var plans  = new FakePlans(active);
        var record = new FakeRecord();
        return (new PowerPlanPark(plans, record, (_, _) => { }), plans, record);
    }

    [Fact]
    public void AProfileThatNamesAPlan_SwitchesToItAndRemembersWhatItDisplaced()
    {
        var (park, plans, record) = Build(Balanced);

        park.Reconcile(Performance, "arrived");

        Assert.Equal(Performance, plans.Active);
        Assert.Equal(Balanced, record.Held);
    }

    [Fact]
    public void WhenNoProfileAsksForAPlan_TheDisplacedOneIsPutBackAndTheRecordCleared()
    {
        var (park, plans, record) = Build(Balanced);
        park.Reconcile(Performance, "arrived");

        park.Reconcile(null, "left");

        Assert.Equal(Balanced, plans.Active);
        Assert.Null(record.Held);
    }

    [Fact]
    public void MovingBetweenTwoProfiles_StillPutsBackThePlanFromBeforeEither()
    {
        // The displaced plan is captured once and never re-captured while a profile holds it —
        // otherwise leaving the second profile would restore the first profile's plan for ever.
        var (park, plans, record) = Build(Balanced);

        park.Reconcile(Performance, "arrived at the first");
        park.Reconcile(Saver, "arrived at the second");
        Assert.Equal(Saver, plans.Active);
        Assert.Equal(1, record.Saves);

        park.Reconcile(null, "left");

        Assert.Equal(Balanced, plans.Active);
        Assert.Null(record.Held);
    }

    [Fact]
    public void ARecordLeftByARunThatEnded_IsPutBackAtTheFirstReconcile()
    {
        // No park happened in this process: the record is all that survives, which is the whole
        // point of writing it to disk before the plan changes.
        var plans  = new FakePlans(Performance);
        var record = new FakeRecord { Held = Balanced };
        var park   = new PowerPlanPark(plans, record, (_, _) => { });

        park.Reconcile(null, "starting up");

        Assert.Equal(Balanced, plans.Active);
        Assert.Null(record.Held);
    }

    [Fact]
    public void NoProfileAndNoRecord_LeavesThePlanAlone()
    {
        // The rule the whole feature rests on: nothing changes a plan unless a profile asked for one.
        var (park, plans, record) = Build(Balanced);

        park.Reconcile(null, "starting up");

        Assert.Empty(plans.Switches);
        Assert.Equal(Balanced, plans.Active);
        Assert.Null(record.Held);
    }

    [Fact]
    public void AProfileNamingThePlanAlreadyRunning_ChangesNothingAndRecordsNothing()
    {
        var (park, plans, record) = Build(Balanced);

        park.Reconcile(Balanced, "arrived");

        Assert.Empty(plans.Switches);
        Assert.Null(record.Held);
    }

    [Fact]
    public void APlanThisMachineNoLongerHas_IsNeitherSwitchedToNorRecorded()
    {
        var (park, plans, record) = Build(Balanced);

        park.Reconcile(Deleted, "arrived");

        Assert.Empty(plans.Switches);
        Assert.Equal(Balanced, plans.Active);
        Assert.Null(record.Held);
    }

    [Fact]
    public void ARecordedPlanThatHasSinceBeenDeleted_IsDroppedRatherThanSwitchedTo()
    {
        var plans  = new FakePlans(Performance);
        var record = new FakeRecord { Held = Deleted };
        var park   = new PowerPlanPark(plans, record, (_, _) => { });

        park.Reconcile(null, "left");

        Assert.Empty(plans.Switches);
        Assert.Null(record.Held);
    }

    [Fact]
    public void AFailedRestore_KeepsTheRecordSoTheNextStartCanTryAgain()
    {
        var (park, plans, record) = Build(Balanced);
        park.Reconcile(Performance, "arrived");
        plans.RefuseSwitches = true;

        park.Reconcile(null, "left");

        Assert.Equal(Balanced, record.Held);
    }

    [Fact]
    public void APlanThatCouldNotBeRecorded_IsNotSwitchedToEither()
    {
        // A switch without a record is a plan nothing can put back.
        var plans  = new FakePlans(Balanced);
        var record = new FakeRecord { RefuseSaves = true };
        var park   = new PowerPlanPark(plans, record, (_, _) => { });

        park.Reconcile(Performance, "arrived");

        Assert.Empty(plans.Switches);
        Assert.Equal(Balanced, plans.Active);
    }
}
