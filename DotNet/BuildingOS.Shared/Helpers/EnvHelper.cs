namespace BuildingOS.Shared.Helpers;

public class EnvHelper
{
    public static string GetEnvironmentVariable(string name) => Environment.GetEnvironmentVariable(name) ??
                                                                 throw new Exception($"環境変数 {name} が設定されていません");

    public static int GetEnvironmentVariableAsInt(string name)
    {
        if (int.TryParse(Environment.GetEnvironmentVariable(name) ??
                         throw new Exception($"環境変数 {name} が設定されていません"), out var result))
            return result;
        throw new Exception($"環境変数 {name} が int 型ではありません");
    }

    public static bool GetEnvironmentVariableAsBool(string name)
    {
        if (bool.TryParse(Environment.GetEnvironmentVariable(name) ??
                          throw new Exception($"環境変数 {name} が設定されていません"), out var result))
            return result;
        throw new Exception($"環境変数 {name} が int 型ではありません");
    }
}