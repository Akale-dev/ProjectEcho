using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using ProjectEcho.Core;
using ProjectEcho.Core.Transcription;

namespace ProjectEcho.App;

public partial class MainWindow
{
    // Product UI consumer test for the adapter's mixed timed/untimed revision contract.
    // All files are synthetic; no engine, audio device, microphone or playback is started.
    internal async Task<object> M3iUntimedUiSmokeAsync(string evidence, CancellationToken token)
    {
        string meeting = Path.Combine(RecordingsRoot, "m3i-mixed-times");
        WriteCloseFixture(meeting);
        var originals = Directory.EnumerateFiles(meeting, "*", SearchOption.AllDirectories).ToDictionary(p => p, CloseFixtureHash);
        string input = Path.Combine(evidence, "m3i-mixed-times.json");
        var segments = new[] {
            new TranscriptSegment("first",0,500,"合成声音 A","unknown","有时间的第一句。","approximate"),
            new TranscriptSegment("middle",null,null,null,"unknown","无时间文字必须保全。","untimed"),
            new TranscriptSegment("last",1500,1900,null,"unknown","有时间的最后一句。","approximate")
        };
        File.WriteAllText(input, JsonSerializer.Serialize(new {
            schemaVersion=1,isSynthetic=true,meetingId="m3i-mixed-times",revisionId="fixture",parentRevisionId=(string?)null,
            provider="Synthetic mixed-time UI contract",inputExportId=(string?)null,segments
        }, new JsonSerializerOptions { PropertyNamingPolicy=JsonNamingPolicy.CamelCase }));
        var revision = TranscriptService.Import(new(meeting,"m3i-mixed-times",2000,input,IsSynthetic:true),token);
        SelectMeeting(meeting,loadSavedMix:false);
        NavigateTo(3); transcriptionView!.WorkspaceTabs.SelectedIndex=1;
        await transcriptWork.WaitAsync(token);
        await LoadTranscriptVersionsAsync(revision.RevisionId);
        var view=transcriptionView!;
        Need(view.SegmentsBox.Items.Count==3,"Mixed-time UI lost text rows.");
        Need(view.WorkspaceTabs.Items.Count == 4, "Pro recognition/review/search/speaker workspaces were lost.");
        view.SegmentsBox.SelectedIndex=0; SelectTranscriptSegment();
        Need(view.SegmentsBox.SelectedItem is TranscriptRow labeled &&
            labeled.DisplayText.Contains("[合成声音 A]"), "Pro speaker labels were removed by the mixed-time UI port.");
        view.SegmentsBox.SelectedIndex=1; SelectTranscriptSegment();
        Need(view.SegmentsBox.SelectedItem is TranscriptRow { Segment.StartMs:null } row && row.TimeLabel==UiText.T("无时间"),"Unsupported time not visible.");
        Need(!view.SeekButton.IsEnabled && view.ExportSubtitlesButton.IsEnabled && view.ExportTextButton.IsEnabled && view.SaveRevisionButton.IsEnabled,"Untimed UI actions are unsafe.");
        SeekTranscript(); Need(player.Source is null,"Untimed row started playback.");
        string text=Path.Combine(evidence,"m3i-complete.txt");
        TranscriptSubtitleExporter.ExportText(meeting,revision.RevisionId,text,token);
        Need(File.ReadAllText(text)==string.Join('\n',segments.Select(s=>s.Text))+"\n","TXT dropped or reordered words.");
        Need(TranscriptService.Search(meeting,"无时间文字",token).Any(),"Untimed words are not searchable.");
        view.EditBox.Text = "尚未保存的无时间人工草稿。";
        view.SegmentsBox.SelectedIndex=0; SelectTranscriptSegment();
        view.SegmentsBox.SelectedIndex=1; SelectTranscriptSegment();
        Need(view.EditBox.Text == "尚未保存的无时间人工草稿。", "Pro per-segment draft was lost while changing timed/untimed selection.");
        Need(TranscriptService.Load(meeting, revision.RevisionId, token).Segments.SequenceEqual(segments),
            "Draft editing overwrote the immutable original revision.");
        SetReplayRevisions(meeting, [revision], revision.RevisionId);
        Need(ReplaySegmentsBox.Items.Count == 2 && ReplayStatusText.Text.Contains("1 段无时间文字") &&
            ReplaySegmentsBox.Items.Cast<TranscriptRow>().First().DisplayText.Contains("[合成声音 A]"),
            "Replay lost Pro speaker labels or omitted the untimed-text notice.");
        state=SessionState.Recording; UpdateControls(); Need(!view.SeekButton.IsEnabled,"Recording allowed seek.");
        state=SessionState.Paused; UpdateControls(); Need(!view.SeekButton.IsEnabled,"Paused allowed seek.");
        state=SessionState.Idle; UpdateControls();
        await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.Render,token);
        var images=new List<string>(); SaveLayoutImage(evidence,"m3i-untimed.png",1,images);
        foreach (var original in originals) Need(CloseFixtureHash(original.Key)==original.Value,"UI changed original audio/clock files.");
        Need(host is null && !microphoneEnabled && player.Source is null,"UI test started media.");
        return new {Status="PASS",Synthetic=true,Rows=3,UntimedRows=1,AllTextPreserved=true,UntimedSeekDisabled=true,SubtitleExportDisabled=true,TextExportEnabled=true,Searchable=true,ProSpeakerLabelsPreserved=true,UnsavedDraftPreserved=true,OriginalRevisionUnchanged=true,ReplayUntimedNoticeVisible=true,OriginalsUnchanged=true,CaptureStarted=false,MicrophoneCollected=false,Images=images};
        static void Need(bool value,string message) {if (!value) throw new InvalidDataException(message);}
    }
}