using System.IO.Pipes;
using System.Text;

namespace Surfio.Services;

/// <summary>
/// Keeps one Surfio window. Opening files from Explorer while Surfio is running
/// sends them to the existing window instead of starting a second player.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    const string Name = "Surfio.SingleInstance.v1";
    readonly Mutex _mutex;
    readonly CancellationTokenSource _cts = new();

    public bool IsFirst { get; }

    public SingleInstance()
    {
        _mutex = new Mutex(true, Name, out var created);
        IsFirst = created;
    }

    /// <summary>Hands the arguments to the running instance. Returns false if it could not be reached.</summary>
    public static bool SendToFirst(string[] args)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", Name, PipeDirection.Out);
            client.Connect(1500);
            var bytes = Encoding.UTF8.GetBytes(string.Join('\n', args));
            client.Write(bytes, 0, bytes.Length);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Listen(Action<string[]> onArgs)
    {
        Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(Name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync(_cts.Token);
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var text = await reader.ReadToEndAsync(_cts.Token);
                    onArgs(text.Split('\n', StringSplitOptions.RemoveEmptyEntries));
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    await Task.Delay(200);
                }
            }
        });
    }

    public void Dispose()
    {
        _cts.Cancel();
        if (IsFirst) _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
