using System.Text.Json;

namespace Sim.Server;

// One shared JSON configuration for the whole HTTP surface. CamelCase on the wire,
// case-insensitive on read, so the contract matches the Unity client's Wire.cs.
internal static class ServerJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };
}
