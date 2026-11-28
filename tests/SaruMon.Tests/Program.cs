using SaruMon;

namespace SaruMon.Tests;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--traffic-helper")
            return Monitoring.TrafficMonitorHelper.RunAsync(args).GetAwaiter().GetResult();
        if (args.Length == 2 && args[0] == "--ipc-host")
        {
            using var instance = new SingleInstance(args[1]);
            if (!instance.IsPrimary) return 10;
            using var activated = new ManualResetEventSlim();
            instance.StartListening(activated.Set);
            Console.WriteLine("READY");
            Console.Out.Flush();
            if (!activated.Wait(TimeSpan.FromSeconds(12))) return 11;
            // Allow the listener to acknowledge before disposing it.
            Console.WriteLine("ACTIVATED");
            Console.Out.Flush();
            Console.ReadLine();
            return 0;
        }
        if (args.Length == 2 && args[0] == "--ipc-client")
        {
            using var instance = new SingleInstance(args[1]);
            if (instance.IsPrimary) return 12;
            return instance.ActivateExistingAsync().GetAwaiter().GetResult() ? 0 : 13;
        }
        try
        {
            if (args.Length == 1 && args[0] == "--udp-live-smoke")
                return UdpLiveSmoke.RunAsync().GetAwaiter().GetResult();
            if (args.Length == 1 && args[0] == "--tcp-live-smoke")
                return TcpLiveSmoke.RunAsync().GetAwaiter().GetResult();
            if (args.Length == 1 && args[0] == "--tcp-network-smoke")
                return TcpNetworkSmoke.RunAsync().GetAwaiter().GetResult();
            if (args.Length == 1 && args[0] == "--investigation-live-smoke")
                return InvestigationTests.RunLiveAsync().GetAwaiter().GetResult();
            if (args.Length == 2 && args[0] == "--icon-preview")
            {
                TrafficIconsTests.WritePreview(args[1]);
                Console.WriteLine("Icon preview: " + Path.GetFullPath(args[1]));
                return 0;
            }
            var count = TrafficTests.Run() + TrafficIconsTests.Run()
                + TcpConnectionTests.Run() + ReverseDnsTests.Run() + UpdateTests.Run()
                + UdpFlowTests.Run() + TrafficProtocolTests.Run() + ConnectionLogTests.Run()
                + TcpPeerTests.Run() + TcpLogTests.Run()
                + SingleInstanceTests.Run() + InstallationTests.Run() + InvestigationTests.Run();
            Console.WriteLine($"PASS: {count} tests");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
