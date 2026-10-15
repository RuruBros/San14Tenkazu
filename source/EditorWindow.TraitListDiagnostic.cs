#if LOCAL_DEVELOPER
using System.Text.Json;

namespace San14Tenkazu;

internal sealed partial class EditorWindow
{
    private async Task ReadDeveloperTraitList()
    {
        if (developerChanging || busy || developerMenu is not { } window) return;
        var identity = game;
        TraitListEvidence? evidence = null;
        string? error = null;
        developerChanging = true; SetBusy(true, false); UpdateDeveloperControls();
        window.AppendTraitDiagnostic("개성 열 정의 읽기 시작 · 메모리 쓰기 없음");
        try
        {
            try
            {
                if (identity is not { Verified: true } verified || seasonClosing || seasonDisconnecting)
                    throw new InvalidOperationException("검증된 게임 연결이 필요합니다.");
                evidence = await Task.Run(() => GameMemory.ReadTraitListDiagnostic(verified));
                error = evidence.Error;
            }
            catch (Exception failure) { error = failure.Message; }
            string directory = Path.Combine(Program.DataFolder, "runtime-rules", "diagnostics");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, $"trait-list-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.json");
            using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                await JsonSerializer.SerializeAsync(file, new { formatVersion = 1, utc = DateTime.UtcNow, readOnly = true,
                    identity?.ProcessId, identity?.StartTimeUtc, identity?.ExeHash,
                    consistent = evidence?.Consistent ?? false, error, evidence }, new JsonSerializerOptions { WriteIndented = true });
            string summary = evidence is { Consistent: true }
                ? $"열 {evidence.ColumnCount}개 비교 완료 · 다른 8바이트 필드 {evidence.Differences.Length}개"
                : "확정 결과 아님 · " + error;
            window.AppendTraitDiagnostic(summary);
            if (evidence is { Consistent: true })
                foreach (var diff in evidence.Differences.Take(20))
                    window.AppendTraitDiagnostic($"열 인덱스 {diff.ColumnIndex}, 레코드 +{diff.RecordOffset}, RVA {diff.Rva}: 기준 {diff.ExpectedHex} / 실제 {diff.ActualHex}");
            if (evidence?.Differences.Length > 20) window.AppendTraitDiagnostic("나머지 차이는 JSON 파일에 모두 기록했습니다.");
            window.AppendTraitDiagnostic("진단 파일: " + path);
            AddLog("9개성 표시 진단: " + summary + " · " + path);
        }
        catch (Exception failure)
        { window.AppendTraitDiagnostic("진단 기록 저장 실패: " + failure.Message); AddLog("9개성 표시 진단 저장 실패: " + failure.Message); }
        finally { developerChanging = false; SetBusy(false, false); UpdateDeveloperControls(); }
    }
}
#endif
