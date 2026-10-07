using System.Text.Json;
using ProjectEcho.Core.Transcription;

internal static class M4AlignmentSentenceTests
{
    internal static void Register(Action<string, Action> run, string output)
    {
        string root = Path.Combine(output, "m4-alignment-sentence"); Directory.CreateDirectory(root);
        run("P4-02-SYNTH raw acoustic alignment preserves Chinese mixed sentence times and punctuation", () => Mixed(root));
        run("P4-02-SYNTH raw alignment rejects wrong text missing tails and timestamp repair needs", () => Reject(root));
        run("P4-02-SYNTH sentence alignment keeps zero-length internal tokens without invented word times", () => Zero(root));
        run("P4-02-SYNTH decimal abbreviation and supplementary Unicode keep original ranges", () => Unicode(root));
    }

    private static void Mixed(string root)
    {
        const string text = "第一句。\n“Project Echo is ready!” 预算不是12%，是13%。末句。";
        string[] words = ["第", "一", "句", "Project", "Echo", "is", "ready", "预", "算", "不", "是", "12", "是", "13", "末", "句"];
        var result = Map(root, "mixed", text, words);
        Need(result.Count == 4 && string.Concat(result.Select(s => text.Substring(s.TextStart,s.TextLength))) == text, "Original punctuation/text lost.");
        Need(result[0].LocalStartFrame == 100 && result[0].LocalEndFrame == 550 && result[1].LocalStartFrame == 700 &&
            result[1].LocalEndFrame == 1350 && result[^1].LocalEndFrame == 3150, "Times were interpolated instead of copied from real fixture boundaries.");
    }
    private static void Reject(string root)
    {
        Bad(() => Map(root,"wrong","不是。",["是"]));
        Bad(() => Map(root,"tail","第一句。末句。",["第","一","句"]));
        Bad(() => Map(root,"negative","甲。",["甲"],[-1],[50]));
        Bad(() => Map(root,"reverse","甲乙。",["甲","乙"],[100,50],[200,250]));
        Bad(() => Map(root,"overlap-sentences","甲。乙。",["甲","乙"],[100,150],[200,250]));
        Bad(() => Map(root,"outside","甲。",["甲"],[100],[50001]));
        Bad(() => Map(root,"empty-duration","甲。",["甲"],[100],[100]));
    }
    private static void Zero(string root)
    {
        var result = Map(root,"zero","甲乙丙。",["甲","乙","丙"],[100,200,200],[200,200,300]);
        Need(result.Count == 1 && result[0].LocalStartFrame == 100 && result[0].LocalEndFrame == 300, "Zero-length interior token wrongly invented a time.");
    }
    private static void Unicode(string root)
    {
        const string text = "Dr. Li measured 12.5 percent. 𠀀到了！";
        var result = Map(root,"unicode",text,["Dr","Li","measured","125","percent","𠀀","到","了"]);
        Need(result.Count == 2 && text.Substring(result[0].TextStart,result[0].TextLength) == "Dr. Li measured 12.5 percent. " &&
            text.Substring(result[1].TextStart,result[1].TextLength) == "𠀀到了！", "Abbreviation/decimal/UTF16 range split incorrectly.");
    }
    private static IReadOnlyList<QwenWindowSentence> Map(string root,string name,string text,string[] words,long[]? starts=null,long[]? ends=null)
    {
        string path=Path.Combine(root,name+".json");
        File.WriteAllText(path,JsonSerializer.Serialize(words.Select((word,i)=>new{word,start_sample=starts?[i]??100+i*200,end_sample=ends?[i]??150+i*200,confidence=0})));
        return QwenAlignmentSentenceMapper.Map(text,path,50000);
    }
    private static void Bad(Action action){try{action();}catch(InvalidDataException){return;}throw new Exception("Invalid raw alignment was accepted.");}
    private static void Need(bool value,string message){if(!value)throw new Exception(message);}
}
