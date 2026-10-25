#if LOCAL_DEVELOPER
using San14Tenkazu.Seasons;
namespace San14Tenkazu;
internal sealed partial class EditorWindow
{
    private DeveloperNoticeDiagnostic? developerNoticeDiagnostic;
    private System.Windows.Threading.DispatcherTimer? developerNoticeDeadline;
    private void StartDeveloperNoticeDeadline()
    {
        developerNoticeDeadline?.Stop();
        developerNoticeDeadline=new(TimeSpan.FromMilliseconds(250),System.Windows.Threading.DispatcherPriority.Normal,
            async(_,_)=>{if(!developerChanging&&developerNoticeDiagnostic is {DeadlineExpired:true} run){run.RecordControllerDeadline();await CancelDeveloperTest();}},Dispatcher);
        developerNoticeDeadline.Start();
    }
    private byte[] PrepareDeveloperNoticeScene(byte[] scene)=>developerNoticeDiagnostic?.PrepareScene(scene)??scene;
    private async Task CompleteDeveloperNoticeDiagnostic(bool ownerRestored=false)
    {
        if(developerNoticeDiagnostic is not {} run)return;
        developerNoticeDeadline?.Stop();developerNoticeDeadline=null;
        developerNoticeDiagnostic=null;
        try{
            GameSnapshot? after=null;string? error=null;
            try{
                if(game!=run.Identity||layout is not {} found)throw new InvalidOperationException(L.Errors.ProcessChanged);
                if(run.Layout is {} initial&&!initial.SameStorage(found))throw new InvalidOperationException(L.Errors.StateMoved);
                after=await Task.Run(()=>GameMemory.ReadSnapshot(run.Identity,found));
            }catch(Exception failed){error=failed.Message;}
            run.Complete(after,environmentController?.Status,seasonController?.Status,error,ownerRestored);
            developerMenu?.Append(L.DeveloperUi.PictureDiagnosticSaved+run.ResultPath);
            AddLog(L.DeveloperUi.PictureDiagnosticSaved+run.ResultPath);
        }catch(Exception error){AddLog(L.DeveloperUi.DiagnosticLogFailed+error.Message);}
    }
}
#endif
