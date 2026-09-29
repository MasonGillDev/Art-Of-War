namespace Sim.Core.Landing;

// One kingdom's share of the landing (docs/two-act-pacing.md): the war bands
// raised against it, each from its own side. Written only by LandingRules
// (raised by LandingEvent; a dead bandit leaves its band via OnUnitRemoved);
// snapshotted (v40).
//
// The host is RECORDED, not steered, here. Where the bands walk is the bandit
// driver's job (Sim.Server, BanditDriver.ActLanding), and it reads this record
// so a restarted driver still knows every band's side, gathering point and
// strike time.
public sealed class LandingHost
{
    public int TargetOwnerId { get; init; }
    // The target's castle when the host landed: the objective.
    public TileCoord Seat { get; init; }
    // When every band strikes, together.
    public long AssaultTick { get; init; }
    // In the order they were raised (compass sides in the landing's hash order).
    public List<LandingFront> Fronts { get; } = new();
}

// One war band.
public sealed class LandingFront
{
    // Where it came out of the fog: dark to every player, far from every kingdom.
    public TileCoord Landed { get; init; }
    // The castle's neighbour it enters through. On the battlefield grid a unit
    // deploys on the edge facing the tile it came from, so this is the band's
    // side of the fight.
    public TileCoord Approach { get; init; }
    // Where it gathers before the assault, in sight of the castle.
    public TileCoord Staging { get; init; }
    // Its living bandits, ascending.
    public List<int> UnitIds { get; } = new();
}
