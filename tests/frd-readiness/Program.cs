global using System.Buffers.Binary;
global using System.Diagnostics;
global using System.Runtime.InteropServices;
using System.Net;
using System.Net.Sockets;
using System.Reflection;

namespace ArdUi;

static class ReadinessTest
{
    static void Check(bool value,string message)
    {if(!value)throw new IOException(message);Console.WriteLine("PASS: "+message);}

    public static async Task<int> Main(string[] args)
    {
        if(args.Length>0&&args[0]=="--fixture")return await Fixture(args[1],int.Parse(args[2]));
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(15));var ct=deadline.Token;
        using(var listener=new TcpListener(IPAddress.Loopback,0))
        {
            listener.Start();var port=((IPEndPoint)listener.LocalEndpoint).Port;
            Check(!FrdListener.Ready(Environment.ProcessId,port),"TCP alone is not ready");
            using var udp=new UdpClient(new IPEndPoint(IPAddress.Loopback,port));
            Check(FrdListener.Ready(Environment.ProcessId,port),"same process owns TCP and UDP on the requested loopback port");
            await WithChild("idle",port,async process=>
            {
                Check(!FrdListener.Ready(process.Id,port),"another process's listeners cannot satisfy readiness");
                await ExpectTimeout(process,port,ct);
            });
        }
        await WithChild("silent",FreePort(),async process=>
        {
            var port=int.Parse(process.StartInfo.ArgumentList[^1]);
            await FrdListener.Wait(process,port,ct,TimeSpan.FromSeconds(3));
            Check(true,"silent child becomes ready without emitting or parsing a log line");
        });
        await WithChild("tcp-first",FreePort(),async process=>
        {
            var port=int.Parse(process.StartInfo.ArgumentList[^1]);
            Check(await process.StandardOutput.ReadLineAsync(ct)=="TCP bound","fixture opened TCP first");
            var ready=FrdListener.Wait(process,port,ct,TimeSpan.FromSeconds(3));
            await Task.Delay(150,ct);Check(!ready.IsCompleted,"waits for UDP rather than accepting TCP-only startup");
            await ready;Check(true,"delayed UDP bind completes readiness");
        });
        await WithChild("log-only",FreePort(),async process=>
        {
            await ExpectTimeout(process,int.Parse(process.StartInfo.ArgumentList[^1]),ct);
            Check(true,"a legacy ready log without sockets does not authorize forwarding");
        });
        await WithChild("exit",FreePort(),async process=>
        {
            try{await FrdListener.Wait(process,int.Parse(process.StartInfo.ArgumentList[^1]),ct,TimeSpan.FromSeconds(3));throw new Exception("Exited child accepted.");}
            catch(IOException ex){Check(ex.Message.Contains("23",StringComparison.Ordinal),"startup failure includes the actual exit code");}
        });
        await WithChild("idle",FreePort(),async process=>
        {
            using var cancel=CancellationTokenSource.CreateLinkedTokenSource(ct);cancel.CancelAfter(150);
            try{await FrdListener.Wait(process,int.Parse(process.StartInfo.ArgumentList[^1]),cancel.Token);throw new Exception("Cancellation ignored.");}
            catch(OperationCanceledException)when(cancel.IsCancellationRequested){Check(true,"authorization cancellation interrupts readiness");}
        });
        Console.WriteLine("ALL FRD READINESS TESTS PASSED");return 0;
    }

    static async Task ExpectTimeout(Process process,int port,CancellationToken ct)
    {
        try{await FrdListener.Wait(process,port,ct,TimeSpan.FromMilliseconds(350));throw new Exception("Missing listeners accepted.");}
        catch(IOException ex){Check(ex.Message.Contains("超时",StringComparison.Ordinal),"missing listeners fail with a bounded, explicit timeout");}
    }

    static int FreePort()
    {
        using var tcp=new TcpListener(IPAddress.Loopback,0);tcp.Start();var port=((IPEndPoint)tcp.LocalEndpoint).Port;
        using var udp=new UdpClient(new IPEndPoint(IPAddress.Loopback,port));return port;
    }

    static async Task WithChild(string mode,int port,Func<Process,Task> test)
    {
        var start=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        if(Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet",StringComparison.OrdinalIgnoreCase))start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        foreach(var arg in new[]{"--fixture",mode,port.ToString()})start.ArgumentList.Add(arg);
        using var process=Process.Start(start)??throw new IOException("Could not start listener fixture.");
        var errors=process.StandardError.ReadToEndAsync();
        try{await test(process);}
        finally
        {
            if(!process.HasExited)process.Kill(true);
            await process.WaitForExitAsync();var text=await errors;
            if(text.Length>0)Console.Error.WriteLine(text);
        }
    }

    static async Task<int> Fixture(string mode,int port)
    {
        if(mode=="exit")return 23;
        if(mode=="log-only")Console.WriteLine("Remote listener ready: simulated old wording");
        if(mode is "idle" or "log-only"){await Task.Delay(8000);return 0;}
        using var listener=new TcpListener(IPAddress.Loopback,port);listener.Start();
        if(mode=="tcp-first"){Console.WriteLine("TCP bound");await Task.Delay(500);}
        using var udp=new UdpClient(new IPEndPoint(IPAddress.Loopback,port));
        await Task.Delay(8000);return 0;
    }
}
