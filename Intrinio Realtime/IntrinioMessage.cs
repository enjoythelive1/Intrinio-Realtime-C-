using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Intrinio_Realtime
{
    class IntrinioMessage<T>
    {
        public class MessageType
        {
            public const string QUOTE = "quote";
            public const string HEARTBEAT = "heartbeat";
        }

        [Newtonsoft.Json.JsonProperty("topic")]
        public string Topic { get; set; }
        [Newtonsoft.Json.JsonProperty("event")]
        public string Event { get; set; }
        [Newtonsoft.Json.JsonProperty("payload")]
        public T Payload { get; set; } = default(T);
        [Newtonsoft.Json.JsonProperty("ref")]
        public object Ref { get; set; }
    }

    class IntrinioMessage: IntrinioMessage<object>
    {

    }
}
