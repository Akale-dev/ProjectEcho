#define PROJECTECHO_LIBRARY_TEST
#include "main.cpp"
int main() {
  try {
    Require(Cluster({}, 2).empty(), "empty features must not invent a speaker");
    std::vector<Row> same(5, Row{0,0,{1,0,0}});
    auto one=Cluster(same, 4);
    Require(std::all_of(one.begin(),one.end(),[&](int v){return v==one[0];}),
            "identical features must not fabricate four voices");
    std::vector<Row> voices;
    for(int i=0;i<12;i++) voices.push_back({i,0,{1,0,0}});
    for(int i=0;i<12;i++) voices.push_back({i,1,{0,1,0}});
    for(int i=0;i<3;i++) voices.push_back({i,2,{0,0,1}});
    auto group=Cluster(voices,2);
    Require(group[0]!=group[12], "two dominant voices must survive a smaller vocal outlier group");
    for(int i=0;i<12;i++) Require(group[i]==group[0]&&group[12+i]==group[12], "same voice return must remain stable");
    Require(group==Cluster(voices,2),"repeated clustering must be deterministic");
    Masks masks(1); masks[0].fill(0);
    // Two simultaneous local slots must each be retained; their final labels are
    // distinct but contain no made-up speech before/after their actual masks.
    for(int i=20;i<40;i++) masks[0][i]=3;
    std::vector<Row> pair={Row{0,0,{1,0}},Row{0,1,{0,1}}};
    auto result=Reconstruct(masks,pair,{0,1},Window);
    Require(result.size()==2&&result[0].speaker!=result[1].speaker,"overlap must retain both active groups");
    Require(result[0].start==result[1].start&&result[0].end==result[1].end,"overlap cannot invent alternating turns");
    auto merged=Reconstruct(masks,pair,{0,0},Window);
    Require(merged.size()==1,"same global speaker in two local slots cannot duplicate output");
    masks[0].fill(0);
    auto silent=Reconstruct(masks,pair,{0,1},Window);
    Require(silent.empty(),"silence cannot acquire an absent speaker through tie-breaking");
    bool rejected=false;try{Number("nan");}catch(...){rejected=true;}
    Require(rejected,"non-finite numbers must be rejected");
    std::puts("PASS: empty/duplicate/outlier/global-return/deterministic/overlap/merge/silence/nonfinite");
    return 0;
  } catch(const std::exception &e) { std::fprintf(stderr,"FAIL: %s\n",e.what());return 1; }
}
