using DiskAtlas;

internal static class DesktopRenderingTests
{
    internal static int Run()
    {
        int count=0;
        foreach(string? name in new string?[]{null,"DiskAtlas.exe","AsukaLens.exe","custom-name.exe"})
        {
            var normal=DesktopRendering.Resolve([],name);
            Check(normal is {Dib:true,Composition:true,Opaque:false,CandidateLabel:false},"OrdinaryDefaultsToAcceptedComposition");
            Check(DesktopRendering.Resolve(["synthetic-folder"],name)==normal,"FolderArgumentDoesNotAlterRendering");
        }
        var modes=new[]{
            ("--legacy-rendering",new DesktopRendering(false,false,false,false)),
            ("--dib-candidate",new DesktopRendering(true,false,false,true)),
            ("--opaque-redirect-candidate",new DesktopRendering(true,true,false,true)),
            ("--composition-dib-candidate",new DesktopRendering(true,false,true,true))};
        foreach(var (flag,expected) in modes)
        {
            Check(DesktopRendering.Resolve([flag],"AsukaLens.exe")==expected,"ExplicitModePreserved");
            Check(DesktopRendering.Resolve([flag,flag],"AsukaLens.exe")==expected,"RepeatedSameFlagStable");
            Check(DesktopRendering.Resolve([flag],"AsukaLens.CompositionDibCandidate.exe")==expected,"ExplicitModeOverridesCandidateName");
            foreach(var (other,_) in modes.Where(x=>x.Item1!=flag))
            {
                bool rejected=false;try{DesktopRendering.Resolve([flag,other],"AsukaLens.exe");}catch(ArgumentException){rejected=true;}
                Check(rejected,"ContradictoryExplicitModesRejected");
            }
        }
        Check(DesktopRendering.Resolve([],"AsukaLens.DibCandidate.exe")==modes[1].Item2,"C55EntryPreserved");
        Check(DesktopRendering.Resolve([],"AsukaLens.OpaqueDibCandidate.exe")==modes[2].Item2,"C56EntryPreserved");
        Check(DesktopRendering.Resolve([],"AsukaLens.CompositionDibCandidate.exe")==modes[3].Item2,"C57EntryPreserved");
        Check(DesktopRendering.Resolve([],"ASUKALENS.DIBCANDIDATE.EXE")==modes[1].Item2,"WindowsEntryNameIgnoresCase");
        Console.WriteLine($"PASS desktop rendering startup: {count}");return 0;
        void Check(bool value,string code){if(!value)throw new InvalidOperationException(code);count++;}
    }
}
