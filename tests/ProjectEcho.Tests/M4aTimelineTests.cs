using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using ProjectEcho.Export;

internal static class M4aTimelineTests
{
    internal static void Register(Action<string,Action> run,string output)
    {
        run("T28/T29-CODEC M4A declared/table/EOF durations and tamper rejection",()=>ActualAndTampered(output));
        run("T28-SYNTH M4A eight-hour sample-size table bounded scan and rational arithmetic",()=>LongMetadata(output));
    }
    private static void ActualAndTampered(string output)
    {
        var root=Path.Combine(output,"m4a-timeline");Directory.CreateDirectory(root);
        var source=Path.Combine(root,"source.wav");TruePeakTests.WriteWave(source,TruePeakTests.Tone(84000,.013,.27,.1));
        var encoded=new MediaFoundationM4aEncoder().Encode(source,Path.Combine(root,"accurate.m4a"));
        Require(encoded.ContainerTimeline is not null,"Encoder omitted independent container timeline verification.");
        var metadata=M4aTimelineProbe.Inspect(encoded.Path);
        M4aTimelineProbe.ValidateAgainstDecoded(metadata,encoded.DecodedFrames,encoded.SampleRate);
        Require(metadata.SampleTableDurationTicks==87040 && metadata.MediaTimescale==48000,"Unexpected complete 1.75s fixture timeline.");
        var original=File.ReadAllBytes(encoded.Path);var hash=SHA256.HashData(original);
        var fields=new[]
        {
            ("movie-duration",new[]{"moov","mvhd"},16,48000u),
            ("track-duration",new[]{"moov","trak","tkhd"},20,48000u),
            ("media-duration",new[]{"moov","trak","mdia","mdhd"},16,48000u),
            ("zero-timescale",new[]{"moov","trak","mdia","mdhd"},12,0u),
            ("wrong-delta",new[]{"moov","trak","mdia","minf","stbl","stts"},12,1023u),
            ("wrong-packet-count",new[]{"moov","trak","mdia","minf","stbl","stsz"},8,84u)
        };
        foreach(var (name,path,offset,value) in fields)
        {
            var changed=original.ToArray();int body=FindBody(changed,path);
            BinaryPrimitives.WriteUInt32BigEndian(changed.AsSpan(body+offset,4),value);
            var target=Path.Combine(root,name+".m4a");File.WriteAllBytes(target,changed);
            Reject(()=>M4aTimelineProbe.ValidateAgainstDecoded(M4aTimelineProbe.Inspect(target),encoded.DecodedFrames,48000));
        }
        var edited=original.ToArray();int stco=FindBody(edited,["moov","trak","mdia","minf","stbl","stco"]);
        "ctts"u8.CopyTo(edited.AsSpan(stco-4,4));
        string unsupported=Path.Combine(root,"unsupported-composition-offset.m4a");File.WriteAllBytes(unsupported,edited);
        Reject(()=>M4aTimelineProbe.Inspect(unsupported));
        using var cancelled=new CancellationTokenSource();cancelled.Cancel();bool stopped=false;
        try{M4aTimelineProbe.Inspect(encoded.Path,cancelled.Token);}catch(OperationCanceledException){stopped=true;}
        Require(stopped && hash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(encoded.Path))),"Inspection cancellation changed original.");
        File.WriteAllText(Path.Combine(root,"actual-container-evidence.json"),JsonSerializer.Serialize(new
        {metadata,encoded.DecodedFrames,encoded.DurationSeconds,originalSha256=Convert.ToHexStringLower(hash),tamperCasesRejected=fields.Length+1},new JsonSerializerOptions{WriteIndented=true}));
    }

    private static void LongMetadata(string output)
    {
        var root=Path.Combine(output,"m4a-timeline");Directory.CreateDirectory(root);
        var path=Path.Combine(root,"synthetic-eight-hour-metadata-only.m4a");
        const uint packets=1_350_000;
        WriteMetadataFixture(path,packets,1024,48000);
        long before=GC.GetAllocatedBytesForCurrentThread();var watch=System.Diagnostics.Stopwatch.StartNew();
        var result=M4aTimelineProbe.Inspect(path);var seconds=watch.Elapsed.TotalSeconds;
        long allocation=GC.GetAllocatedBytesForCurrentThread()-before;
        M4aTimelineProbe.ValidateAgainstDecoded(result,1_382_400_000,48000);
        Require(result.EncodedSampleCount==packets && result.EncodedPayloadBytes==packets && allocation<2_000_000,
            "Eight-hour metadata scan was capped, overflowed or loaded the entire stsz table.");
        const long large=9_007_199_254_740_999;
        var exact=new M4aContainerTimeline(48000,(ulong)large-1,(ulong)large-1,48000,(ulong)large-1,(ulong)large,1,1);
        M4aTimelineProbe.ValidateAgainstDecoded(exact,large,48000);
        Reject(()=>M4aTimelineProbe.ValidateAgainstDecoded(exact with {MovieDurationTicks=(ulong)large-2},large,48000));
        Reject(()=>M4aTimelineProbe.ValidateAgainstDecoded(exact with {SampleTableDurationTicks=(ulong)large-1},large,48000));
        File.WriteAllText(Path.Combine(root,"large-table-evidence.json"),JsonSerializer.Serialize(new
        {
            kind="SYNTHETIC_METADATA_ONLY_NOT_PLAYABLE",durationHours=8,result.EncodedSampleCount,tableBytes=packets*4L,
            allocationBytes=allocation,elapsedSeconds=seconds,integerRationalChecksAbove2Pow53="PASS",
            actualEightHourEncoding="NOT_RUN; no AAC bitstream was fabricated or claimed valid"
        },new JsonSerializerOptions{WriteIndented=true}));
    }

    private static void WriteMetadataFixture(string path,uint packets,uint delta,uint rate)
    {
        using var file=new FileStream(path,FileMode.CreateNew,FileAccess.ReadWrite,FileShare.None,65536);
        long Begin(string type){long start=file.Position;Write32(file,0);file.Write(System.Text.Encoding.ASCII.GetBytes(type));return start;}
        void End(long start){long end=file.Position;file.Position=start;Write32(file,checked((uint)(end-start)));file.Position=end;}
        void Header(string type,int bodySize,int scaleOffset,int durationOffset)
        {
            long box=Begin(type);var body=new byte[bodySize];
            if(scaleOffset>=0)BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(scaleOffset,4),rate);
            BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(durationOffset,4),checked(packets*delta));file.Write(body);End(box);
        }
        long ftyp=Begin("ftyp");file.Write("M4A \0\0\0\0isommp42"u8);End(ftyp);
        long mdat=Begin("mdat");file.SetLength(file.Position+packets);file.Position+=packets;End(mdat);
        long moov=Begin("moov");Header("mvhd",100,12,16);
        long trak=Begin("trak");Header("tkhd",84,-1,20);
        long mdia=Begin("mdia");Header("mdhd",24,12,16);
        long hdlr=Begin("hdlr");file.Write(new byte[8]);file.Write("soun"u8);file.Write(new byte[12]);End(hdlr);
        long minf=Begin("minf"),stbl=Begin("stbl"),stsd=Begin("stsd");Write32(file,0);Write32(file,1);
        long mp4a=Begin("mp4a");file.Write(new byte[28]);End(mp4a);End(stsd);
        long stts=Begin("stts");Write32(file,0);Write32(file,1);Write32(file,packets);Write32(file,delta);End(stts);
        long stsz=Begin("stsz");Write32(file,0);Write32(file,0);Write32(file,packets);
        for(uint index=0;index<packets;index++)Write32(file,1);
        End(stsz);End(stbl);End(minf);End(mdia);End(trak);End(moov);file.Flush(true);
    }
    private static void Write32(Stream stream,uint value)
    {Span<byte> bytes=stackalloc byte[4];BinaryPrimitives.WriteUInt32BigEndian(bytes,value);stream.Write(bytes);}
    private static int FindBody(byte[] file,string[] path)
    {
        int begin=0,end=file.Length;
        foreach(var type in path)
        {
            bool found=false;
            for(int offset=begin;offset+8<=end;)
            {
                ulong size=BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(offset,4));int header=8;
                if(size==1){size=BinaryPrimitives.ReadUInt64BigEndian(file.AsSpan(offset+8,8));header=16;}
                if(size==0)size=(ulong)(end-offset);
                if(size<(ulong)header||size>(ulong)(end-offset))throw new InvalidDataException("Invalid test fixture box.");
                if(System.Text.Encoding.ASCII.GetString(file,offset+4,4)==type){begin=offset+header;end=offset+(int)size;found=true;break;}
                offset+=checked((int)size);
            }
            if(!found)throw new InvalidOperationException("Missing test fixture box: "+type);
        }
        return begin;
    }
    private static void Reject(Action action)
    {
        try{action();}catch(Exception ex)when(ex is InvalidDataException or NotSupportedException){return;}
        throw new InvalidOperationException("Inconsistent/unsupported M4A timeline was accepted.");
    }
    private static void Require(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
}
