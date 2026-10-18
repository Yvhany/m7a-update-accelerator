// 入口：把参数解析后交给 MirrorProxy.Start，编译成控制台 exe 由计划任务隐藏运行
using System;
using System.IO;

public static class Program
{
    public static int Main(string[] args)
    {
        int port = 8899;
        string pfx = "github.com.pfx";
        string password = "m7a-mirror";
        string mirror = "https://gh-proxy.com";
        string log = "mirror_proxy.log";

        for (int i = 0; i + 1 < args.Length; i += 2)
        {
            string key = args[i];
            string value = args[i + 1];
            if (key == "--port") int.TryParse(value, out port);
            else if (key == "--pfx") pfx = value;
            else if (key == "--password") password = value;
            else if (key == "--mirror") mirror = value;
            else if (key == "--log") log = value;
        }

        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        if (!Path.IsPathRooted(pfx)) pfx = Path.Combine(baseDir, pfx);
        if (!Path.IsPathRooted(log)) log = Path.Combine(baseDir, log);

        MirrorProxy.Start(port, pfx, password, mirror, log);
        return 0;
    }
}
