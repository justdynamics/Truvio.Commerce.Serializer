namespace Truvio.Commerce.Serializer.Configuration;

/// <summary>
/// What a deserialize does with a document (page, grid row, paragraph, area or SqlTable row) that
/// already exists on the host. Carried as <c>ownership.onExisting</c> in the document header and set
/// by a predicate's <c>onExisting</c> / <c>bodyOnExisting</c> keys or an <c>ownershipOverrides</c>
/// entry. A document that is missing on the host is always created, whatever its lifecycle.
/// </summary>
public enum ExistingDocumentLifecycle
{
    /// <summary>Source wins: every field is written from YAML. The default under Replace.</summary>
    Overwrite,

    /// <summary>Destination wins, field-level fill: only fields the host left empty are written. The default under Merge.</summary>
    Fill,

    /// <summary>
    /// Create-only: an existing document is left exactly as the host has it. No scalar, item
    /// field, parent, sort, permission or master link is written, and it is not link-resolved.
    /// </summary>
    Keep
}
