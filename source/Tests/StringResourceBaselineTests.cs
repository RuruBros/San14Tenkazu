using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace San14Tenkazu.Tests;

public sealed class StringResourceBaselineTests
{
    // Captured from the original C# string syntax before the move to resources.
    // DetailedHelp was subsequently revised with the save/load preparation flow; its text fixture follows that intentional change.
    // Expected text is assembled from literal fragments, independently of L.Parse/L.Format.
    [Fact]
    public void EveryMovedExpressionPreservesTextWhitespaceAndInsertedValues()
    {
        using var baseline=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"Vectors","StringResourceBaseline.json")));
        var culture=CultureInfo.CurrentCulture;
        var members=L.Members().ToDictionary(m=>m.Key,m=>m.Member);
        var distinct=new HashSet<string>();int count=0;
        try
        {
            foreach(string name in new[]{"ko-KR","en-US","fr-FR"})
            {
                CultureInfo.CurrentCulture=CultureInfo.GetCultureInfo(name);
                foreach(var row in baseline.RootElement.EnumerateArray())
                {
                    string key=row.GetProperty("section").GetString()+"."+row.GetProperty("key").GetString();
                    string[] parts=row.GetProperty("parts").EnumerateArray().Select(p=>p.GetString()!).ToArray();
                    int arguments=row.GetProperty("arguments").GetArrayLength();
                    Assert.Equal(parts.Length,arguments+1);distinct.Add(key);
                    foreach(string? inserted in new string?[]{"",null,"값 {p0} {p1}\n끝 ","0000FFFF · 1,234.5"})
                    {
                        var values=Enumerable.Range(0,arguments).Select(i=>inserted is null?null:inserted+i).ToArray();
                        var expected=new StringBuilder(parts[0]);
                        for(int i=0;i<arguments;i++)expected.Append(values[i]).Append(parts[i+1]);
                        var member=members[key];
                        string actual=member is PropertyInfo property?(string)property.GetValue(null)!:(string)((MethodInfo)member).Invoke(null,values)!;
                        Assert.True(expected.ToString()==actual,$"{name}: {row.GetProperty("file")}:{row.GetProperty("line")} {key}");++count;
                    }
                }
            }
        }
        finally{CultureInfo.CurrentCulture=culture;}
        Assert.Equal(234,baseline.RootElement.GetArrayLength());Assert.Equal(223,distinct.Count);Assert.Equal(2808,count);
    }

    [Fact]
    public void RuntimeAndDiagnosticMessagesRetainOriginalNumericFormatting()
    {
        var culture=CultureInfo.CurrentCulture;
        try
        {
            foreach(string name in new[]{"ko-KR","en-US","fr-FR"})
            {
                CultureInfo.CurrentCulture=CultureInfo.GetCultureInfo(name);
                foreach(int status in new[]{0,-1,unchecked((int)0xC0000001)})
                    Assert.Equal($"프로세스 일시 정지 실패: 0x{status:X8}",L.RuntimeErrors.SuspendFailed($"{status:X8}"));
                foreach(double value in new[]{0,0.123456789,-1.23456789,1e20})
                    Assert.Equal($"글꼴 배율 X={value:G5}, Y={value:G5}",L.DeveloperUi.FontScale($"{value:G5}",$"{value:G5}"));
                foreach(int experience in new[]{0,1234,int.MaxValue})
                    Assert.Equal($"능력치 {200}의 경험치 {experience:N0}가 성장 곡선과 맞지 않습니다.",L.RuntimeErrors.ExperienceCurveMismatch($"{200}",$"{experience:N0}"));
            }
        }
        finally{CultureInfo.CurrentCulture=culture;}
    }
}
