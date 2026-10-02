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
        [typeof(BuildIntent)]             = "BuildIntent",
        [typeof(AssignBuildersIntent)]    = "AssignBuildersIntent",
        [typeof(AssignWorkersIntent)]     = "AssignWorkersIntent",
        [typeof(UnassignWorkersIntent)]   = "UnassignWorkersIntent",
        [typeof(HaulIntent)]              = "HaulIntent",
        [typeof(FormGroupIntent)]         = "FormGroupIntent",
        [typeof(MoveGroupIntent)]         = "MoveGroupIntent",
        [typeof(DisbandGroupIntent)]      = "DisbandGroupIntent",
        // M46 — groups as records (docs/m46-groups-spec.md).
        [typeof(CreateGroupIntent)]       = "CreateGroupIntent",
        [typeof(RenameGroupIntent)]       = "RenameGroupIntent",
        [typeof(SetGroupParentIntent)]    = "SetGroupParentIntent",
        [typeof(AddToGroupIntent)]        = "AddToGroupIntent",
        [typeof(DeleteGroupIntent)]       = "DeleteGroupIntent",
        [typeof(MusterGroupIntent)]       = "MusterGroupIntent",
        [typeof(DismissGroupIntent)]      = "DismissGroupIntent",
        [typeof(MergeGroupsIntent)]       = "MergeGroupsIntent",
        [typeof(SplitGroupIntent)]        = "SplitGroupIntent",
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
        // Automation substrate — the claims ledger. Server-internal like the
        // cursor above (wire-rejected, driver-submitted) but DURABLE: a pull
        // that was in flight at snapshot time must stay committed across
        // recovery, and driverless replay must reproduce the ledger exactly.
        [typeof(Sim.Core.Automation.ClaimUnitIntent)]             = "ClaimUnitIntent",
        // Automation substrate Layer 1 — the universal order record.
        // Set/Clear are ordinary player intents (all three authoring
        // surfaces compile to Set); OrderStatusIntent is server-internal
        // like the claim/cursor intents but equally durable.
        [typeof(Sim.Core.Automation.SetOrderIntent)]              = "SetOrderIntent",
        [typeof(Sim.Core.Automation.ClearOrderIntent)]            = "ClearOrderIntent",
        [typeof(Sim.Core.Automation.OrderStatusIntent)]           = "OrderStatusIntent",
        // M36 — the haul queue (docs/hauling-queue-and-routes.md). Set/Clear
        // are player intents; Requeue is server-internal (driver-submitted,
        // wire-rejected) and durable so recovery keeps the queue order.
        [typeof(Sim.Core.Hauling.SetHaulJobIntent)]              = "SetHaulJobIntent",
        [typeof(Sim.Core.Hauling.ClearHaulJobIntent)]            = "ClearHaulJobIntent",
        [typeof(Sim.Core.Hauling.RequeueHaulJobIntent)]          = "RequeueHaulJobIntent",
        // M36 — named routes. Set/Clear/Add/Remove are player intents;
        // ServeRouteStop is server-internal (driver-submitted) and durable.
        [typeof(Sim.Core.Hauling.SetHaulRouteIntent)]            = "SetHaulRouteIntent",
        [typeof(Sim.Core.Hauling.ClearHaulRouteIntent)]          = "ClearHaulRouteIntent",
        [typeof(Sim.Core.Hauling.AddRouteCrewIntent)]            = "AddRouteCrewIntent",
        [typeof(Sim.Core.Hauling.RemoveRouteCrewIntent)]         = "RemoveRouteCrewIntent",
        [typeof(Sim.Core.Hauling.ServeRouteStopIntent)]          = "ServeRouteStopIntent",
        // M45 — edit a running route in place, name it, change a queued job.
        [typeof(Sim.Core.Hauling.UpdateHaulRouteIntent)]         = "UpdateHaulRouteIntent",
        [typeof(Sim.Core.Hauling.RenameHaulRouteIntent)]         = "RenameHaulRouteIntent",
        [typeof(Sim.Core.Hauling.UpdateHaulJobIntent)]           = "UpdateHaulJobIntent",
        // M20 — scouting dispatch.
        [typeof(Sim.Core.Scouting.DispatchScoutIntent)]          = "DispatchScoutIntent",
        [typeof(Sim.Core.Mining.SurveyIntent)]                   = "SurveyIntent",
        [typeof(Sim.Core.Scouting.ActivateIdolIntent)]           = "ActivateIdolIntent",   // M38
        // M21 — canal digging (whole-path terrain-mutation build).
        [typeof(Sim.Core.Canals.PlaceCanalIntent)]               = "PlaceCanalIntent",
        // M23 — loot a discovered cache.
        [typeof(Sim.Core.Caches.LootCacheIntent)]                = "LootCacheIntent",
        // M26 — wall line (whole-line placement, per-tile sites).
        [typeof(Sim.Core.Fortifications.PlaceWallIntent)]        = "PlaceWallIntent",
        // M26 — reclaim razed ground (rubble → clearing job → empty tile).
        [typeof(Sim.Core.Sieges.ClearRubbleIntent)]              = "ClearRubbleIntent",
        // Pull down your own structure, instantly, no refund (docs/demolish.md).
        [typeof(Sim.Core.Sieges.DemolishStructureIntent)]        = "DemolishStructureIntent",
        // M29 — chase a hostile down. Player-facing (the manual "run them
        // down") AND the verb the patrol driver submits; durable either way,
        // since the whole chase unfolds from this one row on replay.
        [typeof(Sim.Core.Combat.EngageUnitIntent)]               = "EngageUnitIntent",
        // M41 — battlefield orders and doctrine (docs/battlefield-grid.md).
        [typeof(Sim.Core.Battlefields.SetBattleOrderIntent)]     = "SetBattleOrderIntent",
        [typeof(Sim.Core.Battlefields.SetBattleDoctrineIntent)]  = "SetBattleDoctrineIntent",
        // M42 — a drawn subtile route (docs/subtile-movement.md).
        [typeof(Sim.Core.Battlefields.SubtileRouteIntent)]       = "SubtileRouteIntent",
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
            "BuildIntent"            => JsonSerializer.Deserialize<BuildIntent>(payload, Options),
            "AssignBuildersIntent"   => JsonSerializer.Deserialize<AssignBuildersIntent>(payload, Options),
            "AssignWorkersIntent"    => JsonSerializer.Deserialize<AssignWorkersIntent>(payload, Options),
            "UnassignWorkersIntent"  => JsonSerializer.Deserialize<UnassignWorkersIntent>(payload, Options),
            "HaulIntent"             => JsonSerializer.Deserialize<HaulIntent>(payload, Options),
            "FormGroupIntent"        => JsonSerializer.Deserialize<FormGroupIntent>(payload, Options),
            "MoveGroupIntent"        => JsonSerializer.Deserialize<MoveGroupIntent>(payload, Options),
            "DisbandGroupIntent"     => JsonSerializer.Deserialize<DisbandGroupIntent>(payload, Options),
            "CreateGroupIntent"      => JsonSerializer.Deserialize<CreateGroupIntent>(payload, Options),
            "RenameGroupIntent"      => JsonSerializer.Deserialize<RenameGroupIntent>(payload, Options),
            "SetGroupParentIntent"   => JsonSerializer.Deserialize<SetGroupParentIntent>(payload, Options),
            "AddToGroupIntent"       => JsonSerializer.Deserialize<AddToGroupIntent>(payload, Options),
            "DeleteGroupIntent"      => JsonSerializer.Deserialize<DeleteGroupIntent>(payload, Options),
            "MusterGroupIntent"      => JsonSerializer.Deserialize<MusterGroupIntent>(payload, Options),
            "DismissGroupIntent"     => JsonSerializer.Deserialize<DismissGroupIntent>(payload, Options),
            "MergeGroupsIntent"      => JsonSerializer.Deserialize<MergeGroupsIntent>(payload, Options),
            "SplitGroupIntent"       => JsonSerializer.Deserialize<SplitGroupIntent>(payload, Options),
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
            "ClaimUnitIntent"              => JsonSerializer.Deserialize<Sim.Core.Automation.ClaimUnitIntent>(payload, Options),
            "SetOrderIntent"               => JsonSerializer.Deserialize<Sim.Core.Automation.SetOrderIntent>(payload, Options),
            "ClearOrderIntent"             => JsonSerializer.Deserialize<Sim.Core.Automation.ClearOrderIntent>(payload, Options),
            "OrderStatusIntent"            => JsonSerializer.Deserialize<Sim.Core.Automation.OrderStatusIntent>(payload, Options),
            "SetHaulJobIntent"             => JsonSerializer.Deserialize<Sim.Core.Hauling.SetHaulJobIntent>(payload, Options),
            "ClearHaulJobIntent"           => JsonSerializer.Deserialize<Sim.Core.Hauling.ClearHaulJobIntent>(payload, Options),
            "RequeueHaulJobIntent"         => JsonSerializer.Deserialize<Sim.Core.Hauling.RequeueHaulJobIntent>(payload, Options),
            "SetHaulRouteIntent"           => JsonSerializer.Deserialize<Sim.Core.Hauling.SetHaulRouteIntent>(payload, Options),
            "ClearHaulRouteIntent"         => JsonSerializer.Deserialize<Sim.Core.Hauling.ClearHaulRouteIntent>(payload, Options),
            "AddRouteCrewIntent"           => JsonSerializer.Deserialize<Sim.Core.Hauling.AddRouteCrewIntent>(payload, Options),
            "RemoveRouteCrewIntent"        => JsonSerializer.Deserialize<Sim.Core.Hauling.RemoveRouteCrewIntent>(payload, Options),
            "ServeRouteStopIntent"         => JsonSerializer.Deserialize<Sim.Core.Hauling.ServeRouteStopIntent>(payload, Options),
            "UpdateHaulRouteIntent"        => JsonSerializer.Deserialize<Sim.Core.Hauling.UpdateHaulRouteIntent>(payload, Options),
            "RenameHaulRouteIntent"        => JsonSerializer.Deserialize<Sim.Core.Hauling.RenameHaulRouteIntent>(payload, Options),
            "UpdateHaulJobIntent"          => JsonSerializer.Deserialize<Sim.Core.Hauling.UpdateHaulJobIntent>(payload, Options),
            "DispatchScoutIntent"          => JsonSerializer.Deserialize<Sim.Core.Scouting.DispatchScoutIntent>(payload, Options),
            "SurveyIntent"                 => JsonSerializer.Deserialize<Sim.Core.Mining.SurveyIntent>(payload, Options),
            "ActivateIdolIntent"           => JsonSerializer.Deserialize<Sim.Core.Scouting.ActivateIdolIntent>(payload, Options),
            "PlaceCanalIntent"             => JsonSerializer.Deserialize<Sim.Core.Canals.PlaceCanalIntent>(payload, Options),
            "LootCacheIntent"              => JsonSerializer.Deserialize<Sim.Core.Caches.LootCacheIntent>(payload, Options),
            "PlaceWallIntent"              => JsonSerializer.Deserialize<Sim.Core.Fortifications.PlaceWallIntent>(payload, Options),
            "ClearRubbleIntent"            => JsonSerializer.Deserialize<Sim.Core.Sieges.ClearRubbleIntent>(payload, Options),
            "DemolishStructureIntent"      => JsonSerializer.Deserialize<Sim.Core.Sieges.DemolishStructureIntent>(payload, Options),
            "EngageUnitIntent"             => JsonSerializer.Deserialize<Sim.Core.Combat.EngageUnitIntent>(payload, Options),
            "SetBattleOrderIntent"    => JsonSerializer.Deserialize<Sim.Core.Battlefields.SetBattleOrderIntent>(payload, Options),
            "SubtileRouteIntent"      => JsonSerializer.Deserialize<Sim.Core.Battlefields.SubtileRouteIntent>(payload, Options),
            "SetBattleDoctrineIntent" => JsonSerializer.Deserialize<Sim.Core.Battlefields.SetBattleDoctrineIntent>(payload, Options),
            _ => throw new InvalidOperationException(
                $"Unknown intent type-name '{typeName}'. The intent was logged by a build " +
                $"this binary doesn't know about, or the durable type-name was renamed " +
                $"(it must be frozen — see IntentJson.TypeNames)."),
        };
        return intent ?? throw new InvalidDataException(
            $"Failed to deserialize {typeName} payload (System.Text.Json returned null).");
    }
}
