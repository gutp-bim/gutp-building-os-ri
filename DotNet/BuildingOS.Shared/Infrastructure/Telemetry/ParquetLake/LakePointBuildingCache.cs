namespace BuildingOS.Shared.Infrastructure.Telemetry.ParquetLake;

/// <summary>
/// Process-wide switch over the lake reader's learned point → building map (#273, #527). The map
/// prunes a point's reads to the one building it was found in, which assumes a point's partition key
/// does not change. When it does (the twin's topology changed, or #527 moved the key from the
/// <c>sbco:building</c> literal to the topology's building), <see cref="Reset"/> forgets everything
/// learned so far, so later reads scan every building again and re-learn. The reader also detects a
/// point it finds under two buildings by itself, warns, and stops pruning it.
/// </summary>
public static class LakePointBuildingCache
{
    private static long _generation;

    /// <summary>Folded into every cache key: a reset makes all earlier entries unreachable at once.</summary>
    internal static long Generation => Interlocked.Read(ref _generation);

    /// <summary>Forgets every learned point → building entry in this process.</summary>
    public static void Reset() => Interlocked.Increment(ref _generation);
}
