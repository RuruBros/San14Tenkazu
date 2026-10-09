using System.Buffers.Binary;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Text.Json;
using San14Tenkazu;
using San14Tenkazu.Tests;
using W = System.Windows;
using WM = System.Windows.Media;

internal static partial class RenderChecks
{
    // Replay the production snapshot/position code with recorded timestamps, without a game or window.
    private static void CheckUnitInterpolation(string output)
    {
        Directory.CreateDirectory(output);
        string? previous = Environment.GetEnvironmentVariable("SAN14_NO_GPU");
        MapSurface map;
        try { Environment.SetEnvironmentVariable("SAN14_NO_GPU", "1"); map = new MapSurface(); }
        finally { Environment.SetEnvironmentVariable("SAN14_NO_GPU", previous); }
        var made = new TestGame().Force(1, 1, 1).Officer(1, "장수", 1, 1, 4).Leading(1).Build();
        UnitRecord original = made.Units[0];
        int tile = MapTerrain.Columns * (MapTerrain.Rows / 2) + MapTerrain.Columns / 2;
        var snapshotField = typeof(MapSurface).GetField("snapshot", Hidden)!;
        var glidesField = typeof(MapSurface).GetField("glides", Hidden)!;
        var update = typeof(MapSurface).GetMethod("UpdateGlides", Hidden)!;
        var position = typeof(MapSurface).GetMethod("Position", BindingFlags.Static | BindingFlags.NonPublic)!;
        var any = typeof(MapSurface).GetMethod("AnyGliding", Hidden)!;
        using var samples = new StreamWriter(Path.Combine(output, "positions.jsonl"));
        int frames = 0, checks = 0;

        void Read(int? at, double time, int commander = 1)
        {
            GameSnapshot? data = null;
            if (at is { } current)
            {
                UnitRecord unit = original with { Data = (byte[])original.Data.Clone() };
                BinaryPrimitives.WriteUInt16LittleEndian(unit.Data.AsSpan(UnitFields.TileOffset), (ushort)current);
                BinaryPrimitives.WriteUInt16LittleEndian(unit.Data.AsSpan(UnitFields.CommanderOffset), (ushort)commander);
                data = new GameSnapshot { Cities = made.Cities, Counties = made.Counties, Officers = made.Officers,
                    CastleByCity = made.CastleByCity, TileOwners = made.TileOwners, TileCounties = made.TileCounties,
                    Forces = made.Forces, CorpsForce = made.CorpsForce, OfficerById = made.OfficerById, Units = [unit] };
            }
            snapshotField.SetValue(map, data);
            update.Invoke(map, [time]);
        }
        W.Point World(int at)
        {
            var point = MapTerrain.TileCenter(at);
            return new W.Point(point.X, point.Y);
        }
        W.Point Point(double time, int commander = 1)
        {
            var glides = (IDictionary)glidesField.GetValue(map)!;
            object glide = glides[(original.Slot, commander)] ?? throw new InvalidOperationException("Unit glide missing");
            return (W.Point)position.Invoke(null, [glide, time])!;
        }
        bool Moving(double time) => (bool)any.Invoke(map, [time])!;
        void Equal(W.Point actual, W.Point expected, string message)
        {
            Require((actual - expected).Length < 1e-9, message);
            checks++;
        }
        void Record(string phase, double time, W.Point point)
        {
            samples.WriteLine(JsonSerializer.Serialize(new { phase, time, point.X, point.Y, moving = Moving(time) }));
            frames++;
        }
        try
        {
            Read(tile, 0);
            Equal(Point(0), World(tile), "A newly observed unit moved from an invented position");
            Require(!Moving(0), "A new unit started an empty animation");
            // Same two-second reads as the editor: every frame must advance right up to the next read.
            for (int step = 1; step <= 4; step++)
            {
                double start = step * 2;
                W.Point before = Point(start);
                Read(tile + step, start);
                Equal(Point(start), before, "A new read jumped the displayed position");
                W.Point from = World(tile + step - 1), to = World(tile + step);
                double expectedStep = (to - from).Length / 120;
                W.Point last = Point(start);
                for (int frame = 1; frame <= 120; frame++)
                {
                    double time = start + frame / 60.0;
                    W.Point current = Point(time);
                    Require(Math.Abs((current - last).Length - expectedStep) < 1e-9,
                        "Two-second polling caused an idle frame or repeated braking");
                    Equal(current, from + (to - from) * (frame / 120.0), "The displayed move did not follow elapsed time");
                    Record("two-second-reads", time, current);
                    last = current;
                }
                Equal(last, to, "A unit failed to reach its observed tile at the next read");
            }
            // A delayed composition callback must cover elapsed time without changing the trajectory.
            Read(null, 10);
            Read(tile, 20);
            Read(tile + 1, 22.5);
            foreach (double elapsed in new[] { .016, .033, .173, .557, 1.923, 2.499, 2.5, 6.0 })
            {
                W.Point actual = Point(22.5 + elapsed);
                Equal(actual, World(tile) + (World(tile + 1) - World(tile)) * Math.Min(elapsed / 2.5, 1),
                    "Irregular frames changed the position or extrapolated beyond observed data");
                Record("delayed-frames", 22.5 + elapsed, actual);
            }
            // An earlier next read retargets the unfinished move from its actual displayed position.
            Read(null, 30);
            Read(tile, 40);
            Read(tile + 1, 42);
            W.Point midway = Point(43.5);
            Read(tile + 2, 43.5);
            Equal(Point(43.5), midway, "An early read snapped to the previous tile");
            Equal(Point(44.25), midway + (World(tile + 2) - midway) * .5, "Retargeting discarded the measured read interval");
            Read(tile + 2, 44);
            Equal(Point(44.25), midway + (World(tile + 2) - midway) * .5, "An unchanged read restarted the move");
            Equal(Point(45), World(tile + 2), "An unchanged read postponed arrival");
            Read(tile + 2, 46);
            Equal(Point(100), World(tile + 2), "A stopped unit continued moving without new data");
            Require(!Moving(46), "A stopped unit kept the animation active");
            Record("stopped", 46, Point(46));
            // Reused slots, long jumps, invalid tiles and reconnects must not invent a journey.
            Read(tile + 4, 48, commander: 2);
            Equal(Point(48, 2), World(tile + 4), "A reused slot animated the previous commander's movement");
            int distant = tile + MapTerrain.Columns * 40;
            Read(distant, 50, commander: 2);
            Equal(Point(50, 2), World(distant), "A long jump was animated across the map");
            Require(!Moving(50), "A long jump left an active animation");
            Read(GameMemory.TileCount, 52);
            Require(((IDictionary)glidesField.GetValue(map)!).Count == 0, "An invalid tile retained a visible unit");
            Read(null, 54);
            Read(tile, 200);
            Equal(Point(200), World(tile), "Reconnect retained an old position");
            Read(tile + 1, 202);
            Equal(Point(203), World(tile) + (World(tile + 1) - World(tile)) * .5, "Reconnect retained the disconnected read interval");
            Read(tile + 2, 202);
            Equal(Point(202), World(tile + 2), "A zero-time read produced a non-finite position");
            Require(!Moving(202), "A zero-time read left an active animation");
            var summary = new { frames, checks, readIntervalSeconds = 2, idleFramesDuringMarch = 0,
                stopped = true, retargetContinuous = true, extrapolated = false, passed = true };
            File.WriteAllText(Path.Combine(output, "summary.json"), JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine(JsonSerializer.Serialize(summary));
        }
        finally
        {
            var glide = typeof(MapSurface).GetMethod("OnGlide", Hidden)!.CreateDelegate<EventHandler>(map);
            WM.CompositionTarget.Rendering -= glide;
        }
    }
}
