using System.Text.Json;
using ProjectEcho.Core.Transcription;

internal static class M4QwenPortableSettingsTests
{
    internal static void Register(Action<string, Action> run, string output)
    {
        string root = Path.Combine(output, "m4-qwen-portable-settings"); Directory.CreateDirectory(root);
        run("P4-04-SYNTH Qwen bundled executable paths round-trip relative and models stay external", () => Paths(root));
        run("P4-04-SYNTH Qwen portable paths reject traversal and retain custom executable choices", () => Reject(root));
        run("P4-PERF-SYNTH independent alignment hardware and old settings survive relocation", () => Alignment(root));
        run("P4-PERF-SYNTH old CPU compatibility bypasses accelerated engines without losing preferences", () => Compatibility(root));
    }
    private static void Paths(string root)
    {
        foreach (string backend in new[] { "cpu", "vulkan" })
        {
            string meeting = Meeting(root, backend), model = Path.Combine(root, "external-asr.gguf"), alignerModel = Path.Combine(root, "external-aligner.gguf");
            string executable = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, $"Tools/qwen-{backend}/transcribe-cli.exe"));
            string aligner = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Tools/qwen-aligner/audiocpp_cli.exe"));
            var config = new LocalTranscriptionConfiguration(QwenTranscriber.BackendId, executable, model, QwenAlignment: new(aligner, alignerModel));
            QwenSettingsStore.SaveMeeting(meeting, new(1, QwenTranscriber.BackendId, config));
            using var saved = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(meeting, QwenSettingsStore.RelativePath)));
            var c = saved.RootElement.GetProperty("configuration");
            Need(c.GetProperty("executablePath").GetString() == $"@app/Tools/qwen-{backend}/transcribe-cli.exe" &&
                c.GetProperty("qwenAlignment").GetProperty("executablePath").GetString() == "@app/Tools/qwen-aligner/audiocpp_cli.exe", "Bundled exe leaked an installation absolute path.");
            Need(c.GetProperty("modelPath").GetString() == model && c.GetProperty("qwenAlignment").GetProperty("modelPath").GetString() == alignerModel, "External model was rewritten inside application.");
            var restored = QwenSettingsStore.LoadMeeting(meeting).Configuration!;
            Need(restored.ExecutablePath == executable && restored.QwenAlignment?.ExecutablePath == aligner && restored.ModelPath == model, "Relative executable did not resolve at the running application base.");
        }
    }
    private static void Reject(string root)
    {
        string meeting = Meeting(root, "custom"); string exe = Path.Combine(root, "user-chosen.exe");
        var config = new LocalTranscriptionConfiguration(QwenTranscriber.BackendId, exe, Path.Combine(root,"external.gguf"), QwenAlignment: new(exe,Path.Combine(root,"aligner.gguf")));
        QwenSettingsStore.SaveMeeting(meeting,new(1,QwenTranscriber.BackendId,config));
        string path=Path.Combine(meeting,QwenSettingsStore.RelativePath), original=File.ReadAllText(path);
        Need(QwenSettingsStore.LoadMeeting(meeting).Configuration?.ExecutablePath==exe,"A user-selected executable was silently replaced.");
        foreach(string value in new[]{"@app/Tools/../qwen-cpu/transcribe-cli.exe","@app/Tools/qwen-cpu/other.exe","@app//Tools/qwen-cpu/transcribe-cli.exe"})
        {
            File.WriteAllText(path,original.Replace(JsonSerializer.Serialize(exe),JsonSerializer.Serialize(value)));
            try { QwenSettingsStore.LoadMeeting(meeting); } catch(InvalidDataException) { continue; }
            throw new InvalidDataException("An unapproved app-relative executable reference was accepted.");
        }
    }
    private static void Alignment(string root)
    {
        foreach(string name in new[]{"qwen-aligner-avx2","qwen-aligner-vulkan"})
        {
            string meeting=Meeting(root,name), exe=Path.Combine(AppContext.BaseDirectory,"Tools",name,"audiocpp_cli.exe");
            var configuration=new LocalTranscriptionConfiguration(QwenTranscriber.BackendId,exe,Path.Combine(root,"external.gguf"),
                Hardware:LocalTranscriptionHardware.Cpu,QwenAlignment:new(exe,Path.Combine(root,"external-aligner.gguf"),LocalTranscriptionHardware.Vulkan,3));
            QwenSettingsStore.SaveMeeting(meeting,new(1,QwenTranscriber.BackendId,configuration));
            string path=Path.Combine(meeting,QwenSettingsStore.RelativePath);
            Need(File.ReadAllText(path).Contains("@app/Tools/"+name+"/audiocpp_cli.exe"),"Optimized runtime was not portable.");
            var read=QwenSettingsStore.LoadMeeting(meeting).Configuration!;
            Need(read.Hardware==LocalTranscriptionHardware.Cpu && read.QwenAlignment is {Hardware:LocalTranscriptionHardware.Vulkan,GpuDeviceIndex:3},"Independent hardware choices did not persist.");
            var json=System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
            var alignment=json["configuration"]!["qwenAlignment"]!.AsObject();alignment.Remove("hardware");alignment.Remove("gpuDeviceIndex");
            File.WriteAllText(path,json.ToJsonString());
            Need(QwenSettingsStore.LoadMeeting(meeting).Configuration!.QwenAlignment is {Hardware:LocalTranscriptionHardware.Cpu,GpuDeviceIndex:0},"Old configuration no longer defaults to CPU.");
        }
        string custom=Path.Combine(root,"qwen-aligner","audiocpp_cli.exe");
        Need(QwenAlignmentRuntime.ResolveBundledExecutable(custom,LocalTranscriptionHardware.Vulkan)==custom,"Custom aligner was replaced.");
        string generic=Path.Combine(AppContext.BaseDirectory,"Tools","qwen-aligner","audiocpp_cli.exe");
        Need(QwenAlignmentRuntime.ResolveBundledExecutable(generic,LocalTranscriptionHardware.Vulkan)==
            Path.Combine(AppContext.BaseDirectory,"Tools","qwen-aligner-vulkan","audiocpp_cli.exe"),"Bundled Vulkan choice did not resolve independently.");
    }
    private static void Compatibility(string root)
    {
        string generic=Path.Combine(AppContext.BaseDirectory,"Tools","qwen-aligner","audiocpp_cli.exe");
        var saved=new QwenAlignmentConfiguration(Path.Combine(root,"custom-avx2.exe"),Path.Combine(root,"native.gguf"),
            Engine:QwenAlignmentEngine.OfficialPython,Python:new(Path.Combine(root,"python.exe"),Path.Combine(root,"official"),
                Path.Combine(AppContext.BaseDirectory,"Tools","qwen-python","qwen_aligner_bridge.py")),DisableCpuAcceleration:true);
        var effective=QwenAlignmentRuntime.EffectiveConfiguration(saved);
        Need(effective.Engine==QwenAlignmentEngine.Native && effective.ExecutablePath==generic && saved.Engine==QwenAlignmentEngine.OfficialPython,
            "Compatibility did not enforce generic CPU or changed saved preferred engine.");
        Need(effective.Python==saved.Python && effective.ModelPath==saved.ModelPath,"Compatibility lost inactive model/runtime preferences.");
        Need(QwenAlignmentRuntime.EffectiveConfiguration(saved with{Hardware=LocalTranscriptionHardware.Vulkan,Engine=QwenAlignmentEngine.Native}).Hardware==LocalTranscriptionHardware.Vulkan,
            "CPU compatibility changed selected Vulkan hardware.");
        string meeting=Meeting(root,"compatibility");
        var config=new LocalTranscriptionConfiguration(QwenTranscriber.BackendId,Path.Combine(root,"asr.exe"),Path.Combine(root,"asr.gguf"),QwenAlignment:saved);
        QwenSettingsStore.SaveMeeting(meeting,new(1,QwenTranscriber.BackendId,config));
        Need(QwenSettingsStore.LoadMeeting(meeting).Configuration!.QwenAlignment==saved,"CPU compatibility/Python preferences failed to round-trip.");
        Need(File.ReadAllText(Path.Combine(meeting,QwenSettingsStore.RelativePath)).Contains("@app/Tools/qwen-python/qwen_aligner_bridge.py"),"Bridge script was not portable.");
    }
    private static string Meeting(string root,string name){string path=Path.Combine(root,name);Directory.CreateDirectory(path);File.WriteAllText(Path.Combine(path,"meeting.json"),"{}");return path;}
    private static void Need(bool value,string message){if(!value)throw new InvalidDataException(message);}
}
