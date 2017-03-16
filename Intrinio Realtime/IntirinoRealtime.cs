using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using WebSocketSharp;
using System.Threading.Tasks;
using System.Reactive;
using System.Reactive.Linq;

namespace Intrinio_Realtime
{
    public class IntrinioRealtime : IDisposable
    {
        event Action<Exception> OnError;
        event Action<Quote> OnQuote;
        public static TimeSpan TOKEN_EXPIRATION_INTERVAL = TimeSpan.FromDays(7);
        public static TimeSpan HEARTBEAT_INTERVAL = TimeSpan.FromSeconds(20);
        public static TimeSpan[] SELF_HEAL_BACKOFFS = { TimeSpan.FromMilliseconds(0), TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(1000), TimeSpan.FromMilliseconds(2000), TimeSpan.FromMilliseconds(5000) };
        public static ushort WS_CLOSE_REASON_USER = 1000;
        public static string HOST = "realtime.intrinio.com";
        public static int PORT = 443;
        public static string WS_PROTOCOL = "wss";

        protected string token;
        protected WebSocket websocket;
        protected Dictionary<string, bool> channels = new Dictionary<string, bool>();
        protected IList<TimeSpan> selfHealBackoffs = new List<TimeSpan>(SELF_HEAL_BACKOFFS);
        protected System.Timers.Timer selfHealTimer;
        protected System.Timers.Timer tokenExpirationTimer;
        protected System.Timers.Timer heartbeatTimer;
        protected Options options;
        protected object connectionLock = new object();
        protected bool connected = false;

        public IntrinioRealtime(string username, string password, bool debug = false) : this(new Options() { username = username, password = password, debug = debug })
        {
        }

        public IntrinioRealtime(Options options)
        {
            this.options = options;

            connect();

            tokenExpirationTimer = new System.Timers.Timer(TOKEN_EXPIRATION_INTERVAL.TotalMilliseconds);
            tokenExpirationTimer.Elapsed += (t, e) => connect();
            tokenExpirationTimer.AutoReset = true;


            heartbeatTimer = new System.Timers.Timer(HEARTBEAT_INTERVAL.TotalMilliseconds);
            heartbeatTimer.Elapsed += (t, e) => heartbeat();
            heartbeatTimer.AutoReset = true;
        }

        protected void connect(bool rejoin = false)
        {
            this.debug("Connecting...");

            try
            {
                lock (this.connectionLock)
                {
                    refreshToken();
                    refreshWebsocket();
                }
            }
            catch
            {
                trySelfHeal();
            }

            if (rejoin)
            {
                stopSelfHeal();
                rejoinChannels();
            }

        }

        protected async void refreshToken()
        {
            debug("Requesting auth token...");
            try
            {
                using (var client = new System.Net.Http.HttpClient())
                {
                    var uriBuilder = new UriBuilder(Uri.UriSchemeHttps, HOST, PORT, "auth");
                    var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, uriBuilder.Uri);
                    request.Headers.Authorization = getAuthenticationHeader();
                    request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

                    var response = await client.SendAsync(request);

                    if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    {
                        throw new IntrinioRealtimeAuthorizationException();
                    }
                    else if (!response.IsSuccessStatusCode)
                    {
                        throw new IntrinioRealtimeAuthorizationException($"Could not get auth token: Status code { Enum.GetName(typeof(System.Net.HttpStatusCode), response.StatusCode)}");
                    }
                    else
                    {
                        this.token = Encoding.UTF8.GetString(Convert.FromBase64String(await response.Content.ReadAsStringAsync()));
                        this.debug("Received auth token!");
                    }

                }
            }
            catch (Exception e)
            {
                this.error(new IntrinioRealtimeException("Something happened while loading token", e));
            }
        }

        protected void refreshWebsocket()
        {
            this.debug("Establishing websocket...");


            if (this.websocket != null)
            {
                this.websocket.Close(WS_CLOSE_REASON_USER, "User terminated");
            }

            var uriBuilder = new UriBuilder(WS_PROTOCOL, HOST, PORT, "/socket/websocket");
            uriBuilder.Query = $"vsn=1.0.0&token={Uri.EscapeDataString(this.token)}";
            this.websocket = new WebSocket(uriBuilder.ToString());

            websocket.OnClose += (sender, e) =>
            {
                this.connected = true;
                debug("Websocket closed!");

                if (e.Code != WS_CLOSE_REASON_USER)
                {
                    trySelfHeal();
                }
            };


            this.websocket.OnError += (sender, e) =>
            {
                Console.Error.WriteLine("IntrinioRealtime | Websocket error: " + e);
            };

            this.websocket.OnMessage += (sender, e) =>
            {
                IntrinioMessage<Quote> message = Newtonsoft.Json.JsonConvert.DeserializeObject<IntrinioMessage<Quote>>(e.Data);
                if (message.Event == IntrinioMessage<Quote>.MessageType.QUOTE)
                {
                    var quote = message.Payload;
                    OnQuote.DynamicInvoke(quote);
                    debug("Quote: ", quote);
                }
                else
                {
                    debug("Non-quote message: ", e.Data);
                }

            };

            this.websocket.Connect();
            this.connected = true;
            this.debug("Websocket connected!");
        }

        protected void trySelfHeal()
        {
            this.log("No connection! Retrying...");

            var time = this.selfHealBackoffs[0];
            if (this.selfHealBackoffs.Count > 1)
            {
                this.selfHealBackoffs.RemoveAt(0);
            }

            if (this.selfHealTimer != null)
            {
                selfHealTimer.Stop();
                selfHealTimer.Close();
            }

            this.selfHealTimer = new System.Timers.Timer(time.TotalMilliseconds);
            heartbeatTimer.Elapsed += (t, e) => connect(true);
            heartbeatTimer.AutoReset = false;
        }



        protected void stopSelfHeal()
        {
            this.selfHealBackoffs = new List<TimeSpan>(SELF_HEAL_BACKOFFS);

            if (this.selfHealTimer != null)
            {
                selfHealTimer.Stop();
                selfHealTimer.Close();
                this.selfHealTimer = null;
            }
        }

        protected void rejoinChannels()
        {
            foreach (var entry in this.channels)
            {
                this.websocket.Send(Newtonsoft.Json.JsonConvert.SerializeObject(new IntrinioMessage()
                {
                    Topic = this.parseTopic(entry.Key),
                    Event = "phx_join"
                }));

                this.debug("Rejoined channel: ", entry.Key);
            }
        }

        protected void heartbeat()
        {
            lock (this.connectionLock)
            {
                this.websocket.Send(Newtonsoft.Json.JsonConvert.SerializeObject(new IntrinioMessage()
                {
                    Topic = "phoenix",
                    Event = "heartbeat"
                }));
            }
        }

        protected IEnumerable<string> parseChannels(params string[][] args)
        {
            return args.Aggregate(new List<string>(), (accumulator, array) =>
            {
                accumulator.AddRange(parseChannels(channels: array));
                return accumulator;
            });
        }

        protected IEnumerable<string> parseChannels(params string[] channels)
        {
            return channels.Select((channel) =>
            {
                if (channel.Length == 0 || channel.Length > 20)
                {
                    this.error(new IntrinioRealtimeException("Invalid channel provided"));
                }
                return channel.Trim();
            });
        }

        protected string parseTopic(string channel)
        {
            string topic;

            if (channel == "$lobby")
            {
                topic = "iex:lobby";
            }
            else if (channel == "$lobby_last_price")
            {
                topic = "iex:lobby:last_price";
            }
            else
            {
                topic = "iex:securities:" + channel;
            }
            return topic;
        }

        protected void log(params object[] parts)
        {
            var message = "IntrinioRealtime | ";
            foreach (var part in parts)
            {
                if (!(part is string))
                {
                    message += Newtonsoft.Json.JsonConvert.SerializeObject(part);
                }
                else
                {
                    message += (string)part;
                }
            }
            Console.WriteLine(message);
        }

        protected void debug(params object[] parts)
        {
            if (options.debug)
            {
                log(parts: parts);
            }
        }

        protected void error(Exception e)
        {
            OnError.DynamicInvoke(e);
            throw e;
        }

        private System.Net.Http.Headers.AuthenticationHeaderValue getAuthenticationHeader()
        {
            return new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.Default.GetBytes($"{options.username}:{options.password}")));
        }

        public void Dispose()
        {
            tokenExpirationTimer?.Stop();
            tokenExpirationTimer?.Close();

            heartbeatTimer?.Stop();
            heartbeatTimer?.Close();

            selfHealTimer?.Stop();
            selfHealTimer?.Close();

            this.websocket.Close(WS_CLOSE_REASON_USER, "User terminated");
        }

        public IObservable<Quote> join(params string[] args)
        {
            var channels = this.parseChannels(args);

            lock (connectionLock)
            {
                foreach (var channel in channels)
                {
                    this.channels[channel] = true;
                    this.websocket.Send(Newtonsoft.Json.JsonConvert.SerializeObject(new IntrinioMessage()
                    {
                        Topic = this.parseTopic(channel),
                        Event = "phx_join"
                    }));
                    this.debug("Joined channel: ", channel);
                }
            }

            return QuoteObservable.Where(quote => channels.Contains(quote.Ticker));
        }

        public void leave(params string[] args)
        {
            var channels = this.parseChannels(args);

            lock (connectionLock)
            {
                foreach (var channel in channels)
                {
                    this.channels.Remove(channel);
                    this.websocket.Send(Newtonsoft.Json.JsonConvert.SerializeObject(new IntrinioMessage()
                    {
                        Topic = this.parseTopic(channel),
                        Event = "phx_leave"
                    }));

                    this.debug("Left channel: ", channel);
                }
            }
        }

        public void leaveAll()
        {
            this.leave(args: channels.Keys.ToArray());
        }

        public IEnumerable<string> listConnectedChannels()
        {
            return channels.Keys;
        }

        public IObservable<Quote> QuoteObservable
        {
            get
            {
                return Observable.FromEventPattern<Quote>(this, "OnQuote").Select((e) => e.EventArgs);
            }
        }

        public IObservable<Exception> ErrorObservable
        {
            get
            {
                return Observable.FromEventPattern<Exception>(this, "OnError").Select((e) => e.EventArgs);
            }
        }

        public IObservable<Quote> getQuoteObservable(string channel)
        {
            return QuoteObservable.Where((quote) => quote.Ticker == channel);
        }
    }
}
