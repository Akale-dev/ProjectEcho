using NAudio.Wave;
using ProjectEcho.Capture.Windows;
using ProjectEcho.Core;
using ProjectEcho.Export;

internal static class M3iMixProgressTests
{
    internal static void Register(Action<string,Action> run,string output)
    {
        string root=Path.Combine(output,"m3i-mix-progress");Directory.CreateDirectory(root);
        run("M3I-SYNTH real mix phases report measured monotonic bytes/frames without changing PCM",()=>Phases(root));
        run("M3I-SYNTH throwing progress observers cannot damage or change the mixed file",()=>Observer(root));
        run("M3I-SYNTH cancellation during measured rendering/peak scan never reports completion",()=>Cancel(root));
        run("M3I-SYNTH unknown-size stream remains indeterminate and explicit frames match actual EOF",Unknown);
    }
    private static void Phases(string root)
    {
        string meeting=Fixture(root,"phases");var progress=new ObserverProgress();
        var first=M2ExportService.RenderMeeting(meeting,null,CancellationToken.None,progress);
        var plain=M2ExportService.RenderMeeting(meeting,null,CancellationToken.None);
        Need(first.WaveSha256==plain.WaveSha256 && first.FrameCount==48000*3,"Progress changed PCM or frame count.");
        foreach(var group in progress.Values.GroupBy(p=>p.Stage)) {
            long previous=-1;foreach(var p in group){Need(p.Completed>=previous,"Phase regressed: "+group.Key); previous=p.Completed;Need(p.Total is null || p.Completed<=p.Total,"Phase exceeds actual total.");}
        }
        foreach(string stage in new[]{MixStages.ValidatingIndex,MixStages.ValidatingPcm,MixStages.ValidatingFiles,MixStages.Validating,MixStages.RenderAnalysis,MixStages.Rendering,MixStages.TruePeakAnalysis,MixStages.TruePeakProtection,MixStages.TruePeakVerification,MixStages.Hashing})
            Need(progress.Values.Any(p=>p.Stage==stage && p.Total is >0 && p.Completed==p.Total),"Missing measured phase: "+stage);
        Need(progress.Values.First().Stage==MixStages.Preparing && progress.Values.First().Total is null,"Preparation fabricated total.");
        Need(progress.Values.Count(p=>p.Stage==MixStages.Completed)==1 && progress.Values.Last().Stage==MixStages.Completed,"Completion missing, duplicated or premature.");
        Need(progress.Values.Count<300,"Unbounded per-packet notifications.");
    }
    private static void Observer(string root)
    {
        string meeting=Fixture(root,"throwing");var expected=M2ExportService.RenderMeeting(meeting,null,CancellationToken.None);
        var actual=M2ExportService.RenderMeeting(meeting,null,CancellationToken.None,new ObserverProgress(_=>throw new IOException("synthetic observer fault")));
        Need(actual.WaveSha256==expected.WaveSha256,"Observer failure changed audio.");
    }
    private static void Cancel(string root)
    {
        foreach(string stage in new[]{MixStages.Rendering,MixStages.TruePeakAnalysis}) {
            string meeting=Fixture(root,"cancel-"+stage);using var cancel=new CancellationTokenSource();
            var progress=new ObserverProgress(p=>{if(p.Stage==stage && p.Completed>0)cancel.Cancel();});
            bool cancelled=false;try{M2ExportService.RenderMeeting(meeting,null,cancel.Token,progress);}catch(OperationCanceledException){cancelled=true;}
            Need(cancelled && progress.Values.All(p=>p.Stage!=MixStages.Completed),"Cancelled job reported success.");
            Need(!Directory.EnumerateFiles(Path.Combine(meeting,"derived"),"render.json",SearchOption.AllDirectories).Any(),"Cancelled job published manifest.");
            Need(M1Recovery.Read(meeting).IsCleanEnd,"Cancelled mix altered originals.");
        }
    }
    private static void Unknown()
    {
        byte[] pcm=new byte[2048];for(int i=0;i<pcm.Length;i+=2)BitConverter.TryWriteBytes(pcm.AsSpan(i,2),(short)(i%100));
        foreach(long? total in new long?[]{null,1024,1}) {
            var progress=new ObserverProgress();using var stream=new MemoryStream(pcm);
            var result=TruePeakProtection.AnalyzePcm16(stream,16000,CancellationToken.None,progress,total,MixStages.TruePeakAnalysis);
            Need(result.FrameCount==1024,"Progress changed EOF frames.");
            if(total==1024)Need(progress.Values.Last().Completed==1024 && progress.Values.Last().Percent==100,"Known frame total lost.");
            else Need(progress.Values.Last().Total is null && progress.Values.Last().Percent is null,"Unknown/wrong total invented 100%.");
        }
    }
    private static string Fixture(string root,string id)
    {
        string meeting=Path.Combine(root,id);const int rate=16000,packetFrames=320;const ulong epoch=100000000;
        byte[] pcm=new byte[packetFrames*2];for(int i=0;i<packetFrames;i++)BitConverter.TryWriteBytes(pcm.AsSpan(i*2,2),(short)(1000*Math.Sin(i*2*Math.PI*400/rate)));
        using var storage=new M1Storage(meeting,epoch,DateTimeOffset.UtcNow,"Synthetic progress fixture",true,RecordingTimeline.ExcludePaused);
        storage.AddSegment("remote-1","remote",new WaveFormat(rate,16,1));
        for(int f=0;f<rate*3;f+=packetFrames)storage.WritePacket("remote-1",pcm,new(f,packetFrames,packetFrames,(ulong)f,epoch+(ulong)f*10000000/rate,0,true));
        storage.WriteBoundary("remote-1","synthetic-stop","stop",epoch+30000000,null,0,null);storage.Finish(30000000,true,[]);return meeting;
    }
    private sealed class ObserverProgress(Action<MixProgress>? action=null):IProgress<MixProgress>
    {
        internal List<MixProgress> Values{get;}=[];
        public void Report(MixProgress value){Values.Add(value);action?.Invoke(value);}
    }
    private static void Need(bool value,string message){if(!value)throw new InvalidDataException(message);}
}