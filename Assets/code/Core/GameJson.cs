using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Slopbox;

internal static class GameJson
{
    // Desktop builds disable the implicit reflection resolver. Game assemblies retain their metadata.
    internal static readonly JsonSerializerOptions Options = new()
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}
