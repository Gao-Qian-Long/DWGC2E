using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;

internal static class PipelineAcceptance
{
    public static async Task Run(Assembly core, InstalledBundle bundle, string release, string source, string root, bool workerMode = false, string mode = "pipeline")
    {
        var resume = mode == "worker-resume";
        var crashBeforeWrite = mode == "worker-crash-before-write";
        if (resume && !File.Exists(Path.Combine(root,"interrupted.json"))) throw new InvalidOperationException("Resume requires this probe interruption evidence");
        if (!resume && Directory.Exists(root)) throw new InvalidOperationException("Pipeline evidence directory must be new");
        var install = Environment.GetEnvironmentVariable("CAD_PROBE_INSTALL") ?? throw new InvalidOperationException("Set CAD_PROBE_INSTALL to the isolated test host installation");
        if (!File.Exists(Path.Combine(install,"gcad.exe"))) throw new InvalidOperationException("GstarCAD installation not found");
        if (System.Diagnostics.Process.GetProcessesByName("gcad").Length != 0 || System.Diagnostics.Process.GetProcessesByName("acad").Length != 0)
            throw new InvalidOperationException("Close CAD first; this probe must not attach to a user session");
        Directory.CreateDirectory(root);
        string Hash(string p) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)));
        var sourceHash = Hash(source);
        if (resume) {
            var interrupted = JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"interrupted.json"))).RootElement;
            if (interrupted.GetProperty("ExecutableHash").GetString() != bundle.ExecutableHash || interrupted.GetProperty("SourceHash").GetString() != sourceHash)
                throw new InvalidOperationException("Installed executable/source changed after interruption");
            if (File.Exists(Path.Combine(root,"verification.json"))) throw new InvalidOperationException("Resume already accepted; do not repeat CAD writeback");
        }
        Type T(string name) => core.GetType(name, true)!;
        object New(string name, params object?[] values) => Activator.CreateInstance(T(name),values)!;
        object? Get(object o,string name) => o.GetType().GetProperty(name)!.GetValue(o);
        void Set(object o,string name,object value) => o.GetType().GetProperty(name)!.SetValue(o,value);
        void Check(bool ok,string message) { if (!ok) throw new InvalidOperationException(message); }
        void Save(string file,object data) => File.WriteAllText(Path.Combine(root,file),JsonSerializer.Serialize(data,new JsonSerializerOptions { WriteIndented=true }));
        object Read(object reader,string path) => reader.GetType().GetMethod("ExtractFromFile")!.Invoke(reader,new object[]{path})!;
        var appBytes = bundle.ReadAssembly("QLCAD.dll") ?? throw new InvalidDataException("APP assembly missing");
        var app = AssemblyLoadContext.Default.LoadFromStream(new MemoryStream(appBytes));
        var interop = Activator.CreateInstance(app.GetType("DwgTranslator.App.Services.AutoCadInteropService",true)!)!;
        int providerCalls=0,interopCalls=0,offlineWrites=0;
        var client = DispatchProxy.Create(T("DwgTranslator.Core.Services.IDeepSeekClient"),typeof(ProbeProxy));
        ((ProbeProxy)client).Handler=(method,values)=> {
            Check(method.Name=="ChatCompletionAsync","Unexpected provider method");
            Interlocked.Increment(ref providerCalls);
            ((CancellationToken)values![2]!).ThrowIfCancellationRequested();
            return Task.FromResult("Valve feedback");
        };
        PipelineReplyHandler? transportForCrash = null;
        var tracedInterop=DispatchProxy.Create(T("DwgTranslator.Core.Services.IAutoCadInteropService"),typeof(ProbeProxy));
        ((ProbeProxy)tracedInterop).Handler=(method,values)=> {
            if(method.Name=="WritebackViaAutoCadAsync") {
                if (crashBeforeWrite) {
                    Check(transportForCrash != null && transportForCrash.Calls == 1,"Expected completed Worker batch before interruption");
                    var persisted = File.ReadAllText(Path.Combine(root,"tasks.json"));
                    using var document = JsonDocument.Parse(persisted);
                    var record = document.RootElement[0];
                    var persistedPairs=record.GetProperty("SuccessfulTranslations").GetArrayLength();
                    File.WriteAllText(Path.Combine(root,"tasks-at-interruption.json"),persisted);
                    Save("interrupted.json",new { ExecutableHash=bundle.ExecutableHash,SourceHash=sourceHash,TaskId=record.GetProperty("Id").GetString(),HttpCalls=transportForCrash!.Calls,PersistedPairs=persistedPairs,InteropCalls=interopCalls,Phase="Immediately before CAD dispatch",ExitCode=86 });
                    Console.WriteLine("INTERRUPTED: own test process exits before CAD; actual persisted checkpoint captured.");
                    Environment.Exit(86);
                }
                Interlocked.Increment(ref interopCalls);
            }
            return method.Invoke(interop,values);
        };
        // A silent offline fallback must fail acceptance, not look like a real-CAD pass.
        var forbiddenWriter=DispatchProxy.Create(T("DwgTranslator.Core.Services.IDwgWriterService"),typeof(ProbeProxy));
        ((ProbeProxy)forbiddenWriter).Handler=(_,_)=> { Interlocked.Increment(ref offlineWrites); throw new InvalidOperationException("Unexpected offline writer fallback"); };
        var reader=New("DwgTranslator.Core.Services.DwgReaderService");
        var config=New("DwgTranslator.Core.Models.AppConfig");
        foreach(var pair in new Dictionary<string,object> {
            ["AutoCadInstallPath"]=install,["CadPluginPath"]=Path.Combine(release,"CadPlugin","DwgTranslator.Cad.dll"),
            ["ExportDirectory"]=root,["OutputNamingPattern"]="{name}_pipeline",["DuplicatePolicy"]="rename",
            ["BackupSourceBeforeWrite"]=false,["OpenOutputFolderAfterExport"]=false,
            ["SourceLanguage"]="ZH",["TargetLanguage"]="EN",["GlossaryFirst"]=false,["MaxRetryCount"]=0
        }) Set(config,pair.Key,pair.Value);
        var options=New("DwgTranslator.Core.Tasks.TaskManagerOptions");
        Set(options,"LocalWorkerCount",1);Set(options,"AiConcurrency",1);Set(options,"MaxRetryCount",0);
        var storePath=Path.Combine(root,"tasks.json");
        var store=New("DwgTranslator.Core.Tasks.JsonTaskStore",storePath);
        var glossary=New("DwgTranslator.Core.Services.GlossaryService");
        var parser=New("DwgTranslator.Core.Translation.FormatCodeParser");
        var consistency=New("DwgTranslator.Core.Translation.TranslationConsistencyService","");
        using var transport = new PipelineReplyHandler();
        transportForCrash = transport;
        using var http = new HttpClient(transport);
        object manager;
        if (workerMode)
        {
            var api = New("DwgTranslator.Core.Api.WorkerApiClient",http,"https://pipeline.invalid",
                new Func<string?>(()=>"isolated-probe-session"),"probe-device","probe-host");
            var formatter = New("DwgTranslator.Core.Services.TranslationService",glossary,parser,client,
                "Unused local model prompt",50,0,consistency,1);
            Func<string,string,string> restore=(text,raw)=>(string)formatter.GetType()
                .GetMethod("RestoreFormatCodes")!.Invoke(formatter,new object[]{text,raw})!;
            var adapter = New("DwgTranslator.Core.Services.WorkerTranslationService",api,config,restore,null);
            manager=New("DwgTranslator.Core.Tasks.TaskManager",reader,reader,adapter,
                forbiddenWriter,null,store,options,config,tracedInterop);
        }
        else manager=New("DwgTranslator.Core.Tasks.TaskManager",reader,reader,glossary,parser,client,forbiddenWriter,null,store,options,config,"Translate the supplied label to English.",tracedInterop,consistency);
        var stages=new List<string>();var messages=new List<string>();
        var changed=manager.GetType().GetEvent("TaskUpdated")!;
        var invoke=changed.EventHandlerType!.GetMethod("Invoke")!;
        var ps=invoke.GetParameters().Select(p=>Expression.Parameter(p.ParameterType,p.Name)).ToArray();
        Action<object> onTask=o=>{lock(stages)stages.Add(Get(o,"Status")!.ToString()!);};
        var handler=Expression.Lambda(changed.EventHandlerType,Expression.Invoke(Expression.Constant(onTask),Expression.Convert(ps[1],typeof(object))),ps).Compile();
        changed.AddEventHandler(manager,handler);
        manager.GetType().GetEvent("ProgressMessage")!.AddEventHandler(manager,new EventHandler<string>((_,message)=> {lock(messages)messages.Add(message);}));
        object? task=null;
        try {
            manager.GetType().GetMethod("ConfigureRun")!.Invoke(manager,new object?[]{"ZH","EN",root,Enum.Parse(T("DwgTranslator.Core.Tasks.TaskWritebackMode"),"AutoCad")});
            if (resume) {
                var restored=((IEnumerable)Get(manager,"Tasks")!).Cast<object>().ToArray();
                Check(restored.Length==1 && Get(restored[0],"Status")!.ToString()=="Pending","Interrupted task not recovered as Pending");
                task=restored[0];
                var interrupted=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"interrupted.json"))).RootElement;
                Check((string?)Get(task,"Id")==interrupted.GetProperty("TaskId").GetString(),"Recovery replaced the task ID");
                Save("task-before-resume.json",task);
            } else task=manager.GetType().GetMethod("Enqueue")!.Invoke(manager,new object?[]{source,Enum.Parse(T("DwgTranslator.Core.Tasks.TaskPriority"),"Normal")})!;
            using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(3));
            await (Task)manager.GetType().GetMethod("RunAsync")!.Invoke(manager,new object[]{timeout.Token})!;
            Save("task-final.json",task);
            Check(Get(task,"Status")!.ToString()=="Completed","Task did not complete: "+Get(task,"Error"));
            Check((int)Get(task,"TextCount")! == 6 && (int)Get(task,"TranslatedCount")! == 6 && (int)Get(task,"FailedCount")! == 0,"Task counters differ");
            Check(workerMode ? providerCalls==0 && transport.Calls==(resume ? 0 : 1) : providerCalls==1,"Unexpected translation dispatch count");
            Check(interopCalls==1 && offlineWrites==0,"Real CAD interop path not proven");
            var output=(string)Get(task,"OutputPath")!;
            var extracted=Read(reader,output);Save("output-extraction.json",extracted);
            var rows=((IEnumerable)extracted).Cast<object>().ToArray();
            Check(rows.Length==6 && rows.All(e=>Get(e,"PlainText")!.ToString()=="Valve feedback"),"Final APP-reader content differs");
            Check(Hash(source)==sourceHash,"Source file changed");
            Check(!Directory.EnumerateFiles(root,"*.dwgc2e.pending").Any(),"Unresolved CAD lease remains");
            ((IDisposable)manager).Dispose();
            var recovered=New("DwgTranslator.Core.Tasks.JsonTaskStore",storePath);
            var loaded=((IEnumerable)recovered.GetType().GetMethod("Load")!.Invoke(recovered,null)!).Cast<object>().ToArray();
            Check(loaded.Length==1 && Get(loaded[0],"Status")!.ToString()=="Completed" && (string?)Get(loaded[0],"OutputPath")==output,"Persisted terminal state differs");
            Save("verification.json",new {Passed=true,WorkerMode=workerMode,ResumedAfterProcessExit=resume,HttpCalls=transport.Calls,ProviderCalls=providerCalls,InteropCalls=interopCalls,OfflineWrites=offlineWrites,Entities=6,SourceHash=sourceHash,OutputHash=Hash(output),ExecutableHash=bundle.ExecutableHash,TaskRestored=true,Scope="Installed TaskManager/reader/APP CAD interop/JsonTaskStore; selected translation adapter with deterministic reply, not GUI or production cloud"});
            Console.WriteLine("PASS: installed APP task pipeline, real CAD dispatch, six exact output labels, verified translation dispatch and persisted completion.");
        } finally {
            Save("dispatch-counters.json",new {WorkerMode=workerMode,HttpCalls=transport.Calls,ProviderCalls=providerCalls,InteropCalls=interopCalls,OfflineWrites=offlineWrites,ExecutableHash=bundle.ExecutableHash});
            lock(stages)Save("stages.json",stages);
            lock(messages)Save("progress.json",messages);
            if(task!=null)Save("task-final.json",task);
            ((IDisposable)manager).Dispose();((IDisposable)interop).Dispose();
        }
    }
}
public class ProbeProxy : DispatchProxy
{
    public Func<MethodInfo,object?[]?,object?> Handler {get;set;} = null!;
    protected override object? Invoke(MethodInfo? targetMethod,object?[]? args) => Handler(targetMethod!,args);
}

// Handles every request in memory; this HttpClient has no network transport.
internal sealed class PipelineReplyHandler : HttpMessageHandler
{
    public int Calls { get; private set; }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.Method != HttpMethod.Post || request.RequestUri?.Host != "pipeline.invalid"
            || request.RequestUri.AbsolutePath != "/v1/translate"
            || request.Headers.Authorization?.Parameter != "isolated-probe-session"
            || !request.Headers.Contains("Idempotency-Key"))
            throw new InvalidOperationException("Unexpected Worker pipeline request");
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
        if (body.RootElement.GetProperty("source_lang").GetString() != "ZH"
            || body.RootElement.GetProperty("target_lang").GetString() != "EN")
            throw new InvalidOperationException("Unexpected translation direction");
        var rows = body.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => new { id=item.GetProperty("id").GetInt32(),translated_text="Valve feedback" }).ToArray();
        if (rows.Length != 6 || rows.Select(r=>r.id).Distinct().Count()!=6)
            throw new InvalidOperationException("Expected six uniquely identified input items");
        Calls++;
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK) {
            Content=new StringContent(JsonSerializer.Serialize(new {success=true,items=rows}),
                System.Text.Encoding.UTF8,"application/json")
        };
    }
}
