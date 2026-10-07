// ProjectEcho offline speaker worker. No recording, network, or persistent voice features.
// Window masks, frame mapping and vote reconstruction adapted from sherpa-onnx
// Copyright (c) 2024 Xiaomi Corporation; Apache-2.0 (see THIRD_PARTY.md).
#define NOMINMAX
#include <algorithm>
#include <array>
#include <atomic>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <limits>
#include <memory>
#include <numeric>
#include <stdexcept>
#include <string>
#include <vector>
#include "onnxruntime_cxx_api.h"
#include "sherpa-onnx/c-api/c-api.h"
#include <windows.h>
namespace {
  constexpr int Rate=16000, Window=160000, Shift=16000, Frames=589, FrameShift=270, Field=991;
  constexpr int Restarts=16, MaxIterations=80;
  std::atomic<bool> cancelled{
    false
  };
  BOOL WINAPI Control(DWORD){
    cancelled=true;
    return TRUE;
  }
  void Check(){
    if(cancelled)throw std::runtime_error("cancelled");
  }
  void Require(bool ok,const char *s){
    if(!ok)throw std::runtime_error(s);
  }
  std::string Utf8(const std::wstring &s){
    if(s.empty())return {};
    int n=WideCharToMultiByte(CP_UTF8,WC_ERR_INVALID_CHARS,s.data(),static_cast<int>(s.size()),nullptr,0,nullptr,nullptr);
    Require(n>0,"invalid Unicode argument");
    std::string out(n,0);
    WideCharToMultiByte(CP_UTF8,WC_ERR_INVALID_CHARS,s.data(),static_cast<int>(s.size()),out.data(),n,nullptr,nullptr);
    return out;
  }
  std::wstring Wide(const std::string &s){
    int n=MultiByteToWideChar(CP_UTF8,MB_ERR_INVALID_CHARS,s.data(),static_cast<int>(s.size()),nullptr,0);
    Require(n>0,"invalid UTF-8 path");
    std::wstring out(n,0);
    MultiByteToWideChar(CP_UTF8,MB_ERR_INVALID_CHARS,s.data(),static_cast<int>(s.size()),out.data(),n);
    return out;
  }
  struct Options {
    std::string segmentation,embedding,wave;
    int speakers=-1,segThreads=2,embThreads=2;
    float threshold=.9f;
  };
  int Integer(const std::string&s){
    size_t n=0;
    int v=std::stoi(s,&n);
    Require(n==s.size(),"invalid integer");
    return v;
  }
  float Number(const std::string&s){
    size_t n=0;
    float v=std::stof(s,&n);
    Require(n==s.size()&&std::isfinite(v),"invalid finite number");
    return v;
  }
  Options Parse(int argc,wchar_t**argv){
    Options o;
    for(int i=1;i<argc;i++){
      std::string a=Utf8(argv[i]);
      if(a.rfind("--",0)!=0){
        Require(o.wave.empty(),"only one input WAV is allowed");
        o.wave=a;
        continue;
      }
      auto p=a.find('=');
      Require(p!=std::string::npos,"options require =value");
      auto key=a.substr(0,p),v=a.substr(p+1);
      if(key=="--segmentation.pyannote-model")o.segmentation=v;
      else if(key=="--embedding.model")o.embedding=v;
      else if(key=="--segmentation.provider"||key=="--embedding.provider")Require(v=="cpu","only explicit CPU is supported");
      else if(key=="--segmentation.num-threads")o.segThreads=Integer(v);
      else if(key=="--embedding.num-threads")o.embThreads=Integer(v);
      else if(key=="--clustering.num-clusters")o.speakers=Integer(v);
      else if(key=="--clustering.cluster-threshold")o.threshold=Number(v);
      else if(key=="--min-duration-on"||key=="--min-duration-off")Require(Number(v)==0,"duration filtering is unsupported: preserve short speech");
      else if(key=="--segmentation.pyannote-window-shift-ratio")Require(std::abs(Number(v)-.1f)<1e-7f,"only locked 0.1 window shift is supported");
      else if(key=="--clustering.compute-confidence")Require(v=="false"||v=="0","calibrated confidence is unavailable");
      else throw std::runtime_error("unsupported option");
    }
    Require(!o.segmentation.empty()&&!o.embedding.empty()&&!o.wave.empty(),"model and input paths are required");
    Require(o.speakers==-1||(o.speakers>=1&&o.speakers<=64),"known speaker count must be 1..64 or -1");
    Require(o.segThreads>=1&&o.segThreads<=16&&o.embThreads>=1&&o.embThreads<=16,"threads must be 1..16");
    Require(o.threshold>0&&o.threshold<=2,"invalid clustering threshold");
    return o;
  }
  // Third-party C entry points can terminate inside a separately linked CRT.
  // Suppress process error dialogs and reject malformed/wrong-family models
  // through the exception-safe ORT API before handing them to sherpa's C API.
  void ConfigureFailureMode(){
    SetErrorMode(SEM_FAILCRITICALERRORS|SEM_NOGPFAULTERRORBOX|SEM_NOOPENFILEERRORBOX);
    _set_error_mode(_OUT_TO_STDERR);
    auto crt=GetModuleHandleW(L"msvcrt.dll");
    if(crt){
      using AbortBehavior=unsigned int (__cdecl *)(unsigned int,unsigned int);
      auto symbol=GetProcAddress(crt,"_set_abort_behavior");
      AbortBehavior fn=nullptr;
      static_assert(sizeof(fn)==sizeof(symbol));
      std::memcpy(&fn,&symbol,sizeof(fn));
      if(fn)fn(0,_WRITE_ABORT_MSG|_CALL_REPORTFAULT);
    }
    std::set_terminate([](){
      std::fputs("ProjectEcho speaker worker failed: unhandled native exception\n",stderr);
      std::fflush(stderr);
      std::_Exit(3);
    });
  }
  void ValidateModels(const Options&o){
    Ort::Env env(ORT_LOGGING_LEVEL_ERROR,"ProjectEcho.ModelPreflight");
    Ort::SessionOptions opt;
    opt.SetIntraOpNumThreads(1);
    opt.SetInterOpNumThreads(1);
    Ort::AllocatorWithDefaultOptions allocator;
    for(bool embedding:{false,true}){
      Check();
      const auto&path=embedding?o.embedding:o.segmentation;
      try{
        Ort::Session model(env,Wide(path).c_str(),opt);
        auto metadata=model.GetModelMetadata();
        auto property=[&](const char*key){
          auto value=metadata.LookupCustomMetadataMapAllocated(key,allocator);
          Require(bool(value),"required model metadata is missing");
          return std::string(value.get());
        };
        Require(Integer(property("sample_rate"))==Rate,"model sample rate must be 16 kHz");
        if(embedding){
          auto framework=property("framework");
          Require(framework=="nemo"||framework=="3d-speaker"||framework=="wespeaker",
                  "unsupported speaker embedding model family");
          int dimensions=Integer(property("output_dim"));
          Require(dimensions>0&&dimensions<=4096,"invalid speaker embedding dimensions");
          Require(!property("feature_normalize_type").empty(),"missing feature normalization metadata");
          if(framework=="nemo"){
            Require(Integer(property("feat_dim"))>0&&Integer(property("feat_dim"))<=512,
                    "invalid NeMo feature dimension");
            Require(Integer(property("window_size_ms"))>0&&Integer(property("window_stride_ms"))>0,
                    "invalid NeMo feature window metadata");
            Require(!property("window_type").empty(),"missing NeMo window type");
          }
        }else{
          Require(property("model_type")=="pyannote-segmentation-3.0", "unsupported segmentation model family");
          Require(Integer(property("window_size"))==Window&&Integer(property("receptive_field_shift"))==FrameShift&&
                  Integer(property("receptive_field_size"))==Field&&Integer(property("num_speakers"))==3&&
                  Integer(property("num_classes"))==7&&Integer(property("powerset_max_classes"))==2,
                  "unsupported segmentation metadata");
        }
        Require(model.GetInputCount()>0&&model.GetOutputCount()>0,"model has no inference IO");
      }catch(const std::exception&){
        // Keep native exception details (which may contain local paths) out of
        // the user-facing failure line. Original files are never rewritten.
        throw std::runtime_error(embedding?"embedding model validation failed: unreadable, invalid, or unsupported ONNX/model metadata":
          "segmentation model validation failed: unreadable, invalid, or unsupported ONNX/model metadata");
      }
    }
  }
  uint32_t U32(const unsigned char*p){
    return uint32_t(p[0])|(uint32_t(p[1])<<8)|(uint32_t(p[2])<<16)|(uint32_t(p[3])<<24);
  }
  uint16_t U16(const unsigned char*p){
    return uint16_t(p[0])|(uint16_t(p[1])<<8);
  }
  std::vector<float> ReadWave(const std::string&name){
    std::ifstream f(std::filesystem::path(Wide(name)),std::ios::binary);
    Require(bool(f),"cannot open input WAV");
    f.seekg(0,std::ios::end);
    auto length=f.tellg();
    Require(length>=44,"truncated WAV");
    f.seekg(0);
    std::array<unsigned char,12>head{
    };
    f.read(reinterpret_cast<char*>(head.data()),12);
    Require(!std::memcmp(head.data(),"RIFF",4)&&!std::memcmp(head.data()+8,"WAVE",4),"input must be RIFF WAVE");
    Require(uint64_t(U32(head.data()+4))+8==uint64_t(length),"WAV size mismatch");
    bool format=false;
    uint64_t dataAt=0;
    uint32_t dataBytes=0;
    uint64_t position=12;
    while(position+8<=uint64_t(length)){
      std::array<unsigned char,8>ch{
      };
      f.seekg(position);
      f.read(reinterpret_cast<char*>(ch.data()),8);
      uint32_t size=U32(ch.data()+4);
      Require(position+8+size<=uint64_t(length),"WAV chunk outside input");
      if(!std::memcmp(ch.data(),"fmt ",4)){
        Require(!format&&size>=16,"duplicate or invalid WAV format");
        std::array<unsigned char,16>fmt{
        };
        f.read(reinterpret_cast<char*>(fmt.data()),16);
        Require(U16(fmt.data())==1&&U16(fmt.data()+2)==1&&U32(fmt.data()+4)==Rate&&U32(fmt.data()+8)==Rate*2&&U16(fmt.data()+12)==2&&U16(fmt.data()+14)==16,"expected 16 kHz mono PCM16 WAV");
        format=true;
      }
      else if(!std::memcmp(ch.data(),"data",4)){
        Require(dataAt==0,"duplicate WAV data");
        dataAt=position+8;
        dataBytes=size;
      }
      position+=8+size+(size&1);
    }
    Require(format&&dataAt&&dataBytes&&dataBytes%2==0,"missing/invalid WAV data");
    size_t n=dataBytes/2;
    Require(n<=size_t(Rate)*60*60*8,"input exceeds eight-hour worker bound");
    std::vector<float>x(n);
    f.seekg(dataAt);
    std::array<unsigned char,65536>b{
    };
    size_t i=0;
    while(i<n){
      Check();
      size_t frames=std::min(n-i,b.size()/2);
      f.read(reinterpret_cast<char*>(b.data()),frames*2);
      Require(bool(f),"truncated PCM data");
      for(size_t j=0;j<frames;j++)x[i+j]=static_cast<int16_t>(U16(b.data()+j*2))/32768.0f;
      i+=frames;
    }
    return x;
  }
  void Progress(double p){
    std::fprintf(stderr,"progress %.2f%%\n",p);
    std::fflush(stderr);
  }
  struct Segment {
    float start,end;
    int speaker;
  };
  struct Row {
    int chunk,slot;
    std::vector<float> embedding;
  };
  using Masks=std::vector<std::array<unsigned char,Frames>>;
  Masks SegmentAudio(const Options&o,const std::vector<float>&audio){
    Ort::Env env(ORT_LOGGING_LEVEL_ERROR,"ProjectEcho.Diarization");
    Ort::SessionOptions opt;
    opt.SetIntraOpNumThreads(o.segThreads);
    opt.SetInterOpNumThreads(1);
    Ort::Session session(env,Wide(o.segmentation).c_str(),opt);
    Ort::AllocatorWithDefaultOptions allocator;
    auto meta=session.GetModelMetadata();
    const std::array<std::pair<const char*,int>,7> expected={
      {
        {
          "sample_rate",Rate
        }
        ,{
          "window_size",Window
        }
        ,{
          "receptive_field_shift",FrameShift
        }
        ,{
          "receptive_field_size",Field
        }
        ,{
          "num_speakers",3
        }
        ,{
          "num_classes",7
        }
        ,{
          "powerset_max_classes",2
        }
      }
    };
    for(auto&[key,v]:expected){
      auto value=meta.LookupCustomMetadataMapAllocated(key,allocator);
      Require(value&&Integer(value.get())==v,"unsupported segmentation metadata");
    }
    auto type=meta.LookupCustomMetadataMapAllocated("model_type",allocator);
    Require(type&&std::string(type.get())=="pyannote-segmentation-3.0","unsupported segmentation model family");
    Require(session.GetInputCount()==1&&session.GetOutputCount()==1,"unsupported segmentation IO");
    auto in=session.GetInputNameAllocated(0,allocator),out=session.GetOutputNameAllocated(0,allocator);
    const char*ins[]={
      in.get()
    };
    const char*outs[]={
      out.get()
    };
    auto mem=Ort::MemoryInfo::CreateCpu(OrtArenaAllocator,OrtMemTypeDefault);
    int n=static_cast<int>(audio.size());
    int chunks=n<=Window?1:(n-Window)/Shift+1+((n-Window)%Shift>0);
    Masks masks(chunks);
    const std::array<unsigned char,7> mapping={
      0,1,2,4,3,5,6
    };
    std::vector<float>x(Window);
    for(int c=0;c<chunks;c++){
      Check();
      std::fill(x.begin(),x.end(),0);
      int count=std::min(Window,n-c*Shift);
      std::copy_n(audio.data()+c*Shift,count,x.data());
      std::array<int64_t,3>shape={
        1,1,Window
      };
      auto input=Ort::Value::CreateTensor<float>(mem,x.data(),x.size(),shape.data(),shape.size());
      auto y=session.Run(Ort::RunOptions{
        nullptr
      }
      ,ins,&input,1,outs,1);
      auto shapeOut=y[0].GetTensorTypeAndShapeInfo().GetShape();
      Require(shapeOut==std::vector<int64_t>({
        1,Frames,7
      }
      ),"invalid segmentation output shape");
      const float*p=y[0].GetTensorData<float>();
      for(int frame=0;frame<Frames;frame++){
        int best=0;
        for(int k=0;k<7;k++){
          Require(std::isfinite(p[frame*7+k]),"non-finite segmentation output");
          if(p[frame*7+k]>p[frame*7+best])best=k;
        }
        masks[c][frame]=mapping[best];
      }
      if(c%10==0||c==chunks-1)Progress(35.0*(c+1)/chunks);
    }
    return masks;
  }
  int Pop(unsigned char m){
    return (m&1)+((m>>1)&1)+((m>>2)&1);
  }
  std::vector<Row> Extract(const Options&o,const std::vector<float>&audio,const Masks&masks){
    SherpaOnnxSpeakerEmbeddingExtractorConfig config{
    };
    config.model=o.embedding.c_str();
    config.num_threads=o.embThreads;
    config.provider="cpu";
    auto ex=std::unique_ptr<const SherpaOnnxSpeakerEmbeddingExtractor,decltype(&SherpaOnnxDestroySpeakerEmbeddingExtractor)>(SherpaOnnxCreateSpeakerEmbeddingExtractor(&config),SherpaOnnxDestroySpeakerEmbeddingExtractor);
    Require(bool(ex),"embedding model initialization failed");
    int dim=SherpaOnnxSpeakerEmbeddingExtractorDim(ex.get());
    Require(dim>=1&&dim<=4096,"invalid embedding dimension");
    std::vector<Row> rows;
    rows.reserve(masks.size()*2);
    for(int chunk=0;chunk<static_cast<int>(masks.size());chunk++){
      Check();
      for(int slot=0;slot<3;slot++){
        int on=0;
        for(auto m:masks[chunk])if(Pop(m)==1&&(m&(1<<slot)))on++;
        if(on<10)continue;
        auto stream=std::unique_ptr<const SherpaOnnxOnlineStream,decltype(&SherpaOnnxDestroyOnlineStream)>(SherpaOnnxSpeakerEmbeddingExtractorCreateStream(ex.get()),SherpaOnnxDestroyOnlineStream);
        Require(bool(stream),"embedding stream allocation failed");
        int start=-1;
        for(int frame=0;frame<=Frames;frame++){
          bool active=frame<Frames&&Pop(masks[chunk][frame])==1&&(masks[chunk][frame]&(1<<slot));
          if(active&&start<0)start=frame;
          if(!active&&start>=0){
            int end=std::min(frame,Frames-1);
            int a=static_cast<float>(start)/Frames*Window+chunk*Shift;
            int b=std::min(static_cast<int>(static_cast<float>(end)/Frames*Window)+chunk*Shift,static_cast<int>(audio.size()));
            if(b>a)SherpaOnnxOnlineStreamAcceptWaveform(stream.get(),Rate,audio.data()+a,b-a);
            start=-1;
          }
        }
        SherpaOnnxOnlineStreamInputFinished(stream.get());
        if(!SherpaOnnxSpeakerEmbeddingExtractorIsReady(ex.get(),stream.get()))continue;
        auto value=std::unique_ptr<const float,decltype(&SherpaOnnxSpeakerEmbeddingExtractorDestroyEmbedding)>(SherpaOnnxSpeakerEmbeddingExtractorComputeEmbedding(ex.get(),stream.get()),SherpaOnnxSpeakerEmbeddingExtractorDestroyEmbedding);
        Require(bool(value),"embedding inference failed");
        double norm=0;
        bool valid=true;
        for(int d=0;d<dim;d++){
          valid=valid&&std::isfinite(value.get()[d]);
          norm+=double(value.get()[d])*value.get()[d];
        }
        if(!valid||norm<=1e-20)continue;
        Row row{
          chunk,slot,std::vector<float>(dim)
        };
        float inv=1.0/std::sqrt(norm);
        for(int d=0;d<dim;d++)row.embedding[d]=value.get()[d]*inv;
        rows.push_back(std::move(row));
      }
      if(chunk%10==0||chunk==static_cast<int>(masks.size())-1)Progress(35+55.0*(chunk+1)/masks.size());
    }
    return rows;
  }
  double Dot(const std::vector<float>&a,const float*b){
    double x=0;
    for(size_t i=0;i<a.size();i++)x+=double(a[i])*b[i];
    return x;
  }
  struct Random {
    uint64_t state;
    uint64_t Next(){
      uint64_t z=(state+=0x9e3779b97f4a7c15ULL);
      z=(z^(z>>30))*0xbf58476d1ce4e5b9ULL;
      z=(z^(z>>27))*0x94d049bb133111ebULL;
      return z^(z>>31);
    }
    double Uniform(){
      return (Next()>>11)*(1.0/9007199254740992.0);
    }
  };
  std::vector<int> Cluster(const std::vector<Row>&rows,int requested){
    if(rows.empty())return {};
    const size_t n=rows.size(),dim=rows[0].embedding.size();
    int k=std::min(requested,static_cast<int>(n));
    if(k==1)return std::vector<int>(n,0);
    std::vector<int>bestLabels;
    double bestObjective=-std::numeric_limits<double>::infinity();
    int bestRestart=-1;
    for(int restart=0;restart<Restarts;restart++){
      Check();
      Random rng{
        uint64_t(restart+1)
      };
      std::vector<float>centers(static_cast<size_t>(k)*dim);
      rng.state=uint64_t(restart+1);
      size_t first=rng.Next()%n;
      std::copy(rows[first].embedding.begin(),rows[first].embedding.end(),centers.begin());
      std::vector<double>distance(n,2),weight(n);
      int actual=1;
      for(int c=1;c<k;c++){
        double sum=0;
        for(size_t i=0;i<n;i++){
          distance[i]=std::min(distance[i],std::max(0.0,1-Dot(rows[i].embedding,centers.data()+(c-1)*dim)));
          weight[i]=distance[i]*distance[i];
          sum+=weight[i];
        }
        if(sum<1e-16)break;
        double target=rng.Uniform()*sum,accum=0;
        size_t selected=n-1;
        for(size_t i=0;i<n;i++){
          accum+=weight[i];
          if(accum>=target){
            selected=i;
            break;
          }
        }
        std::copy(rows[selected].embedding.begin(),rows[selected].embedding.end(),centers.begin()+c*dim);
        actual++;
      }
      std::vector<int>labels(n,-1);
      for(int iteration=0;iteration<MaxIterations;iteration++){
        Check();
        bool changed=false;
        std::vector<double>sums(static_cast<size_t>(actual)*dim);
        std::vector<size_t>counts(actual);
        std::vector<double>similarities(n);
        for(size_t i=0;i<n;i++){
          if(i%256==0)Check();
          int label=0;
          double value=Dot(rows[i].embedding,centers.data());
          for(int c=1;c<actual;c++){
            double v=Dot(rows[i].embedding,centers.data()+c*dim);
            if(v>value){
              value=v;
              label=c;
            }
          }
          changed=changed||labels[i]!=label;
          labels[i]=label;
          similarities[i]=value;
          counts[label]++;
          for(size_t d=0;d<dim;d++)sums[label*dim+d]+=rows[i].embedding[d];
        }
        bool empty=false;
        for(int c=0;c<actual;c++){
          double norm=0;
          for(size_t d=0;d<dim;d++)norm+=sums[c*dim+d]*sums[c*dim+d];
          if(!counts[c]||norm<=1e-20){
            size_t worst=std::min_element(similarities.begin(),similarities.end())-similarities.begin();
            std::copy(rows[worst].embedding.begin(),rows[worst].embedding.end(),centers.begin()+c*dim);
            similarities[worst]=2;
            empty=true;
            continue;
          }
          double inv=1/std::sqrt(norm);
          for(size_t d=0;d<dim;d++)centers[c*dim+d]=sums[c*dim+d]*inv;
        }
        if(!changed&&!empty)break;
      }
      double objective=0;
      for(size_t i=0;i<n;i++){
        if(i%256==0)Check();
        int label=0;
        double value=Dot(rows[i].embedding,centers.data());
        for(int c=1;c<actual;c++){
          double v=Dot(rows[i].embedding,centers.data()+c*dim);
          if(v>value){
            value=v;
            label=c;
          }
        }
        labels[i]=label;
        objective+=value;
      }
      if(objective>bestObjective+1e-10){
        bestObjective=objective;
        bestLabels=std::move(labels);
        bestRestart=restart;
      }
      Progress(90+8.0*(restart+1)/Restarts);
    }
    std::fprintf(stderr,"global spherical clustering: vectors=%zu requested=%d restarts=%d max_iterations=%d best_restart=%d objective=%.9f\n",n,requested,Restarts,MaxIterations,bestRestart,bestObjective);
    return bestLabels;
  }
  std::vector<Segment> Reconstruct(const Masks&masks,const std::vector<Row>&rows,std::vector<int>cluster,size_t samples){
    if(rows.empty())return {};
    int k=*std::max_element(cluster.begin(),cluster.end())+1;
    std::vector<int>first(k,std::numeric_limits<int>::max());
    for(size_t i=0;i<rows.size();i++){
      auto&r=rows[i];
      int local=0;
      while(local<Frames&&!(masks[r.chunk][local]&(1<<r.slot)))local++;
      first[cluster[i]]=std::min(first[cluster[i]],r.chunk*Shift+local*FrameShift);
    }
    std::vector<int>order(k);
    std::iota(order.begin(),order.end(),0);
    std::stable_sort(order.begin(),order.end(),[&](int a,int b){
      return first[a]<first[b];
    }
    );
    std::vector<int>map(k);
    for(int c=0;c<k;c++)map[order[c]]=c;
    for(int&c:cluster)c=map[c];
    size_t frames=(Window+(masks.size()-1)*Shift)/FrameShift+1;
    std::vector<unsigned char>count(frames),weight(frames),votes(frames*k);
    for(size_t chunk=0;chunk<masks.size();chunk++){
      int start=static_cast<float>(chunk)*Shift/FrameShift+.5f;
      for(int f=0;f<Frames;f++){
        count[start+f]+=Pop(masks[chunk][f]);
        weight[start+f]++;
      }
    }
    std::vector<std::array<int,3>> localMap(masks.size(),std::array<int,3>{
      -1,-1,-1
    }
    );
    for(size_t i=0;i<rows.size();i++)localMap[rows[i].chunk][rows[i].slot]=cluster[i];
    for(size_t chunk=0;chunk<masks.size();chunk++){
      int start=static_cast<float>(chunk)*Shift/FrameShift+.5f;
      for(int f=0;f<Frames;f++){
        uint64_t active=0;
        for(int slot=0;slot<3;slot++)if(localMap[chunk][slot]>=0&&(masks[chunk][f]&(1<<slot)))active|=uint64_t(1)<<localMap[chunk][slot];
        for(int c=0;c<k;c++)if(active&(uint64_t(1)<<c))votes[(start+f)*k+c]++;
      }
    }
    size_t keep=samples<=Window?std::min<size_t>(Frames,samples/FrameShift+1):((samples-Window)%Shift? samples/FrameShift+1:frames);
    std::vector<uint64_t>final(keep);
    std::vector<int>ranking(k);
    for(size_t f=0;f<keep;f++){
      if(f%16384==0)Check();
      int wanted=weight[f]?int(float(count[f])/weight[f]+.5f):0;
      std::iota(ranking.begin(),ranking.end(),0);
      std::stable_sort(ranking.begin(),ranking.end(),[&](int a,int b){
        return votes[f*k+a]>votes[f*k+b];
      }
      );
      for(int j=0;j<std::min(wanted,k);j++)if(votes[f*k+ranking[j]])final[f]|=uint64_t(1)<<ranking[j];
    }
    std::vector<Segment>result;
    for(int c=0;c<k;c++){
      int start=-1;
      for(size_t f=0;f<=keep;f++){
        bool active=f<keep&&(final[f]&(uint64_t(1)<<c));
        if(active&&start<0)start=static_cast<int>(f);
        if(!active&&start>=0){
          int end=std::min(f,keep-1);
          if(end>start)result.push_back({
            start*(float(FrameShift)/Rate)+.5f*Field/Rate,end*(float(FrameShift)/Rate)+.5f*Field/Rate,c
          }
          );
          start=-1;
        }
      }
    }
    std::stable_sort(result.begin(),result.end(),[](const Segment&a,const Segment&b){
      return a.start<b.start;
    }
    );
    return result;
  }
  int Callback(int done,int total,void*){
    if(cancelled)std::_Exit(130);
    if(total>0)Progress(100.0*done/total);
    return 0;
  }
  std::vector<Segment> LegacyAutomatic(const Options&o,const std::vector<float>&audio){
    SherpaOnnxOfflineSpeakerDiarizationConfig c{
    };
    c.segmentation.pyannote.model=o.segmentation.c_str();
    c.segmentation.pyannote.window_shift_ratio=.1f;
    c.segmentation.num_threads=o.segThreads;
    c.segmentation.provider="cpu";
    c.embedding.model=o.embedding.c_str();
    c.embedding.num_threads=o.embThreads;
    c.embedding.provider="cpu";
    c.clustering.num_clusters=-1;
    c.clustering.threshold=o.threshold;
    auto sd=std::unique_ptr<const SherpaOnnxOfflineSpeakerDiarization,decltype(&SherpaOnnxDestroyOfflineSpeakerDiarization)>(SherpaOnnxCreateOfflineSpeakerDiarization(&c),SherpaOnnxDestroyOfflineSpeakerDiarization);
    Require(bool(sd),"automatic pipeline initialization failed");
    auto r=std::unique_ptr<const SherpaOnnxOfflineSpeakerDiarizationResult,decltype(&SherpaOnnxOfflineSpeakerDiarizationDestroyResult)>(SherpaOnnxOfflineSpeakerDiarizationProcessWithCallback(sd.get(),audio.data(),static_cast<int>(audio.size()),Callback,nullptr),SherpaOnnxOfflineSpeakerDiarizationDestroyResult);
    Require(bool(r),"automatic analysis failed");
    int n=SherpaOnnxOfflineSpeakerDiarizationResultGetNumSegments(r.get());
    Require(n>=0&&n<=2000000,"invalid automatic result size");
    auto p=std::unique_ptr<const SherpaOnnxOfflineSpeakerDiarizationSegment,decltype(&SherpaOnnxOfflineSpeakerDiarizationDestroySegment)>(SherpaOnnxOfflineSpeakerDiarizationResultSortByStartTime(r.get()),SherpaOnnxOfflineSpeakerDiarizationDestroySegment);
    Require(n==0||bool(p),"missing automatic segments");
    std::vector<Segment>v;
    for(int i=0;i<n;i++)v.push_back({
      p.get()[i].start,p.get()[i].end,p.get()[i].speaker
    }
    );
    return v;
  }
}
#ifndef PROJECTECHO_LIBRARY_TEST
int wmain(int argc,wchar_t**argv){
  ConfigureFailureMode();
  SetConsoleCtrlHandler(Control,TRUE);
  try{
    auto options=Parse(argc,argv);
    ValidateModels(options);
    auto audio=ReadWave(options.wave);
    std::printf("ProjectEchoDiarizationConfig(segmentation(provider=\"cpu\",threads=%d),embedding(provider=\"cpu\",threads=%d),known_speakers=%d)\n",options.segThreads,options.embThreads,options.speakers);
    std::printf("projectecho-clustering=%s\n",options.speakers>0?"spherical-kmeans-global-v1":"sherpa-complete-link-global-v1");
    std::puts("Started");
    std::fflush(stdout);
    std::vector<Segment>result;
    if(options.speakers>0){
      auto masks=SegmentAudio(options,audio);
      auto rows=Extract(options,audio,masks);
      auto clusters=Cluster(rows,options.speakers);
      result=Reconstruct(masks,rows,std::move(clusters),audio.size());
    }
    else result=LegacyAutomatic(options,audio);
    Check();
    for(const auto&s:result){
      Require(std::isfinite(s.start)&&std::isfinite(s.end)&&s.start>=0&&s.end>s.start&&s.end<=double(audio.size())/Rate+.063&&s.speaker>=0&&s.speaker<64,"invalid output interval");
    }
    for(const auto&s:result)std::printf("%.3f -- %.3f speaker_%02d\n",s.start,s.end,s.speaker);
    std::fflush(stdout);
    Progress(100);
    return 0;
  }
  catch(const std::exception&e){
    std::fprintf(stderr,"ProjectEcho speaker worker failed: %s\n",e.what());
    return cancelled?130:2;
  }
  catch(...){
    std::fputs("ProjectEcho speaker worker failed: native exception\n",stderr);
    return 3;
  }
}


#endif



