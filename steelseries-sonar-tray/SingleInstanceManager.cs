using System.IO.Pipes;
using System.Text;

namespace SonarQuickMixer;

internal sealed class SingleInstanceManager : IDisposable
{
    private const string MutexName = "SonarQuickMixer.SingleInstance";
    private const string PipeName = "SonarQuickMixer.SingleInstance";
    private const string ShowCommand = "show";

    private Mutex? _mutex;
    private CancellationTokenSource? _listenerCts;

    public bool TryAcquireOwnership()
    {
        _mutex = new Mutex(true, MutexName, out var createdNew);
        return createdNew;
    }

    public void NotifyExistingInstance()
    {
        var commandBytes = Encoding.UTF8.GetBytes(ShowCommand);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                client.Connect(500);
                client.Write(commandBytes);
                client.Flush();
                return;
            }
            catch
            {
                Thread.Sleep(100);
            }
        }
    }

    public void StartListening(Action onShowRequested)
    {
        _listenerCts = new CancellationTokenSource();
        var token = _listenerCts.Token;

        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(
                        PipeName,
                        PipeDirection.In,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    await server.WaitForConnectionAsync(token).ConfigureAwait(false);

                    var buffer = new byte[ShowCommand.Length];
                    var read = 0;
                    while (read < buffer.Length)
                    {
                        var chunk = await server.ReadAsync(buffer.AsMemory(read), token).ConfigureAwait(false);
                        if (chunk == 0)
                        {
                            break;
                        }

                        read += chunk;
                    }

                    if (read == buffer.Length
                        && Encoding.UTF8.GetString(buffer) == ShowCommand)
                    {
                        onShowRequested();
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    try
                    {
                        await Task.Delay(100, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
        }, token);
    }

    public void Dispose()
    {
        _listenerCts?.Cancel();
        _listenerCts?.Dispose();
        _listenerCts = null;

        if (_mutex is null)
        {
            return;
        }

        try
        {
            _mutex.ReleaseMutex();
        }
        catch
        {
            // Mutex was not acquired or already released.
        }

        _mutex.Dispose();
        _mutex = null;
    }
}
