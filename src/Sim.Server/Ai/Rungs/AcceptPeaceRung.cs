using Sim.Core.Diplomacy;
using Sim.Core.Intents;

namespace Sim.Server.Ai.Rungs;

// M25 — the Homesteader answers white flags. It never initiates
// diplomacy (that stays deferred — docs/m25-rival-spec.md), but a
// peaceful brain that ignores a Neutral offer forever would make every
// war against it unendable by treaty: the loser sues, nobody answers,
// lazy expiry buries the offer, repeat. Accepting peace ends a war the
// Homesteader never wanted — so this sits at the TOP of its ladder
// (ending the war beats maneuvering in it) and fires only when an offer
// is actually pending.
//
// Ally offers are ignored: alliances carry no mechanics yet (deferred),
// and consenting to a meaningless state would only confuse the ledger.
//
// The RIVAL does not run this rung — WarRung owns its peace policy
// (accept only when LOSING; a winning Rival lets offers expire).
public sealed class AcceptPeaceRung : IRung
{
    public Decision? TryClaim(ThinkContext ctx)
    {
        var accepts = ctx.View.IncomingProposals
            .Where(p => p.DesiredState == (int)RelationshipState.Neutral)
            .Select(p => (Intent)new RespondToProposalIntent(ctx.PlayerId, p.Id, accept: true)
                { PlayerId = ctx.PlayerId })
            .ToList();
        return accepts.Count == 0 ? null
            : new Decision("peace", $"accepting {accepts.Count} peace offer(s)", accepts);
    }
}
