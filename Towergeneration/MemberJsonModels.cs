using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Towergeneration
{
    /// <summary>Root object in members_*.json export files.</summary>
    public sealed class MembersJsonFile
    {
        [JsonPropertyName("Members")]
        public List<MemberRecord> Members { get; set; } = new();
    }

    /// <summary>One angle or plate member between start and end coordinates (mm).</summary>
    public sealed class MemberRecord
    {
        [JsonPropertyName("Mark")]
        public string Mark { get; set; } = "";

        [JsonPropertyName("Type")]
        public string Type { get; set; } = "";

        [JsonPropertyName("Description")]
        public string Description { get; set; } = "";

        [JsonPropertyName("Xs")]
        public double Xs { get; set; }

        [JsonPropertyName("Ys")]
        public double Ys { get; set; }

        [JsonPropertyName("Zs")]
        public double Zs { get; set; }

        [JsonPropertyName("Xe")]
        public double Xe { get; set; }

        [JsonPropertyName("Ye")]
        public double Ye { get; set; }

        [JsonPropertyName("Ze")]
        public double Ze { get; set; }
    }
}
