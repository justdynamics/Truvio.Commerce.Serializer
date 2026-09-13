namespace Truvio.Commerce.Serializer.AdminUI.Commands;

/// <summary>
/// Deprecated name of <see cref="DeserializeCommand"/>, kept through the 1.0 beta so
/// <c>POST /Admin/Api/SerializerDeserialize</c> keeps working. Same parameters and behaviour; the
/// response message ends with a deprecation notice. Removed in the 1.0.0 release.
/// </summary>
[Obsolete("Renamed to DeserializeCommand (API route 'Deserialize'). This alias is removed in the 1.0.0 release.")]
public sealed class SerializerDeserializeCommand : DeserializeCommand
{
    internal override string? DeprecatedRoute => "SerializerDeserialize";
}
