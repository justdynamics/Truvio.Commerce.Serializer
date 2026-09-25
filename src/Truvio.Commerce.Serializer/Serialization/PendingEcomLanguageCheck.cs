namespace Truvio.Commerce.Serializer.Serialization;

/// <summary>
/// Engine issue #35: an area whose <c>AreaEcomLanguageID</c> did not exist on target when the
/// area was written. Content entries can run before the SqlTable entry that delivers the
/// language (LINK-02 ordering), so the orchestrator re-checks every pending record after the
/// whole run and warns (strict: fails) only for a language that is still missing.
/// </summary>
public sealed record PendingEcomLanguageCheck(int AreaId, string EcomLanguageId);
