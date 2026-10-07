using System.Diagnostics;
using System.Text.Json;
using ProjectEcho.Core.Transcription;

internal static class M4QwenAdapterTests
{
    internal static void Register(Action<string, Action> run, string output)
    {
        string root = Path.Combine(output, "m4-qwen-adapter"); Directory.CreateDirectory(root);
        run("P4-04-SYNTH Qwen missing models do not change default Whisper", () => Missing(root));
        run("P4-02/04-WINDOWS SYNTHETIC CLI Qwen exact aligned originals and immutable review evidence", () => Success(root));
        run("P4-01/02-WINDOWS SYNTHETIC CLI Qwen rejects fallback truncated missing and repaired timestamps", () => Reject(root));
        run("P4-04-WINDOWS SYNTHETIC CLI Qwen owned cancellation and independent retry", () => Cancel(root));
        run("P4-03/04-SYNTH Qwen zero audio and hash mismatch skip inference", () => Silence(root));
        run("P4-04-SYNTH Qwen per-meeting settings preserve other backends and keep prompt out of defaults", () => Settings(root));
        run("P4-04-WINDOWS SYNTHETIC CLI log creation failure promptly kills owned inference", () => LogFailure(root));
        run("P4-04-WINDOWS SYNTHETIC CLI Qwen canonical paths and locked window PCM bind inference", () => RelativeAndLocked(root));
        run("P4-PERF-WINDOWS SYNTHETIC bounded alignment batch preserves all 17 windows", () => Batch(root));
        run("P4-PERF-WINDOWS SYNTHETIC alignment rejects missing duplicated and fallback device evidence", () => RejectBatch(root));
        run("P4-PERF-WINDOWS SYNTHETIC cancellation during alignment batch retains originals and permits retry", () => CancelBatch(root));
        run("P4-PERF-WINDOWS SYNTHETIC legacy CPU alignment stays compatible without silent GPU fallback", () => Legacy(root));
        run("P4-PERF-WINDOWS SYNTHETIC official CPU bridge binds complete model files and raw output", () => Official(root));
    }

    internal static LocalTranscriptionRequest Request(string root, string name, string mode = "ok", bool silence = false, int seconds = 1)
    {
        string meeting = M3TranscriptTests.Meeting(root, name), wav = Path.Combine(meeting, "input.wav");
        using (var output = new BinaryWriter(File.Create(wav)))
        {
            output.Write("RIFF"u8); output.Write(32000 * seconds + 36); output.Write("WAVEfmt "u8); output.Write(16);
            output.Write((short)1); output.Write((short)1); output.Write(16000); output.Write(32000);
            output.Write((short)2); output.Write((short)16); output.Write("data"u8); output.Write(32000 * seconds);
            for (int frame = 0; frame < 16000 * seconds; frame++) output.Write((short)(silence ? 0 : frame % 127));
        }
        string model = M3TranscriptTests.Input(meeting, "synthetic-qwen.gguf", mode);
        string executable = Path.Combine(AppContext.BaseDirectory, "ProjectEcho.Tests.exe");
        var configuration = new LocalTranscriptionConfiguration("qwen3-asr", executable, model, ModelName: "synthetic-cli-not-real-model",
            ModelLicenseAcknowledged: true, Hardware: LocalTranscriptionHardware.Vulkan,
            QwenAlignment: new(executable, model));
        return new(meeting, name, 1000 * seconds, wav, M3TranscriptTests.Hash(wav), configuration, true,
            new string('a', 64), "pause-excluded-v1");
    }
    private static LocalTranscriptionResult Run(LocalTranscriptionRequest request) => new QwenTranscriber().RunAsync(request).GetAwaiter().GetResult();
    private static void Missing(string root)
    {
        Need(LocalTranscriptionBackends.Default is WhisperCppLocalTranscriber, "Default Whisper was replaced.");
        var request = Request(root, "missing");
        Need(!new QwenTranscriber().Inspect(request.Configuration with { QwenAlignment = null }).Ready, "Missing aligner accepted.");
        Need(Run(request with { Configuration = request.Configuration with { ModelPath = "" } }).Status == LocalTranscriptionStatus.NotConfigured,
            "Missing model not explicit.");
        Need(!Directory.Exists(Path.Combine(request.MeetingDirectory,"transcripts")), "Missing model generated an attempt.");
    }
    private static void Success(string root)
    {
        var request = Request(root, "success"); var result = Run(request);
        Need(result.Status == LocalTranscriptionStatus.Succeeded, result.Message);
        var revision = result.Revision!;
        Need(revision.Segments.Count == 2 && revision.Segments[0].Text == "第一句。" && revision.Segments[1].Text == "末句。" &&
            revision.Segments[0].StartMs == 100 && revision.Segments[1].StartMs == 600 && revision.Segments[1].EndMs == 900,
            "Actual fixture sample boundaries not mapped separately.");
        Need(revision.Alignment is { TimelinePolicy: "pause-excluded-v1", FrameCount:16000 } && revision.Alignment.InputWaveSha256 == request.ExpectedWaveSha256,
            "Input/policy evidence missing.");
        Need(TranscriptService.Load(request.MeetingDirectory, revision.RevisionId).Alignment == revision.Alignment, "Evidence changed on reload.");
        var edited = TranscriptService.CreateRevision(request.MeetingDirectory, revision.RevisionId,
            revision.Segments.Select((s,i)=>i==0?s with {Text="人工修订，时间仍需复核。"}:s).ToArray());
        var retry = Run(request);
        Need(retry.Revision!.RevisionId != revision.RevisionId && TranscriptService.Load(request.MeetingDirectory,edited.RevisionId).Segments[0].Text.StartsWith("人工修订"),
            "Retry overwrote original/edit.");
        File.AppendAllText(Path.Combine(request.MeetingDirectory, revision.Alignment!.EvidenceFile), " ");
        Bad(()=>TranscriptService.Load(request.MeetingDirectory,revision.RevisionId));
        Need(M3TranscriptTests.Hash(request.WavePath) == request.ExpectedWaveSha256, "Input changed.");
    }
    private static void Reject(string root)
    {
        foreach(string mode in new[]{"fallback","truncated","missing","duplicate","repair","wrong-text","wrong-family"})
        {
            var request=Request(root,mode,mode); var result=Run(request);
            Need(result.Status==LocalTranscriptionStatus.Failed && result.Revision is null && TranscriptService.ListRevisions(request.MeetingDirectory).Count==0,
                mode+" incorrectly accepted: "+result.Message);
            Need(File.Exists(Path.Combine(result.AttemptDirectory,"attempt.json")),"Failure evidence missing.");
        }
        // M5D follow-up preserves fully verified model text when only acoustic times fail.
        // Strict Map is tested separately and still rejects these raw timestamps.
        var reversedRequest=Request(root,"reverse","reverse"); var reversed=Run(reversedRequest);
        Need(reversed.Status==LocalTranscriptionStatus.Succeeded && reversed.Revision?.Segments.Single() is
            {StartMs:null,EndMs:null,TimestampQuality:"untimed",Text:"第一句。末句。"},
            "Verified text with reversed acoustic times was lost or given invented timing: "+reversed.Message);
        Need(M3TranscriptTests.Hash(reversedRequest.WavePath)==reversedRequest.ExpectedWaveSha256,"Time fallback changed audio.");
    }
    private static void Cancel(string root)
    {
        var request=Request(root,"cancel","wait"); using var cancellation=new CancellationTokenSource();
        var watch=Stopwatch.StartNew(); var task=new QwenTranscriber().RunAsync(request,cancellation.Token);
        while(!task.IsCompleted && watch.Elapsed<TimeSpan.FromSeconds(10) &&
            !Directory.EnumerateFiles(request.MeetingDirectory,"qwen-fixture-ready.txt",SearchOption.AllDirectories).Any()) Thread.Sleep(20);
        cancellation.Cancel(); var result=task.GetAwaiter().GetResult();
        Need(result.Status==LocalTranscriptionStatus.Cancelled && result.Revision is null && watch.Elapsed<TimeSpan.FromSeconds(15),"Owned cancellation failed.");
        File.WriteAllText(request.Configuration.ModelPath,"ok"); var retry=Run(request);
        Need(retry.Status==LocalTranscriptionStatus.Succeeded && retry.AttemptDirectory!=result.AttemptDirectory,"Retry reused cancelled attempt.");
    }
    private static void Silence(string root)
    {
        var request=Request(root,"silence",silence:true); var result=Run(request);
        Need(result.Status==LocalTranscriptionStatus.NoSpeech && !File.Exists(Path.Combine(result.AttemptDirectory,"asr.stdout.txt")),"Zero PCM invoked ASR.");
        var bad=Run(request with{ExpectedWaveSha256=new string('f',64)});
        Need(bad.Status==LocalTranscriptionStatus.Failed && bad.Revision is null,"Hash mismatch accepted.");
    }
    private static void Settings(string root)
    {
        var request=Request(root,"settings"); string defaults=Path.Combine(root,"defaults");Directory.CreateDirectory(defaults);
        var saved=new QwenMeetingSettings(1,"qwen3-asr",request.Configuration with{InitialPrompt="本场独立术语"});
        QwenSettingsStore.SaveMeeting(request.MeetingDirectory,saved);QwenSettingsStore.SaveDefaults(defaults,saved);
        Need(QwenSettingsStore.LoadMeeting(request.MeetingDirectory).Configuration?.InitialPrompt=="本场独立术语" &&
            QwenSettingsStore.LoadDefaults(defaults).Configuration?.InitialPrompt is null,"Prompt leaked to defaults or disappeared.");
        string other=M3TranscriptTests.Meeting(root,"other");
        Need(QwenSettingsStore.LoadMeeting(other).BackendId=="whisper.cpp","Unvisited legacy meeting switched automatically.");
        QwenSettingsStore.SnapshotDefaults(other,QwenSettingsStore.LoadDefaults(defaults));
        Need(QwenSettingsStore.LoadMeeting(other).BackendId=="qwen3-asr","New meeting default not snapshotted.");
        QwenSettingsStore.SnapshotDefaults(other,new());
        Need(QwenSettingsStore.LoadMeeting(other).BackendId=="qwen3-asr","Snapshot overwritten.");
    }

    private static void LogFailure(string root)
    {
        var request = Request(root, "log-failure", "wait"); using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        var progress = new InlineProgress(value =>
        {
            if (value.Stage != LocalTranscriptionStage.LoadingModel) return;
            string attempt = Directory.EnumerateDirectories(Path.Combine(request.MeetingDirectory,"transcripts","asr")).Single();
            File.WriteAllText(Path.Combine(attempt,"asr.stdout.txt"),"preexisting log must survive");
        });
        var watch = Stopwatch.StartNew();
        var result = new QwenTranscriber().RunAsync(request,deadline.Token,progress).GetAwaiter().GetResult();
        Need(result.Status == LocalTranscriptionStatus.Failed && result.Revision is null && watch.Elapsed < TimeSpan.FromSeconds(4),
            "Log creation failure waited for inference/deadline instead of killing the owned process.");
        Need(File.ReadAllText(Path.Combine(result.AttemptDirectory,"asr.stdout.txt"))=="preexisting log must survive","Existing log overwritten.");
        foreach (string pidPath in Directory.EnumerateFiles(result.AttemptDirectory,"qwen-fixture-pid.txt"))
        {
            try { using var process=Process.GetProcessById(int.Parse(File.ReadAllText(pidPath)));Need(process.HasExited,"Owned wait process survived log failure."); }
            catch (ArgumentException) { }
        }
    }

    private static void RelativeAndLocked(string root)
    {
        var request = Request(root,"relative-locked","request-lock"); bool blocked = false;
        string relative = Path.GetRelativePath(Environment.CurrentDirectory,request.Configuration.ModelPath);
        request=request with { Configuration=request.Configuration with{ModelPath=relative,QwenAlignment=request.Configuration.QwenAlignment! with{ModelPath=relative}}};
        var progress=new InlineProgress(value=>
        {
            if(value.Stage!=LocalTranscriptionStage.LoadingModel)return;
            string window=Directory.EnumerateFiles(Path.Combine(request.MeetingDirectory,"transcripts","asr"),"window-*.wav",SearchOption.AllDirectories).Single();
            try {using var attemptedWrite=new FileStream(window,FileMode.Open,FileAccess.Write,FileShare.ReadWrite);}
            catch(IOException){blocked=true;}
        });
        var result=new QwenTranscriber().RunAsync(request,default,progress).GetAwaiter().GetResult();
        Need(blocked && result.Status==LocalTranscriptionStatus.Succeeded,"Relative model resolution failed or inference window was writable: "+result.Message);
        Need(File.ReadAllText(Path.Combine(result.AttemptDirectory,"qwen-request-lock.txt"))=="blocked", "Alignment request manifest was writable by the worker.");
    }

    private static void Batch(string root)
    {
        var request = Request(root, "bounded-batch", seconds: 481);
        request = request with { Configuration = request.Configuration with { QwenAlignment = request.Configuration.QwenAlignment! with
            { Hardware = LocalTranscriptionHardware.Vulkan, GpuDeviceIndex = 2 } } };
        var result = Run(request);
        Need(result.Status == LocalTranscriptionStatus.Succeeded && result.Revision?.Segments.Count == 34, result.Message);
        var batches = Directory.GetFiles(result.AttemptDirectory, "align-batch-*.requests.json").Order().ToArray();
        Need(batches.Length == 2, "Meeting did not use bounded model sessions.");
        using var first = JsonDocument.Parse(File.ReadAllText(batches[0])); using var last = JsonDocument.Parse(File.ReadAllText(batches[1]));
        Need(first.RootElement.GetArrayLength() == 16 && last.RootElement.GetArrayLength() == 1, "Batch limits or final window incorrect.");
        using var attempt = JsonDocument.Parse(File.ReadAllText(Path.Combine(result.AttemptDirectory,"attempt.json")));
        Need(attempt.RootElement.GetProperty("alignmentHardware").GetString() == "Vulkan2" &&
            attempt.RootElement.GetProperty("alignmentDevice").GetString() == "Synthetic fixture device" &&
            attempt.RootElement.GetProperty("processMetrics").GetArrayLength() == 3, "Actual device/process evidence lost.");
        var late = Run(Request(root, "late-batch-failure", "late-fail", seconds: 481));
        Need(late.Status == LocalTranscriptionStatus.Failed && late.Revision is null &&
            Directory.GetFiles(late.AttemptDirectory,"window-*.words.json").Length == 17, "Late bad alignment published a partial transcript or discarded evidence.");
    }

    private static void RejectBatch(string root)
    {
        foreach (string mode in new[] { "no-complete", "duplicate-complete", "unknown-complete", "align-fallback", "no-device", "two-devices", "no-manifest", "bad-manifest" })
        {
            var request = Request(root, mode, mode);
            request = request with { Configuration = request.Configuration with { QwenAlignment = request.Configuration.QwenAlignment! with { Hardware = LocalTranscriptionHardware.Vulkan } } };
            var result = Run(request);
            Need(result.Status == LocalTranscriptionStatus.Failed && result.Revision is null, mode + " accepted: " + result.Message);
        }
    }

    private static void CancelBatch(string root)
    {
        var request = Request(root,"cancel-batch","align-wait",seconds:61);
        using var cancellation = new CancellationTokenSource(); var watch = Stopwatch.StartNew();
        var task = new QwenTranscriber().RunAsync(request,cancellation.Token);
        while (!task.IsCompleted && watch.Elapsed < TimeSpan.FromSeconds(10) &&
            !Directory.GetFiles(request.MeetingDirectory,"qwen-align-ready.txt",SearchOption.AllDirectories).Any()) Thread.Sleep(20);
        cancellation.Cancel(); var result = task.GetAwaiter().GetResult();
        Need(result.Status == LocalTranscriptionStatus.Cancelled && result.Revision is null && watch.Elapsed < TimeSpan.FromSeconds(15),"Batch process survived cancellation.");
        Need(M3TranscriptTests.Hash(request.WavePath) == request.ExpectedWaveSha256, "Batch cancellation changed audio.");
        File.WriteAllText(request.Configuration.ModelPath,"ok"); var retry = Run(request);
        Need(retry.Status == LocalTranscriptionStatus.Succeeded && retry.AttemptDirectory != result.AttemptDirectory,"Batch retry reused cancelled output.");
    }

    private static void Legacy(string root)
    {
        var request = Request(root,"legacy","legacy"); var result = Run(request);
        Need(result.Status == LocalTranscriptionStatus.Succeeded && File.Exists(Path.Combine(result.AttemptDirectory,"align-000000.stdout.txt")), "Legacy CPU broke: " + result.Message);
        var failed = Run(request with { Configuration = request.Configuration with { QwenAlignment = request.Configuration.QwenAlignment! with { Hardware = LocalTranscriptionHardware.Vulkan } } });
        Need(failed.Status == LocalTranscriptionStatus.Failed && failed.Revision is null,"Legacy CPU was labeled Vulkan.");
    }

    private static void Official(string root)
    {
        var request = Request(root,"official-synthetic");
        string model=Path.Combine(request.MeetingDirectory,"official-model"), bridge=Path.Combine(request.MeetingDirectory,"bridge.py");
        Directory.CreateDirectory(model);File.WriteAllText(bridge,"Synthetic fixture only; not executed as Python.");
        foreach(string name in new[]{"model.safetensors","config.json","preprocessor_config.json","tokenizer_config.json","vocab.json","merges.txt"})
            File.WriteAllText(Path.Combine(model,name),"synthetic");
        File.WriteAllText(Path.Combine(model,"synthetic-behavior.txt"),"ok");
        request=request with { Configuration=request.Configuration with { QwenAlignment=new("","",Engine:QwenAlignmentEngine.OfficialPython,
            Python:new(request.Configuration.ExecutablePath,model,bridge)) } };
        bool modelLocked=false,bridgeLocked=false;
        var progress=new InlineProgress(value=>
        {
            if(value.Stage!=LocalTranscriptionStage.LoadingModel)return;
            try{using var write=File.OpenWrite(Path.Combine(model,"model.safetensors"));}catch(IOException){modelLocked=true;}
            try{using var write=File.OpenWrite(bridge);}catch(IOException){bridgeLocked=true;}
        });
        var result=new QwenTranscriber().RunAsync(request,default,progress).GetAwaiter().GetResult();
        Need(result.Status==LocalTranscriptionStatus.Succeeded && modelLocked && bridgeLocked,result.Message);
        using var attempt=JsonDocument.Parse(File.ReadAllText(Path.Combine(result.AttemptDirectory,"attempt.json")));
        Need(attempt.RootElement.GetProperty("alignmentEngine").GetString()=="OfficialPython" &&
            attempt.RootElement.GetProperty("alignmentModelFiles").EnumerateObject().Count()==7 &&
            attempt.RootElement.GetProperty("hashes").TryGetProperty("alignerBridge",out _),"Python model/script provenance was missing.");
        Need(File.ReadAllText(Path.Combine(result.AttemptDirectory,"align-batch-000000.arguments.json")).Contains("-I"),"Python was not started in isolated mode.");
        File.Delete(Path.Combine(model,"vocab.json"));
        Need(!new QwenTranscriber().Inspect(request.Configuration).Ready,"Incomplete official model directory accepted.");
        Need(!new QwenTranscriber().Inspect(request.Configuration with{QwenAlignment=request.Configuration.QwenAlignment! with{Hardware=LocalTranscriptionHardware.Vulkan}}).Ready,
            "Official CPU was allowed to claim Vulkan.");
    }

    internal static int FixtureVersion()
    {
        string model = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "..", "synthetic-qwen.gguf"));
        Console.WriteLine(File.Exists(model) && File.ReadAllText(model) == "legacy" ? "synthetic legacy raw-v1" : "projectecho-aligner-protocol: batch-v2");
        return 0;
    }

    internal static async Task<int> Fixture(string[] args)
    {
        string Get(string name)=>args[Array.IndexOf(args,name)+1];
        string model=Get("--model");
        string behavior=File.ReadAllText(Directory.Exists(model)?Path.Combine(model,"synthetic-behavior.txt"):model);
        File.WriteAllText(Path.Combine(Directory.GetCurrentDirectory(),"qwen-fixture-pid.txt"),Environment.ProcessId.ToString());
        if(args.Contains("--task"))
        {
            if(behavior!="repair") Console.Error.WriteLine("projectecho-alignment: raw-model-boundaries-v1; sample_rate=16000; timestamp_quantum_ms=80");
            void Words(string path, string id, long frames = 16000)
            {
                if (behavior.StartsWith("m5d-", StringComparison.Ordinal))
                { M5dQwenPartialAlignmentTests.WriteFixtureWords(path, id, behavior, frames); return; }
                string[] words=behavior=="wrong-text" || behavior=="late-fail" && id=="w000016" ? ["第","二","句","末","句"]:["第","一","句","末","句"];
                long[] start=[1600,3200,4800,9600,11200],end=[3200,4800,6400,11200,14400];
                if(behavior=="reverse")start[3]=100;
                File.WriteAllText(path,JsonSerializer.Serialize(words.Select((word,i)=>new{word,start_sample=start[i],end_sample=end[i],confidence=0})));
            }
            if (args.Contains("--request-sequence"))
            {
                if(behavior=="request-lock")
                {
                    bool blocked=false;
                    try{File.WriteAllText(Get("--request-sequence"),"[]");}catch(IOException){blocked=true;}
                    File.WriteAllText(Path.Combine(Directory.GetCurrentDirectory(),"qwen-request-lock.txt"),blocked?"blocked":"not-blocked");
                    if(!blocked)return 1;
                }
                Console.Error.WriteLine("projectecho-aligner-backend: " + (behavior=="align-fallback" || Get("--backend")=="cpu" ? "CPU" : "Vulkan" + Get("--device")));
                if(behavior!="no-device") Console.Error.WriteLine("projectecho-aligner-device: Synthetic fixture device");
                if(behavior=="two-devices") Console.Error.WriteLine("projectecho-aligner-device: Other fixture device");
                using var requests=JsonDocument.Parse(File.ReadAllText(Get("--request-sequence")));
                foreach(var item in requests.RootElement.EnumerateArray())
                {
                    string id=item.GetProperty("id").GetString()!, output=Get("--words-out");
                    Words(Path.Combine(Path.GetDirectoryName(output)!,Path.GetFileNameWithoutExtension(output)+"_"+id+".json"),id,
                        (new FileInfo(item.GetProperty("audio").GetString()!).Length - 44) / 2);
                    if(behavior!="no-complete") Console.Error.WriteLine("projectecho-aligner-complete: "+(behavior=="unknown-complete"?"wrong":id));
                    if(behavior=="duplicate-complete") Console.Error.WriteLine("projectecho-aligner-complete: "+id);
                    if(behavior=="align-wait")
                    {
                        File.WriteAllText(Path.Combine(Directory.GetCurrentDirectory(),"qwen-align-ready.txt"),"synthetic");
                        await Task.Delay(TimeSpan.FromMinutes(5));
                    }
                }
            }
            else Words(Get("--words-out"),"legacy");
            if(args.Contains("--batch-manifest-out") && behavior!="no-manifest")
                File.WriteAllText(Get("--batch-manifest-out"),behavior=="bad-manifest"?"{": "{\"fixture\":\"synthetic-only\"}");
            return 0;
        }
        Console.Error.WriteLine("projectecho-backend: "+(behavior=="fallback"?"CPU":"Vulkan0"));
        if (behavior != "wrong-family") Console.Error.WriteLine("[info] qwen3_asr: using selected vulkan device: Vulkan0");
        Console.WriteLine("{\"type\":\"batch_header\",\"load_ms\":1.0}");
        if(behavior=="wait")
        {
            File.WriteAllText(Path.Combine(Directory.GetCurrentDirectory(),"qwen-fixture-ready.txt"),"synthetic");
            await Task.Delay(TimeSpan.FromMinutes(5));return 0;
        }
        if(behavior=="missing")return 0;
        foreach(string file in File.ReadAllLines(Get("--batch")).Where(s=>s.Length>0))
        {
            string line=behavior=="truncated"?JsonSerializer.Serialize(new{file,text="第一句。末句。",error="output_truncated"}):
                JsonSerializer.Serialize(new{file,text="第一句。末句。"});
            Console.WriteLine(line);if(behavior=="duplicate")Console.WriteLine(line);
        }
        return 0;
    }
    private static void Need(bool condition,string message){if(!condition)throw new InvalidDataException(message);}
    private static void Bad(Action action){try{action();}catch(InvalidDataException){return;}throw new Exception("Invalid evidence accepted.");}
    private sealed class InlineProgress(Action<LocalTranscriptionProgress> report) : IProgress<LocalTranscriptionProgress>
    { public void Report(LocalTranscriptionProgress value) => report(value); }
}
