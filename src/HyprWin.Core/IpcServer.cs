using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace HyprWin.Core;

/// <summary>
/// Named Pipe IPC Server providing hyprctl-compatible CLI and external automation.
/// Pipe: \\.\pipe\hyprwin-ipc
/// ACL is restricted to the current user. One request produces one response, then the client is disconnected.
/// </summary>
public sealed class IpcServer : IDisposable
{
    public const string PipeName = "hyprwin-ipc";
    private const int MaxRequestBytes = 8192;
    private readonly Func<string, Task<string>> _commandHandler;
    private CancellationTokenSource? _cts;
    private Task? _serverTask;
    private bool _disposed;

    public IpcServer(Func<string, Task<string>> commandHandler)
    {
        _commandHandler = commandHandler;
    }

    public void Start()
    {
        if (_serverTask != null) return;

        if (IsProcessElevated())
        {
            Logger.Instance.Warn("IPC pipe not started: process is elevated. Named pipe ACL is for the logged-in user only.");
            return;
        }

        _cts = new CancellationTokenSource();
        _serverTask = Task.Run(() => ServerLoopAsync(_cts.Token));
        Logger.Instance.Info($"IPC Server started on pipe \\\\.\\pipe\\{PipeName} (current-user ACL)");
    }

    private async Task ServerLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var pipeServer = CreateSecurePipe();
                await pipeServer.WaitForConnectionAsync(ct);

                using var reader = new StreamReader(pipeServer, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: MaxRequestBytes, leaveOpen: true);
                using var writer = new StreamWriter(pipeServer, Encoding.UTF8, bufferSize: MaxRequestBytes, leaveOpen: true) { AutoFlush = true };

                string? line = await reader.ReadLineAsync(ct);

                if (line is null)
                {
                    await writer.WriteLineAsync("error: empty request");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    await writer.WriteLineAsync("error: empty request");
                    continue;
                }

                int byteCount = Encoding.UTF8.GetByteCount(line);
                if (byteCount > MaxRequestBytes)
                {
                    await writer.WriteLineAsync("error: request too large");
                    continue;
                }

                string response = await _commandHandler(line.Trim());
                await writer.WriteLineAsync(response);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Logger.Instance.Debug($"IPC Server connection error: {ex.Message}");
                try { await Task.Delay(100, ct); } catch (OperationCanceledException) { break; }
            }
        }
    }

    private static NamedPipeServerStream CreateSecurePipe()
    {
        var identity = WindowsIdentity.GetCurrent();
        var userSid = identity.User ?? throw new InvalidOperationException("Current user SID is unavailable");

        var pipeSecurity = new PipeSecurity();
        pipeSecurity.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        pipeSecurity.AddAccessRule(new PipeAccessRule(
            userSid,
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            PipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: MaxRequestBytes,
            outBufferSize: MaxRequestBytes,
            pipeSecurity);
    }

    internal static bool IsProcessElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts?.Cancel();
        _cts?.Dispose();
        Logger.Instance.Info("IPC Server stopped");
    }
}
