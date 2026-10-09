using System.Text.Json;
using San14Tenkazu;
using W = System.Windows;
using WM = System.Windows.Media;

internal static partial class RenderChecks
{
    private static void CheckCoast()
    {
        string? previous = Environment.GetEnvironmentVariable("SAN14_NO_GPU");
        MapSurface map;
        try { Environment.SetEnvironmentVariable("SAN14_NO_GPU", "1"); map = new MapSurface { Width = 1280, Height = 800 }; }
        finally { Environment.SetEnvironmentVariable("SAN14_NO_GPU", previous); }
        map.Measure(new W.Size(1280, 800));
        map.Arrange(new W.Rect(0, 0, 1280, 800));
        static T Field<T>(MapSurface map, string name) => (T)typeof(MapSurface).GetField(name, Hidden)!.GetValue(map)!;
        static void Set(MapSurface map, string name, object value) => typeof(MapSurface).GetField(name, Hidden)!.SetValue(map, value);
        static object? Call(MapSurface map, string name, params object?[] args) => typeof(MapSurface).GetMethod(name, Hidden)!.Invoke(map, args);
        var origin = new W.Point(MapTerrain.WorldWidth / 2, MapTerrain.WorldHeight / 2);
        var speed = new W.Vector(0, 1200);
        const double tau = .25, threshold = 15;
        var rows = new List<object>();
        try
        {
            foreach (var times in new[] {
                Enumerable.Range(1, 60).Select(i => i / 60.0).ToArray(),
                new[] { .016, .033, .173, .19, .207, .557, .573, .923, 1.0 },
                new[] { 1.0 } })
            {
                Set(map, "zoom", 8.0); Set(map, "center", origin);
                Set(map, "velocity", speed); Call(map, "ApplyView"); Call(map, "StartLoop");
                Set(map, "animationUpdatedAt", 0.0);
                double scale = (double)typeof(MapSurface).GetProperty("Scale", Hidden)!.GetValue(map)!;
                foreach (double time in times) Call(map, "AdvanceAnimation", time);
                W.Vector travelled = (origin - Field<W.Point>(map, "center")) * scale;
                W.Vector expected = speed * (tau * (1 - Math.Exp(-1 / tau)));
                W.Vector remaining = Field<W.Vector>(map, "velocity");
                Require((travelled - expected).Length < 1e-6, "Coast distance changed with callback cadence");
                Require((remaining - speed * Math.Exp(-1 / tau)).Length < 1e-6, "Delayed callback changed coast speed");
                Call(map, "AdvanceAnimation", 2.0);
                W.Vector stoppedTravel = (origin - Field<W.Point>(map, "center")) * scale;
                W.Vector stoppedExpected = speed / speed.Length * (tau * (speed.Length - threshold));
                Require((stoppedTravel - stoppedExpected).Length < 1e-6, "Final coast distance changed with stop callback timing");
                Require(!Field<bool>(map, "looping") && Field<W.Vector>(map, "velocity") == default, "Coast did not stop after its actual elapsed duration");
                rows.Add(new { callbacks = times.Length, travelled, remaining, stoppedTravel, passed = true });
            }
            Set(map, "zoom", 8.0); Set(map, "center", origin); Set(map, "velocity", new W.Vector(400000, 0));
            Call(map, "ApplyView"); Call(map, "StartLoop"); Set(map, "animationUpdatedAt", 0.0);
            Call(map, "AdvanceAnimation", .35);
            Require(Field<W.Vector>(map, "velocity") == default && !Field<bool>(map, "looping"), "Coast kept moving after hitting a map boundary");
            Console.WriteLine(JsonSerializer.Serialize(new { TimeAndDistanceChecks = rows, BoundaryStopPassed = true }));
        }
        finally
        {
            Call(map, "StopLoop");
            var handler = typeof(MapSurface).GetMethod("OnGpuFrame", Hidden)!.CreateDelegate<EventHandler>(map);
            WM.CompositionTarget.Rendering -= handler;
        }
    }
}
