namespace ArdUi;

sealed record FrdOffer(string Id,int Port,string Token);

static class FrdRuntime
{
    public const string Version="v1.pre8";
    public static async Task Cleanup(string name,Func<ValueTask> close,Action<string>? report=null)
    {
        try{await close();}
        catch(Exception ex)
        {
            var message=name+"："+ex.Message;Diagnostics.Log("frd-cleanup",message);
            try{report?.Invoke(message);}catch(Exception noticeError){Diagnostics.Log("frd-cleanup-notice",noticeError.Message);}
        }
    }
    public static string Resolve(string configured="")
    {
        var paths=new[]{Environment.GetEnvironmentVariable("ARDUI_FRD_PATH"),configured,
            Path.Combine(Directory.GetParent(Program.DataRoot)!.FullName,"frd",Version,"FRD.exe"),
            Path.Combine(AppContext.BaseDirectory,"frd",Version,"FRD.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Programs","FRD",Version,"FRD.exe")};
        foreach(var candidate in paths.Where(p=>!string.IsNullOrWhiteSpace(p)))
        {
            var path=Path.GetFullPath(candidate!);if(!File.Exists(path))continue;
            var folder=Path.GetDirectoryName(path)!;
            if(!File.Exists(Path.Combine(folder,"codec-config.json"))||!Directory.Exists(Path.Combine(folder,"ffmpeg")))
                throw new IOException("FRD 运行文件不完整，请重新执行 ArdUi 安装脚本修复。");
            return path;
        }
        throw new IOException($"未找到 FRD {Version}，请重新执行 ArdUi 安装脚本自动部署。");
    }
}

sealed class FrdProcess : IAsyncDisposable
{
    readonly Process process;
    readonly ChildJob job;
    readonly Task pump;
    readonly TaskCompletionSource ready=new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly string secret;
    readonly object disposeGate=new();
    Task? disposal;
    public Task Exited{get;}
    int exitCode=-1;
    public int ExitCode=>exitCode;
    public bool Live=>!Exited.IsCompleted&&disposal==null;
    public FrdProcess(string exe,string token,params string[] arguments)
    {
        secret=token;
        var info=new ProcessStartInfo(exe){WorkingDirectory=Path.GetDirectoryName(exe),UseShellExecute=false,CreateNoWindow=true,
            RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var argument in arguments)info.ArgumentList.Add(argument);
        info.ArgumentList.Add("--token");info.ArgumentList.Add(token);
        process=Process.Start(info)??throw new IOException("FRD 启动失败。");
        try{job=new ChildJob(process);}
        catch(Exception ex)
        {
            Diagnostics.Log("frd-job",ex.Message);
            try{if(!process.HasExited)process.Kill(true);}catch(Exception closeError){Diagnostics.Log("frd-job-cleanup",closeError.Message);}
            finally{process.Dispose();}
            throw;
        }
        Exited=WaitExit();pump=Task.WhenAll(Read(process.StandardOutput),Read(process.StandardError));
    }
    async Task WaitExit(){await process.WaitForExitAsync();exitCode=process.ExitCode;}
    async Task Read(StreamReader reader)
    {
        try
        {
            while(await reader.ReadLineAsync() is{} text)
            {
                Diagnostics.Log("frd",text.Replace(secret,"[redacted]",StringComparison.Ordinal));
                if(text.Contains("Remote listener ready:",StringComparison.Ordinal))ready.TrySetResult();
            }
        }
        catch(Exception ex){Diagnostics.Log("frd-output",ex.Message);}
    }
    public async Task WaitReady(CancellationToken ct)
    {
        await Task.WhenAny(ready.Task,Exited).WaitAsync(TimeSpan.FromSeconds(20),ct);
        if(Exited.IsCompleted)throw new IOException("FRD 被控端启动失败，请查看诊断日志。");
        await ready.Task.WaitAsync(ct);
    }
    public ValueTask DisposeAsync(){lock(disposeGate)return new(disposal??=Close());}
    async Task Close()
    {
        var errors=new List<Exception>();
        void Act(string name,Action action)
        {
            try{action();}
            catch(Exception ex)when(ex is InvalidOperationException or System.ComponentModel.Win32Exception&&process.HasExited)
            {Diagnostics.Log("frd-exit-race",name+": "+ex.Message);}
            catch(Exception ex){Diagnostics.Log("frd-process-close",name+": "+ex.Message);errors.Add(ex);}
        }
        async Task Wait(string name,Task task,int seconds)
        {
            try{await task.WaitAsync(TimeSpan.FromSeconds(seconds));}
            catch(Exception ex){Diagnostics.Log("frd-process-close",name+": "+ex.Message);errors.Add(ex);}
        }
        try
        {
            if(!Exited.IsCompleted)
            {
                Act("close window",()=>process.CloseMainWindow());
                if(await Task.WhenAny(Exited,Task.Delay(3000))!=Exited)Act("terminate process",()=>process.Kill(true));
            }
            Act("close child job",job.Dispose);
            await Wait("process exit",Exited,10);await Wait("output drain",pump,3);
        }
        finally{Act("close child job",job.Dispose);process.Dispose();}
        if(errors.Count>0)throw new AggregateException("FRD 进程清理遇到错误。",errors);
    }
}

sealed class FrdHosted : IAsyncDisposable
{
    readonly FrdProcess process;
    readonly FrdHostProxy proxy;
    readonly Gateway gateway;
    readonly Action<string> report;
    readonly object disposeGate=new();
    Task? disposal;
    public FrdOffer Offer{get;}
    public bool Live=>process.Live&&proxy.Live&&disposal==null;
    FrdHosted(FrdProcess process,FrdHostProxy proxy,Gateway gateway,string token,Action<string> report)
    {this.process=process;this.proxy=proxy;this.gateway=gateway;this.report=report;proxy.Failed=report;Offer=new(Guid.NewGuid().ToString("N"),proxy.Port,token);_=Monitor();}
    public static async Task<FrdHosted> Start(string exe,Gateway gateway,CancellationToken lifetime,CancellationToken ct,Action<string> report)
    {
        var token=Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();var port=Wire.Port();
        var process=new FrdProcess(exe,token,"--host","--listen","127.0.0.1","--port",port.ToString());
        FrdHostProxy? proxy=null;
        try
        {
            await process.WaitReady(ct);proxy=new(port,token,gateway.PermitFrdUdp,lifetime);gateway.PermitFrdTcp(proxy.Port,true);
            Diagnostics.Log("frd-host","FRD loopback listener ready.");return new(process,proxy,gateway,token,report);
        }
        catch
        {
            if(proxy!=null)
            {
                gateway.PermitFrdTcp(proxy.Port,false);
                await FrdRuntime.Cleanup("关闭 FRD 被控代理",proxy.DisposeAsync,report);
            }
            await FrdRuntime.Cleanup("关闭 FRD 被控进程",process.DisposeAsync,report);throw;
        }
    }
    async Task Monitor()
    {
        try
        {
            await Task.WhenAny(process.Exited,proxy.Completion);var stopping=disposal!=null;
            await DisposeAsync();
            if(!stopping&&process.ExitCode!=0)report("FRD 被控端已退出（退出码 "+process.ExitCode+"），请查看诊断日志。");
        }
        catch(Exception ex){Diagnostics.Log("frd-host-close",ex.Message);}
    }
    public ValueTask DisposeAsync(){lock(disposeGate)return new(disposal??=Close());}
    async Task Close()
    {
        gateway.PermitFrdTcp(Offer.Port,false);
        await FrdRuntime.Cleanup("关闭 FRD 被控代理",proxy.DisposeAsync,report);
        await FrdRuntime.Cleanup("关闭 FRD 被控进程",process.DisposeAsync,report);
        Diagnostics.Log("frd-host","FRD host stopped; temporary forwarding permissions removed.");
    }
}

sealed class FrdOpened : IAsyncDisposable
{
    readonly FrdProcess process;
    readonly FrdClientProxy proxy;
    readonly Session session;
    readonly FrdOffer offer;
    readonly object disposeGate=new();
    Task? disposal;
    int failed;
    public Task Completion{get;}
    public bool Live=>process.Live&&proxy.Live&&disposal==null;
    public int ExitCode{get;set;}=-1;
    FrdOpened(FrdProcess process,FrdClientProxy proxy,Session session,FrdOffer offer)
    {this.process=process;this.proxy=proxy;this.session=session;this.offer=offer;proxy.Failed=Report;Completion=Monitor();}
    void Report(string message){if(Interlocked.Exchange(ref failed,1)==0)session.ReportFrdError(message);}
    public static async Task<FrdOpened> Start(Session session,string exe,FrdOffer offer,int testSeconds,string? report)
    {
        var proxy=new FrdClientProxy(session,offer.Port,offer.Token,session.Token);
        try
        {
            var args=new List<string>{"--connect","127.0.0.1","--port",proxy.Port.ToString()};
            if(testSeconds>0){args.AddRange(["--test-seconds",testSeconds.ToString(),"--report",Path.GetFullPath(report!)]);}
            return new(new FrdProcess(exe,offer.Token,args.ToArray()),proxy,session,offer);
        }
        catch{await FrdRuntime.Cleanup("关闭 FRD 主控代理",proxy.DisposeAsync,session.ReportFrdError);throw;}
    }
    async Task Monitor()
    {
        try
        {
            await Task.WhenAny(process.Exited,proxy.Completion);var stopping=disposal!=null||session.Token.IsCancellationRequested;
            await DisposeAsync();ExitCode=process.ExitCode;
            if(!stopping&&ExitCode!=0)Report("FRD 已退出（退出码 "+ExitCode+"），请查看诊断日志。");
        }
        catch(Exception ex){Diagnostics.Log("frd-client-close",ex.Message);}
    }
    public ValueTask DisposeAsync(){lock(disposeGate)return new(disposal??=Close());}
    async Task Close()
    {
        try
        {
            await FrdRuntime.Cleanup("关闭 FRD 主控代理",proxy.DisposeAsync,Report);
            await FrdRuntime.Cleanup("关闭 FRD 主控进程",process.DisposeAsync,Report);
        }
        finally
        {
            session.CloseFrdPort(offer.Port);
            if(session.Overlay!=null&&!session.Token.IsCancellationRequested)
            {
                using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try{await session.Overlay.Request("frd-stop",new{id=offer.Id},timeout.Token);}
                catch(Exception ex){Diagnostics.Log("frd-stop",ex.Message);}
            }
        }
    }
}
