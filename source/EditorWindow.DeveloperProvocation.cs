#if LOCAL_DEVELOPER
using System.Text.Json;
using W = System.Windows;

namespace San14Tenkazu;

internal sealed partial class EditorWindow
{
    private GameMemory.ProvocationPlan? heldProvocationPlan;
    private GameMemory.ProvocationReceipt? heldProvocationReceipt;
    private string? heldProvocationReceiptPath;
    private void LoadHeldProvocation()
    {
        if (!CanPrepareCompleteEdits || developerMenu is not { } window) return;
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "JSON|*.json", CheckFileExists = true };
        if (dialog.ShowDialog(window) != true) return;
        try
        {
            string text = File.ReadAllText(dialog.FileName);
            using var json = JsonDocument.Parse(text);
            if (json.RootElement.TryGetProperty("identity", out _))
            {
                heldProvocationReceipt = JsonSerializer.Deserialize<GameMemory.ProvocationReceipt>(text, CompleteEditJson)!;
                heldProvocationPlan = heldProvocationReceipt.Plan;
                heldProvocationReceiptPath = dialog.FileName; SetBusy(true, false);
            }
            else heldProvocationPlan = JsonSerializer.Deserialize<GameMemory.ProvocationPlan>(text, CompleteEditJson)!;
            GameMemory.ValidateProvocationPlan(heldProvocationPlan);
            window.HeldProvocationLog.AppendText(dialog.FileName + System.Environment.NewLine +
                $"{heldProvocationPlan.Commander} → {heldProvocationPlan.SourceName}: {Convert.FromHexString(heldProvocationPlan.HeldHex)[0x25]}" + System.Environment.NewLine);
        }
        catch (Exception error) { window.HeldProvocationLog.AppendText(error.Message + System.Environment.NewLine); }
        UpdateDeveloperControls();
    }

    private async Task RunHeldProvocation(bool apply, bool decrease)
    {
        if (developerMenu is not { } window || game is not { } identity || layout is not { } current || developerChanging ||
            apply && (!CanPrepareCompleteEdits || heldProvocationPlan is null) || !apply && heldProvocationReceipt is null) return;
        string confirm = apply ? L.DeveloperUi.ProvocationApplyConfirm : L.DeveloperUi.ProvocationFinishConfirm;
        if ((apply || decrease) && W.MessageBox.Show(window, confirm, AppTitle, W.MessageBoxButton.YesNo,
            W.MessageBoxImage.Question, W.MessageBoxResult.No) != W.MessageBoxResult.Yes) return;
        developerChanging = true; SetBusy(true, false); UpdateDeveloperControls();
        try
        {
            string directory = Path.Combine(seasonStorageRoot, "compatibility", $"held-provocation-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            using var stream = new FileStream(Path.Combine(directory, "evidence.jsonl"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            using var output = new StreamWriter(stream) { AutoFlush = true };
            void Record(object row) { output.WriteLine(JsonSerializer.Serialize(row, CompleteEditJson)); stream.Flush(true); }
            if (apply)
            {
                heldProvocationReceiptPath = Path.Combine(directory, "receipt.json");
                heldProvocationReceipt = await Task.Run(() => GameMemory.BeginDeveloperProvocation(identity, current, heldProvocationPlan!, receipt =>
                {
                    using var file = new FileStream(heldProvocationReceiptPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                    JsonSerializer.Serialize(file, receipt, CompleteEditJson); file.Flush(true);
                    heldProvocationReceipt = receipt;
                }, Record));
                window.HeldProvocationLog.AppendText(L.DeveloperUi.ProvocationHeld + heldProvocationReceiptPath + System.Environment.NewLine);
            }
            else
            {
                if (heldProvocationReceipt!.Identity != identity || !current.SameStorage(heldProvocationReceipt.Layout))
                    throw new InvalidOperationException(L.Errors.StateMoved);
                var result = await Task.Run(() => GameMemory.FinishDeveloperProvocation(heldProvocationReceipt, decrease, Record));
                window.HeldProvocationLog.AppendText(JsonSerializer.Serialize(result, CompleteEditJson) + System.Environment.NewLine);
                heldProvocationReceipt = null; heldProvocationPlan = null;
            }
        }
        catch (Exception error) { window.HeldProvocationLog.AppendText(error.ToString() + System.Environment.NewLine); }
        finally { developerChanging = false; SetBusy(heldProvocationReceipt is not null, false); UpdateDeveloperControls(); }
    }
}
#endif
