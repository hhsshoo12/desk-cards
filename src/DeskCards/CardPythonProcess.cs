using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;

namespace DeskCards;

internal sealed class CardPythonProcess : IDisposable
{
    private readonly Process _process;
    private readonly CardProcessJob _job = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly StringBuilder _stderr = new();
    private readonly object _gate = new();
    private bool _disposed;
    public event Action<string>? Failed;
    public Task Ready { get; }
    public CardPythonProcess(string folder, string address, int port, string? python = null)
    {
        var start = new ProcessStartInfo(python ?? PythonRuntime.Executable())
        {
            WorkingDirectory = folder, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardError = true, RedirectStandardOutput = true,
        };
        start.ArgumentList.Add("-u"); start.ArgumentList.Add(Path.Combine(folder, "main.py"));
        start.Environment["PYTHONUTF8"] = "1";
        start.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
        start.Environment["DESK_ADDR"] = address;
        start.Environment["DESK_PORT"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _process = new Process { StartInfo = start, EnableRaisingEvents = true };
        _process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (_gate)
            {
                _stderr.AppendLine(e.Data);
                if (_stderr.Length > 8192) _stderr.Remove(0, _stderr.Length - 8192);
            }
        };
        _process.OutputDataReceived += (_, _) => { };
        _process.Exited += (_, _) => { if (!_disposed) Failed?.Invoke(ErrorText("Python 서버가 종료됐어요.")); };
        try
        {
            _process.Start();
            _job.Add(_process.Handle);
            _process.BeginErrorReadLine(); _process.BeginOutputReadLine();
            Ready = WaitReady(address, port);
        }
        catch
        {
            _job.Dispose();
            try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            _process.Dispose(); _stop.Dispose(); throw;
        }
    }
    private async Task WaitReady(string address, int port)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(15))
        {
            _stop.Token.ThrowIfCancellationRequested();
            if (_process.HasExited) throw new IOException(ErrorText("Python 서버를 시작하지 못했어요."));
            using var client = new TcpClient();
            try { await client.ConnectAsync(address, port, _stop.Token); return; }
            catch (SocketException) { }
            await Task.Delay(100, _stop.Token);
        }
        string error = ErrorText("Python 서버가 제한 시간 안에 포트를 열지 못했어요.");
        Dispose();
        throw new IOException(error);
    }
    private string ErrorText(string message) { lock (_gate) return message + "\n" + _stderr; }
    public void Suspend(bool suspend)
    {
        if (_disposed || _process.HasExited) return;
        if (suspend) NtSuspendProcess(_process.Handle); else NtResumeProcess(_process.Handle);
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _stop.Cancel();
        try { if (!_process.HasExited) { NtResumeProcess(_process.Handle); _process.Kill(entireProcessTree: true); } }
        catch (InvalidOperationException) { }
        finally { _job.Dispose(); _process.Dispose(); }
    }
    [DllImport("ntdll.dll")] private static extern int NtSuspendProcess(IntPtr process);
    [DllImport("ntdll.dll")] private static extern int NtResumeProcess(IntPtr process);
}
