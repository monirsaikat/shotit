using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace ShotIt.App.Infrastructure;

/// <summary>
/// Ensures one ShotIt per user session. A second launch forwards its command line to the running
/// instance over a named pipe restricted to the current user, then exits.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _stop = new();
    private bool _owned;

    public SingleInstance()
    {
        string user = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        int session = System.Diagnostics.Process.GetCurrentProcess().SessionId;
        string id = $"ShotIt.{user}.{session}";
        _pipeName = id + ".pipe";
        _mutex = new Mutex(initiallyOwned: true, @"Local\" + id + ".mutex", out _owned);
    }

    /// <summary>True when this process is the first instance.</summary>
    public bool IsFirst => _owned;

    /// <summary>Sends the command line to the running instance. Returns false when it does not answer.</summary>
    public bool SendToFirst(string[] args, int timeoutMs = 3000)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(timeoutMs);
            byte[] payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(args));
            client.Write(payload);
            client.Flush();
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Starts listening for forwarded command lines. The callback runs on a background thread.</summary>
    public void Listen(Action<string[]> onArguments)
    {
        ArgumentNullException.ThrowIfNull(onArguments);
        _ = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(_stop.Token);

                    using var buffer = new MemoryStream();
                    byte[] chunk = new byte[4096];
                    int read;
                    // A command line is tiny; cap the read so a misbehaving client cannot exhaust memory.
                    while (buffer.Length < 64 * 1024 && (read = await server.ReadAsync(chunk, _stop.Token)) > 0)
                        buffer.Write(chunk, 0, read);

                    var args = JsonSerializer.Deserialize<string[]>(buffer.ToArray());
                    if (args is not null) onArguments(args);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
                {
                    await Task.Delay(200);
                }
            }
        });
    }

    public void Dispose()
    {
        _stop.Cancel();
        if (_owned)
        {
            try { _mutex.ReleaseMutex(); }
            catch (ApplicationException) { /* released on a different thread; the OS frees it at exit */ }
            _owned = false;
        }

        _mutex.Dispose();
        _stop.Dispose();
    }
}

/// <summary>The commands ShotIt accepts on its command line and from a second instance. Anything else is ignored.</summary>
public enum AppCommand
{
    None,
    CaptureRegion,
    CaptureFullScreen,
    CaptureWindow,
    CaptureActiveWindow,
    CaptureFreeform,
    CaptureDelayed,
    CaptureScrolling,
    RecordVideo,
    RecordGif,
    EditFile,
    PinFile,
    OpenSettings,
    OpenHistory,
    Quit,
    SelfTest,
}

public sealed record AppCommandLine(AppCommand Command, string? Argument = null, string? Argument2 = null)
{
    public bool Silent { get; init; }

    /// <summary>Parses a command line. Unknown switches are ignored; a bare existing image path means "edit this file".</summary>
    public static AppCommandLine Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var command = AppCommand.None;
        string? argument = null, argument2 = null;
        bool silent = false;

        for (int i = 0; i < args.Count; i++)
        {
            string a = args[i];
            string? Next() => i + 1 < args.Count ? args[++i] : null;
            switch (a.ToLowerInvariant())
            {
                case "--capture":
                    command = (Next() ?? "region").ToLowerInvariant() switch
                    {
                        "fullscreen" or "screen" => AppCommand.CaptureFullScreen,
                        "window" => AppCommand.CaptureWindow,
                        "active" or "activewindow" => AppCommand.CaptureActiveWindow,
                        "freeform" => AppCommand.CaptureFreeform,
                        "delayed" => AppCommand.CaptureDelayed,
                        "scrolling" or "scroll" => AppCommand.CaptureScrolling,
                        _ => AppCommand.CaptureRegion,
                    };
                    break;
                case "--record":
                    command = string.Equals(Next(), "gif", StringComparison.OrdinalIgnoreCase) ? AppCommand.RecordGif : AppCommand.RecordVideo;
                    break;
                case "--edit":
                    command = AppCommand.EditFile;
                    argument = Next();
                    break;
                case "--pin":
                    command = AppCommand.PinFile;
                    argument = Next();
                    break;
                case "--settings":
                    command = AppCommand.OpenSettings;
                    break;
                case "--history":
                    command = AppCommand.OpenHistory;
                    break;
                case "--quit":
                    command = AppCommand.Quit;
                    break;
                case "--selftest":
                    command = AppCommand.SelfTest;
                    argument = Next();
                    argument2 = Next();
                    break;
                case "--silent" or "--autostart":
                    silent = true;
                    break;
                default:
                    if (command == AppCommand.None && !a.StartsWith('-') && File.Exists(a))
                    {
                        command = AppCommand.EditFile;
                        argument = a;
                    }

                    break;
            }
        }

        return new AppCommandLine(command, argument, argument2) { Silent = silent };
    }
}
