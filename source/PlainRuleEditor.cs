using W = System.Windows;
using WC = System.Windows.Controls;

namespace San14Tenkazu;

internal sealed class PlainRuleEditorCard : EditorCard
{
    private readonly PlainRuleSpec spec;
    internal sealed record View(PlainRuleRecord Record, RuleProfile Profile);
    private readonly Dictionary<PlainRuleField,NumberField> fields=[];
    private readonly Dictionary<PlainRuleField,NameChoice> kinds=[];
    private GameName[] costNames=[];
    private readonly Dictionary<PlainRuleField,WC.TextBlock> details=[];
    private readonly WC.CheckBox auto=new() {Content=L.Rules.AutoApply,ToolTip=L.Rules.AutoTip};
    private readonly PlateButton restore=new(L.Rules.Restore,icon:Icons.Revert) {ToolTip=L.Rules.RestoreTip};
    private bool showing;
    internal string Table => spec.Table;
    internal int RecordId {get;private set;}
    internal override EditorTarget? Target => RecordId==0 ? null : new PlainRuleTarget(Table,RecordId);
    internal override int Pending => Target is null ? 0 : fields.Values.Count(f => f.IsChanged)+kinds.Values.Count(f => f.Picked!=f.Current);
    internal event Action? RestoreClicked;
    internal event Action<bool>? AutoChanged;
    internal PlainRuleEditorCard(PlainRuleSpec spec)
    {
        this.spec=spec;
        Shield.Visibility=W.Visibility.Collapsed; var page=new WC.StackPanel();
        page.Children.Add(new WC.TextBlock {Text=L.Rules.Note,TextWrapping=W.TextWrapping.Wrap,Foreground=CardSkin.InkMuted,Margin=new W.Thickness(0,0,0,12)});
        page.Children.Add(auto);
        foreach (var f in spec.Fields) {
            var detail=new WC.TextBlock {Foreground=CardSkin.InkMuted,TextWrapping=W.TextWrapping.Wrap,VerticalAlignment=W.VerticalAlignment.Center}; details[f]=detail;
            if (f.Id is "kind1" or "kind2") {
                var choice=new NameChoice(80); kinds[f]=choice;
                choice.SetNames(Enumerable.Range(1,6).Select(id => new GameName(id,$"#{id}","")));
                choice.Box.SelectionChanged+=(_,_) => {if (!showing) {choice.Mark(); Count();}};
                page.Children.Add(Line(f.Label,choice.Box,detail));
            } else {
                var number=new NumberField(f.Input(spec.RecordKind),80); fields[f]=number; number.Edited+=Count;
                page.Children.Add(Line(f.Label,number.Box,detail));
            }
        }
        page.Children.Add(new WC.TextBlock {Text=spec.Hint,TextWrapping=W.TextWrapping.Wrap,Foreground=CardSkin.InkMuted,Margin=new W.Thickness(0,10,0,0)});
        AddPage(spec.Title,page); Shortcuts.Children.Add(restore); restore.Click+=() => RestoreClicked?.Invoke();
        auto.Checked+=(_,_) => {if (!showing) AutoChanged?.Invoke(true);};
        auto.Unchecked+=(_,_) => {if (!showing) AutoChanged?.Invoke(false);};
    }
    internal void SetAvailable(bool available) {restore.IsEnabled=auto.IsEnabled=available;}
    internal void Open(View view) {
        if (RecordId!=view.Record.Id) StartOver(); RecordId=view.Record.Id;
        foreach (var f in fields) f.Value.Show(view.Record.Value(f.Key));
        showing=true; foreach (var f in kinds) f.Value.Show(view.Record.Value(f.Key)); showing=false; Follow(view);
    }
    internal void Follow(View view) {
        if (RecordId!=view.Record.Id) return;
        Title.Text=$"{view.Record.Name} · #{RecordId}"; Subtitle.Text=spec.Title; showing=true; auto.IsChecked=view.Profile.AutoApply;
        foreach (var f in spec.Fields) {
            var key=new RuleKey(Table,RecordId,f.Id); int now=view.Record.Value(f),baseline=view.Profile.Default(key),desired=view.Profile.Desired(key);
            if (fields.TryGetValue(f,out var number)) number.Follow(now); else {kinds[f].Follow(now); kinds[f].Mark();}
            string state=desired==baseline ? now==baseline ? L.Rules.Unchanged : L.Rules.Conflict
                : now==desired ? L.Rules.Applied : now==baseline ? L.Rules.Waiting : L.Rules.Conflict;
            details[f].Text=$"{L.Rules.Current} {now} · {L.Rules.Baseline} {baseline}\n{L.Rules.Setting} {desired} · {state}";
        }
        showing=false; Count();
    }
    internal RuleChange[]? Changes(out string error) {
        error=""; var changes=new List<RuleChange>();
        foreach (var f in fields.Where(f => f.Value.IsChanged)) {
            if (!f.Value.TryRead(out long value,out error)) return null;
            changes.Add(new(new(Table,RecordId,f.Key.Id),(int)f.Value.Current,(int)value));
        }
        foreach (var f in kinds.Where(f => f.Value.Picked!=f.Value.Current)) {
            if (f.Value.Picked<f.Key.Minimum || f.Value.Picked>f.Key.Maximum) {error=f.Key.Input(spec.RecordKind).RangeMessage; return null;}
            changes.Add(new(new(Table,RecordId,f.Key.Id),f.Value.Current,f.Value.Picked));
        }
        if (Table=="doctrine" && changes.Count>0 && !DoctrineRules.ValidCosts(id => kinds.FirstOrDefault(f => f.Key.Id==id).Value is { } kind ? kind.Picked : (int)fields.Single(f => f.Key.Id==id).Value.Value))
        {error=L.Rules.DoctrineZeroCost; return null;}
        return changes.ToArray();
    }
    internal void SetCostNames(GameName[] names) {
        GameName[] allowed=Enumerable.Range(1,6).Select(id => names.FirstOrDefault(n => n.Id==id) is {Name:not null} name
            ? name : new GameName(id,$"#{id}","")).ToArray();
        if (costNames.SequenceEqual(allowed)) return;
        costNames=allowed; bool wasShowing=showing; showing=true;
        try {
            foreach (var choice in kinds.Values) {
                int current=choice.Current,picked=choice.Picked; choice.SetNames(allowed); choice.Show(current); choice.Pick(picked); choice.Mark();
            }
        } finally {showing=wasShowing;}
    }
}
