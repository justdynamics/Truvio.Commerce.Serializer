namespace Truvio.Commerce.Serializer.AdminUI.Commands;

/// <summary>
/// Deprecated name of <see cref="SerializeCommand"/>, kept through the 1.0 beta so
/// <c>POST /Admin/Api/SerializerSerialize</c> keeps working. Same parameters and behaviour; the
/// response message ends with a deprecation notice. Removed in the 1.0.0 release.
/// </summary>
[Obsolete("Renamed to SerializeCommand (API route 'Serialize'). This alias is removed in the 1.0.0 release.")]
public sealed class SerializerSerializeCommand : SerializeCommand
{
    internal override string? DeprecatedRoute => "SerializerSerialize";
}
