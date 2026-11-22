using System.Diagnostics;
using System.Net.WebSockets;

namespace PhoneBox.Agent;

/// <summary>Relays a single paired phone's live audio to the local virtual audio devices.</summary>
public sealed class DesktopAudio(IHostApplicationLifetime application, ILogger<DesktopAudio> log)
{
    private const int PacketBytes = 960 * sizeof(short);
    private readonly string? _helper = Environment.GetEnvironmentVariable("PHONEBOX_DESKTOP_AUDIO_EXECUTABLE");
    private readonly string _microphone = Environment.GetEnvironmentVariable("PHONEBOX_DESKTOP_AUDIO_MIC") ?? "BlackHole 2ch";
    private readonly string _speaker = Environment.GetEnvironmentVariable("PHONEBOX_DESKTOP_AUDIO_SPEAKER") ?? "BlackHole 16ch";
    private readonly SemaphoreSlim _phone = new(1);

    public bool Enabled => OperatingSystem.IsMacOSVersionAtLeast(14, 2) &&
        File.Exists(_helper) && !string.Equals(_microphone, _speaker, StringComparison.Ordinal);

    public async Task ConnectAsync(HttpContext context)
    {
        // The phone only opens this socket over the paired HTTPS connection.
        if (!Enabled || !context.Request.IsHttps || !context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        if (!_phone.Wait(0))
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            return;
        }

        try
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, application.ApplicationStopping);
            using var helper = new Process
            {
                StartInfo = new ProcessStartInfo(_helper!)
                {
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                },
            };
            helper.StartInfo.ArgumentList.Add("--run");
            helper.StartInfo.ArgumentList.Add(_microphone);
            helper.StartInfo.ArgumentList.Add(_speaker);
            if (!helper.Start()) throw new InvalidOperationException("Could not start the audio bridge.");
            var stderr = helper.StandardError.ReadToEndAsync();
            try
            {
                var upstream = ReceiveMicrophoneAsync(socket, helper.StandardInput.BaseStream, stop.Token);
                var startCodex = context.Request.Query["startCodex"] == "1";
                var downstream = SendSpeakerAsync(socket, helper.StandardOutput.BaseStream, startCodex, stop.Token);
                try
                {
                    var finished = await Task.WhenAny(upstream, downstream);
                    await finished;
                }
                finally
                {
                    await stop.CancelAsync();
                    if (!helper.HasExited) helper.Kill(entireProcessTree: true);
                    try { await Task.WhenAll(upstream, downstream); }
                    catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException) { }
                }
            }
            finally
            {
                if (!helper.HasExited) helper.Kill(entireProcessTree: true);
                await helper.WaitForExitAsync();
                if (helper.ExitCode > 0)
                {
                    var details = (await stderr).Trim();
                    if (details.Length != 0)
                        log.LogWarning("Desktop audio helper exited: {Details}", details[..Math.Min(500, details.Length)]);
                }
                if (socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseReceived)
                {
                    using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Audio disconnected.", limit.Token);
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidOperationException
                                   or System.ComponentModel.Win32Exception or WebSocketException)
        {
            log.LogDebug("Desktop audio disconnected ({Type})", ex.GetType().Name);
        }
        finally
        {
            _phone.Release();
        }
    }

    private static async Task ReceiveMicrophoneAsync(WebSocket socket, Stream input, CancellationToken cancellation)
    {
        var frame = new byte[PacketBytes + 1];
        var received = 0;
        while (!cancellation.IsCancellationRequested)
        {
            var chunk = await socket.ReceiveAsync(frame.AsMemory(received), cancellation);
            if (chunk.MessageType == WebSocketMessageType.Close) return;
            if (chunk.MessageType != WebSocketMessageType.Binary)
                throw new IOException("Expected a binary microphone frame.");
            received += chunk.Count;
            if (received > PacketBytes) throw new IOException("Microphone frame is too large.");
            if (!chunk.EndOfMessage) continue;
            if (received != PacketBytes) throw new IOException("Invalid microphone frame length.");
            await input.WriteAsync(frame.AsMemory(0, PacketBytes), cancellation);
            received = 0;
        }
    }

    private static async Task SendSpeakerAsync(WebSocket socket, Stream output, bool startCodex, CancellationToken cancellation)
    {
        var kind = new byte[1];
        var frame = new byte[PacketBytes];
        while (await output.ReadAsync(kind.AsMemory(), cancellation) != 0)
        {
            switch (kind[0])
            {
                case 0:
                    await socket.SendAsync("waiting"u8.ToArray().AsMemory(), WebSocketMessageType.Text, true, cancellation);
                    if (startCodex)
                    {
                        startCodex = false;
                        cancellation.ThrowIfCancellationRequested();
#if !WINDOWS
                        if (KeySender.StartMacCodexVoice() is { } error)
                            await socket.SendAsync(System.Text.Encoding.UTF8.GetBytes("start-error:" + error).AsMemory(),
                                WebSocketMessageType.Text, true, cancellation);
#endif
                    }
                    break;
                case 1:
                    startCodex = false;
                    await socket.SendAsync("active"u8.ToArray().AsMemory(), WebSocketMessageType.Text, true, cancellation);
                    break;
                case 2:
                    await output.ReadExactlyAsync(frame.AsMemory(), cancellation);
                    await socket.SendAsync(frame.AsMemory(), WebSocketMessageType.Binary, true, cancellation);
                    break;
                default:
                    throw new IOException("Invalid audio bridge message.");
            }
        }
    }
}
