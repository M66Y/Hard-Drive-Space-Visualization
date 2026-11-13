namespace DiskAtlas;

// Fixed diagnostic categories only. No exception text, paths, object identity
// or user-dependent counters may be placed in the worker failure envelope.
internal enum FastScanFailureDetail
{
    None, NamespaceEntryLimit, NamespaceNameLimit, NamespacePathLimit, NamespaceQueryLimit,
    MftPageLimit, BitmapPageLimit, NativeObjectLimit, RawRequestLimit,
    MftExtensionsUnsupported, MftMappingUnsupported, GeometryUnsupported,
    MftAttributeListUnsupported, MftMetadataUnsupported, MftMetadataBudget,
    MftMetadataRead, MftBitmapRead, MftDataRead, MftBitmapValidation,
    MftIndexValidation, NamespaceValidation, UnexpectedPipelineFailure,
    IndexInput, IndexNamespaceIncomplete, IndexMap, IndexIdentity,
    IndexSlotOutsideMap, IndexDirectoryAlias, IndexSlotReused, IndexAllocationRead,
    IndexBoundary, IndexOverflow, IndexDecode, IndexCoverage, IndexNegative
}

internal sealed record FastScanFailureContext(FastScanWireStage Stage, FastScanFailureDetail Detail,
    int? NativeError = null);
