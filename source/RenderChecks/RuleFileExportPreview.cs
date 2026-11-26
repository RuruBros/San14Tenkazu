using San14Tenkazu;
using System.Windows.Controls;
using System.Windows.Threading;

internal static partial class RenderChecks
{
    private static void CheckRuleExportPreview(string sample, string profile)
    {
        var window = new RuleFileExportWindow(null);
        var context = SynchronizationContext.Current;
        T Field<T>(string name) => (T)typeof(RuleFileExportWindow).GetField(name, Hidden)!.GetValue(window)!;
        try
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            typeof(RuleFileExportWindow).GetField("input", Hidden)!.SetValue(window, sample);
            typeof(RuleFileExportWindow).GetField("profile", Hidden)!.SetValue(window, profile);
            window.Show();
            var task = (Task)typeof(RuleFileExportWindow).GetMethod("Preview", Hidden)!.Invoke(window, null)!;
            var frame = new DispatcherFrame();
            task.GetAwaiter().OnCompleted(() => frame.Continue = false);
            Dispatcher.PushFrame(frame);
            task.GetAwaiter().GetResult();
            var before = Field<RuleFileExport>("plan") ?? throw new InvalidOperationException(Field<TextBlock>("summary").Text);
            var conflict = before.Edits.Single(e => e.Status == FileEditStatus.Conflict);
            var selected = (StackPanel)((ScrollViewer)Field<Expander>("choices").Content).Content;
            var check = (CheckBox)selected.Children[0];
            check.IsChecked = true;
            Require(Field<RuleFileExport>("plan").Edits.Single(e => e.Key == conflict.Key).Status == FileEditStatus.Apply,
                "Selected conflict remains subject to semantic checks");
            check.IsChecked = false;
            Require(Field<RuleFileExport>("plan").Edits.Single(e => e.Key == conflict.Key).Status == FileEditStatus.Conflict
                && Field<DataGrid>("grid").Items.Count == before.Edits.Count && Field<PlateButton>("save").IsEnabled,
                "Clearing the choice retains the original file value and other compatible fields");
            Console.WriteLine($"PASS file export preview, {before.Edits.Count} settings, conflict {conflict.Key}, choose/clear; gameAccess=false");
        }
        finally { window.Close(); SynchronizationContext.SetSynchronizationContext(context); }
    }
}
