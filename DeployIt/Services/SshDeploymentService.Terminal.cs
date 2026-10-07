using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using DeployIt.DTOs;
using DeployIt.Models;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace DeployIt.Services;

public sealed partial class SshDeploymentService
{
    public async Task RunTerminalAsync(DeploymentProject project, WebSocket socket,
        Func<bool> sessionValid, CancellationToken cancellationToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ct = lifetime.Token;
        using var key = LoadKey(project);
        using var client = new SshClient(Connection(project, key));
        using var sendGate = new SemaphoreSlim(1, 1);
        Pin(client, project.HostFingerprint);
        ShellStream? shell = null;
        Task? outgoing = null, incoming = null, session = null;
        var output = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(32) {
            SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait
        });

        async Task Send(ReadOnlyMemory<byte> data, WebSocketMessageType type, CancellationToken token)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await sendGate.WaitAsync(timeout.Token);
            try { await socket.SendAsync(data, type, true, timeout.Token); }
            finally { sendGate.Release(); }
        }
        Task Status(string state, string message) => Send(
            JsonSerializer.SerializeToUtf8Bytes(new { type = "status", state, message }), WebSocketMessageType.Text, ct);
        var readGate = new object();
        void Drain()
        {
            lock (readGate)
            {
                try
                {
                    // Drain ShellStream's internal buffer as well as bounding the browser queue.
                    while (shell is not null && shell.DataAvailable && !ct.IsCancellationRequested)
                    {
                        var buffer = new byte[16384];
                        var count = shell.Read(buffer, 0, buffer.Length);
                        if (count == 0) break;
                        if (count != buffer.Length) Array.Resize(ref buffer, count);
                        if (!output.Writer.TryWrite(buffer))
                        {
                            output.Writer.TryComplete(new DomainException("Терминал закрыт: браузер не успевает принимать вывод. Подключитесь заново; ограничьте объём вывода команды."));
                            break;
                        }
                    }
                }
                catch (Exception) { output.Writer.TryComplete(new DomainException("SSH-соединение прервано. Подключитесь к терминалу заново.")); }
            }
        }
        try
        {
            await Status("connecting", "Подключаемся по SSH…");
            if (!sessionValid()) throw new DomainException("Сессия панели истекла. Войдите заново.");
            try { await client.ConnectAsync(ct); }
            catch (SshAuthenticationException) { throw AuthenticationError(project); }
            shell = client.CreateShellStream("xterm-256color", 80, 24, 0, 0, 16384);
            shell.DataReceived += (_, _) => Drain();
            shell.Closed += (_, _) => output.Writer.TryComplete();
            shell.ErrorOccurred += (_, _) => output.Writer.TryComplete(new DomainException("SSH-соединение прервано. Подключитесь к терминалу заново."));
            await Status("connected", $"SSH · {project.Username}@{project.Host}:{project.Port}");
            Drain();
            outgoing = SendOutput();
            incoming = ReceiveInput(shell);
            session = WatchSession();
            var startup = $"cd -- {Quote(project.WorkingDirectory + "/current")} 2>/dev/null || cd -- {Quote(project.WorkingDirectory)} 2>/dev/null || cd ~; "
                + $"export DEPLOYIT_PROJECT_DIR={Quote(project.WorkingDirectory)} DEPLOYIT_ENV_FILE={Quote(project.WorkingDirectory + "/.env")} COMPOSE_PROJECT_NAME=deployit-{project.Id:N}";
            shell.WriteLine(startup);
            var finished = await Task.WhenAny(outgoing, incoming, session);
            await finished;
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            if (socket.State == WebSocketState.Open)
            {
                try { await Status("error", e is DomainException ? e.Message
                    : "Не удалось открыть или продолжить SSH-терминал. Проверьте адрес, ключ или пароль и отпечаток сервера. Пример подключения: ssh -p 22 deploy@app.example.com."); }
                catch { /* A disconnected browser cannot receive an error message. */ }
            }
        }
        finally
        {
            // Send the close frame before cancelling the pending receive, which can abort the socket.
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var closing = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                var acquired = false;
                try
                {
                    await sendGate.WaitAsync(closing.Token);
                    acquired = true;
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "SSH session closed", closing.Token);
                }
                catch { socket.Abort(); }
                finally { if (acquired) sendGate.Release(); }
            }
            lifetime.Cancel();
            output.Writer.TryComplete();
            shell?.Dispose();
            client.Dispose();
            foreach (var task in new[] { outgoing, incoming, session })
                if (task is not null) { try { await task; } catch { } }
        }

        async Task SendOutput()
        {
            await foreach (var data in output.Reader.ReadAllAsync(ct))
                await Send(data, WebSocketMessageType.Binary, ct);
        }
        async Task ReceiveInput(ShellStream stream)
        {
            var buffer = new byte[8192];
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult received;
                do
                {
                    received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                    if (received.MessageType == WebSocketMessageType.Close) return;
                    if (received.MessageType != WebSocketMessageType.Text || message.Length + received.Count > 32768)
                        throw new DomainException("Слишком большой ввод терминала. Вставляйте команды небольшими частями.");
                    message.Write(buffer, 0, received.Count);
                } while (!received.EndOfMessage);
                using var json = JsonDocument.Parse(message.ToArray());
                var root = json.RootElement;
                switch (root.GetProperty("type").GetString())
                {
                    case "input":
                        var data = root.GetProperty("data").GetString() ?? "";
                        if (data.Length > 4096) throw new DomainException("Слишком большой ввод терминала. Вставляйте команды небольшими частями.");
                        stream.Write(data);
                        break;
                    case "resize":
                        var columns = Math.Clamp(root.GetProperty("columns").GetInt32(), 20, 500);
                        var rows = Math.Clamp(root.GetProperty("rows").GetInt32(), 5, 300);
                        stream.ChangeWindowSize((uint)columns, (uint)rows, 0, 0);
                        break;
                    default: throw new DomainException("Неизвестный запрос терминала. Обновите страницу и подключитесь заново.");
                }
            }
        }
        async Task WatchSession()
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(15000, ct);
                if (!sessionValid()) throw new DomainException("Сессия панели истекла. Войдите заново, чтобы открыть терминал.");
                if (!client.IsConnected) return;
            }
        }
    }
}
