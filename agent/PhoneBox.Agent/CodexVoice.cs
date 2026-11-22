using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PhoneBox.Agent;

/// <summary>Signaling for one phone-initiated Codex WebRTC call. Media stays on the WebRTC connection.</summary>
public sealed class CodexVoice(IHostApplicationLifetime application, SessionMonitor sessions, ILogger<CodexVoice> log)
{
    public sealed record Offer(Guid Id, string? Sdp, Guid? ThreadId, string? Ticket);
    public sealed record Conversation(Guid Id, string Title, string Project, long UpdatedAt, bool Busy, string Ticket);
    internal sealed record VoiceMessage(string role, string text);

    private readonly string? _workspace = Environment.GetEnvironmentVariable("PHONEBOX_CODEX_WORKSPACE");
    private readonly string? _executable = Environment.GetEnvironmentVariable("PHONEBOX_CODEX_EXECUTABLE");
    private readonly byte[] _conversationKey = RandomNumberGenerator.GetBytes(32);
    private readonly object _gate = new();
    private Call? _active;

    public bool Enabled => Directory.Exists(_workspace) && File.Exists(_executable);

    public async Task<IResult> ListAsync(string device, string? cursor, CancellationToken requestCancellation)
    {
        if (device.Length is < 1 or > 64 || cursor is { Length: > 6000 })
            return Results.BadRequest(new { error = "Invalid conversation list request." });
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(requestCancellation, application.ApplicationStopping);
        deadline.CancelAfter(TimeSpan.FromSeconds(3.5));
        var ct = deadline.Token;
        using var process = CreateProcess();
        var started = false;
        try
        {
            started = process.Start();
            if (!started) throw new InvalidOperationException("Could not launch Codex App Server.");
            await InitializeAsync(process, ct);
            await SendAsync(process, new
            {
                id = 2, method = "thread/list", @params = new
                {
                    cursor, limit = 24, sortKey = "updated_at", currentAccountOnly = true, useStateDbOnly = true,
                    sourceKinds = new[] { "cli", "vscode", "exec", "appServer" },
                },
            }, ct);
            var result = await ReadResultAsync(process, 2, ct);
            var threads = new List<Conversation>();
            foreach (var entry in result.GetProperty("data").EnumerateArray())
            {
                if (!Guid.TryParse(Text(entry, "id"), out var id)) continue;
                var title = Text(entry, "name");
                if (string.IsNullOrWhiteSpace(title)) title = Text(entry, "preview");
                if (string.IsNullOrWhiteSpace(title)) title = "Untitled Codex conversation";
                var cwd = Text(entry, "cwd")?.TrimEnd('/', '\\') ?? "";
                var project = cwd[(Math.Max(cwd.LastIndexOf('/'), cwd.LastIndexOf('\\')) + 1)..];
                var updatedAt = entry.TryGetProperty("updatedAt", out var timestamp) && timestamp.TryGetInt64(out var value) ? value : 0;
                threads.Add(new Conversation(id, title[..Math.Min(title.Length, 130)], project, updatedAt,
                    IsBusy(id) || IsActive(entry), Convert.ToHexString(ConversationTicket(device, id))));
            }
            return Results.Json(new { threads, nextCursor = Text(result, "nextCursor") });
        }
        catch (Exception error)
        {
            log.LogWarning("Could not list Codex conversations ({Type})", error.GetType().Name);
            return Results.Json(new { error = "Could not load Codex conversations. Try again." },
                statusCode: StatusCodes.Status502BadGateway);
        }
        finally
        {
            if (started) await CloseProcessAsync(process, null);
        }
    }

    public async Task<IResult> StartAsync(string device, Offer? offer, bool viaWifi, CancellationToken requestCancellation)
    {
        if (device.Length is < 1 or > 64 || offer is null || offer.Id == Guid.Empty ||
            offer.Sdp is not { Length: <= 24 * 1024 } sdp || !sdp.StartsWith("v=0", StringComparison.Ordinal) ||
            !sdp.Contains("\nm=audio ", StringComparison.Ordinal) ||
            !sdp.Contains("\nm=application ", StringComparison.Ordinal))
            return Results.BadRequest(new { error = "Invalid WebRTC offer." });
        if (offer.ThreadId is { } selected)
        {
            if (!MatchesConversation(device, selected, offer.Ticket))
                return Results.BadRequest(new { error = "Reload the conversation list and choose the conversation again." });
            if (IsBusy(selected))
                return Results.Conflict(new { error = "This Codex conversation is still running a task. Wait for it to finish." });
        }

        Call call;
        lock (_gate)
        {
            if (_active is not null)
                return Results.Conflict(new { error = "A Codex voice call is already running." });
            call = new Call(device, offer.Id, application.ApplicationStopping);
            _active = call;
        }

        _ = RunAsync(call, sdp, offer.ThreadId, viaWifi);
        try
        {
            var answer = await call.Answer.Task.WaitAsync(TimeSpan.FromSeconds(25), requestCancellation);
            return Results.Json(new { sdp = answer });
        }
        catch (ConversationInUseException error)
        {
            return Results.Conflict(new { error = error.Message });
        }
        catch (Exception error) when (error is TimeoutException or OperationCanceledException or InvalidOperationException)
        {
            Stop(device, offer.Id);
            var message = error is InvalidOperationException ? error.Message : "Codex did not start the voice call in time.";
            return Results.Json(new { error = message }, statusCode: StatusCodes.Status502BadGateway);
        }
    }

    public void Stop(string device, Guid id)
    {
        lock (_gate)
        {
            if (_active is { } active && active.Device == device && active.Id == id)
                active.Cancellation.Cancel();
        }
    }

    private Process CreateProcess()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _executable!,
            WorkingDirectory = _workspace!,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("app-server");
        startInfo.ArgumentList.Add("--stdio");
        return new Process { StartInfo = startInfo };
    }

    private async Task RunAsync(Call call, string offer, Guid? selection, bool viaWifi)
    {
        using var cancellation = call.Cancellation;
        var ct = cancellation.Token;
        using var process = CreateProcess();
        string? threadId = null;
        var started = false;
        var realtimeClosed = false;
        try
        {
            started = process.Start();
            if (!started) throw new InvalidOperationException("Could not launch Codex App Server.");
            await InitializeAsync(process, ct);
            var (thread, history) = await OpenConversationAsync(process, selection, ct);
            threadId = Text(thread, "id")
                ?? throw new InvalidOperationException("Codex did not return a conversation ID.");
            await SendAsync(process, new
            {
                id = 5,
                method = "thread/realtime/start",
                @params = new
                {
                    threadId,
                    version = "v3",
                    model = "gpt-live-1-codex",
                    outputModality = "audio",
                    initialItems = history,
                    transport = new { type = "webrtc", sdp = offer },
                },
            }, ct);

            // The SDP and session events arrive asynchronously. Keep reading while the call is live.
            while (!ct.IsCancellationRequested)
            {
                var message = await ReadAsync(process, ct);
                if (message.TryGetProperty("error", out var error))
                    throw ServiceError(error);
                if (!message.TryGetProperty("method", out var method)) continue;
                switch (method.GetString())
                {
                    case "thread/realtime/sdp":
                        var answer = message.GetProperty("params").GetProperty("sdp").GetString();
                        if (answer is null) throw new InvalidOperationException("Codex returned an empty WebRTC answer.");
                        if (call.Answer.TrySetResult(answer))
                            log.LogInformation("Codex voice WebRTC call started over {Transport} ({Conversation})",
                                viaWifi ? "Wi-Fi" : "USB", selection is null ? "new temporary conversation" : "resumed conversation");
                        break;
                    case "thread/realtime/transcript/done":
                        var transcript = message.GetProperty("params");
                        if (transcript.TryGetProperty("role", out var role) &&
                            role.GetString() is "user" or "assistant" &&
                            transcript.TryGetProperty("text", out var text) && !string.IsNullOrWhiteSpace(text.GetString()))
                            log.LogInformation("Codex voice transcribed {Speaker} audio", role.GetString());
                        break;
                    case "thread/realtime/error":
                        throw ServiceError(message.GetProperty("params"));
                    case "thread/realtime/closed":
                        realtimeClosed = true;
                        return;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (InvalidOperationException error)
        {
            call.Answer.TrySetException(error);
            log.LogWarning("Codex voice session failed: {Message}", error.Message);
        }
        catch (Exception error)
        {
            call.Answer.TrySetException(new InvalidOperationException("Could not connect to Codex App Server."));
            log.LogWarning("Codex voice connection failed ({Type})", error.GetType().Name);
        }
        finally
        {
            call.Answer.TrySetException(new InvalidOperationException("The Codex voice call closed."));
            try
            {
                if (started) await CloseProcessAsync(process, realtimeClosed ? null : threadId,
                    waitForRealtimeClose: call.Answer.Task.IsCompletedSuccessfully);
            }
            finally
            {
                lock (_gate)
                {
                    _active = null;
                }
            }
        }
    }

    private static async Task InitializeAsync(Process process, CancellationToken ct)
    {
        _ = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
        await SendAsync(process, new
        {
            id = 1, method = "initialize", @params = new
            {
                clientInfo = new { name = "phonebox", version = AppPaths.Version },
                capabilities = new { experimentalApi = true },
            },
        }, ct);
        await ReadResultAsync(process, 1, ct);
        await SendAsync(process, new { method = "initialized" }, ct);
    }

    private async Task<(JsonElement Thread, VoiceMessage[] History)> OpenConversationAsync(
        Process process, Guid? selection, CancellationToken ct)
    {
        if (selection is null)
        {
            await SendAsync(process, new
            {
                id = 2, method = "thread/start",
                @params = new { cwd = _workspace, ephemeral = true, sandbox = "read-only", approvalPolicy = "never" },
            }, ct);
            return ((await ReadResultAsync(process, 2, ct)).GetProperty("thread"), []);
        }

        var threadId = selection.Value.ToString();
        await SendAsync(process, new { id = 2, method = "thread/read", @params = new { threadId, includeTurns = false } }, ct);
        var metadata = (await ReadResultAsync(process, 2, ct)).GetProperty("thread");
        if (IsActive(metadata) || IsBusy(selection.Value))
            throw new InvalidOperationException("This Codex conversation is still running a task. Wait for it to finish.");
        await SendAsync(process, new
        {
            id = 3, method = "thread/resume",
            @params = new { threadId, excludeTurns = true, sandbox = "read-only", approvalPolicy = "never" },
        }, ct);
        var resumed = (await ReadResultAsync(process, 3, ct)).GetProperty("thread");
        await SendAsync(process, new
        {
            id = 4, method = "thread/turns/list", @params = new { threadId, limit = 8, itemsView = "full", sortDirection = "desc" },
        }, ct);
        return (resumed, GetRecentMessages(await ReadResultAsync(process, 4, ct)));
    }

    internal static VoiceMessage[] GetRecentMessages(JsonElement turns)
    {
        var history = new List<VoiceMessage>();
        var remaining = 9000;
        foreach (var turn in turns.GetProperty("data").EnumerateArray())
        {
            foreach (var item in turn.GetProperty("items").EnumerateArray().Reverse())
            {
                string? text;
                string role;
                switch (Text(item, "type"))
                {
                    case "userMessage":
                        role = "user";
                        text = string.Join("\n", item.GetProperty("content").EnumerateArray()
                            .Where(part => Text(part, "type") == "text").Select(part => Text(part, "text")));
                        break;
                    case "agentMessage" when Text(item, "phase") != "commentary":
                        role = "assistant";
                        text = Text(item, "text");
                        break;
                    default:
                        continue;
                }
                if (string.IsNullOrWhiteSpace(text)) continue;
                var length = Math.Min(text.Length, Math.Min(remaining, 2400));
                history.Add(new VoiceMessage(role, text[..length]));
                remaining -= length;
                if (history.Count == 16 || remaining == 0) return history.AsEnumerable().Reverse().ToArray();
            }
        }
        return history.AsEnumerable().Reverse().ToArray();
    }

    private bool IsBusy(Guid id) => sessions.Snapshot().Any(session => session.Status == "busy" &&
        session.Id.StartsWith("codex:", StringComparison.Ordinal) &&
        session.Id.EndsWith(id.ToString() + ".jsonl", StringComparison.OrdinalIgnoreCase));

    private static bool IsActive(JsonElement thread) =>
        thread.TryGetProperty("status", out var status) && Text(status, "type") == "active";

    private byte[] ConversationTicket(string device, Guid id) =>
        HMACSHA256.HashData(_conversationKey, Encoding.UTF8.GetBytes(device + "\n" + id));

    private bool MatchesConversation(string device, Guid id, string? ticket)
    {
        if (ticket?.Length != 64) return false;
        try { return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(ticket), ConversationTicket(device, id)); }
        catch (FormatException) { return false; }
    }

    private static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var text) &&
        text.ValueKind == JsonValueKind.String ? text.GetString() : null;

    private static async Task CloseProcessAsync(Process process, string? threadId, bool waitForRealtimeClose = false)
    {
        try
        {
            if (process.HasExited) return;
            using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                    if (threadId is not null)
                    {
                        await SendAsync(process, new { id = 6, method = "thread/realtime/stop", @params = new { threadId } }, shutdown.Token);
                        if (waitForRealtimeClose)
                        {
                            // EOF shuts down the app server. Keep its input open until it has recorded the end of the call.
                            using var finalize = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                            try { await WaitForRealtimeCloseAsync(process, finalize.Token); }
                            catch (OperationCanceledException) when (finalize.IsCancellationRequested) { }
                        }
                    }
                    process.StandardInput.Close();
                    using var exit = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await process.WaitForExitAsync(exit.Token);
            }
            finally
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception error) when (error is OperationCanceledException or InvalidOperationException or IOException or System.ComponentModel.Win32Exception) { }
    }

    private static async Task WaitForRealtimeCloseAsync(Process process, CancellationToken ct)
    {
        while (true)
        {
            var message = await ReadAsync(process, ct);
            if (Text(message, "method") == "thread/realtime/closed" ||
                (message.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number &&
                    id.GetInt32() == 6 && message.TryGetProperty("error", out _)))
                return;
        }
    }

    private static async Task SendAsync(Process process, object message, CancellationToken ct)
    {
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), ct);
        await process.StandardInput.FlushAsync(ct);
    }

    private static async Task<JsonElement> ReadAsync(Process process, CancellationToken ct)
    {
        var line = await process.StandardOutput.ReadLineAsync(ct)
            ?? throw new InvalidOperationException("Codex App Server exited before the voice call ended.");
        using var document = JsonDocument.Parse(line);
        return document.RootElement.Clone();
    }

    private static async Task<JsonElement> ReadResultAsync(Process process, int id, CancellationToken ct)
    {
        while (true)
        {
            var message = await ReadAsync(process, ct);
            if (!message.TryGetProperty("id", out var responseId) || responseId.ValueKind != JsonValueKind.Number ||
                responseId.GetInt32() != id) continue;
            if (message.TryGetProperty("error", out var error)) throw ServiceError(error);
            return message.GetProperty("result");
        }
    }

    private static InvalidOperationException ServiceError(JsonElement error)
    {
        var text = error.TryGetProperty("message", out var message) ? message.GetString() : null;
        if (string.IsNullOrWhiteSpace(text)) return new("Codex rejected the realtime voice request.");
        if (text.Contains("already has an active writer", StringComparison.OrdinalIgnoreCase))
            return new ConversationInUseException();
        return new InvalidOperationException(text[..Math.Min(text.Length, 240)]);
    }

    private sealed class ConversationInUseException()
        : InvalidOperationException("这条会话仍被另一个 Codex 进程占用。请先选择一条尚未在桌面打开的会话。")
    {
    }

    private sealed class Call(string device, Guid id, CancellationToken stopping)
    {
        public string Device { get; } = device;
        public Guid Id { get; } = id;
        public TaskCompletionSource<string> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenSource Cancellation { get; } = CreateCancellation(stopping);

        private static CancellationTokenSource CreateCancellation(CancellationToken stopping)
        {
            var source = CancellationTokenSource.CreateLinkedTokenSource(stopping);
            source.CancelAfter(TimeSpan.FromMinutes(45));
            return source;
        }
    }
}
