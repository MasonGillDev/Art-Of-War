using System.Text.Json;
using Sim.Core.Groups;
using Sim.Core.Intents;
using Sim.Core.Logistics;
using Sim.Core.Movement;

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
            _ => throw new InvalidOperationException(
                $"Unknown intent type-name '{typeName}'. The intent was logged by a build " +
                $"this binary doesn't know about, or the durable type-name was renamed " +
                $"(it must be frozen — see IntentJson.TypeNames)."),
        };
        return intent ?? throw new InvalidDataException(
            $"Failed to deserialize {typeName} payload (System.Text.Json returned null).");
    }
}
