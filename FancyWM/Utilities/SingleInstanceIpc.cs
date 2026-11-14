using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;

namespace FancyWM.Utilities
{
    internal static class SingleInstanceIpc
    {
        private static readonly string PipeName = $"ScreenSplit_IPC_{Environment.UserName}";
        private static readonly string EventName = $@"Local\ScreenSplit_Wakeup_{Environment.UserName}";
        private static CancellationTokenSource? s_cts;
        private static EventWaitHandle? s_wakeupEvent;

        public static bool SendCommand(string command, int timeoutMs = 2000)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                try
                {
                    using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                    int remaining = Math.Max(100, timeoutMs - (int)sw.ElapsedMilliseconds);
                    client.Connect(remaining);
                    using var writer = new StreamWriter(client, Encoding.UTF8) { AutoFlush = true };
                    writer.WriteLine(command);
                    return true;
                }
                catch (TimeoutException)
                {
                    break;
                }
                catch (Exception)
                {
                    Thread.Sleep(50);
                }
            }
            return false;
        }

        public static void SignalWakeupEvent()
        {
            try
            {
                using var ev = EventWaitHandle.OpenExisting(EventName);
                ev.Set();
            }
            catch
            {
            }
        }

        public static void StartServer(Action<string> onCommandReceived)
        {
            s_cts = new CancellationTokenSource();
            var token = s_cts.Token;

            var thread = new Thread(() =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        using var server = new NamedPipeServerStream(
                            PipeName,
                            PipeDirection.In,
                            NamedPipeServerStream.MaxAllowedServerInstances,
                            PipeTransmissionMode.Byte);

                        var asyncResult = server.BeginWaitForConnection(null, null);
                        WaitHandle.WaitAny(new[] { asyncResult.AsyncWaitHandle, token.WaitHandle });

                        if (token.IsCancellationRequested)
                        {
                            break;
                        }

                        server.EndWaitForConnection(asyncResult);

                        using var reader = new StreamReader(server, Encoding.UTF8);
                        var line = reader.ReadLine();
                        if (!string.IsNullOrEmpty(line))
                        {
                            onCommandReceived(line);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception)
                    {
                        Thread.Sleep(100);
                    }
                }
            })
            {
                IsBackground = true,
                Name = "ScreenSplit_IPC_Server"
            };

            thread.Start();
        }

        public static void StartWakeupListener(Action onWakeup)
        {
            try
            {
                s_wakeupEvent = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
                var token = s_cts?.Token ?? CancellationToken.None;

                var thread = new Thread(() =>
                {
                    while (!token.IsCancellationRequested)
                    {
                        try
                        {
                            WaitHandle.WaitAny(new[] { s_wakeupEvent, token.WaitHandle });
                            if (token.IsCancellationRequested)
                            {
                                break;
                            }
                            onWakeup();
                        }
                        catch (Exception)
                        {
                            Thread.Sleep(200);
                        }
                    }
                })
                {
                    IsBackground = true,
                    Name = "ScreenSplit_Wakeup_Listener"
                };

                thread.Start();
            }
            catch
            {
            }
        }

        public static void StopServer()
        {
            try
            {
                s_cts?.Cancel();
                s_wakeupEvent?.Dispose();
            }
            catch
            {
            }
        }
    }
}
