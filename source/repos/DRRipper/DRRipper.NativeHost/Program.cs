using DRRipper.BrowserBridge;

namespace DRRipper.NativeHost
{
    /// <summary>
    /// DRRipper native-messaging host (Ticket #007 §9): stdin framing loop,
    /// strict validation, named-pipe forward to the running desktop app
    /// (launching it hidden when needed), acknowledgement back to the browser.
    /// Never executes shell commands from extension data; never touches the
    /// queue database — the desktop bridge owns all scheduler access (§11).
    /// </summary>
    internal static class Program
    {
        private const int ExitOk = 0;
        private const int ExitUsage = 2;

        public static async Task<int> Main(string[] args)
        {
            try
            {
                var mode = ParseMode(args);
                switch (mode.Command)
                {
                    case HostCommand.Serve:
                        return await ServeAsync(mode, CancellationToken.None).ConfigureAwait(false);
                    case HostCommand.Register:
                        return Register(mode);
                    case HostCommand.Unregister:
                        return Unregister(mode);
                    case HostCommand.Status:
                        return Status(mode);
                    default:
                        PrintUsage();
                        return ExitUsage;
                }
            }
            catch (Exception ex)
            {
                try { Console.Error.WriteLine("DRRipper.NativeHost fatal: " + Trim(ex.Message)); } catch { }
                return 1;
            }
        }

        private sealed class HostOptions
        {
            public HostCommand Command = HostCommand.Serve;
            public BrowserKind Browser = BrowserKind.Chrome;
            public string ExtensionId = string.Empty;
            public string PipeName = BrowserBridgeProtocol.PipeName;
            public string DesktopExe = string.Empty;
        }

        private enum HostCommand { Serve, Register, Unregister, Status }

        private static HostOptions ParseMode(string[] args)
        {
            var options = new HostOptions();
            if (args == null)
                return options;
            for (int i = 0; i < args.Length; i++)
            {
                var a = (args[i] ?? string.Empty).Trim().ToLowerInvariant();
                switch (a)
                {
                    case "--register": options.Command = HostCommand.Register; break;
                    case "--unregister": options.Command = HostCommand.Unregister; break;
                    case "--status": options.Command = HostCommand.Status; break;
                    case "--serve": options.Command = HostCommand.Serve; break;
                    case "--browser":
                        if (i + 1 < args.Length && TryParseBrowser(args[++i], out var b))
                            options.Browser = b;
                        break;
                    case "--extension-id":
                        if (i + 1 < args.Length)
                            options.ExtensionId = (args[++i] ?? string.Empty).Trim();
                        break;
                    case "--pipe-name":
                        if (i + 1 < args.Length && !string.IsNullOrWhiteSpace(args[i + 1]))
                            options.PipeName = args[++i].Trim();
                        break;
                    case "--desktop-exe":
                        if (i + 1 < args.Length)
                            options.DesktopExe = (args[++i] ?? string.Empty).Trim();
                        break;
                    case "--help":
                    case "-h":
                    case "/?":
                        PrintUsage();
                        Environment.Exit(ExitUsage);
                        break;
                }
            }
            return options;
        }

        private static bool TryParseBrowser(string? value, out BrowserKind browser)
        {
            browser = BrowserKind.Chrome;
            if (string.Equals(value, "edge", StringComparison.OrdinalIgnoreCase)) { browser = BrowserKind.Edge; return true; }
            if (string.Equals(value, "firefox", StringComparison.OrdinalIgnoreCase)) { browser = BrowserKind.Firefox; return true; }
            if (string.Equals(value, "chrome", StringComparison.OrdinalIgnoreCase)) { return true; }
            return false;
        }

        /// <summary>Browser stdio loop: one framed message in, one framed answer out.</summary>
        private static async Task<int> ServeAsync(HostOptions options, CancellationToken ct)
        {
            var stdin = Console.OpenStandardInput();
            var stdout = Console.OpenStandardOutput();
            var forwarder = new PipeDesktopForwarder(options.PipeName,
                string.IsNullOrWhiteSpace(options.DesktopExe) ? null : options.DesktopExe);
            var handler = new NativeHostRequestHandler(forwarder);
            while (!ct.IsCancellationRequested)
            {
                byte[] payload;
                try
                {
                    payload = await NativeMessageFraming.ReadPayloadAsync(stdin, ct).ConfigureAwait(false);
                }
                catch (EndOfStreamException)
                {
                    return ExitOk; // browser closed the port
                }
                catch (InvalidDataException ex)
                {
                    // Framing violation: answer with a categorized error when the
                    // frame lets us, then keep serving (the port stays usable).
                    var code = ex.Message.Contains("exceeds", StringComparison.OrdinalIgnoreCase)
                        ? BrowserErrorCodes.MessageTooLarge : BrowserErrorCodes.MalformedMessage;
                    try
                    {
                        await NativeMessageFraming.WriteAsync(stdout,
                            new BrowserEnqueueResponse { Accepted = false, ErrorCode = code, Message = Trim(ex.Message) },
                            ct).ConfigureAwait(false);
                    }
                    catch { return ExitOk; }
                    continue;
                }
                BrowserEnqueueResponse response;
                try
                {
                    response = await handler.HandlePayloadAsync(payload, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    response = new BrowserEnqueueResponse
                    {
                        Accepted = false,
                        ErrorCode = BrowserErrorCodes.InternalError,
                        Message = Trim(ex.Message),
                    };
                }
                try
                {
                    await NativeMessageFraming.WriteAsync(stdout, response, ct).ConfigureAwait(false);
                }
                catch
                {
                    return ExitOk; // browser went away mid-answer
                }
            }
            return ExitOk;
        }

        private static int Register(HostOptions options)
        {
            if (string.IsNullOrWhiteSpace(options.ExtensionId))
            {
                Console.Error.WriteLine("Missing --extension-id (see browser-extension/README.md).");
                return ExitUsage;
            }
            var hostExe = ResolveOwnPath();
            var status = BrowserRegistration.Install(
                options.Browser, hostExe, options.ExtensionId, new WindowsRegistryView());
            Console.WriteLine(status.State + (string.IsNullOrEmpty(status.Detail) ? "" : ": " + status.Detail));
            return status.State == BrowserRegistrationState.Installed ? ExitOk : 1;
        }

        private static int Unregister(HostOptions options)
        {
            BrowserRegistration.Uninstall(options.Browser, new WindowsRegistryView());
            Console.WriteLine("unregistered " + options.Browser);
            return ExitOk;
        }

        private static int Status(HostOptions options)
        {
            var status = BrowserRegistration.GetStatus(options.Browser, new WindowsRegistryView());
            Console.WriteLine(status.Browser + ": " + status.State +
                (string.IsNullOrEmpty(status.ManifestPath) ? "" : " " + status.ManifestPath) +
                (string.IsNullOrEmpty(status.Detail) ? "" : " (" + status.Detail + ")"));
            return ExitOk;
        }

        private static string ResolveOwnPath()
        {
            try
            {
                var path = Environment.ProcessPath;
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                    return path;
            }
            catch { }
            return Path.Combine(AppContext.BaseDirectory, "DRRipper.NativeHost.exe");
        }

        private static void PrintUsage()
        {
            Console.WriteLine("DRRipper.NativeHost — browser native-messaging host (Ticket #007).");
            Console.WriteLine("  (no args)                  serve Native Messaging on stdio");
            Console.WriteLine("  --register --browser <chrome|edge|firefox> --extension-id <id>");
            Console.WriteLine("  --unregister --browser <chrome|edge|firefox>");
            Console.WriteLine("  --status --browser <chrome|edge|firefox>");
            Console.WriteLine("  --pipe-name <name> --desktop-exe <path>   (diagnostics/tests)");
        }

        private static string Trim(string message)
        {
            if (string.IsNullOrEmpty(message))
                return "unknown error";
            message = message.Replace('\r', ' ').Replace('\n', ' ');
            return message.Length > 200 ? message.Substring(0, 200) : message;
        }
    }
}
