using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using San14Tenkazu;
using San14Tenkazu.Tests;
using W = System.Windows;
using WM = System.Windows.Media;

internal static partial class RenderChecks
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Contains("--unified-drafts")) { CheckUnifiedDrafts(args.First(a => !a.StartsWith("--"))); return 0; }
            if (args.Contains("--table-drafts")) { CheckTableDrafts(args.First(a => !a.StartsWith("--"))); return 0; }
            if(args.Contains("--runtime-rule-worker")){RunRuntimeRuleWorker();return 0;}
            if(args.Contains("--public-loader-offline")){CheckPublicLibraryLoader(args.First(a=>!a.StartsWith("--")));return 0;}
            if(args.Contains("--runtime-trait-list-export")){ExportRuntimeTraitList(args);return 0;}
            if(args.Contains("--runtime-rules-offline")){CheckRuntimeRulesOffline(args.First(a=>!a.StartsWith("--")));return 0;}
            if(args.Contains("--runtime-rule-persistence")){CheckRuntimeRulePersistence(args.First(a=>!a.StartsWith("--")));return 0;}
            if(args.Contains("--developer-menu-check")){CheckDeveloperMenu(bool.Parse(args.First(a=>!a.StartsWith("--"))));return 0;}
            if (args.Contains("--column-editing")) { CheckColumnEditing(args.First(a => !a.StartsWith("--"))); return 0; }
            if(args.Contains("--environment-menu")){CheckEnvironmentMenu(args.First(a=>!a.StartsWith("--")));return 0;}
            if(args.Contains("--environment-overlay")){CheckEnvironmentOverlay(args.First(a=>!a.StartsWith("--")));return 0;}
            if(args.Contains("--environment-vectors")){CheckEnvironmentVectors(args.First(a=>!a.StartsWith("--")));return 0;}
            if(args.Contains("--environment-live")){CheckEnvironmentLive(args.First(a=>!a.StartsWith("--")));return 0;}
            if(args.Contains("--environment-notification")){CheckEnvironmentNotification(args.First(a=>!a.StartsWith("--")));return 0;}
            if(args.Contains("--environment-notification-art")){CheckEnvironmentNotificationArt(args.First(a=>!a.StartsWith("--")),args.Contains("--notification-original-only"),args.Contains("--notification-wait-ready"));return 0;}
            if(args.Contains("--environment-art")){ExportEnvironmentArt(args.First(a=>!a.StartsWith("--")));return 0;}
            if(args.Contains("--environment-art-assets")){PrepareEnvironmentArt(args.First(a=>!a.StartsWith("--")));return 0;}
            if(args.Contains("--environment-bundle-check")){San14Tenkazu.EnvironmentRules.EnvironmentNativeBundle.VerifySources(args.First(a=>!a.StartsWith("--")));Console.WriteLine("PASS environment bundle/source pins");return 0;}
            if (args.Contains("--steam-discovery")) { CheckSteamDiscovery(args.First(a => !a.StartsWith("--"))); return 0; }
            if (args.Contains("--package-smoke")) { CheckPortablePackage(args.First(a => !a.StartsWith("--"))); return 0; }
            if (args.Contains("--unit-conditions")) { CheckUnitConditions(args.First(a => !a.StartsWith("--"))); return 0; }
            if (args.Contains("--officer-merit")) { CheckOfficerMerit(args.First(a => !a.StartsWith("--"))); return 0; }
            if (args.Contains("--officer-revival")) { CheckOfficerRevival(args.First(a => !a.StartsWith("--"))); return 0; }
            if (args.Contains("--officer-status")) { CheckOfficerStatusChanges(args.First(a => !a.StartsWith("--"))); return 0; }
            if (args.Contains("--force-policies")) { CheckForcePolicies(args.First(a => !a.StartsWith("--"))); return 0; }
            if (args.Contains("--pending-inputs")) { CheckPendingInputs(args.First(a => !a.StartsWith("--"))); return 0; }
            if (args.Contains("--pending-cards-game-read")) { ReadPendingCardsGame(args.First(a => !a.StartsWith("--"))); return 0; }
            if (args.Contains("--unit-conditions-game-read")) { ReadUnitConditionsGame(args.First(a => !a.StartsWith("--"))); return 0; }
            if (args.Contains("--tooltips")) { CheckTooltips(args.FirstOrDefault(a => !a.StartsWith("--")) ?? "source/RenderChecks/artifacts/tooltips"); return 0; }
            if (args.Contains("--choices")) { CheckChoices(args.FirstOrDefault(a => !a.StartsWith("--")) ?? "source/RenderChecks/artifacts/choices"); return 0; }
            if (args.Contains("--calendar")) { CheckCalendar(args.First(a => !a.StartsWith("--"))); return 0; }
            if (args.Contains("--calendar-game-read")) { ReadCalendarGame(args.First(a => !a.StartsWith("--"))); return 0; }
            if(args.Contains("--season-calendar-vectors"))
            {
                string calendarOutput=args.FirstOrDefault(a=>a.StartsWith("--output="))?[9..]??throw new ArgumentException("New output file required");
                using var writer=new System.IO.StreamWriter(new System.IO.FileStream(calendarOutput,System.IO.FileMode.CreateNew));
                int count=0;
                foreach(int year in new[]{195,196})for(int month=1;month<=12;month++)for(int day=1;day<=30;day++)
                {
                    if(!San14Tenkazu.Seasons.SeasonDate.TryCreate(year,month,day,out var date))throw new InvalidOperationException("Calendar fixture rejected");
                    var w=San14Tenkazu.Seasons.SeasonTimeline.At(date).Weights;
                    writer.WriteLine(FormattableString.Invariant($"{year} {month} {day} {w.Spring:R} {w.Summer:R} {w.Autumn:R} {w.Winter:R}"));++count;
                }
                Console.WriteLine($"Exported {count} managed calendar vectors without game access");return 0;
            }
            if(args.Contains("--season-game-scene-data"))
            {
                string directory=args.FirstOrDefault(a=>a.StartsWith("--game-directory="))?[17..]??throw new ArgumentException("Game directory required");
                string sceneOutput=args.FirstOrDefault(a=>a.StartsWith("--output="))?[9..]??throw new ArgumentException("New output file required");
                if(System.IO.File.Exists(sceneOutput))throw new System.IO.IOException("Scene output already exists");
                byte[] bytes=GameSeasonScene.ReadDirectory(directory);
                string? captureOutput=args.FirstOrDefault(a=>a.StartsWith("--capture-output="))?[17..];
                if(captureOutput is not null)bytes=San14Tenkazu.Seasons.SeasonSceneData.WithWeatherCapture(bytes,captureOutput);
                System.IO.File.WriteAllBytes(sceneOutput,bytes);
                Console.WriteLine(JsonSerializer.Serialize(new{gameAccess=false,bytes=bytes.Length,sha256=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant()}));return 0;
            }
            if(args.Contains("--season-game-ground-assets"))
            {InspectSeasonGroundAssets(args);return 0;}
            if(args.Contains("--season-game-tree-masks"))
            {PrepareSeasonGameMasks(args);return 0;}
            if(args.Contains("--season-bundle-check"))
            {San14Tenkazu.Seasons.SeasonNativeBundle.VerifySources(args.FirstOrDefault(a=>!a.StartsWith("--"))??"source/Seasons");Console.WriteLine("Native bundle and checkout sources verified");return 0;}
            if(args.Contains("--season-source-check"))
            {
                string directory=args.FirstOrDefault(a=>a.StartsWith("--game-directory="))?[17..]??System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),"Steam","steamapps","common","Romance_of_the_Three_Kingdoms_14");
                byte[] bytes=GameSeasonSources.ReadDirectory(directory);
                Console.WriteLine(JsonSerializer.Serialize(new{gameAccess=false,assets=San14Tenkazu.Seasons.SeasonTreeSources.Assets.Length,bytes=bytes.Length,
                    sha256=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant(),verified=true}));return 0;
            }
            if(args.Contains("--season-editor")||args.Contains("--season-editor-live")||args.Contains("--season-editor-date-live"))
            {CheckSeasonEditor(args.FirstOrDefault(a=>!a.StartsWith("--"))??"source/RenderChecks/artifacts/season-editor",
                args.Contains("--season-editor-live")||args.Contains("--season-editor-date-live"),args.Contains("--season-editor-date-live")?180:0);return 0;}
            if(args.Contains("--season-native-connection")){CheckSeasonNativeConnection(args);return 0;}
            if(args.Contains("--season-native-scene")){CheckSeasonNativeScene(args);return 0;}
            if(args.Contains("--season-native-tree-color")){CheckSeasonNativeTreeColor(args);return 0;}
            if(args.Contains("--season-native-tree-recovery")){CheckSeasonNativeTreeRecovery(args);return 0;}
            if(args.Contains("--season-native-tree-date")){CheckSeasonNativeTreeDate(args);return 0;}
            if (args.Contains("--ai-extensions") || args.Contains("--ai-extensions-native"))
            { CheckAiExtensions(args.FirstOrDefault(a => !a.StartsWith("--")) ?? "source/RenderChecks/artifacts/ai-extensions", args.Contains("--ai-extensions-native")); return 0; }
            if (args.Contains("--alliances")) { CheckAlliances(args.FirstOrDefault(a => !a.StartsWith("--")) ?? "source/RenderChecks/artifacts/alliances"); return 0; }
            if (args.Contains("--independence")) { CheckIndependence(args.FirstOrDefault(a => !a.StartsWith("--")) ?? "source/RenderChecks/artifacts/independence"); return 0; }
            if (args.Contains("--season-assets")) { PrepareSeasonAssets(args.FirstOrDefault(a=>!a.StartsWith("--")) ?? "source/Seasons",args.Contains("--terrain-only"),args.FirstOrDefault(a=>a.StartsWith("--game-directory="))?[17..]); return 0; }
            if (args.Contains("--season-climate")) { PrepareSeasonClimate(args.FirstOrDefault(a=>!a.StartsWith("--")) ?? "source/Seasons"); return 0; }
            if (args.Contains("--season-trees")) { PrepareSeasonTrees(args.FirstOrDefault(a=>!a.StartsWith("--")) ?? "source/Seasons",args.FirstOrDefault(a=>a.StartsWith("--game-directory="))?[17..]); return 0; }
            if (args.Contains("--season-textures")) { CheckSeasonTextures(args.FirstOrDefault(a=>!a.StartsWith("--")) ?? "source/Seasons"); return 0; }
            if (args.Any(a=>a.StartsWith("--season-",StringComparison.Ordinal))) throw new ArgumentException("Unknown season preparation command");
            if (args.Contains("--performance")) { CheckPerformance(args.FirstOrDefault(a => !a.StartsWith("--")) ?? "source/RenderChecks/artifacts/performance"); return 0; }
            if (args.Contains("--window-move")) { CheckWindowMove(args.FirstOrDefault(a => !a.StartsWith("--")) ?? "source/RenderChecks/artifacts/window-move", args.Contains("--expect-responsive")); return 0; }
            if (args.Contains("--unit-interpolation")) { CheckUnitInterpolation(args.FirstOrDefault(a => !a.StartsWith("--")) ?? "source/RenderChecks/artifacts/unit-interpolation"); return 0; }
            if (args.Contains("--motion")) { CheckMotion(args.FirstOrDefault(a => !a.StartsWith("--")) ?? "source/RenderChecks/artifacts/motion", args.Contains("--quick")); return 0; }
            if (args.Contains("--focus")) { CheckFocus(args.FirstOrDefault(a => !a.StartsWith("--")) ?? "source/RenderChecks/artifacts/focus"); return 0; }
            if (args.Contains("--coast")) { CheckCoast(); return 0; }
            if (args.Contains("--swipe")) { CheckDrag(args.FirstOrDefault(a => !a.StartsWith("--")) ?? "source/RenderChecks/artifacts/swipe", true); return 0; }
            if (args.Contains("--drag")) { CheckDrag(args.FirstOrDefault(a => !a.StartsWith("--")) ?? "source/RenderChecks/artifacts/drag", liveScene: args.Contains("--live-scene")); return 0; }
            if (args.Contains("--zoom")) { CheckDrag(args.FirstOrDefault(a => !a.StartsWith("--")) ?? "source/RenderChecks/artifacts/zoom", zoomReplay: true, liveScene: args.Contains("--live-scene"), capture: args.Contains("--capture")); return 0; }
            if (args.Contains("--panel-layout")) { CheckPanelLayout(args.FirstOrDefault(a => !a.StartsWith("--")) ?? "source/RenderChecks/artifacts/panel-layout", args.Contains("--expect-fluid")); return 0; }
            if (args.Contains("--navigation")) { CheckNavigation(args.FirstOrDefault(a => !a.StartsWith("--"))); return 0; }
            if (args.Contains("--map-camera-live"))
            {
                string tile = args.FirstOrDefault(a => a.StartsWith("--tile="))?[7..] ?? throw new ArgumentException("Explicit --tile=<hex> required");
                CheckMapCameraLive(args.FirstOrDefault(a => !a.StartsWith("--")) ?? throw new ArgumentException("New evidence directory required"), int.Parse(tile));
                return 0;
            }
            if (args.Contains("--map-display")) { CheckMapDisplay(); return 0; }
            if (args.Contains("--gpu-lifecycle")) { CheckGpuLifecycle(); return 0; }
            if (args.Contains("--memory-resources")) { CheckMemoryResources(); return 0; }
            if (args.Contains("--panes")) { CheckPanes(); return 0; }
            if (args.Contains("--pane-history")) { CheckPaneHistory(args.First(a => !a.StartsWith("--"))); return 0; }
            if (args.Contains("--rule-game-read")) { ReadRuleGame(args.First(a => !a.StartsWith("--"))); return 0; }
            if (args.Contains("--rules")) { CheckRulePane(args.First(a => !a.StartsWith("--"))); return 0; }
            if (args.Contains("--policy-rules")) { CheckPolicyRules(args.First(a => !a.StartsWith("--"))); return 0; }
            if (args.Contains("--doctrine-rules")) { CheckDoctrineRules(args.First(a => !a.StartsWith("--"))); return 0; }
            if (args.Contains("--rank-rules")) { CheckRankRules(args.First(a => !a.StartsWith("--"))); return 0; }
            if (args.Contains("--office-rules")) { CheckOfficeRules(args.First(a => !a.StartsWith("--"))); return 0; }
            if (args.Contains("--facilities")) { CheckFacilities(args.FirstOrDefault(a => !a.StartsWith("--"))); return 0; }
            if (args.Contains("--military-points")) { CheckMilitaryPoints(args.First(a => !a.StartsWith("--"))); return 0; }
            if (args.Contains("--military-game-read")) { ReadMilitaryGame(args.First(a => !a.StartsWith("--"))); return 0; }
            if (args.Contains("--registered"))
            {
                int original = Array.IndexOf(args, "--original");
                CheckRegistered(args.FirstOrDefault(a => !a.StartsWith("--")), original >= 0 ? args[original + 1] : null);
                return 0;
            }
            if (args.Contains("--assets")) { CheckAssetPreparation(args.Contains("--assets-all")); return 0; }
            if (args.Contains("--tints")) { CheckGpuTints(); return 0; }
            if (args.Contains("--shadows")) { CheckGpuShadows(); return 0; }
            bool benchmarkOnly = args.Contains("--benchmark-only");
            if (!benchmarkOnly) { CheckSpatialIndexes(); CheckGpuFrames(); }
            var map = new MapSurface { Width = 1280, Height = 800, ProfileUnits = true };
            map.Measure(new W.Size(1280, 800));
            map.Arrange(new W.Rect(0, 0, 1280, 800));
            var game = new TestGame().Force(1, 1, 1);
            for (int i = 1; i <= 180; i++) game.Officer(i, "장수" + i, 1, 1, 3).Leading(i);
            GameSnapshot made = game.Build();
            var data = new GameSnapshot { Cities = made.Cities, Counties = made.Counties, Officers = made.Officers,
                CastleByCity = made.CastleByCity, TileOwners = new byte[GameMemory.TileCount], TileCounties = new ushort[GameMemory.TileCount],
                Forces = made.Forces, ForceRecords = made.ForceRecords, CorpsForce = made.CorpsForce, OfficerById = made.OfficerById, Units = made.Units };
            for (int i = 0; i < data.Units.Length; i++)
            {
                // A fixed dense scene: clustered units, partially filled troop rings and every city banner.
                UnitRecord unit = data.Units[i];
                System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(unit.Data.AsSpan(UnitFields.TileOffset), (ushort)(220 * (104 + i / 30 * 2) + 80 + (i % 30) * 3));
                UnitFields.Troops.Encode(3000 + i * 73).CopyTo(unit.Data, UnitFields.Troops.Offset);
            }
            map.SetSnapshot(data);
            if (!benchmarkOnly) CheckFrameReplay(data);
            using GpuMap gpu = GpuMap.TryCreate() ?? throw new InvalidOperationException("GPU renderer unavailable");
            gpu.Resize(1280, 800, 96);
            if (args.Contains("--profile"))
            {
                MethodInfo portrait = typeof(MapSurface).GetMethod("DrawPortraitUnit", Hidden)!;
                MethodInfo ring = typeof(MapSurface).GetMethod("DrawTroopRing", Hidden)!;
                foreach (bool onlyRing in new[] { true, false, false })
                {
                    long bytes = GC.GetAllocatedBytesForCurrentThread();
                    gpu.Render(canvas =>
                    {
                        for (int i = 0; i < 180; i++)
                        {
                            if (onlyRing) ring.Invoke(map, [canvas, System.Drawing.Color.FromArgb(90, 150, 210), 7007L, new W.Point(100, 100), 20.0]);
                            else portrait.Invoke(map, [canvas, data, data.Units[i], new W.Point(100, 100), 20.0, 1.0]);
                        }
                    });
                    Console.WriteLine($"Profile {(onlyRing ? "rings" : "portraits")}: {GC.GetAllocatedBytesForCurrentThread() - bytes} bytes");
                    Finish(gpu);
                }
                WM.FormattedText text = (WM.FormattedText)typeof(MapSurface).GetMethod("Mark", Hidden)!.Invoke(map, ["장", 21.0, WM.Brushes.White, 1.0, null])!;
                foreach (bool onlyText in new[] { false, true })
                {
                    long bytes = GC.GetAllocatedBytesForCurrentThread();
                    gpu.Render(canvas =>
                    {
                        for (int i = 0; i < 1000; i++)
                        {
                            if (onlyText) canvas.DrawText(text, new W.Point(100, 100));
                            else canvas.DrawEllipse(WM.Brushes.Gray, new WM.Pen(WM.Brushes.White, 2), new W.Point(100, 100), 20, 20);
                        }
                    });
                    Console.WriteLine($"Profile {(onlyText ? "text" : "ellipse")}: {GC.GetAllocatedBytesForCurrentThread() - bytes} bytes");
                    Finish(gpu);
                }
            }
            MethodInfo drawCities = typeof(MapSurface).GetMethod("DrawCities", Hidden)!;
            MethodInfo drawUnits = typeof(MapSurface).GetMethod("DrawUnits", Hidden)!;
            FieldInfo zoom = typeof(MapSurface).GetField("zoom", Hidden)!;
            var times = new List<double>();
            var cpuTimes = new List<double>();
            var completionTimes = new List<double>();
            long cityBytes = 0, unitBytes = 0;
            double unitLayout = 0, unitTokens = 0, unitShields = 0;
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            int submittedBefore = gpu.SubmittedFrames, presentedBefore = gpu.PresentedFrames;
            for (int frame = 0; frame < 180; frame++)
            {
                zoom.SetValue(map, 1 + 3.0 * frame / 179);
                var watch = Stopwatch.StartNew();
                if (!gpu.Render(canvas =>
                {
                    canvas.Clear(WM.Colors.Black);
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    drawCities.Invoke(map, [canvas]);
                    cityBytes += GC.GetAllocatedBytesForCurrentThread() - before;
                    before = GC.GetAllocatedBytesForCurrentThread();
                    drawUnits.Invoke(map, [canvas]);
                    unitBytes += GC.GetAllocatedBytesForCurrentThread() - before;
                    unitLayout += map.UnitProfile.Layout; unitTokens += map.UnitProfile.Tokens; unitShields += map.UnitProfile.Shields;
                })) throw new InvalidOperationException("Frame was not submitted");
                cpuTimes.Add(watch.Elapsed.TotalMilliseconds);
                Finish(gpu);
                completionTimes.Add(gpu.LastPresentDelay);
                times.Add(watch.Elapsed.TotalMilliseconds);
                if (gpu.Lost) throw new InvalidOperationException("GPU device lost");
            }
            allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
            var cache = (System.Collections.IDictionary)typeof(D2DCanvas).GetField("geometries", Hidden)!.GetValue(gpu.Canvas)!;
            times.Sort();
            string result = JsonSerializer.Serialize(new { Scenario = "dense-overlay-zoom", Frames = times.Count,
                MeanMs = times.Average(), P95Ms = times[(int)(times.Count * 0.95)], AllocatedBytes = allocated,
                UnitLayoutMeanMs = unitLayout / 180, UnitTokensMeanMs = unitTokens / 180, UnitShieldsMeanMs = unitShields / 180, CpuMeanMs = cpuTimes.Average(), CompletionMeanMs = completionTimes.Average(), CityBytes = cityBytes, UnitBytes = unitBytes,
                GeometryCacheEntries = cache.Count, SubmittedFrames = gpu.SubmittedFrames - submittedBefore, PresentedFrames = gpu.PresentedFrames - presentedBefore, gpu.Lost }, new JsonSerializerOptions { WriteIndented = true });
            Console.WriteLine($"CPU mean {cpuTimes.Average():F3} ms; completion mean {completionTimes.Average():F3} ms; submitted {gpu.SubmittedFrames}; presented {gpu.PresentedFrames}");
            Console.WriteLine(result);
            string? output = args.FirstOrDefault(arg => !arg.StartsWith("--", StringComparison.Ordinal));
            if (output is not null)
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(output))!);
                System.IO.File.WriteAllText(output, result);
            }
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static void Finish(GpuMap gpu)
    {
        var watch = Stopwatch.StartNew();
        while (gpu.Pending && !gpu.Lost)
        {
            gpu.Present();
            if (watch.Elapsed.TotalSeconds > 5) throw new TimeoutException("GPU frame was not presented");
            if (gpu.Pending) Thread.Yield();
        }
        if (gpu.Lost) throw new InvalidOperationException("GPU device lost");
    }
}
