using SekaiToolsEngine.Ipc;
using SekaiToolsEngine.Handlers;

var transport = new IpcTransport(Console.OpenStandardInput(), Console.OpenStandardOutput());
var dispatcher = new Dispatcher();

var subtitleHandler = new SubtitleHandler(transport);
var suppressHandler = new SuppressHandler(transport);
var downloadHandler = new DownloadHandler(transport);
var translateHandler = new TranslateHandler(transport);
var settingsHandler = new SettingsHandler(transport);

subtitleHandler.Register(dispatcher);
suppressHandler.Register(dispatcher);
downloadHandler.Register(dispatcher);
translateHandler.Register(dispatcher);
settingsHandler.Register(dispatcher);

dispatcher.Register("system.ping", _ => Task.FromResult<object?>(new { ok = true }));
dispatcher.Register("system.version", _ => Task.FromResult<object?>(new
{
    name = "SekaiCoreEngine",
    // ToString(3)：对外展示三位语义化版本（1.1.0），AssemblyVersion 固有的第四位(Revision)不外露
    version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0",
    protocol = 1,
}));

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

while (!cts.Token.IsCancellationRequested)
{
    var request = await transport.ReadRequestAsync(cts.Token);
    if (request == null) break;

    var (result, error) = await dispatcher.DispatchAsync(request.Method, request.Params);
    transport.SendResponse(request.Id, result, error);
}
