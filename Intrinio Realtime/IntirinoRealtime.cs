using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Intrinio_Realtime
{
    public class IntirinoRealtime
    {
        public static TimeSpan TOKEN_EXPIRATION_INTERVAL = TimeSpan.FromDays(7);
        public static TimeSpan HEARTBEAT_INTERVAL = TimeSpan.FromSeconds(20);
        public static TimeSpan[] SELF_HEAL_BACKOFFS = { TimeSpan.FromMilliseconds(0), TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(1000), TimeSpan.FromMilliseconds(2000), TimeSpan.FromMilliseconds(5000) };
        public static int WS_CLOSE_REASON_USER = 1000;
        public static string HOST = "realtime.intrinio.com";
        public static int PORT = 443;
        public static string WS_PROTOCOL = "wss";

        protected string token;
        protected WebSocket4Net.WebSocket websocket;
        protected Dictionary<string, bool> chanels = new Dictionary<string, bool>();

        public IntirinoRealtime(string username, string password, bool debug = false) 
        {

        }


    }
}
