using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using SkillTipsResponseAnalyzer;
using UmamusumeResponseAnalyzer.Plugin;
using WatsonWebserver.Core;
using WatsonWebserver.Lite;
using HttpMethod = WatsonWebserver.Core.HttpMethod;

internal static class BridgeSmoke
{
    static readonly SkillTipsTraining Training = new(123, "2026-10-30 育成", 72, 500, [new(3, 1)]);

    internal static async Task RunAsync()
    {
        Console.WriteLine("Bridge WebSocket smoke: exchange");
        await VerifyExchangeAsync();
        Console.WriteLine("Bridge WebSocket smoke: message boundaries and connection policy");
        await VerifyMessageBoundariesAsync();
        await VerifyConnectionPolicyAsync();
        Console.WriteLine("Bridge WebSocket smoke: disconnect");
        await VerifyDisconnectAsync();
        Console.WriteLine("Bridge WebSocket smoke: cancellation");
        await VerifyCancellationAsync();
        Console.WriteLine("Bridge WebSocket smoke: timeout");
        await VerifyTimeoutAsync();
        Console.WriteLine("Bridge WebSocket smoke: protocol errors");
        await VerifyErrorsAsync();
        Console.WriteLine("Bridge WebSocket smoke passed (fragments, prepare/replace, errors, cancellation and reconnect).");
    }

    static SkillTipsBridgeState State(ulong revision = 1, int[]? selected = null) =>
        new(true, 7, revision, false, Training, selected ?? [5], 450, null);

    static async Task VerifyExchangeAsync()
    {
        await using var server = await BridgeSmokeServer.StartAsync(sendHello: false);
        await server.SendAsync(BridgeSmokeServer.Hello);
        // A single JSON message spans fragments, including a split UTF-8 character.
        var text = BridgeSmokeServer.Serialize(State()).Replace("2026-10-30 \\u80B2\\u6210", "2026-10-30 育成");
        var bytes = Encoding.UTF8.GetBytes(text);
        var split = Array.IndexOf(bytes, (byte)0xE8) + 1;
        Require(split > 0, "The split-frame fixture must contain a UTF-8 character.");
        await server.SendRawAsync(bytes[..split], endOfMessage: false);
        await server.SendRawAsync(bytes[split..]);
        await server.WaitForStateAsync(state => state?.Revision == 1);
        Require(Training.Matches(server.Bridge.Current!.Training), "Training identity did not survive WebSocket fragmentation.");
        Require(!server.Bridge.Current.SelectionEvent, "The initial snapshot must not be a selection event.");

        await FailsAsync<ArgumentException>(() => server.Bridge.PrepareAsync(Training, [20, 20], default));
        var preparation = server.Bridge.PrepareAsync(Training, [30, 20], default);
        var request = await server.ReadAsync();
        Require(request.GetProperty("kind").GetString() == "prepare"
            && request.GetProperty("page_id").GetUInt64() == 7
            && request.GetProperty("revision").GetUInt64() == 1
            && request.GetProperty("skill_ids").EnumerateArray().Select(value => value.GetInt32()).SequenceEqual([30, 20]),
            "Preparation must preserve the recommendation order and current page identity.");
        var preparationId = request.GetProperty("request_id").GetInt64();
        await server.SendAsync(new SkillTipsPreparedSelection(preparationId, 7, 1, [10, 20, 30], [5], 300));
        var prepared = await preparation.WaitAsync(TimeSpan.FromSeconds(5));
        Require(server.Bridge.Current!.SelectedSkillIds.SequenceEqual([5]), "Preflight changed the current selection.");
        await FailsAsync<InvalidOperationException>(() => server.Bridge.ReplaceAsync(prepared with { }, default));

        var replacement = server.Bridge.ReplaceAsync(prepared, default);
        request = await server.ReadAsync();
        Require(request.GetProperty("kind").GetString() == "replace"
            && request.GetProperty("prepared_request_id").GetInt64() == preparationId,
            "Replacement did not reference the exact preflight request.");
        Require(!replacement.IsCompleted, "Sending a request was treated as completed selection.");
        var result = new SkillTipsSelectionResult(request.GetProperty("request_id").GetInt64(), [10, 20, 30], 300);
        await server.SendAsync(State(2, [10, 20, 30]));
        await server.SendAsync(result);
        Require((await replacement.WaitAsync(TimeSpan.FromSeconds(5))).RemainingPoints == 300, "Replacement result was lost.");
        await FailsAsync<InvalidOperationException>(() => server.Bridge.ReplaceAsync(prepared, default));
        await server.SendAsync(State(3));
        await server.WaitForStateAsync(state => state?.Revision == 3);
        await FailsAsync<InvalidOperationException>(() => server.Bridge.PrepareAsync(Training with { Turn = 71 }, [20], default));

        preparation = server.Bridge.PrepareAsync(Training, [20], default);
        request = await server.ReadAsync();
        await server.SendAsync(new SkillTipsPreparedSelection(request.GetProperty("request_id").GetInt64(), 7, 3, [20], [5], 400));
        prepared = await preparation.WaitAsync(TimeSpan.FromSeconds(5));
        await server.SendAsync(State(4));
        await server.WaitForStateAsync(state => state?.Revision == 4);
        await FailsAsync<InvalidOperationException>(() => server.Bridge.ReplaceAsync(prepared, default));
    }

    static async Task VerifyMessageBoundariesAsync()
    {
        await using var server = await BridgeSmokeServer.StartAsync();
        var json = BridgeSmokeServer.Serialize(State());
        var full = Encoding.UTF8.GetBytes(json + new string(' ', SkillTipsBridge.MaxFrameBytes - Encoding.UTF8.GetByteCount(json)));
        await server.SendRawAsync(full[..32768], endOfMessage: false);
        await server.SendRawAsync(full[32768..]);
        await server.WaitForStateAsync(state => state?.Revision == 1);
        Require(server.Bridge.LastError is null, "A fragmented message of exactly 64 KiB must be accepted.");

        await server.SendRawAsync(full, endOfMessage: false);
        await server.SendRawAsync(" "u8.ToArray());
        await server.WaitForStateAsync(state => state is null);
        Require(!string.IsNullOrEmpty(server.Bridge.LastError), "The 64 KiB limit must apply to all fragments combined.");
        server.Disconnect();
        await server.AcceptAsync();
        await server.SendAsync(State(2));
        await server.WaitForStateAsync(state => state?.Revision == 2);
        await server.SendRawAsync(Encoding.UTF8.GetBytes(BridgeSmokeServer.Serialize(State(3))), WebSocketMessageType.Binary);
        await server.WaitForStateAsync(state => state is null);
        Require(!string.IsNullOrEmpty(server.Bridge.LastError), "Binary messages must be rejected even when they contain valid JSON.");
    }

    static async Task VerifyConnectionPolicyAsync()
    {
        await using var server = await BridgeSmokeServer.StartAsync(connect: false);
        await using (var browser = await server.ConnectPeerAsync("https://example.invalid"))
        {
            var close = await browser.Closed.WaitAsync(TimeSpan.FromSeconds(5));
            Require(close == WebSocketCloseStatus.PolicyViolation, "Browser-origin connections must be explicitly rejected.");
        }
        await server.AcceptAsync();
        await server.SendAsync(State());
        await server.WaitForStateAsync(state => state is not null);
        var current = server.Bridge.Current;
        await using (var second = await server.ConnectPeerAsync())
        {
            var close = await second.Closed.WaitAsync(TimeSpan.FromSeconds(5));
            Require(close == WebSocketCloseStatus.PolicyViolation, "A second game connection must be explicitly rejected.");
        }
        Require(ReferenceEquals(current, server.Bridge.Current) && server.Bridge.LastError is null,
            "Rejected peers must not replace or invalidate the active game session.");
    }

    static async Task VerifyDisconnectAsync()
    {
        await using var server = await BridgeSmokeServer.StartAsync();
        await server.SendAsync(State());
        await server.WaitForStateAsync(state => state is not null);
        var preparation = server.Bridge.PrepareAsync(Training, [20], default);
        await server.ReadAsync();
        server.Disconnect();
        await FailsDisconnectedAsync(() => preparation);
        await server.WaitForStateAsync(state => state is null);
        await server.AcceptAsync();
        await server.SendAsync(State(2));
        await server.WaitForStateAsync(state => state?.Revision == 2);
        await server.AssertNoRequestAsync();
        Require(server.Bridge.LastError is null, "A fresh state did not clear the disconnected diagnostic.");

        preparation = server.Bridge.PrepareAsync(Training, [20], default);
        var request = await server.ReadAsync();
        await server.SendAsync(new SkillTipsPreparedSelection(request.GetProperty("request_id").GetInt64(), 7, 2, [20], [5], 400));
        var prepared = await preparation.WaitAsync(TimeSpan.FromSeconds(5));
        var replacement = server.Bridge.ReplaceAsync(prepared, default);
        await server.ReadAsync();
        server.Disconnect();
        await FailsDisconnectedAsync(() => replacement);
        await server.WaitForStateAsync(state => state is null);
        await server.AcceptAsync();
        await server.SendAsync(State(3));
        await server.WaitForStateAsync(state => state?.Revision == 3);
        await server.AssertNoRequestAsync();
        await FailsAsync<InvalidOperationException>(() => server.Bridge.ReplaceAsync(prepared, default));
    }

    static async Task VerifyCancellationAsync()
    {
        await using var server = await BridgeSmokeServer.StartAsync();
        await server.SendAsync(State());
        await server.WaitForStateAsync(state => state is not null);
        using var cancellation = new CancellationTokenSource();
        var preparation = server.Bridge.PrepareAsync(Training, [20], cancellation.Token);
        await server.ReadAsync();
        await cancellation.CancelAsync();
        await FailsAsync<OperationCanceledException>(() => preparation);
        await server.WaitForStateAsync(state => state is null);
        server.Disconnect();
        await server.AcceptAsync();
        await server.SendAsync(State(2));
        await server.WaitForStateAsync(state => state?.Revision == 2);
        await server.AssertNoRequestAsync();
        await server.Bridge.DisposeAsync();
        Require(server.Bridge.Current is null, "Disposal must clear the current game state.");
    }

    static async Task VerifyErrorsAsync()
    {
        foreach (var response in new[] { "native", "wrong-id", "bad-target", "bad-extra", "negative-points", "extra-points" })
        {
            await using var server = await BridgeSmokeServer.StartAsync();
            await server.SendAsync(State());
            await server.WaitForStateAsync(state => state is not null);
            var preparation = server.Bridge.PrepareAsync(Training, [20], default);
            var request = await server.ReadAsync();
            var id = request.GetProperty("request_id").GetInt64();
            if (response == "native")
                await server.SendAsync(new { kind = "error", request_id = id, message = "preflight: managed exception" });
            else
                await server.SendAsync(new SkillTipsPreparedSelection(response == "wrong-id" ? id + 1 : id, 7, 1,
                    response == "bad-target" ? [10] : [20], response == "bad-extra" ? [] : [5],
                    response == "negative-points" ? -1 : response == "extra-points" ? 501 : 400));
            await FailsAsync<InvalidDataException>(() => preparation);
            await server.WaitForStateAsync(state => state is null);
            Require(!string.IsNullOrEmpty(server.Bridge.LastError), "Request failure must remain visible.");
            server.Disconnect();
            await server.AcceptAsync();
            await server.SendAsync(State(2));
            await server.WaitForStateAsync(state => state?.Revision == 2);
            await server.AssertNoRequestAsync();
        }

        foreach (var frame in new[] { "{}", "{\"kind\":3}", "{\"kind\":\"state\"}", "{\"kind\":\"unknown\"}",
            new string('x', SkillTipsBridge.MaxFrameBytes + 1) })
        {
            await using var server = await BridgeSmokeServer.StartAsync();
            await server.SendAsync(State());
            await server.WaitForStateAsync(state => state is not null);
            await server.SendRawAsync(Encoding.UTF8.GetBytes(frame));
            await server.WaitForStateAsync(state => state is null);
            Require(!string.IsNullOrEmpty(server.Bridge.LastError), "Malformed protocol must fail visibly.");
            server.Disconnect();
            await server.AcceptAsync();
            await server.SendAsync(State(2));
            await server.WaitForStateAsync(state => state?.Revision == 2);
        }

        foreach (var invalid in new[] { "version", "protocol", "profile" })
        {
            await using var invalidHello = await BridgeSmokeServer.StartAsync(sendHello: false);
            await invalidHello.SendAsync(new
            {
                kind = "hello", protocol = invalid == "protocol" ? "other" : "skill-tips-bridge",
                version = invalid == "version" ? 1 : 2,
                profile = invalid == "profile" ? "other" : "umamusume-20261026-il2cpp-x64"
            });
            await invalidHello.WaitForStateAsync(_ => invalidHello.Bridge.LastError is not null);
            Require(invalidHello.Bridge.Current is null, "An incompatible protocol/version/profile must not become available.");
        }
    }

    static async Task VerifyTimeoutAsync()
    {
        await using var server = await BridgeSmokeServer.StartAsync(requestTimeout: TimeSpan.FromMilliseconds(100));
        await server.SendAsync(State());
        await server.WaitForStateAsync(state => state is not null);
        var preparation = server.Bridge.PrepareAsync(Training, [20], default);
        await server.ReadAsync();
        await FailsAsync<TimeoutException>(() => preparation);
        await server.WaitForStateAsync(state => state is null);
        Require(server.Bridge.LastError == SkillTipsResponseAnalyzer.i18n.ParseSkillTipsResponse.I18N_BridgeTimeout,
            "An unacknowledged command must explain that it will not be retried.");
        server.Disconnect();
        await server.AcceptAsync();
        await server.SendAsync(State(2));
        await server.WaitForStateAsync(state => state?.Revision == 2);
        await server.AssertNoRequestAsync();
    }

    static async Task FailsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action().WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    static async Task FailsDisconnectedAsync(Func<Task> action)
    {
        try { await action().WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception error) when (error is IOException or WebSocketException) { return; }
        throw new InvalidOperationException("The pending operation must fail when its WebSocket disconnects.");
    }

    static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

internal sealed class BridgeSmokeServer : IAsyncDisposable
{
    internal static readonly object Hello = new
    {
        kind = "hello", protocol = "skill-tips-bridge", version = 2, profile = "umamusume-20261026-il2cpp-x64"
    };
    static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    readonly WebserverLite server;
    readonly CancellationTokenSource lifetime = new();
    readonly ConcurrentBag<Task> handlers = [];
    readonly List<NativePeer> peers = [];
    readonly Uri endpoint;
    NativePeer? connection;
    internal SkillTipsBridge Bridge { get; }

    BridgeSmokeServer(TimeSpan? requestTimeout)
    {
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        Bridge = new(requestTimeout);
        var pluginType = typeof(SkillTipsResponseAnalyzer.SkillTipsResponseAnalyzer);
        var route = pluginType.GetMethods(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)
            .Select(method => method.GetCustomAttribute<WebSocketRouteAttribute>()).OfType<WebSocketRouteAttribute>().Single();
        var path = $"/{pluginType.Name}/{route.Path}";
        endpoint = new($"ws://127.0.0.1:{port}{path}");
        server = new(new WebserverSettings("127.0.0.1", port), context =>
        {
            context.Response.StatusCode = 404;
            return context.Response.Send(string.Empty);
        });
        server.Routes.PreAuthentication.Static.Add(HttpMethod.GET, path, context =>
        {
            var handler = WebSocketUpgrade.RunAsync(context,
                (socket, token) => Bridge.AcceptAsync(context, socket, token), lifetime.Token);
            handlers.Add(handler);
            return handler;
        });
        server.Start(lifetime.Token);
    }

    internal static async Task<BridgeSmokeServer> StartAsync(
        bool sendHello = true, TimeSpan? requestTimeout = null, bool connect = true)
    {
        var server = new BridgeSmokeServer(requestTimeout);
        try
        {
            if (connect) await server.AcceptAsync(sendHello);
            return server;
        }
        catch { await server.DisposeAsync(); throw; }
    }

    internal async Task AcceptAsync(bool sendHello = true)
    {
        connection = await ConnectPeerAsync();
        if (sendHello) await SendAsync(Hello);
    }

    internal async Task<NativePeer> ConnectPeerAsync(string? origin = null)
    {
        var peer = await NativePeer.ConnectAsync(endpoint, origin);
        peers.Add(peer);
        return peer;
    }

    internal static string Serialize(object message) => JsonSerializer.Serialize(message, JsonOptions);
    internal Task SendAsync(object message) => SendRawAsync(Encoding.UTF8.GetBytes(Serialize(message)));
    internal Task SendRawAsync(byte[] bytes, WebSocketMessageType type = WebSocketMessageType.Text, bool endOfMessage = true)
        => connection!.SendAsync(bytes, type, endOfMessage);

    internal Task<JsonElement> ReadAsync() => connection!.ReadAsync();

    internal async Task WaitForStateAsync(Func<SkillTipsBridgeState?, bool> predicate)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate(Bridge.Current)) await Task.Delay(10, cancellation.Token);
    }

    internal Task AssertNoRequestAsync() => connection!.AssertNoRequestAsync();

    internal void Disconnect()
    {
        connection?.Abort();
        connection = null;
    }

    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync();
        await Bridge.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        foreach (var peer in peers) await peer.DisposeAsync();
        server.Dispose();
        await Task.WhenAll(handlers).WaitAsync(TimeSpan.FromSeconds(5));
        lifetime.Dispose();
    }
}

internal sealed class NativePeer : IAsyncDisposable
{
    readonly ClientWebSocket socket;
    readonly Channel<JsonElement> commands = Channel.CreateUnbounded<JsonElement>(new() { SingleWriter = true });
    readonly TaskCompletionSource<WebSocketCloseStatus?> closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly Task receiving;
    int disposed;

    NativePeer(ClientWebSocket socket)
    {
        this.socket = socket;
        receiving = ReceiveAsync();
    }

    internal Task<WebSocketCloseStatus?> Closed => closed.Task;

    internal static async Task<NativePeer> ConnectAsync(Uri endpoint, string? origin)
    {
        var socket = new ClientWebSocket();
        try
        {
            if (origin is not null) socket.Options.SetRequestHeader("Origin", origin);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await socket.ConnectAsync(endpoint, cancellation.Token);
            return new(socket);
        }
        catch { socket.Dispose(); throw; }
    }

    internal async Task SendAsync(byte[] bytes, WebSocketMessageType type, bool endOfMessage)
        => await socket.SendAsync(bytes.AsMemory(), type, endOfMessage, CancellationToken.None);

    internal async Task<JsonElement> ReadAsync()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        return await commands.Reader.ReadAsync(cancellation.Token);
    }

    internal async Task AssertNoRequestAsync()
    {
        using var cancellation = new CancellationTokenSource(100);
        try
        {
            var message = await commands.Reader.ReadAsync(cancellation.Token);
            throw new InvalidOperationException($"Unexpected command after reconnect: {message}");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
    }

    async Task ReceiveAsync()
    {
        Exception? failure = null;
        try
        {
            var buffer = new byte[4096];
            using var message = new MemoryStream();
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer.AsMemory(), CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    closed.TrySetResult(socket.CloseStatus);
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                    return;
                }
                if (result.MessageType != WebSocketMessageType.Text)
                    throw new InvalidDataException("The Host sent a non-text bridge command.");
                message.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage) continue;
                using var json = JsonDocument.Parse(message.ToArray());
                await commands.Writer.WriteAsync(json.RootElement.Clone());
                message.SetLength(0);
            }
        }
        catch (Exception error) { failure = error; }
        finally
        {
            commands.Writer.TryComplete(failure);
            closed.TrySetResult(socket.CloseStatus);
        }
    }

    internal void Abort() => socket.Abort();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        socket.Abort();
        await receiving.WaitAsync(TimeSpan.FromSeconds(5));
        socket.Dispose();
    }
}
