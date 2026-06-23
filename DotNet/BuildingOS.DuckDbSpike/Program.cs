using BuildingOS.DuckDbSpike;

// Usage:
//   dotnet run -- \
//     --minio http://localhost:9000 \
//     --access-key buildingos --secret-key buildingos123 \
//     --bucket lake --building B01 --point P001 \
//     --start 2025-11-01T00:00:00Z --end 2025-11-01T23:59:59Z \
//     --iterations 3

var argMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
for (var i = 0; i < Environment.GetCommandLineArgs().Length - 1; i++)
{
    var arg = Environment.GetCommandLineArgs()[i];
    if (arg.StartsWith("--"))
        argMap[arg[2..]] = Environment.GetCommandLineArgs()[i + 1];
}

string Get(string key, string def = "") => argMap.TryGetValue(key, out var v) ? v : def;

var cfg = new BenchmarkRunner.Config(
    MinioEndpoint: Get("minio", "http://localhost:9000"),
    AccessKey:     Get("access-key", "buildingos"),
    SecretKey:     Get("secret-key", "buildingos123"),
    Bucket:        Get("bucket", "lake"),
    Building:      Get("building", "B01"),
    PointId:       Get("point", "P001"),
    Start:         DateTime.TryParse(Get("start"), out var s) ? s.ToUniversalTime() : DateTime.UtcNow.AddHours(-24),
    End:           DateTime.TryParse(Get("end"),   out var e) ? e.ToUniversalTime() : DateTime.UtcNow,
    Iterations:    int.TryParse(Get("iterations", "3"), out var it) ? it : 3);

await BenchmarkRunner.RunAsync(cfg);
