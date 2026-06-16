using System.Text.Json;
using Sim.Core.Boats;
using Sim.Core.Diplomacy;
using Sim.Core.Equipment;
using Sim.Core.Groups;
using Sim.Core.Intents;
using Sim.Core.Logistics;
using Sim.Core.Movement;
using Sim.Core.Population;

namespace Sim.Persistence;

// Type-name → Intent JSON registry. Hand-written switch (no reflection) so
// the durable-intent type surface is explicit and auditable. The type-name
// strings ARE durable; once an intent type is shipped, its name is frozen
// for the life of the game (renaming the C# class is fine; the JSON
// type-name stays).
//
// PlayerId on the base Intent serializes automatically as a public init
// property. Each intent's constructor parameters round-trip via
// [JsonConstructor] (added in the M4 Phase C annotation pass).
public static class IntentJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        // System.Text.Json camel-cases property names by default; we want
        // the C# names verbatim so the registry stays "what you see is what
        // you get" for auditing.
        PropertyNamingPolicy = null,
    };

    // Stable durable name → C# type. Adding a new intent: add a row here AND
    // a case in Deserialize. Renaming a C# class without updating the key
    // here breaks Serialize lookup; the registry IS the durable contract.
    private static readonly Dictionary<Type, string> TypeNames = new()
    {
        [typeof(MoveIntent)]              = "MoveIntent",
        [typeof(PlaceSiteIntent)]         = "PlaceSiteIntent",
        [typeof(AssignBuildersIntent)]    = "AssignBuildersIntent",
        [typeof(AssignWorkersIntent)]     = "AssignWorkersIntent",
        [typeof(UnassignWorkersIntent)]   = "UnassignWorkersIntent",
        [typeof(HaulIntent)]              = "HaulIntent",
        [typeof(FormGroupIntent)]         = "FormGroupIntent",
        [typeof(MoveGroupIntent)]         = "MoveGroupIntent",
        [typeof(DisbandGroupIntent)]      = "DisbandGroupIntent",
        [typeof(DeclareWarIntent)]            = "DeclareWarIntent",
        [typeof(ProposeRelationshipIntent)]   = "ProposeRelationshipIntent",
        [typeof(RespondToProposalIntent)]     = "RespondToProposalIntent",
        [typeof(BeginBreedingIntent)]         = "BeginBreedingIntent",
        [typeof(TrainUnitIntent)]             = "TrainUnitIntent",
        [typeof(EmbarkIntent)]                = "EmbarkIntent",
        [typeof(DisembarkIntent)]             = "DisembarkIntent",
        [typeof(UnloadCargoIntent)]           = "UnloadCargoIntent",
        [typeof(LoadCargoIntent)]             = "LoadCargoIntent",
        [typeof(CraftEquipmentIntent)]        = "CraftEquipmentIntent",
        [typeof(EquipUnitIntent)]             = "EquipUnitIntent",
        // M16 — server-internal (the wire rejects them; the bandit driver
        // submits in-process) but DURABLE like any intent: recovery replays
        // bandit spawns/despawns from the log.
        [typeof(Sim.Core.Bandits.SpawnBanditPartyIntent)]   = "SpawnBanditPartyIntent",
        [typeof(Sim.Core.Bandits.DespawnBanditPartyIntent)] = "DespawnBanditPartyIntent",
        // M18 — standing-order automation. AdvanceOrderCursorIntent is
        // server-internal (wire-rejected, driver-submitted) but durable:
        // replay reproduces cursor state without the driver.
        [typeof(Sim.Core.Automation.SetStandingOrderIntent)]     = "SetStandingOrderIntent",
        [typeof(Sim.Core.Automation.ClearStandingOrderIntent)]   = "ClearStandingOrderIntent",
        [typeof(Sim.Core.Automation.AdvanceOrderCursorIntent)]   = "AdvanceOrderCursorIntent",
        // M20 — scouting dispatch.
        [typeof(Sim.Core.Scouting.DispatchScoutIntent)]          = "DispatchScoutIntent",
        // M21 — canal digging (whole-path terrain-mutation build).
        [typeof(Sim.Core.Canals.PlaceCanalIntent)]               = "PlaceCanalIntent",
        // M23 — loot a discovered cache.
        [typeof(Sim.Core.Caches.LootCacheIntent)]                = "LootCacheIntent",
    };

    public static (string TypeName, string Payload) Serialize(Intent intent)
    {
        var type = intent.GetType();
        if (!TypeNames.TryGetValue(type, out var name))
            throw new InvalidOperationException(
                $"Intent type '{type.FullName}' is not registered in IntentJson. " +
                $"Add it to IntentJson.TypeNames and Deserialize.");
        var payload = JsonSerializer.Serialize(intent, type, Options);
        return (name, payload);
    }

    public static Intent Deserialize(string typeName, string payload)
    {
        Intent? intent = typeName switch
        {
            "MoveIntent"             => JsonSerializer.Deserialize<MoveIntent>(payload, Options),
            "PlaceSiteIntent"        => JsonSerializer.Deserialize<PlaceSiteIntent>(payload, Options),
            "AssignBuildersIntent"   => JsonSerializer.Deserialize<AssignBuildersIntent>(payload, Options),
            "AssignWorkersIntent"    => JsonSerializer.Deserialize<AssignWorkersIntent>(payload, Options),
            "UnassignWorkersIntent"  => JsonSerializer.Deserialize<UnassignWorkersIntent>(payload, Options),
            "HaulIntent"             => JsonSerializer.Deserialize<HaulIntent>(payload, Options),
            "FormGroupIntent"        => JsonSerializer.Deserialize<FormGroupIntent>(payload, Options),
            "MoveGroupIntent"        => JsonSerializer.Deserialize<MoveGroupIntent>(payload, Options),
            "DisbandGroupIntent"     => JsonSerializer.Deserialize<DisbandGroupIntent>(payload, Options),
            "DeclareWarIntent"             => JsonSerializer.Deserialize<DeclareWarIntent>(payload, Options),
            "ProposeRelationshipIntent"    => JsonSerializer.Deserialize<ProposeRelationshipIntent>(payload, Options),
            "RespondToProposalIntent"      => JsonSerializer.Deserialize<RespondToProposalIntent>(payload, Options),
            "BeginBreedingIntent"          => JsonSerializer.Deserialize<BeginBreedingIntent>(payload, Options),
            "TrainUnitIntent"              => JsonSerializer.Deserialize<TrainUnitIntent>(payload, Options),
            "EmbarkIntent"                 => JsonSerializer.Deserialize<EmbarkIntent>(payload, Options),
            "DisembarkIntent"              => JsonSerializer.Deserialize<DisembarkIntent>(payload, Options),
            "UnloadCargoIntent"            => JsonSerializer.Deserialize<UnloadCargoIntent>(payload, Options),
            "LoadCargoIntent"              => JsonSerializer.Deserialize<LoadCargoIntent>(payload, Options),
            "CraftEquipmentIntent"         => JsonSerializer.Deserialize<CraftEquipmentIntent>(payload, Options),
            "EquipUnitIntent"              => JsonSerializer.Deserialize<EquipUnitIntent>(payload, Options),
            "SpawnBanditPartyIntent"       => JsonSerializer.Deserialize<Sim.Core.Bandits.SpawnBanditPartyIntent>(payload, Options),
            "DespawnBanditPartyIntent"     => JsonSerializer.Deserialize<Sim.Core.Bandits.DespawnBanditPartyIntent>(payload, Options),
            "SetStandingOrderIntent"       => JsonSerializer.Deserialize<Sim.Core.Automation.SetStandingOrderIntent>(payload, Options),
            "ClearStandingOrderIntent"     => JsonSerializer.Deserialize<Sim.Core.Automation.ClearStandingOrderIntent>(payload, Options),
            "AdvanceOrderCursorIntent"     => JsonSerializer.Deserialize<Sim.Core.Automation.AdvanceOrderCursorIntent>(payload, Options),
            "DispatchScoutIntent"          => JsonSerializer.Deserialize<Sim.Core.Scouting.DispatchScoutIntent>(payload, Options),
            "PlaceCanalIntent"             => JsonSerializer.Deserialize<Sim.Core.Canals.PlaceCanalIntent>(payload, Options),
            "LootCacheIntent"              => JsonSerializer.Deserialize<Sim.Core.Caches.LootCacheIntent>(payload, Options),
            _ => throw new InvalidOperationException(
                $"Unknown intent type-name '{typeName}'. The intent was logged by a build " +
                $"this binary doesn't know about, or the durable type-name was renamed " +
                $"(it must be frozen — see IntentJson.TypeNames)."),
        };
        return intent ?? throw new InvalidDataException(
            $"Failed to deserialize {typeName} payload (System.Text.Json returned null).");
    }
}
