using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vigil.Core.Protocol;

/// <summary>
/// The one set of JSON options for everything that crosses a process boundary.
/// </summary>
/// <remarks>
/// camelCase comes from the policy rather than per-property attributes, because the Rust side
/// gets its names from <c>rename_all = "camelCase"</c> the same way. ContractTests is what proves
/// the two algorithms agree on every field vigil actually sends, which is the only claim that
/// matters; a disagreement on some hypothetical name would be caught the moment it is used.
/// </remarks>
public static class VigilJson
{
    /// <summary>
    /// The source-generated context's options: no reflection, so the NativeAOT build has
    /// nothing to trim away. A type outside
    /// <see cref="VigilJsonContext"/> fails here at run time, in the tests first.
    /// </summary>
    public static JsonSerializerOptions Options => VigilJsonContext.Default.Options;
}

/// <summary>
/// Every type vigil-core reads or writes as JSON, generated at compile time. The roots are
/// listed; what they contain (sensors, pools, alerts) is generated with them.
/// </summary>
/// <remarks>
/// The settings are VigilJson's. A null is meaningful here: <c>"celsiusOrNull": null</c> is how
/// an unreadable sensor is reported, and omitting it would make the field absent rather than
/// null. Nothing is indented: every byte goes to one consumer, 13 drives every two seconds.
/// </remarks>
[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,

    // `required` only says a property is present: without this, "sensors": null deserializes
    // into a property declared non-null, and the first thing to enumerate it throws on the
    // runtime's loop. With it the snapshot fails to parse, which the client already survives.
    RespectNullableAnnotations = true,
    WriteIndented = false)]
[JsonSerializable(typeof(Snapshot))]
[JsonSerializable(typeof(LiveMessage))]
[JsonSerializable(typeof(HistoryResponse))]
[JsonSerializable(typeof(StatusResponse))]
[JsonSerializable(typeof(RangeKey))]
[JsonSerializable(typeof(List<EventRecord>))]
[JsonSerializable(typeof(EventRecord[]))]
[JsonSerializable(typeof(List<DriveHealthRecord>))]
public sealed partial class VigilJsonContext : JsonSerializerContext;
