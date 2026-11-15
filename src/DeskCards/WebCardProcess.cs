using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace DeskCards;

/// <summary>
/// 카드의 exe 하나를 자식 프로세스로 돌린다. 앱이 끝나거나 죽으면 Job Object가 같이 끝낸다.
/// 표준 출력의 한 줄 = 카드가 앱에 보내는 JSON 메시지, 표준 입력의 한 줄 = 앱이 카드에 보내는 JSON 메시지.
/// </summary>
internal sealed class WebCardProcess : IDisposable
{
    private static CardProcessJob? _job;
    private static readonly object JobLock = new();

    private readonly Process _process;
    private readonly object _writeLock = new();
    private bool _disposed;

    /// <summary>exe가 한 줄을 보냈다(다른 스레드에서 불린다).</summary>
    public event Action<string>? Message;

    /// <summary>exe가 끝났다(종료 코드). Dispose로 끝낸 경우에는 불리지 않는다.</summary>
    public event Action<int>? Exited;

    public WebCardProcess(string exe, string workingDirectory, string cardId, string dataDirectory, string? arguments = null)
    {
        Directory.CreateDirectory(dataDirectory);
        var start = new ProcessStartInfo(exe)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (!string.IsNullOrEmpty(arguments)) start.Arguments = arguments;
        start.Environment["DESKCARD_ID"] = cardId;
        start.Environment["DESKCARD_DATA"] = dataDirectory;
        _process = new Process { StartInfo = start, EnableRaisingEvents = true };
        _process.OutputDataReceived += (_, e) => { if (e.Data != null) Message?.Invoke(e.Data); };
        _process.ErrorDataReceived += (_, _) => { };
        _process.Exited += (_, _) =>
        {
            if (_disposed) return;
            int code = 0;
            try { code = _process.ExitCode; } catch (InvalidOperationException) { }
            Exited?.Invoke(code);
        };
    }

    public void Start()
    {
        _process.Start();
        try
        {
            lock (JobLock) _job ??= new CardProcessJob();
            _job.Add(_process.Handle);
        }
        catch (System.ComponentModel.Win32Exception) { /* 이미 다른 Job에 묶인 환경: 앱이 정상 종료할 때 Dispose로 정리한다 */ }
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
    }

    public bool HasExited
    {
        get { try { return _process.HasExited; } catch (InvalidOperationException) { return true; } }
    }

    /// <summary>exe에 한 줄을 보낸다. 이미 끝났으면 조용히 무시한다.</summary>
    public void Send(string line)
    {
        if (_disposed) return;
        try { lock (_writeLock) { _process.StandardInput.WriteLine(line); _process.StandardInput.Flush(); } }
        catch (IOException) { }
        catch (InvalidOperationException) { }
    }

    /// <summary>입력을 닫아 스스로 끝나길 잠깐 기다리고, 안 끝나면 강제로 끈다.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _process.StandardInput.Close(); } catch (Exception ex) when (ex is IOException or InvalidOperationException) { }
        try
        {
            if (!_process.WaitForExit(800)) _process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        _process.Dispose();
    }
}