namespace BuildingOS.Shared.Helpers;

public static class DateTimeExtension
{
    /// <summary>
    /// UnixTime における最小値に対応する DateTime
    /// </summary>
    public static readonly DateTime UnixTimeZero = new DateTime(1970, 1, 1);
    
    /// <summary>
    /// UnixTime String に変換
    /// </summary>
    /// <param name="self"></param>
    /// <returns></returns>
    public static string ToUnixTime(this DateTime self) => Math.Floor(self.Subtract(UnixTimeZero).TotalMilliseconds).ToString();
}