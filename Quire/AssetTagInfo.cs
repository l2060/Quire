using System;

namespace Quire;

/// <summary>Identity and build time of a Tag observed in the loaded packages.</summary>
public sealed record AssetTagInfo(Guid TagUid, string Name, DateTimeOffset BuildTimeUtc);
