using SaruMon;

namespace SaruMon.Tests;

internal static class Program
{
    private static int Main(string[] args)
    {
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
            var count = TrafficTests.Run() + UpdateTests.Run() + SingleInstanceTests.Run();
            Console.WriteLine($"PASS: {count} tests");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
