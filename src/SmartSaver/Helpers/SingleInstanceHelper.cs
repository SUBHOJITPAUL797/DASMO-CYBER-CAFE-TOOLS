using System.IO;
using System.IO.Pipes;
using System.Text;
using Serilog;
namespace SmartSaver.Helpers;

/// <summary>
/// Enforces single-instance application behavior using a named Mutex
/// </summary>
public static class SingleInstanceHelper
{
    private const string MutexName = "Global\\DASMO_CYBER_CAFE_TOOLS_SingleInstance_A1B2C3D4";
    private const string PipeName = "DASMO_CYBER_CAFE_TOOLS_IPC_Pipe_A1B2C3D4";
    private static Mutex? _mutex;
    private static CancellationTokenSource? _pipeCts;

    /// <summary>
    /// Event raised when a message is received from another instance
    /// </summary>
    public static event Action<string>? MessageReceived;

    /// <summary>
    /// Attempts to acquire the single-instance mutex.
    /// Returns true if this is the first instance, false if another is already running.
    /// </summary>
    public static bool TryAcquire()
    {
        _mutex = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            _mutex.Dispose();
            _mutex = null;
            return false;
        }

        // We are the first instance, start the named pipe server
        StartNamedPipeServer();
        return true;
    }

    /// <summary>
    /// Releases the mutex and stops the named pipe server when the application exits
    /// </summary>
    public static void Release()
    {
        try
        {
            _pipeCts?.Cancel();
            _pipeCts?.Dispose();
            _pipeCts = null;

            _mutex?.ReleaseMutex();
            _mutex?.Dispose();
            _mutex = null;
        }
        catch (ApplicationException)
        {
            // Mutex was not owned — ignore
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error releasing single instance mutex/pipe");
        }
    }

    /// <summary>
    /// Sends a message to the running instance via Named Pipes
    /// </summary>
    public static void SignalExistingInstance(string message)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.Asynchronous);
            // Connect with a short timeout since it should be local and fast
            client.Connect(2000);

            byte[] buffer = Encoding.UTF8.GetBytes(message);
            client.Write(buffer, 0, buffer.Length);
            client.Flush();
        }
        catch (TimeoutException)
        {
            Log.Warning("Timeout connecting to existing SmartSaver instance via IPC pipe.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to signal existing instance via IPC");
        }
    }

    private static void StartNamedPipeServer()
    {
        _pipeCts = new CancellationTokenSource();
        var token = _pipeCts.Token;

        Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Message, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync(token);

                    using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
                    string? message = await reader.ReadToEndAsync(token);

                    if (!string.IsNullOrEmpty(message))
                    {
                        Log.Information("IPC Message Received: {Message}", message);
                        MessageReceived?.Invoke(message);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Server stopping
                    break;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error in IPC Named Pipe Server");
                    // Wait a bit before retrying to prevent hot loop on persistent error
                    await Task.Delay(1000, token);
                }
            }
        }, token);
    }
}
