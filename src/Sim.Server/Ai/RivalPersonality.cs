namespace Sim.Server.Ai;

// M25 addendum (2026-07-13) — RIVAL PERSONALITIES. Every Rival used to
// fight with the SAME thresholds, so a 4-rival field behaved like one
// mind with four castles: identical declare timing, identical caution —
// learn one and you've learned them all. A personality is a named bundle
// of WAR-POLICY knobs — nothing else — derived from the shipping
// defaults with `with`, assigned by rival RANK (GameHost cycles the
// table over the rivals in ascending faction-id order), so variety costs
// zero determinism: same seed + same args = the same temperaments making
// the same calls.
//
// THE HARD RULE: a personality may only vary the M25 war-policy block
// (floor / ratios / raid doctrine). Peacetime economy knobs are off
// limits — "a peacetime Rival's curve is EXACTLY a Homesteader's" is a
// pinned invariant (the Sparta lab pins ride on it), and
// RivalPersonalities_OnlyTouchWarPolicyKnobs enforces the allowlist by
// reflection: reach for any other knob and the test names it.
public enum RivalPersonality : byte
{
    // The shipping M25 defaults, untouched — rank 0, so a lone
    // `--rivals 1` opponent IS the baseline every lab curve was tuned on.
    Conqueror = 0,

    // Declares young and cheap (floor 24, 120% advantage), fights past
    // parity (retreats only under 80%), and sends EVERY sword to the
    // siege (RaidPartySize 0 — the doctrine A/B's siege-rush arm, now a
    // temperament instead of an experiment).
    Warlord = 1,

    // Picks only sure wars (180% to declare) and almost never launches
    // the campaign (180% GO gate) — it fights by raiding (party of 5)
    // and sues for peace while still ahead (retreat bar at 120%). The
    // war of a thousand cuts.
    Raider = 2,
}

public static class RivalPersonalities
{
    // Rank = position among the RIVALS (ascending faction id), not the
    // faction id itself — the spread stays stable however many
    // Homesteaders precede the rivals in the id order.
    public static RivalPersonality ForRivalRank(int rank) =>
        (RivalPersonality)(rank % 3);

    public static AiConfig Apply(AiConfig cfg, RivalPersonality personality) => personality switch
    {
        RivalPersonality.Warlord => cfg with
        {
            CampaignPopulationFloor = 24,    // marches while the Conqueror still homesteads
            WarAdvantageRatioPercent = 120,  // a fair-ish fight is good enough
            AttackOvermatchPercent = 120,    // GO on the same nerve it declared with
            RetreatBelowPercent = 80,        // fights on until clearly behind
            RaidPartySize = 0,               // siege-rush: no swords skimmed for loot
        },
        RivalPersonality.Raider => cfg with
        {
            WarAdvantageRatioPercent = 180,  // only sure wars
            AttackOvermatchPercent = 180,    // even surer sieges — rarely fires; raids carry the war
            RetreatBelowPercent = 120,       // sues for peace while still ahead
            RaidPartySize = 5,               // the raid IS the campaign
        },
        _ => cfg,   // Conqueror: the baseline, by construction
    };
}
