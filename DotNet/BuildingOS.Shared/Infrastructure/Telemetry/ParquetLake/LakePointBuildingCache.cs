namespace BuildingOS.Shared.Infrastructure.Telemetry.ParquetLake;

/// <summary>
/// Process-wide generation of the lake reader's learned point → building map (#273). Bumped when
/// partition keys are recorded as changed (#527), so every entry learned before is dropped at once.
/// </summary>
internal static class LakePointBuildingCache
{
    private static long _generation;

    /// <summary>Folded into every cache key: a reset makes all earlier entries unreachable at once.</summary>
    internal static long Generation => Interlocked.Read(ref _generation);

    /// <summary>Forgets every learned point → building entry in this process.</summary>
    internal static void Reset() => Interlocked.Increment(ref _generation);
}
