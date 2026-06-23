using ParityHarness.Commands;

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    PrintHelp();
    return 0;
}

var command = args[0].ToLowerInvariant();
var remaining = args[1..];

return command switch
{
    "golden" => RunGolden(remaining),
    "api"    => await RunApiAsync(remaining),
    "self"   => await RunSelfAsync(remaining),
    _ => Error($"Unknown command '{command}'. Use: golden | api | self"),
};

static int RunGolden(string[] args)
{
    var goldenDir  = GetFlag(args, "--golden-dir") ?? "../../tests/golden/connector";
    var actualDir  = GetFlag(args, "--actual-dir");
    var reportDir  = GetFlag(args, "--report-dir") ?? "./reports";
    return GoldenCommand.Run(goldenDir, actualDir, reportDir);
}

static async Task<int> RunApiAsync(string[] args)
{
    var urlA      = GetFlag(args, "--url-a") ?? throw new InvalidOperationException("--url-a required");
    var urlB      = GetFlag(args, "--url-b") ?? throw new InvalidOperationException("--url-b required");
    var rawPaths  = GetFlag(args, "--paths");
    var paths     = rawPaths?.Split(',', StringSplitOptions.RemoveEmptyEntries) ?? [];
    var goldenDir = GetFlag(args, "--golden-dir");
    var reportDir = GetFlag(args, "--report-dir") ?? "./reports";
    return await ApiCommand.RunAsync(urlA, urlB, paths, goldenDir, reportDir);
}

static async Task<int> RunSelfAsync(string[] args)
{
    var baseUrl   = GetFlag(args, "--base-url") ?? throw new InvalidOperationException("--base-url required");
    var rawPaths  = GetFlag(args, "--paths");
    var paths     = rawPaths?.Split(',', StringSplitOptions.RemoveEmptyEntries) ?? [];
    var reportDir = GetFlag(args, "--report-dir") ?? "./reports";
    return await SelfCommand.RunAsync(baseUrl, paths, reportDir);
}

static string? GetFlag(string[] args, string flag)
{
    for (var i = 0; i < args.Length - 1; i++)
        if (args[i] == flag) return args[i + 1];
    return null;
}

static int Error(string msg)
{
    Console.Error.WriteLine($"Error: {msg}");
    PrintHelp();
    return 1;
}

static void PrintHelp()
{
    Console.WriteLine("""
        Building OS Parity Harness

        USAGE:
          parity-harness golden [--golden-dir <path>] [--actual-dir <path>] [--report-dir <path>]
          parity-harness api    --url-a <url> --url-b <url> [--paths <p1,p2>] [--golden-dir <path>] [--report-dir <path>]
          parity-harness self   --base-url <url> [--paths <p1,p2>] [--report-dir <path>]

        COMMANDS:
          golden   Compare JSON files in --actual-dir against golden files in --golden-dir.
                   If --actual-dir is omitted, verifies golden files are self-consistent.
          api      Compare responses from two API endpoints (url-a vs url-b).
          self     Send the same request twice to verify idempotency (baseline = 100% match).

        OPTIONS:
          --golden-dir   Directory of golden JSON files (default: ../../tests/golden/connector)
          --actual-dir   Directory of actual JSON output files (golden mode)
          --url-a        First API base URL (api mode)
          --url-b        Second API base URL (api mode)
          --base-url     API base URL for self-comparison (self mode)
          --paths        Comma-separated API paths (default: built-in path list)
          --report-dir   Output directory for reports (default: ./reports)

        EXIT CODES:
          0  All scenarios passed
          1  One or more scenarios failed
          2  Configuration error (missing required args or directories)

        EXAMPLES:
          # Verify golden files are self-consistent (Azure vs Azure baseline)
          dotnet run -- golden --golden-dir ../../tests/golden/connector

          # Compare actual connector output against golden files
          dotnet run -- golden --golden-dir ../../tests/golden --actual-dir /tmp/actual

          # Compare Azure API vs OSS API
          dotnet run -- api --url-a https://api-azure.example.com --url-b https://api-oss.example.com

          # Self-comparison on local dev server (should always be 100%)
          dotnet run -- self --base-url http://localhost:5000 --paths /health,/buildings
        """);
}
