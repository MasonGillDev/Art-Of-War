using Sim.Core.Engine;

namespace Sim.Server.Ai;

// M25 — the Rival's PERSONALITY (docs/m25-rival-spec.md). Each AI faction is
// born with a posture, assigned deterministically from the world seed + faction
// id, so a twin-run (and a replay, and a recovered server) reproduces the same
// cast of characters. The brain reads the posture off AiConfig like any other
// knob — it never computes it from world state, so the fairness pin
// (AiPlayerTests.Brain_TouchesOnlyTheView: the brain sees only the view + its
// config) is untouched. Personality lives ONLY on the ephemeral AiConfig; it is
// never serialized, so it imposes no snapshot/append-only obligation.
public enum AiPersonality : byte
{
    // Peaceful economy brain — the M17 Homesteader, unchanged. Defends its
    // turf (against bandits AND, from M25, hostile factions), but NEVER
    // initiates an offensive war. The default, so every existing scenario and
    // test that builds `new AiConfig()` keeps today's behavior.
    Homesteader = 0,

    // A predator of weakness and a taker of land. Initiates LIMITED wars when
    // it sees an opening (a weak rival garrison) or is crowded (encroachment /
    // land hunger); sues for peace once the grievance is settled or a campaign
    // turns against it.
    Opportunist = 1,

    // Plays the M24 win condition. Masses armies, sieges and razes enemy
    // Castles, presses to eliminate rivals. Manufactures a casus belli against
    // the nearest reachable rival if none presents itself.
    Warlord = 2,
}

public static class RivalDoctrine
{
    // Deterministic per-faction posture from the world seed + owner id. PURE:
    // same (seed, ownerId) → same personality forever, so twin-runs agree and a
    // recovered server re-derives the same cast. Uniform over the three
    // postures — a multi-faction world gets a varied, replayable mix, which is
    // the whole point of seeded personalities (the 2026-06-23 design call). The
    // owner id is folded in with a golden-ratio odd multiplier so adjacent
    // faction ids don't land on correlated streams of one seed.
    public static AiPersonality AssignPersonality(ulong seed, int ownerId)
    {
        var rng = new Rng(seed ^ ((ulong)(uint)ownerId * 0x9E3779B97F4A7C15UL));
        return (AiPersonality)rng.NextInt(3);
    }

    // War-capable = anything but the peaceful Homesteader. The single gate the
    // RivalRung checks before it will ever evaluate a casus belli.
    public static bool IsWarCapable(AiPersonality p) => p != AiPersonality.Homesteader;
}
