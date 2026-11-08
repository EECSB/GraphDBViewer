using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GraphDBViewer.Mcp
{
    ///<summary>What a linked tab answered: what the model is told, whether it is a refusal, and a picture when it drew one.</summary>
    public sealed record TabAnswer(string Text, bool IsError, byte[]? Png = null);

    ///<summary>Why a tab could not be linked, or a call could not reach it - said to the model as an answer, never thrown at the client.</summary>
    public sealed class LinkRefusal : Exception
    {
        public LinkRefusal(string message) : base(message)
        {
        }
    }

    ///
    ///`gdbv-mcp`'s live link: a Graph DB Viewer tab, joined over a WebSocket on this computer, which the query tools
    ///then run in - against the database that tab is connected to, so a model reads and changes what the person is
    ///looking at, as they look. GDS Viewer's TabLink (2026-11-03), brought across with its protocol.
    ///
    ///**The tab comes to the server**, since a page cannot listen. It opens a WebSocket to 127.0.0.1 on
    ///<see cref="Port"/> and says hello with a code it made up and shows; the person tells the model the code, and
    ///<see cref="LinkAsync"/> finds the tab that offered it. Until then a tab waits here and is sent nothing.
    ///
    ///**The code is the lock.** Anything on this computer can open a socket to the port - a web page can, from any
    ///site - and say hello with a code. What it cannot do is show the person the one their own tab shows, and calls go
    ///only to the tab that code named. Bound to the loopback address, so nothing off this computer reaches it at all.
    ///
    ///**The WebSocket is written here rather than taken from a web server**, which would bring ASP.NET's host into a
    ///console tool for one socket - or HttpListener, which on Windows needs a URL reservation for anybody who is not an
    ///administrator. The handshake is one header in and one out; past it, <see cref="WebSocket.CreateFromStream(Stream,
    ///WebSocketCreationOptions)"/> is the framework's own.
    ///
    ///The messages are JSON text. A tab says <c>{"type": "hello", "code": "48217356", "graph": "db.example:8182"}</c> first
    ///and is told <c>{"type": "waiting"}</c>; linked, it is told <c>{"type": "linked", "client": "..."}</c>, then
    ///sent <c>{"type": "call", "id": 1, "name": "...", "arguments": {...}}</c> one at a time, and answers
    ///<c>{"type": "answer", "id": 1, "text": "...", "isError": false, "png": null}</c>. Either end closes the socket
    ///to end the link.
    ///
    public sealed class TabLink : IAsyncDisposable
    {
        ///<summary>The port a tab looks for unless it is told another, and `gdbv-mcp` listens on unless `--link-port` says.</summary>
        public const int DefaultPort = 43708;

        ///<summary>How many digits a code is: a hundred million of them, against a handful of tabs ever waiting at once.</summary>
        public const int CodeLength = 8;

        ///<summary>How many tabs may wait at once. One more and the one that has waited longest is let go.</summary>
        public const int MostWaiting = 8;

        ///
        ///What the tab let go of is told, word for word, because the tab's end reads it: a tab closed with this
        ///reason ends rather than dialing again. Dialing again made it the ninth tab once more, which let the
        ///next-oldest go, which dialed again - nine tabs taking turns at being turned away. See LiveLink.
        ///
        public const string TurnedAwayReason = "Too many tabs were waiting.";

        ///<summary>How long a socket has to finish its handshake and say hello before it is closed.</summary>
        private static readonly TimeSpan HelloWithin = TimeSpan.FromSeconds(10);

        ///<summary>The most a handshake's header may be, and a hello. A browser's are a few hundred bytes.</summary>
        private const int MostHead = 8 * 1024;

        private const int MostHello = 4 * 1024;

        ///<summary>The most an answer may be: a picture of a large view is a few megabytes of base64.</summary>
        private const int MostAnswer = 48 * 1024 * 1024;

        ///<summary>What RFC 6455 has every server add to a key before hashing it.</summary>
        private const string HandshakeGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

        public TabLink(int port)
        {
            Port = port;
        }

        public int Port { get; }

        ///
        ///What a tab from a page this takes no tabs from is closed with, word for word, because the tab's end reads it and
        ///ends rather than dialing again (LiveLink.ForeignPageReason). Short, as a close's reason must be: the tab says
        ///which address to add itself.
        ///
        public const string ForeignPageReason = "This page is not one gdbv-mcp takes tabs from.";

        ///
        ///The public viewer's own address, whose tabs are taken without being named: the standalone edition, as
        ///https://eecs.blog/BlazorApps/GraphDBViewer/ serves it.
        ///
        public const string PublicViewer = "https://eecs.blog";

        ///
        ///**The pages whose tabs are taken** besides the defaults, from `--link-origin`: each a page's origin, its scheme,
        ///host and port, such as a Treeality instance's https://treeality.example (the viewer audit's decision a, the
        ///user, 2026-11-07). A web page anywhere may open a socket to this port, and the code is the lock that keeps it
        ///from being linked; the origin keeps it from even waiting in line, where it could push the person's own tab out
        ///(MostWaiting). The browser sets the header, and a page cannot.
        ///
        public IReadOnlyCollection<string> Origins { get; init; } = [];

        ///
        ///Whether a tab from this origin is taken: one on this computer (localhost, 127.0.0.1 or [::1], any port, over
        ///http or https), the public viewer, or one named in <paramref name="named"/>. A socket that says no origin is
        ///not a browser's, and is taken: anything else on this computer can say whatever origin it likes, so turning it
        ///away would stop nothing.
        ///
        public static bool TakesTabsFrom(string origin, IEnumerable<string> named)
        {
            if (string.IsNullOrEmpty(origin))
                return true;

            var said = OriginOf(origin);

            if (said == null)
                return false;

            var host = new Uri(said).Host;

            if (host == "localhost" || host == "127.0.0.1" || host == "[::1]")
                return true;

            if (said == PublicViewer)
                return true;

            return named.Select(OriginOf).Contains(said);
        }

        ///<summary>An origin as compared: scheme, host and a port other than the scheme's own, as Uri writes them (lowercased); null for anything not an http or https address.</summary>
        public static string? OriginOf(string text)
        {
            if (!Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri))
                return null;

            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                return null;

            return uri.GetLeftPart(UriPartial.Authority);
        }

        ///<summary>Told a line for a person reading the server's log when a tab comes, is linked or goes. `gdbv-mcp` writes them to standard error.</summary>
        public Action<string>? Log { get; init; }

        ///<summary>Whether the port is open, which it is from the first <see cref="TryListen"/> that could open it.</summary>
        public bool Listening
        {
            get
            {
                lock (gate)
                    return listener is not null;
            }
        }

        public bool IsLinked
        {
            get
            {
                lock (gate)
                    return linked is not null;
            }
        }

        ///<summary>The graph the linked tab had open when it said hello - its connection, as the tab names it - or null when none is linked.</summary>
        public string? LinkedGraph
        {
            get
            {
                lock (gate)
                    return linked?.Graph;
            }
        }

        ///<summary>How many tabs are waiting to be linked.</summary>
        public int Waiting
        {
            get
            {
                lock (gate)
                    return waiting.Count;
            }
        }

        private readonly object gate = new object();
        private readonly CancellationTokenSource stopping = new CancellationTokenSource();
        private readonly List<Tab> waiting = new List<Tab>();

        private TcpListener? listener;
        private Tab? linked;
        private int lastCall;

        ///<summary>Why the last link ended without being asked to, until it has been said - see <see cref="TakeEnding"/>.</summary>
        private string? ending;

        ///<summary>Done each time a tab says hello, and replaced, so a link waiting for one wakes up to look.</summary>
        private TaskCompletionSource arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        #region Listening *******************************************************************

        ///
        ///Opens the port, or says why it could not. Opening it twice is opening it once.
        ///
        ///**Taken is the usual why**: one `gdbv-mcp` per Claude window, and the first one up has the port. That is not a
        ///failure of this one - it keeps answering, and tries the port again when link_tab is called.
        ///
        public bool TryListen(out string why)
        {
            why = "";

            TcpListener opened;

            lock (gate)
            {
                if (listener is not null)
                    return true;

                //
                //**Not exclusive.** Windows' exclusive use refuses the port while connections a server before this one
                //closed are still in TIME_WAIT, which is minutes - so Claude restarting gdbv-mcp after a link would find
                //the port "taken" by nothing. Another process listening on it is still refused either way.
                //
                opened = new TcpListener(IPAddress.Loopback, Port);

                try
                {
                    opened.Start();
                }
                catch (SocketException taken)
                {
                    why = $"Port {Port} is taken - most likely by another gdbv-mcp, started by another Claude window, which has the link to the tab: {taken.Message}";

                    return false;
                }

                listener = opened;
            }

            _ = acceptAsync(opened);

            return true;
        }

        private async Task acceptAsync(TcpListener listening)
        {
            while (!stopping.IsCancellationRequested)
            {
                TcpClient client;

                try
                {
                    client = await listening.AcceptTcpClientAsync(stopping.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (SocketException)
                {
                    continue;
                }

                _ = greetAsync(client);
            }
        }

        ///
        ///One connection: the handshake, the hello, and then waiting in line - or closed, for anything that is not a
        ///tab saying hello in time.
        ///
        private async Task greetAsync(TcpClient client)
        {
            Tab? tab = null;

            try
            {
                using var hurry = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);

                hurry.CancelAfter(HelloWithin);

                if (await upgradeAsync(client.GetStream(), hurry.Token) is not (WebSocket socket, string from))
                {
                    client.Dispose();

                    return;
                }

                var hello = await receiveAsync(socket, MostHello, hurry.Token);

                //A page this takes no tabs from is told so and closed before it can wait in line (Origins). Its hello is
                //read first: a socket closed with what it sent still unread is reset, and the reason never reaches it.
                if (!TakesTabsFrom(from, Origins))
                {
                    Log?.Invoke($"Turned away a tab from {from}: start gdbv-mcp with --link-origin {OriginOf(from) ?? from} to take tabs from it.");

                    await closeAsync(socket, WebSocketCloseStatus.PolicyViolation, ForeignPageReason);

                    socket.Dispose();
                    client.Dispose();

                    return;
                }

                tab = tabFrom(socket, from, hello);

                if (tab is null)
                {
                    await closeAsync(socket, WebSocketCloseStatus.PolicyViolation, "Say hello with a code first.");

                    socket.Dispose();
                    client.Dispose();

                    return;
                }
            }
            catch (Exception)
            {
                client.Dispose();

                return;
            }

            Tab? turnedAway = null;

            lock (gate)
            {
                if (waiting.Count >= MostWaiting)
                {
                    turnedAway = waiting[0];
                    waiting.RemoveAt(0);
                }

                waiting.Add(tab);

                var woken = arrived;

                arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                woken.TrySetResult();
            }

            if (turnedAway is not null)
                await closeAsync(turnedAway, TurnedAwayReason);

            //
            //**Said back to the tab, so it knows what answered.** Until 2026-11-03 a waiting tab heard nothing
            //until it was linked, and could not tell gdbv-mcp from anything else on the port that takes a
            //WebSocket and keeps quiet - which it would have waited on for ever. A tab that hears nothing within
            //a while says so now, and goes on waiting for the gdbv-mcp that may be an older one; see LiveLink.
            //
            try
            {
                await sendAsync(tab, new JsonObject { ["type"] = "waiting" }.ToJsonString(), stopping.Token);
            }
            catch (Exception) { }

            Log?.Invoke($"A Graph DB Viewer tab from {pageSaid(tab.From)} is waiting to be linked.");

            await readAsync(tab, client);
        }

        ///<summary>A tab's messages until it closes: answers to calls, and nothing else it says is acted on.</summary>
        private async Task readAsync(Tab tab, TcpClient client)
        {
            try
            {
                while (true)
                {
                    string? message = await receiveAsync(tab.Socket, MostAnswer, stopping.Token);

                    if (message is null)
                        break;

                    answered(tab, message);
                }
            }
            catch (Exception) { }
            finally
            {
                gone(tab);

                //The other half of a close the tab began, so it is not left waiting for one.
                if (tab.Socket.State == WebSocketState.CloseReceived)
                    await closeAsync(tab, "");

                tab.Socket.Dispose();
                client.Dispose();
            }
        }

        ///
        ///A tab that closed, or whose socket broke. Linked, it takes the link with it, and the model's next call is told
        ///so rather than finding no tab and wondering - see <see cref="TakeEnding"/>.
        ///
        private void gone(Tab tab)
        {
            lock (gate)
            {
                waiting.Remove(tab);

                if (ReferenceEquals(linked, tab))
                {
                    linked = null;
                    ending = "The Graph DB Viewer tab closed the link - Stop was pressed under MCP server in its AI settings, or the page was reloaded or closed.";

                    Log?.Invoke("The linked tab closed the link.");
                }

                foreach (var asked in tab.Asked.Values)
                    asked.TrySetException(new LinkRefusal("The Graph DB Viewer tab closed the link before it answered, so whether that was done is for the person's screen to say."));

                tab.Asked.Clear();
            }
        }

        #endregion **************************************************************************



        #region Linking *********************************************************************

        ///
        ///Links the tab that offered <paramref name="code"/>, and returns the graph it has open. Waits up to
        ///<paramref name="wait"/> for one when none is waiting yet: the person may press Start after giving the code.
        ///
        ///**Refused at once when tabs are waiting with other codes**, since then the likeliest thing is a code read or
        ///typed wrong, and the person is better asked again now than in twenty seconds.
        ///
        ///A link already made is replaced: the person started another, in this tab or another one, and the one they
        ///gave the code of is the one they mean.
        ///
        public async Task<string> LinkAsync(string code, string client, TimeSpan wait, CancellationToken cancel)
        {
            string wanted = Digits(code);

            if (wanted.Length != CodeLength)
                throw new LinkRefusal($"A code is the {CodeLength} digits the viewer shows under MCP server in its AI settings, like 4821 7356, and \"{code}\" is not one.");

            if (!TryListen(out string why))
                throw new LinkRefusal(why);

            var until = DateTime.UtcNow + wait;

            while (true)
            {
                Tab? found = null;
                Tab? replaced = null;
                Task arrival;

                lock (gate)
                {
                    var offering = waiting.Where(tab => same(tab.Code, wanted)).ToList();

                    if (offering.Count > 1)
                        throw new LinkRefusal("Two tabs offered that code, so neither was linked. Ask the person to press Stop and then Start under MCP server in their AI settings, for a new code.");

                    if (offering.Count == 1)
                    {
                        found = offering[0];
                        replaced = linked;

                        waiting.Remove(found);

                        linked = found;
                        ending = null;
                    }
                    else if (waiting.Count > 0)
                        throw new LinkRefusal($"No Graph DB Viewer tab offered {Spaced(wanted)}, and {waitingSaid(waiting.Count)} with another code. Ask the person to read the code their AI settings show under MCP server again.");

                    arrival = arrived.Task;
                }

                if (found is not null)
                {
                    if (replaced is not null)
                        await closeAsync(replaced, "Another tab was linked.");

                    var linkedMessage = new JsonObject { ["type"] = "linked", ["client"] = client };

                    await sendAsync(found, linkedMessage.ToJsonString(), cancel);

                    Log?.Invoke($"Linked the tab from {pageSaid(found.From)}.");

                    return found.Graph;
                }

                var left = until - DateTime.UtcNow;

                if (left <= TimeSpan.Zero)
                {
                    throw new LinkRefusal(
                        $"No Graph DB Viewer tab asked to link in {wait.TotalSeconds:0} seconds. Ask the person to open Settings, AI settings in the viewer "
                        + $"and press Start under MCP server, with the port at {Port}.");
                }

                await Task.WhenAny(arrival, Task.Delay(left, cancel));

                cancel.ThrowIfCancellationRequested();
            }
        }

        ///<summary>Ends the link, if there is one; whether there was.</summary>
        public async Task<bool> UnlinkAsync()
        {
            Tab? was;

            lock (gate)
            {
                was = linked;
                linked = null;
                ending = null;
            }

            if (was is null)
                return false;

            await closeAsync(was, "Claude ended the link.");

            return true;
        }

        ///
        ///Why the link ended without anybody here asking it to, once - and then null until it ends that way again.
        ///
        ///Taken rather than read, because it is said to the model once: on the first call after the tab went, which is
        ///refused with the reason rather than with a bare "no tab is linked".
        ///
        public string? TakeEnding()
        {
            lock (gate)
            {
                string? said = ending;

                ending = null;

                return said;
            }
        }

        ///<summary>Where a tab's page came from, as a log line says it.</summary>
        private static string pageSaid(string from)
        {
            if (from.Length == 0)
                return "a page that did not say where it came from";

            return from;
        }

        ///<summary>"a tab is waiting", "3 tabs are waiting".</summary>
        private static string waitingSaid(int count)
        {
            if (count == 1)
                return "a tab is waiting";

            return $"{count} tabs are waiting";
        }

        #endregion **************************************************************************



        #region Calls ***********************************************************************

        ///
        ///Sends one call to the linked tab and waits up to <paramref name="within"/> for its answer.
        ///
        ///A tab that closes first, or takes too long, is a <see cref="LinkRefusal"/> saying which - and in neither case
        ///can this say whether the change was made, so it says that too.
        ///
        public async Task<TabAnswer> CallAsync(string name, JsonElement arguments, TimeSpan within, CancellationToken cancel)
        {
            Tab tab;
            int id;
            var answer = new TaskCompletionSource<TabAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);

            lock (gate)
            {
                if (linked is null)
                    throw new LinkRefusal("No Graph DB Viewer tab is linked.");

                tab = linked;
                id = ++lastCall;

                tab.Asked[id] = answer;
            }

            JsonNode given = new JsonObject();

            if (arguments.ValueKind == JsonValueKind.Object)
                given = JsonNode.Parse(arguments.GetRawText())!;

            var call = new JsonObject
            {
                ["type"] = "call",
                ["id"] = id,
                ["name"] = name,
                ["arguments"] = given
            };

            try
            {
                await sendAsync(tab, call.ToJsonString(), cancel);

                return await answer.Task.WaitAsync(within, cancel);
            }
            catch (TimeoutException)
            {
                throw new LinkRefusal($"The Graph DB Viewer tab did not answer {name} in {within.TotalSeconds:0} seconds. It may still be at it; what the person's screen shows is what happened.");
            }
            catch (Exception failed) when (failed is WebSocketException || failed is ObjectDisposedException)
            {
                throw new LinkRefusal("The Graph DB Viewer tab closed the link before it answered, so whether that was done is for the person's screen to say.");
            }
            finally
            {
                lock (gate)
                    tab.Asked.Remove(id);
            }
        }

        ///<summary>An answer from a tab, given to the call that is waiting for it. Anything else a tab sends is let go.</summary>
        private void answered(Tab tab, string message)
        {
            int id;
            TabAnswer answer;

            try
            {
                using var document = JsonDocument.Parse(message);

                var root = document.RootElement;

                if (root.ValueKind != JsonValueKind.Object || text(root, "type") != "answer")
                    return;

                if (!root.TryGetProperty("id", out var number) || !number.TryGetInt32(out id))
                    return;

                bool isError = root.TryGetProperty("isError", out var error) && error.ValueKind == JsonValueKind.True;
                byte[]? png = null;

                if (text(root, "png") is string picture && picture.Length > 0)
                    png = Convert.FromBase64String(picture);

                answer = new TabAnswer(text(root, "text") ?? "", isError, png);
            }
            catch (Exception failed) when (failed is JsonException || failed is FormatException)
            {
                return;
            }

            TaskCompletionSource<TabAnswer>? waiter;

            lock (gate)
                tab.Asked.TryGetValue(id, out waiter);

            waiter?.TrySetResult(answer);
        }

        private static string? text(JsonElement root, string name)
        {
            if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString();

            return null;
        }

        #endregion **************************************************************************



        #region The WebSocket ***************************************************************

        ///<summary>One tab: its socket, what it said in its hello, and the calls it has not answered yet.</summary>
        private sealed class Tab
        {
            public required WebSocket Socket { get; init; }

            public required string Code { get; init; }

            public required string Graph { get; init; }

            ///<summary>The page it came from, as its browser said - kept for nothing but a person reading a log.</summary>
            public required string From { get; init; }

            ///<summary>One send at a time: a WebSocket takes no second message while one is going out.</summary>
            public SemaphoreSlim Sending { get; } = new SemaphoreSlim(1, 1);

            public Dictionary<int, TaskCompletionSource<TabAnswer>> Asked { get; } = new Dictionary<int, TaskCompletionSource<TabAnswer>>();
        }

        ///<summary>A tab from its hello, or null for anything that is not one.</summary>
        private static Tab? tabFrom(WebSocket socket, string from, string? hello)
        {
            if (hello is null)
                return null;

            try
            {
                using var document = JsonDocument.Parse(hello);

                var root = document.RootElement;

                if (root.ValueKind != JsonValueKind.Object || text(root, "type") != "hello")
                    return null;

                string code = Digits(text(root, "code") ?? "");

                if (code.Length != CodeLength)
                    return null;

                string graph = text(root, "graph") ?? "";

                if (graph.Length > 260)
                    graph = graph.Substring(0, 260);

                return new Tab { Socket = socket, Code = code, Graph = graph, From = from };
            }
            catch (JsonException)
            {
                return null;
            }
        }

        ///
        ///The opening handshake, as RFC 6455 has a server answer it: the socket and the page it came from, or null
        ///after saying no to anything that is not a WebSocket asking to open.
        ///
        ///**Read a byte at a time**, so nothing past the blank line that ends the request is read - a client sends no
        ///frame before it has the answer, and a header is a few hundred bytes.
        ///
        private static async Task<(WebSocket, string)?> upgradeAsync(NetworkStream stream, CancellationToken cancel)
        {
            string? head = await headOf(stream, cancel);

            if (head is null)
                return null;

            string[] lines = head.Split("\r\n");
            string[] request = lines[0].Split(' ');

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (string line in lines.Skip(1))
            {
                int colon = line.IndexOf(':');

                if (colon > 0)
                    headers[line.Substring(0, colon).Trim()] = line.Substring(colon + 1).Trim();
            }

            bool asked = request.Length == 3 && request[0] == "GET" && request[2].StartsWith("HTTP/1.1", StringComparison.Ordinal);
            bool upgrade = headers.TryGetValue("Upgrade", out string? to) && to.Equals("websocket", StringComparison.OrdinalIgnoreCase);
            bool connection = headers.TryGetValue("Connection", out string? how)
                && how.Split(',').Any(part => part.Trim().Equals("Upgrade", StringComparison.OrdinalIgnoreCase));

            headers.TryGetValue("Sec-WebSocket-Key", out string? key);

            if (!asked || !upgrade || !connection || key is null || !isKey(key))
            {
                await refuseAsync(stream, "400 Bad Request", "", cancel);

                return null;
            }

            if (!headers.TryGetValue("Sec-WebSocket-Version", out string? version) || version != "13")
            {
                await refuseAsync(stream, "426 Upgrade Required", "Sec-WebSocket-Version: 13\r\n", cancel);

                return null;
            }

            string accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + HandshakeGuid)));

            byte[] answer = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n");

            await stream.WriteAsync(answer, cancel);

            headers.TryGetValue("Origin", out string? origin);

            var socket = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions
            {
                IsServer = true,
                KeepAliveInterval = TimeSpan.FromSeconds(20)
            });

            return (socket, origin ?? "");
        }

        ///<summary>The request's header, up to the blank line that ends it, or null for a connection that ends first or runs on too long.</summary>
        private static async Task<string?> headOf(NetworkStream stream, CancellationToken cancel)
        {
            var read = new List<byte>(1024);
            var one = new byte[1];

            while (read.Count < MostHead)
            {
                if (await stream.ReadAsync(one, cancel) == 0)
                    return null;

                read.Add(one[0]);

                int count = read.Count;

                if (count >= 4 && read[count - 4] == '\r' && read[count - 3] == '\n' && read[count - 2] == '\r' && read[count - 1] == '\n')
                    return Encoding.ASCII.GetString(read.ToArray(), 0, count - 4);
            }

            return null;
        }

        ///<summary>Whether a Sec-WebSocket-Key is what the RFC says one is: sixteen bytes, in base64.</summary>
        private static bool isKey(string key)
        {
            var decoded = new byte[24];

            return Convert.TryFromBase64String(key, decoded, out int written) && written == 16;
        }

        private static async Task refuseAsync(NetworkStream stream, string status, string extra, CancellationToken cancel)
        {
            const string Body = "This is gdbv-mcp's port for a Graph DB Viewer tab, to open a WebSocket to.";

            byte[] answer = Encoding.UTF8.GetBytes(
                $"HTTP/1.1 {status}\r\n{extra}Content-Type: text/plain; charset=utf-8\r\nContent-Length: {Encoding.UTF8.GetByteCount(Body)}\r\nConnection: close\r\n\r\n{Body}");

            try
            {
                await stream.WriteAsync(answer, cancel);
            }
            catch (IOException) { }
        }

        ///<summary>One whole message as text, or null when the other end closed. Throws past <paramref name="most"/> bytes.</summary>
        private static async Task<string?> receiveAsync(WebSocket socket, int most, CancellationToken cancel)
        {
            var buffer = new ArrayBufferWriter<byte>();

            while (true)
            {
                var result = await socket.ReceiveAsync(buffer.GetMemory(16 * 1024), cancel);

                if (result.MessageType == WebSocketMessageType.Close)
                    return null;

                buffer.Advance(result.Count);

                if (buffer.WrittenCount > most)
                    throw new InvalidDataException($"A message over {most} bytes.");

                if (result.EndOfMessage)
                    break;
            }

            return Encoding.UTF8.GetString(buffer.WrittenSpan);
        }

        private static async Task sendAsync(Tab tab, string message, CancellationToken cancel)
        {
            await tab.Sending.WaitAsync(cancel);

            try
            {
                await tab.Socket.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, true, cancel);
            }
            finally
            {
                tab.Sending.Release();
            }
        }

        ///<summary>Says goodbye to a tab, with a reason its window shows. The reading loop sees the tab's answer to it and lets go.</summary>
        private static async Task closeAsync(Tab tab, string reason)
        {
            bool mine;

            try
            {
                mine = await tab.Sending.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            //A send that has not finished in five seconds is a socket that is not going to say goodbye either.
            if (!mine)
                return;

            try
            {
                await closeAsync(tab.Socket, WebSocketCloseStatus.NormalClosure, reason);
            }
            finally
            {
                tab.Sending.Release();
            }
        }

        private static async Task closeAsync(WebSocket socket, WebSocketCloseStatus status, string reason)
        {
            if (socket.State != WebSocketState.Open && socket.State != WebSocketState.CloseReceived)
                return;

            try
            {
                using var hurry = new CancellationTokenSource(TimeSpan.FromSeconds(5));

                await socket.CloseOutputAsync(status, reason, hurry.Token);
            }
            catch (Exception) { }
        }

        #endregion **************************************************************************



        #region Codes ***********************************************************************

        ///<summary>A code's digits, whatever was written between them - "4821 7356", "4821-7356" and "48217356" are one code.</summary>
        public static string Digits(string code)
        {
            var digits = new StringBuilder(CodeLength);

            foreach (char character in code)
            {
                if (character >= '0' && character <= '9')
                    digits.Append(character);
            }

            return digits.ToString();
        }

        ///<summary>A code as the tab shows it, in two fours.</summary>
        public static string Spaced(string digits)
        {
            if (digits.Length != CodeLength)
                return digits;

            return digits.Substring(0, 4) + " " + digits.Substring(4);
        }

        ///<summary>Two codes compared in the same time whatever they hold, so how long a refusal took says nothing about how close a guess came.</summary>
        private static bool same(string one, string other)
        {
            return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(one), Encoding.ASCII.GetBytes(other));
        }

        #endregion **************************************************************************

        public async ValueTask DisposeAsync()
        {
            List<Tab> tabs;

            lock (gate)
            {
                stopping.Cancel();

                listener?.Stop();
                listener = null;

                tabs = waiting.ToList();

                if (linked is not null)
                    tabs.Add(linked);

                waiting.Clear();
                linked = null;
            }

            foreach (var tab in tabs)
                await closeAsync(tab, "gdbv-mcp stopped.");
        }
    }
}
