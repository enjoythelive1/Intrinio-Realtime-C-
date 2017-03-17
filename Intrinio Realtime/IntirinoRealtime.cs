using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Net.WebSockets;
using System.Threading.Tasks;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading;
using System.Net;

namespace Intrinio_Realtime
{
    public class IntrinioRealtime : IDisposable
    {
        event EventHandler<Exception> OnError;
        event EventHandler<Quote> OnQuote;
        public static TimeSpan TOKEN_EXPIRATION_INTERVAL = TimeSpan.FromDays(7);
        public static TimeSpan HEARTBEAT_INTERVAL = TimeSpan.FromSeconds(20);
        public static TimeSpan[] SELF_HEAL_BACKOFFS = { TimeSpan.FromMilliseconds(0), TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(1000), TimeSpan.FromMilliseconds(2000), TimeSpan.FromMilliseconds(5000) };
        public static ushort WS_CLOSE_REASON_USER = 1000;
        public static string HOST = "realtime.intrinio.com";
        public static int PORT = 443;
        public static string WS_PROTOCOL = "wss";
        const int bufferSize = 0xff;

        protected string token;
        protected ClientWebSocket websocket;
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
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls
        | SecurityProtocolType.Tls11
        | SecurityProtocolType.Tls12
        | SecurityProtocolType.Ssl3;

            this.options = options;

            connect();

            tokenExpirationTimer = new System.Timers.Timer(TOKEN_EXPIRATION_INTERVAL.TotalMilliseconds);
            tokenExpirationTimer.Elapsed += (t, e) => connect();
            tokenExpirationTimer.AutoReset = true;
            tokenExpirationTimer.Start();


            heartbeatTimer = new System.Timers.Timer(HEARTBEAT_INTERVAL.TotalMilliseconds);
            heartbeatTimer.Elapsed += (t, e) => heartbeat();
            heartbeatTimer.AutoReset = true;
            tokenExpirationTimer.Start();
        }

        protected async void connect(bool rejoin = false)
        {
            this.debug("Connecting...");

            try
            {
                await refreshToken();
                await refreshWebsocket();
                if (rejoin)
                {
                    stopSelfHeal();
                }
                await rejoinChannels();
            }
            catch (Exception e)
            {
                error(e, false);
                trySelfHeal();
            }

        }

        protected async Task refreshToken()
        {
            debug("Requesting auth token...");
            try
            {
                var client = new System.Net.Http.HttpClient();

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
                    this.token = await response.Content.ReadAsStringAsync();
                    this.debug("Received auth token!");
                }

                client.Dispose();
            }
            catch (Exception e)
            {
                this.error(new IntrinioRealtimeException("Something happened while loading token", e));
            }
        }

        protected async Task refreshWebsocket()
        {
            this.debug("Establishing websocket...");


            if (this.websocket != null)
            {
                await this.websocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "User terminated", CancellationToken.None);
            }

            var uriBuilder = new UriBuilder(WS_PROTOCOL, HOST, PORT, "/socket/websocket");
            uriBuilder.Query = $"vsn=1.0.0&token={Uri.EscapeDataString(this.token)}";
            this.websocket = new ClientWebSocket();

            await this.websocket.ConnectAsync(uriBuilder.Uri, CancellationToken.None);
            this.debug("Websocket connected!");
            this.recieveMessages();
        }

        protected async void recieveMessages()
        {
            try
            {
                var buffer = new byte[bufferSize];
                while (this.websocket.State == WebSocketState.Open)
                {
                    WebSocketReceiveResult result;
                    var stringResult = new StringBuilder();
                    do
                    {
                        result = await websocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);

                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            await websocket.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None);
                            this.connected = false;
                        }
                        else
                        {
                            var str = Encoding.UTF8.GetString(buffer, 0, result.Count);
                            stringResult.Append(str);
                        }

                    } while (!result.EndOfMessage);

                    IntrinioMessage<Quote> message = Newtonsoft.Json.JsonConvert.DeserializeObject<IntrinioMessage<Quote>>(stringResult.ToString());
                    if (message.Event == IntrinioMessage<Quote>.MessageType.QUOTE)
                    {
                        var quote = message.Payload;
                        if (OnQuote != null)
                        {
                            OnQuote.DynamicInvoke(this, quote);
                        }
                        debug("Quote: ", quote);
                    }
                    else
                    {
                        debug("Non-quote message: ", stringResult.ToString());
                    }
                }
            }
            catch (Exception e)
            {
                trySelfHeal();
                debug("Websocket closed!");
                Console.Error.WriteLine("IntrinioRealtime | Websocket error: " + e);
            }
            finally
            {
                websocket.Dispose();
            }
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

            if (time.TotalMilliseconds != 0)
            {
                this.selfHealTimer = new System.Timers.Timer(time.TotalMilliseconds);
                selfHealTimer.Elapsed += (t, e) => connect(true);
                selfHealTimer.AutoReset = false;
                selfHealTimer.Start();
            }
            else
            {
                connect(true);
            }
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

        protected async Task rejoinChannels()
        {
            foreach (var entry in this.channels)
            {
                var message = new IntrinioMessage()
                {
                    Topic = this.parseTopic(entry.Key),
                    Event = "phx_join"
                };

                await sendMessage(message, CancellationToken.None);

                this.debug("Rejoined channel: ", entry.Key);
            }
        }

        protected async void heartbeat()
        {
            if (this.connected)
            {
                await this.sendMessage(new IntrinioMessage()
                {
                    Topic = "phoenix",
                    Event = "heartbeat"
                }, CancellationToken.None);
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

        protected void error(Exception e, bool doThow = true)
        {
            if (OnError != null)
            {
                OnError.DynamicInvoke(this, e);
            }

            if (doThow)
            {
                throw e;
            }
        }

        private System.Net.Http.Headers.AuthenticationHeaderValue getAuthenticationHeader()
        {
            return new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.Default.GetBytes($"{options.username}:{options.password}")));
        }

        protected async Task sendMessage<T>(IntrinioMessage<T> message, CancellationToken _cancellationToken)
        {
            var messageString = Newtonsoft.Json.JsonConvert.SerializeObject(message);

            var messageBuffer = Encoding.UTF8.GetBytes(messageString);
            var messagesCount = (int)Math.Ceiling((double)messageBuffer.Length / bufferSize);

            for (var i = 0; i < messagesCount; i++)
            {
                var offset = (bufferSize * i);
                var count = bufferSize;
                var lastMessage = ((i + 1) == messagesCount);

                if ((count * (i + 1)) > messageBuffer.Length)
                {
                    count = messageBuffer.Length - offset;
                }

                await websocket.SendAsync(new ArraySegment<byte>(messageBuffer, offset, count), WebSocketMessageType.Text, lastMessage, _cancellationToken);
            }
        }

        public void Dispose()
        {
            tokenExpirationTimer?.Stop();
            tokenExpirationTimer?.Close();

            heartbeatTimer?.Stop();
            heartbeatTimer?.Close();

            selfHealTimer?.Stop();
            selfHealTimer?.Close();

            this.websocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "User terminated", CancellationToken.None);
        }

        public async Task<IObservable<Quote>> join(params string[] args)
        {
            var channels = this.parseChannels(args);

            foreach (var channel in channels)
            {
                this.channels[channel] = true;
                if (this.websocket != null)
                {
                    await this.sendMessage(new IntrinioMessage()
                    {
                        Topic = this.parseTopic(channel),
                        Event = "phx_join"
                    }, CancellationToken.None);
                    this.debug("Joined channel: ", channel);
                }
            }


            return QuoteObservable.Where(quote => channels.Contains(quote.Ticker));
        }

        public async void leave(params string[] args)
        {
            var channels = this.parseChannels(args);

            foreach (var channel in channels)
            {
                this.channels.Remove(channel);
                await this.sendMessage(new IntrinioMessage()
                {
                    Topic = this.parseTopic(channel),
                    Event = "phx_leave"
                }, CancellationToken.None);

                this.debug("Left channel: ", channel);
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
                return Observable.FromEventPattern<Quote>(
                    (ev) => this.OnQuote += ev,
                    (ev) => this.OnQuote -= ev
                    ).Select((e) => e.EventArgs);
            }
        }

        public IObservable<Exception> ErrorObservable
        {
            get
            {
                return Observable.FromEventPattern<Exception>((ev) => this.OnError += ev, (ev) => this.OnError -= ev).Select((e) => e.EventArgs);
            }
        }

        public IObservable<Quote> getQuoteObservable(string channel)
        {
            return QuoteObservable.Where((quote) => quote.Ticker == channel);
        }
    }
}
