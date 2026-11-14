namespace Loom.Core.Restore;

/// <summary>
/// Restricts what an automatic restore may re-add. Manual restores use <see cref="Permissive"/>,
/// automatic restores pass the check's RestoreCandidates so user-disabled bundles are never re-added
/// against the user's will (design 12.4).
/// </summary>
public sealed class RestoreConstraints
{
    public static readonly RestoreConstraints Permissive = new();

    /// <summary>Null = no restriction; otherwise only these bundle names may be inserted.</summary>
    public IReadOnlyCollection<string>? AllowedBundles { get; init; }

    /// <summary>Null = no restriction; otherwise only these dependency names may be added.</summary>
    public IReadOnlyCollection<string>? AllowedDependencies { get; init; }

    /// <summary>Null = follow the plan; false = report patch changes as skipped.</summary>
    public bool? AllowPatchRestore { get; init; }

    /// <summary>Null = follow the plan; false = report compatibility exemptions as skipped.</summary>
    public bool? AllowCompatibilityRestore { get; init; }

    public bool IsPermissive =>
        AllowedBundles is null && AllowedDependencies is null
        && AllowPatchRestore is null && AllowCompatibilityRestore is null;
}
