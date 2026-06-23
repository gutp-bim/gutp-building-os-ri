using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace BuildingOs.Shared
{
    public static class JsonSerializerHelper
    {
        public static readonly JsonSerializerOptions JsonSerializerOptions = new()
        {
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.All), WriteIndented = false,
        };
    }
}