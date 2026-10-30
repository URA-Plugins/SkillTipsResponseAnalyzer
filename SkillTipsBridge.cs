using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WatsonWebserver.Core;
using static SkillTipsResponseAnalyzer.i18n.ParseSkillTipsResponse;

namespace SkillTipsResponseAnalyzer;

internal readonly record struct SkillTipsLearnedSkill(int Id, int Level);

internal sealed record SkillTipsTraining(
    int CharaId, string StartTime, int Turn, int SkillPoints, SkillTipsLearnedSkill[] LearnedSkills)
{
    internal bool Matches(SkillTipsTraining? other) => other is not null
        && CharaId == other.CharaId && StartTime == other.StartTime && Turn == other.Turn
        && SkillPoints == other.SkillPoints && LearnedSkills.SequenceEqual(other.LearnedSkills);
}

internal sealed record SkillTipsBridgeState(
    bool Active, ulong PageId, ulong Revision, bool SelectionEvent, SkillTipsTraining? Training,
    int[] SelectedSkillIds, int RemainingPoints, string? Error)
{
    public string Kind { get; init; } = "state";
}

internal sealed record SkillTipsPreparedSelection(
    long RequestId, ulong PageId, ulong Revision, int[] SelectedSkillIds, int[] ExtraSkillIds, int RemainingPoints)
{
    public string Kind { get; init; } = "prepared";
}

internal sealed record SkillTipsSelectionResult(long RequestId, int[] SelectedSkillIds, int RemainingPoints)
{
    public string Kind { get; init; } = "result";
}

internal sealed class SkillTipsBridge(TimeSpan? requestTimeout = null) : IAsyncDisposable
{
    internal const int MaxFrameBytes = 65536;
    static readonly UTF8Encoding StrictUtf8 = new(false, true);
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        AllowDuplicateProperties = false
    };
    readonly object sync = new();
    readonly CancellationTokenSource lifetime = new();
    readonly SemaphoreSlim requestGate = new(1);
    readonly SemaphoreSlim sendGate = new(1);
    WebSocket? client;
    TaskCompletionSource? connectionClosed;
    SkillTipsBridgeState? current;
    string? lastError;
    SkillTipsPreparedSelection? prepared;
    PendingRequest? pending;
    long nextRequestId;

    internal SkillTipsBridgeState? Current { get { lock (sync) return current; } }
    internal string? LastError { get { lock (sync) return lastError; } }
    internal event Action? Changed;

    internal async Task AcceptAsync(HttpContextBase context, WebSocket connection, CancellationToken cancellationToken)
    {
        if (!IPAddress.TryParse(context.Request.Source.IpAddress, out var address)
            || !IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address)
            || context.Request.Headers["Origin"] is not null)
        {
            await connection.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, I18N_BridgeLocalOnly, cancellationToken).ConfigureAwait(false);
            return;
        }

        TaskCompletionSource? closed = null;
        lock (sync)
        {
            if (!lifetime.IsCancellationRequested && client is null)
            {
                client = connection;
                connectionClosed = closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
        if (closed is null)
        {
            await connection.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, I18N_BridgeConnectionRejected, cancellationToken).ConfigureAwait(false);
            return;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        var token = linked.Token;
        using var abort = token.Register(connection.Abort);
        Exception failure = new IOException(I18N_BridgeDisconnected);
        try
        {
            await ReceiveAsync(connection, token).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            failure = error;
            if (error is not (OperationCanceledException or WebSocketException or IOException or InvalidDataException or JsonException
                or DecoderFallbackException or ObjectDisposedException))
                throw;
        }
        finally
        {
            try { Disconnect(connection, token.IsCancellationRequested ? new OperationCanceledException(token) : failure); }
            finally { closed.TrySetResult(); }
        }
    }

    async Task ReceiveAsync(WebSocket connection, CancellationToken token)
    {
        var frame = new byte[MaxFrameBytes + 1];
        var length = 0;
        var helloReceived = false;
        while (true)
        {
            var result = await connection.ReceiveAsync(frame.AsMemory(length), token).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                await sendGate.WaitAsync(token).ConfigureAwait(false);
                try { await connection.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, token).ConfigureAwait(false); }
                finally { sendGate.Release(); }
                return;
            }
            if (result.MessageType != WebSocketMessageType.Text)
                throw new InvalidDataException(I18N_BridgeTextRequired);
            length += result.Count;
            if (length > MaxFrameBytes)
                throw new InvalidDataException(string.Format(I18N_BridgeMessageTooLarge, MaxFrameBytes));
            if (!result.EndOfMessage)
                continue;

            var json = StrictUtf8.GetString(frame, 0, length);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("kind", out var kindValue)
                || kindValue.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("SkillTips bridge message requires a string kind.");
            var kind = kindValue.GetString();
            if (!helloReceived)
            {
                var hello = root.Deserialize<Hello>(JsonOptions)!;
                if (hello.Kind != "hello" || hello.Protocol != "skill-tips-bridge" || hello.Version != 2
                    || hello.Profile != "umamusume-20261026-il2cpp-x64")
                    throw new InvalidDataException("SkillTips bridge hello protocol/version/profile does not match.");
                helloReceived = true;
            }
            else
                ReceiveMessage(connection, kind, root);
            length = 0;
        }
    }

    void ReceiveMessage(WebSocket connection, string? kind, JsonElement root)
    {
        if (kind == "state")
        {
            var state = root.Deserialize<SkillTipsBridgeState>(JsonOptions)!;
            ValidateIds(state.SelectedSkillIds);
            if (state.Active && (state.Training is null || state.PageId == 0 || state.Revision == 0))
                throw new InvalidDataException("SkillTips bridge active state requires a training baseline and positive page_id/revision.");
            if (state.Training is { } training)
                ValidateIds(training.LearnedSkills.Select(skill => skill.Id).ToArray());
            lock (sync)
            {
                if (client != connection) return;
                current = state;
                lastError = state.Error;
                if (prepared is { } saved && (!state.Active || state.Error is not null
                    || saved.PageId != state.PageId || saved.Revision != state.Revision))
                    prepared = null;
            }
            Changed?.Invoke();
            return;
        }

        lock (sync)
        {
            if (client != connection) return;
            if (pending is not { } request || !root.TryGetProperty("request_id", out var requestId)
                || requestId.ValueKind != JsonValueKind.Number
                || !requestId.TryGetInt64(out var responseId) || responseId != request.Id)
                throw new InvalidDataException("SkillTips bridge response has no matching request_id.");
            if (kind == "error")
            {
                var error = root.Deserialize<RemoteError>(JsonOptions)!;
                throw new InvalidDataException($"SkillTips bridge request {error.RequestId}: {error.Message}");
            }
            if (kind != request.Kind)
                throw new InvalidDataException($"SkillTips bridge expected {request.Kind}, received {kind}.");
            request.Completion.TrySetResult(root.Clone());
        }
    }

    internal async Task<SkillTipsPreparedSelection> PrepareAsync(
        SkillTipsTraining training, int[] skillIds, CancellationToken cancellationToken)
    {
        await requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (skillIds.Any(id => id <= 0) || skillIds.Distinct().Count() != skillIds.Length)
                throw new ArgumentException("SkillTips bridge requested skill IDs must be positive and unique.", nameof(skillIds));
            WebSocket connection;
            SkillTipsBridgeState state;
            lock (sync)
            {
                connection = client ?? throw new InvalidOperationException("SkillTips bridge is not connected.");
                state = current ?? throw new InvalidOperationException("SkillTips bridge has no current state.");
                if (!state.Active || state.Error is not null || !training.Matches(state.Training))
                    throw new InvalidOperationException("SkillTips bridge training baseline does not match the current skill page.");
                prepared = null;
            }
            var id = Interlocked.Increment(ref nextRequestId);
            var response = await RequestAsync(connection, id, "prepared", new
            {
                kind = "prepare", request_id = id, page_id = state.PageId, revision = state.Revision,
                training, skill_ids = skillIds
            }, cancellationToken).ConfigureAwait(false);
            var selection = response.Deserialize<SkillTipsPreparedSelection>(JsonOptions)!;
            ValidateIds(selection.SelectedSkillIds);
            ValidateIds(selection.ExtraSkillIds);
            if (selection.PageId != state.PageId || selection.Revision != state.Revision
                || selection.RemainingPoints < 0 || selection.RemainingPoints > training.SkillPoints
                || skillIds.Except(selection.SelectedSkillIds).Any()
                || !selection.ExtraSkillIds.SequenceEqual(state.SelectedSkillIds.Except(selection.SelectedSkillIds)))
                throw new InvalidDataException("SkillTips bridge prepared selection does not match the requested page/revision or target skills.");
            lock (sync)
            {
                if (client != connection || current is not { Active: true, Error: null } latest
                    || latest.PageId != state.PageId || latest.Revision != state.Revision
                    || !training.Matches(latest.Training))
                    throw new InvalidOperationException("SkillTips bridge state changed during preparation.");
                prepared = selection;
            }
            return selection;
        }
        // Keep type checks inside catch: an async filtered catch left sync held in the .NET 10 smoke probe.
        catch (Exception ex)
        {
            if (ex is JsonException or InvalidDataException) DisconnectCurrent(ex);
            throw;
        }
        finally { requestGate.Release(); }
    }

    internal async Task<SkillTipsSelectionResult> ReplaceAsync(
        SkillTipsPreparedSelection selection, CancellationToken cancellationToken)
    {
        await requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            WebSocket connection;
            lock (sync)
            {
                connection = client ?? throw new InvalidOperationException("SkillTips bridge is not connected.");
                if (!ReferenceEquals(prepared, selection) || current is not { Active: true, Error: null } state
                    || state.PageId != selection.PageId || state.Revision != selection.Revision)
                    throw new InvalidOperationException("SkillTips bridge prepared selection has expired.");
                prepared = null;
            }
            var id = Interlocked.Increment(ref nextRequestId);
            var response = await RequestAsync(connection, id, "result", new
            {
                kind = "replace", request_id = id, prepared_request_id = selection.RequestId
            }, cancellationToken).ConfigureAwait(false);
            var result = response.Deserialize<SkillTipsSelectionResult>(JsonOptions)!;
            ValidateIds(result.SelectedSkillIds);
            if (!result.SelectedSkillIds.SequenceEqual(selection.SelectedSkillIds)
                || result.RemainingPoints != selection.RemainingPoints)
                throw new InvalidDataException("SkillTips bridge replacement result differs from the prepared selection.");
            return result;
        }
        catch (Exception ex)
        {
            if (ex is JsonException or InvalidDataException) DisconnectCurrent(ex);
            throw;
        }
        finally { requestGate.Release(); }
    }

    async Task<JsonElement> RequestAsync(WebSocket connection, long id, string kind, object message, CancellationToken token)
    {
        var request = new PendingRequest(id, kind);
        using var timeout = new CancellationTokenSource(requestTimeout ?? TimeSpan.FromSeconds(15));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token, lifetime.Token);
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
            if (bytes.Length > MaxFrameBytes)
                throw new InvalidDataException(string.Format(I18N_BridgeMessageTooLarge, MaxFrameBytes));
            lock (sync)
            {
                if (client != connection)
                    throw new IOException("SkillTips bridge disconnected before the request was sent.");
                pending = request;
            }
            await sendGate.WaitAsync(linked.Token).ConfigureAwait(false);
            try { await connection.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, linked.Token).ConfigureAwait(false); }
            finally { sendGate.Release(); }
            return await request.Completion.Task.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (ex is OperationCanceledException && timeout.IsCancellationRequested
                && !token.IsCancellationRequested && !lifetime.IsCancellationRequested)
            {
                var failure = new TimeoutException(I18N_BridgeTimeout, ex);
                Disconnect(connection, failure);
                throw failure;
            }
            Disconnect(connection, ex);
            throw;
        }
        finally
        {
            lock (sync)
                if (pending == request) pending = null;
        }
    }

    static void ValidateIds(int[] ids)
    {
        var previous = 0;
        foreach (var id in ids)
        {
            if (id <= previous)
                throw new InvalidDataException("SkillTips bridge skill IDs must be positive, unique and sorted.");
            previous = id;
        }
    }

    void DisconnectCurrent(Exception error)
    {
        WebSocket? connection;
        lock (sync) connection = client;
        if (connection is not null) Disconnect(connection, error);
    }

    void Disconnect(WebSocket connection, Exception error)
    {
        lock (sync)
        {
            if (client != connection) return;
            client = null;
            current = null;
            prepared = null;
            lastError = error is OperationCanceledException ? null : error.Message;
            if (error is OperationCanceledException canceled)
                pending?.Completion.TrySetCanceled(canceled.CancellationToken);
            else
                pending?.Completion.TrySetException(error);
            pending = null;
        }
        connection.Abort();
        Changed?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync().ConfigureAwait(false);
        DisconnectCurrent(new OperationCanceledException(lifetime.Token));
        Task? running;
        lock (sync) running = connectionClosed?.Task;
        if (running is not null) await running.ConfigureAwait(false);
    }

    sealed record Hello(string Kind, string Protocol, int Version, string Profile);
    sealed record RemoteError(string Kind, long RequestId, string Message);
    sealed class PendingRequest(long id, string kind)
    {
        internal long Id { get; } = id;
        internal string Kind { get; } = kind;
        internal TaskCompletionSource<JsonElement> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
