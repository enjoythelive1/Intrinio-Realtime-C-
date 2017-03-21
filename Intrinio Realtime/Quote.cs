using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace IntrinioRealtime
{
    public class Quote
    {
        [Newtonsoft.Json.JsonProperty("ticker")]
        public string Ticker { get; set; }
        [Newtonsoft.Json.JsonProperty("type")]
        public string Type { get; set; }
        [Newtonsoft.Json.JsonProperty("price")]
        public decimal Price { get; set; }
        [Newtonsoft.Json.JsonProperty("size")]
        public string Size { get; set; }
        [Newtonsoft.Json.JsonProperty("timestamp")]
        public long Timestamp { get; set; }
    }
}
