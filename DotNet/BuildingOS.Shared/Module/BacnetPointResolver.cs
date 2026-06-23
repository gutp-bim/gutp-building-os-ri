using TinyCsvParser;
using System.Reflection;

namespace BuildingOS.Shared.Module;

[Serializable]
public class BacnetPointInfo
{
    public string DeviceIdBacnet { get; set; }
    public int InstanceNoBacnet { get; set; }
    public string Name { get; set; }
    public string ObjectTypeBacnet { get; set; }
    public string PointId { get; set; }
    public string PointName { get; set; }
    public string PointSpecification { get; set; }
}

public class BacnetPointResolver
{
    private readonly List<BacnetPointInfo> _pointInfos = [];
    private const string BacnetPointInfoFileName = "bacnet-point-map.csv";

    public async Task<BacnetPointInfo?> GetPointInfoAsync(string bacnetDeviceId, string instanceNo, string objectType)
    {
        if (_pointInfos.Count == 0)
        {
            var result = await ReadPointIdInfosAsync();
            _pointInfos.AddRange(result);
        }

        var pointIds = _pointInfos
            .Where(x => x.DeviceIdBacnet == bacnetDeviceId &&
                        x.InstanceNoBacnet.ToString() == instanceNo &&
                        x.ObjectTypeBacnet == objectType)
            .ToArray();

        if (pointIds.Length == 0) return null;

        return pointIds.First();
    }

    private async Task<BacnetPointInfo[]> ReadPointIdInfosAsync()
    {
        // 1. まずEmbeddedResourceから読み込みを試行
        try
        {
            return await ReadPointIdInfosFromEmbeddedResourceAsync();
        }
        catch (Exception ex)
        {
            // EmbeddedResourceからの読み込みに失敗した場合はファイルシステムから読み込み
            Console.WriteLine($"Failed to read from embedded resource: {ex.Message}. Trying file system...");
        }

        // 2. ファイルシステムから読み込み
        var filePath = GetBacnetPointInfoFilePath();
        return await ReadPointIdInfosFromCsvAsync(filePath);
    }

    private async Task<BacnetPointInfo[]> ReadPointIdInfosFromEmbeddedResourceAsync()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(x => x.EndsWith(BacnetPointInfoFileName));

        if (string.IsNullOrEmpty(resourceName))
        {
            throw new FileNotFoundException($"Embedded resource '{BacnetPointInfoFileName}' not found in assembly.");
        }

        await using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
        {
            throw new FileNotFoundException($"Failed to get embedded resource stream for '{resourceName}'.");
        }

        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
        var csvContent = await reader.ReadToEndAsync();

        return ParseCsvContent(csvContent);
    }

    private BacnetPointInfo[] ParseCsvContent(string csvContent)
    {
        var options = new CsvParserOptions(skipHeader: true, fieldsSeparator: ',');
        var mapper = new BacnetPointInfoMapping();
        var csvParser = new CsvParser<BacnetPointInfo>(options, mapper);

        return csvParser
            .ReadFromString(new CsvReaderOptions(["\n"]), csvContent)
            .Select(x => x.Result)
            .ToArray();
    }

    private string GetBacnetPointInfoFilePath()
    {
        // Azure Function環境対応: 複数のパスパターンを試行する
        var possiblePaths = new[]
        {
            // 1. 現在のディレクトリ/Data配下
            Path.Combine(Directory.GetCurrentDirectory(), "Data", BacnetPointInfoFileName),
            
            // 2. アセンブリの場所/Data配下
            Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "Data", BacnetPointInfoFileName),
            
            // 3. アセンブリの場所と同じディレクトリ
            Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), BacnetPointInfoFileName),
            
            // 4. Azure Functions の home/site/wwwroot/Data 配下
            Path.Combine(Environment.GetEnvironmentVariable("HOME") ?? "", "site", "wwwroot", "Data", BacnetPointInfoFileName),
            
            // 5. Azure Functions の現在のディレクトリのParent/Data配下
            Path.Combine(Directory.GetParent(Directory.GetCurrentDirectory())?.FullName ?? "", "Data", BacnetPointInfoFileName)
        };

        foreach (var path in possiblePaths)
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                return path;
            }
        }

        // ファイルが見つからない場合、最初のパスを返してエラーメッセージを分かりやすくする
        throw new FileNotFoundException(
            $"BacNet point mapping file not found. Searched paths: {string.Join(", ", possiblePaths.Where(p => !string.IsNullOrEmpty(p)))}");
    }

    private async Task<BacnetPointInfo[]> ReadPointIdInfosFromCsvAsync(string filePath)
    {
        // CsvParserとマッピングを作成
        var options = new CsvParserOptions(skipHeader: true, fieldsSeparator: ',');
        var mapper = new BacnetPointInfoMapping();
        var csvParser = new CsvParser<BacnetPointInfo>(options, mapper);

        // CSVファイルを読み込んで解析
        return csvParser
            .ReadFromFile(filePath, System.Text.Encoding.UTF8)
            .Select(x => x.Result)
            .ToArray();
    }
}