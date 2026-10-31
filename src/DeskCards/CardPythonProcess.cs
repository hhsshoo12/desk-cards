using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;

namespace DeskCards;

/// <summary>
/// 카드의 main.py를 돌리는 Python 프로세스. Ready는 (필요하면 패키지를 설치하고) 프로세스를 띄워 포트가 열릴 때까지 기다린다.
/// python이 null이면 앱에 품은 Python, "python"이면 PATH의 Python(시스템 Python)이다. pip은 품은 Python에서만 쓴다.
/// </summary>
internal sealed class CardPythonProcess : IDisposable
{
    private readonly string _folder, _address, _python;
    private readonly string? _pip;
    private readonly int _port;
    private readonly CardProcessJob _job = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly StringBuilder _stderr = new();
    private readonly object _gate = new();
    private Process? _process;
    private bool _suspendRequested, _disposed;
    public event Action<string>? Failed;
    public Task Ready { get; }

    public CardPythonProcess(string folder, string address, int port, string? python = null, string? pip = null)
    {
        _folder = folder; _address = address; _port = port; _pip = pip;
        _python = python ?? PythonRuntime.Executable();
        Ready = RunAsync();
    }

    private async Task RunAsync()
    {
        if (_pip != null && CardPackages.NeedsInstall(_folder))
            await CardPackages.InstallAsync(_python, _pip, _folder, _stop.Token).ConfigureAwait(false);
        Launch();
        await WaitReady().ConfigureAwait(false);
    }

    private void Launch()
    {
        var start = new ProcessStartInfo(_python)
        {
            WorkingDirectory = _folder, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardError = true, RedirectStandardOutput = true,
        };
        start.ArgumentList.Add("-u"); start.ArgumentList.Add(Path.Combine(_folder, "main.py"));
        start.Environment["DESK_ADDR"] = _address;
        start.Environment["DESK_PORT"] = _port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        start.Environment["PYTHONUTF8"] = "1";
        start.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
        string lib = Path.Combine(_folder, CardPackages.LibFolder);
        string? inherited = Environment.GetEnvironmentVariable("PYTHONPATH");
        start.Environment["PYTHONPATH"] = string.IsNullOrEmpty(inherited) ? lib : lib + Path.PathSeparator + inherited;
        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (_gate)
            {
                _stderr.AppendLine(e.Data);
                if (_stderr.Length > 8192) _stderr.Remove(0, _stderr.Length - 8192);
            }
        };
        process.OutputDataReceived += (_, _) => { };
        process.Exited += (_, _) => { if (!_disposed) Failed?.Invoke(ErrorText("Python 서버가 종료됐어요.")); };
        lock (_gate)
        {
            if (_disposed) { process.Dispose(); throw new OperationCanceledException(); }
            try
            {
                process.Start();
                _job.Add(process.Handle);
                process.BeginErrorReadLine(); process.BeginOutputReadLine();
            }
            catch (System.ComponentModel.Win32Exception ex) when (!Path.IsPathRooted(_python))
            {
                process.Dispose();
                throw new IOException("PATH에서 Python을 찾지 못했어요. python.org에서 설치하거나 manifest의 \"python\": \"system\"을 지워 주세요.", ex);
            }
            catch
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                process.Dispose(); throw;
            }
            _process = process;
            if (_suspendRequested) SuspendLocked(true);
        }
    }

    private async Task WaitReady()
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(15))
        {
            _stop.Token.ThrowIfCancellationRequested();
            if (_process!.HasExited) throw new IOException(ErrorText("Python 서버를 시작하지 못했어요."));
            using var client = new TcpClient();
            try { await client.ConnectAsync(_address, _port, _stop.Token).ConfigureAwait(false); return; }
            catch (SocketException) { }
            await Task.Delay(100, _stop.Token).ConfigureAwait(false);
        }
        string error = ErrorText("Python 서버가 제한 시간 안에 포트를 열지 못했어요.");
        Dispose();
        throw new IOException(error);
    }

    private string ErrorText(string message) { lock (_gate) return message + "\n" + _stderr; }

    public void Suspend(bool suspend)
    {
        lock (_gate)
        {
            _suspendRequested = suspend;
            SuspendLocked(suspend);
        }
    }

    private void SuspendLocked(bool suspend)
    {
        if (_disposed || _process == null || _process.HasExited) return;
        if (suspend) NtSuspendProcess(_process.Handle); else NtResumeProcess(_process.Handle);
    }

    public void Dispose()
    {
        Process? process;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true; process = _process;
        }
        _stop.Cancel();
        try { if (process != null && !process.HasExited) { NtResumeProcess(process.Handle); process.Kill(entireProcessTree: true); } }
        catch (InvalidOperationException) { }
        finally { _job.Dispose(); process?.Dispose(); }
    }

    [DllImport("ntdll.dll")] private static extern int NtSuspendProcess(IntPtr process);
    [DllImport("ntdll.dll")] private static extern int NtResumeProcess(IntPtr process);
}
