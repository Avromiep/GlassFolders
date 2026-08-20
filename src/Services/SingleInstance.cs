using System.IO;
using System.IO.Pipes;
using System.Threading;

namespace GlassFolders.Services;

/// <summary>
/// Ensures one running instance. Secondary launches (from a desktop shortcut click)
/// forward their request over a named pipe to the primary, then exit.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\GlassFolders.Instance";
    private const string PipeName = "GlassFolders.Pipe";

    private Mutex? _mutex;
    private CancellationTokenSource? _cts;

    public bool IsPrimary { get; private set; }

    public bool TryAcquire()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        IsPrimary = createdNew;
        return createdNew;
    }

    /// <summary>Primary listens for forwarded commands; onMessage runs on a worker thread.</summary>
    public void StartServer(Action<string> onMessage)
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        PipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync(token);
                    using var reader = new StreamReader(server);
                    string? msg = await reader.ReadToEndAsync(token);
                    if (!string.IsNullOrWhiteSpace(msg))
                        onMessage(msg.Trim());
                }
                catch (OperationCanceledException) { break; }
                catch { /* keep the listener alive */ }
            }
        }, token);
    }

    public static bool SendToPrimary(string message, int timeoutMs = 1500)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(timeoutMs);
            using var writer = new StreamWriter(client) { AutoFlush = true };
            writer.Write(message);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _mutex?.Dispose();
    }
}
