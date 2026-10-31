using System.ComponentModel;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;

if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows job fixture required.");
if (args[0] == "conductor-start-caller") return ConductorStartCallerProbe.Run(args[1], args[2]);
if (args[0] == "fake-conductor") return FakeConductorProbe.Run(args[1], args[2], args[3]);
if (args[0] == "acceptance-claim") return AcceptanceCustodyProbe.Run(args[1], args[2]);
var root = args[1];
var token = args[2];
if (args[0] == "supervisor") return await RealSupervisorProbe.RunAsync(root, token,
    int.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture),
    TimeSpan.FromSeconds(int.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture)));
using var ready = EventWaitHandle.OpenExisting(token + "-ready");
using var boundary = EventWaitHandle.OpenExisting(token + "-boundary");
using var survived = EventWaitHandle.OpenExisting(token + "-survived");
using var stop = EventWaitHandle.OpenExisting(token + "-stop");
if (args[0] == "child")
{
    ready.Set();
    if (WaitHandle.WaitAny([boundary, stop], TimeSpan.FromMinutes(2)) == 0)
    {
        survived.Set();
        if (!stop.WaitOne(TimeSpan.FromMinutes(2))) throw new TimeoutException("Fixture stop signal absent.");
    }
    return 0;
}
using var resultReady = EventWaitHandle.OpenExisting(token + "-result");
var nativeError = 0;
var processId = 0;
DateTimeOffset? startedAt = null;
string? launchDetail = null;
string? error = null;
try
{
    var command = new[] { Environment.ProcessPath!, typeof(Program).Assembly.Location, "child", root, token };
    var request = new ConductLoopLaunchRequest("authority-probe", [],
        Path.Combine(root, "child.out.log"), Path.Combine(root, "child.err.log"), root, 0);
    var launched = ConductorLoopHandoff.LaunchDetachedWindows(request, command);
    processId = launched.ProcessId;
    startedAt = launched.StartedAt;
    launchDetail = launched.LaunchDetail;
    if (!ready.WaitOne(TimeSpan.FromMinutes(1))) throw new TimeoutException("Native successor readiness absent.");
}
catch (Win32Exception ex)
{
    nativeError = ex.NativeErrorCode;
    error = ex.ToString();
}
catch (Exception ex)
{
    nativeError = -1;
    error = ex.ToString();
}
var receipt = new { nativeError, ProcessId = processId, StartedAt = startedAt, LaunchDetail = launchDetail, error };
File.WriteAllText(Path.Combine(root, "result.json"), JsonSerializer.Serialize(receipt));
resultReady.Set();
if (!stop.WaitOne(TimeSpan.FromMinutes(2))) throw new TimeoutException("Fixture predecessor stop signal absent.");
return 0;
