using System.Text.Json;
using San14Tenkazu.Formulas;

namespace San14Tenkazu.Tests.Formulas;

public class ResultEffectsTests
{
    [Fact]
    public void SemanticNotificationRequestsMatchCompleteNativeBodiesAndCallerPrefixes()
    {
        JsonElement root = Vectors.Read("result_effects.json.gz");
        Assert.True(root.GetProperty("native_full_body").GetBoolean());
        Assert.Empty(root.GetProperty("native_stubs").EnumerateArray());
        World w = Vectors.LoadWorld(root.GetProperty("world").GetString()!);
        int count = 0, events = 0, callerPrefixes = 0;
        foreach (JsonElement c in root.GetProperty("cases").EnumerateArray())
        {
            JsonElement f = c.GetProperty("fixture"), observed = c.GetProperty("observed");
            Action restore = Vectors.Poke(w, f.GetProperty("pokes"));
            try
            {
                int selector = f.GetProperty("selector").GetInt32(), reason = f.GetProperty("reason").GetInt32();
                Rec a = w.Table(Tables.Officers)[f.GetProperty("officers")[0].GetInt32()];
                Rec b = w.Table(Tables.Officers)[f.GetProperty("officers")[1].GetInt32()];
                ResultEffects.Notice? result = ResultEffects.FromCaller(w, a, b, reason, selector);
                int[] messages = observed.GetProperty("messages").EnumerateArray().Select(v => v.GetInt32()).ToArray();
                if (result is not { } n)
                {
                    Assert.Empty(messages);
                    Assert.Empty(observed.GetProperty("history").EnumerateArray());
                    Assert.Empty(observed.GetProperty("events").EnumerateArray());
                }
                else
                {
                    Assert.Equal(n.SecondaryMessage is { } extra ? new[] { n.PrimaryMessage, extra } : [n.PrimaryMessage], messages);
                    Assert.Equal(new[] { selector, reason, n.First.Index, n.Second.Index },
                        observed.GetProperty("entries")[0].EnumerateArray().Select(v => v.GetInt32()));
                    int[][] arguments = [[0, n.First.U16(0x10)], [1, n.Second.U16(0x10)], [0, 0x1394],
                        [1, n.ForceName], [2, n.First.U16(0x10)], [3, n.Second.U16(0x10)]];
                    Assert.Equal(arguments.Length, observed.GetProperty("format_args").GetArrayLength());
                    for (int i = 0; i < arguments.Length; i++)
                        Assert.Equal(arguments[i], observed.GetProperty("format_args")[i].EnumerateArray().Select(v => v.GetInt32()));
                    Assert.Single(observed.GetProperty("history").EnumerateArray());
                    Assert.Equal(new[] { n.Kind, n.Corps, n.Hex, 0, n.Tick, 0x1394, n.Mode, 1, 0, 0 },
                        observed.GetProperty("history")[0].EnumerateArray().Select(v => v.GetInt32()));
                    Assert.Equal(n.RelationEvent ? 1 : 0, observed.GetProperty("events").GetArrayLength());
                    if (n.RelationEvent)
                    {
                        Assert.Equal(new[] { 0xDA, 1, n.Tick, 1, 0x1A, n.Hex, 1 },
                            observed.GetProperty("events")[0].EnumerateArray().Select(v => v.GetInt32()));
                        Assert.Equal(n.Force, observed.GetProperty("event_forces")[0].GetInt32());
                        events++;
                    }
                }
                JsonElement caller = c.GetProperty("caller_observed");
                if (caller.ValueKind != JsonValueKind.Null)
                {
                    Assert.Equal(result.HasValue ? 1 : 0, caller.GetProperty("entries").GetArrayLength());
                    foreach (string key in new[] { "messages", "format_args", "history", "events", "event_forces", "locations" })
                        Assert.Equal(observed.GetProperty(key).GetRawText(), caller.GetProperty(key).GetRawText());
                    callerPrefixes++;
                }
            }
            finally { restore(); }
            count++;
        }
        Assert.True(count >= 17 && events >= 4 && callerPrefixes >= 6,
            $"cases {count}, event requests {events}, caller prefixes {callerPrefixes}");
    }
}
