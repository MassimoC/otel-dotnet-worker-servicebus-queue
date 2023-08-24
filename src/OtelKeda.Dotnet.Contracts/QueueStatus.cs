using Newtonsoft.Json;

namespace OtelKeda.Dotnet.Contracts
{
    public class QueueStatus
    {
        [JsonProperty]
        public long MessageCount { get; set; }
    }
}
