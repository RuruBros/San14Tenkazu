using System.Text.Json;
using San14Tenkazu;
using WM = System.Windows.Media;

internal static partial class RenderChecks
{
    private static void CheckGpuLifecycle()
    {
        using GpuMap gpu = GpuMap.TryCreate() ?? throw new InvalidOperationException("GPU renderer unavailable");
        gpu.Resize(128, 128, 96);
        Require(gpu.Render(canvas => canvas.Clear(WM.Colors.Red)), "Initial frame was not submitted");
        Finish(gpu);

        // The real WPF compositor's write event, held unavailable to exercise TryLock's timeout path.
        var available = (ManualResetEvent)typeof(System.Windows.Interop.D3DImage).GetField("_canWriteEvent", Hidden)!.GetValue(gpu.Image)!;
        var locks = typeof(System.Windows.Interop.D3DImage).GetField("_lockCount", Hidden)!;
        int submitted = gpu.SubmittedFrames;
        available.Reset();
        try
        {
            Require(gpu.Render(canvas => canvas.Clear(WM.Colors.Blue)) && gpu.Pending,
                "Busy compositor prevented rendering into the private target");
            for (int i = 0; i < 5; i++)
            {
                Thread.Sleep(5);
                Require(!gpu.Present() && gpu.Pending,
                    "Busy compositor allowed publication into its shared surface");
                Require((uint)locks.GetValue(gpu.Image)! == 0, "Timed-out TryLock leaked a WPF image lock");
            }
            Require(gpu.SubmittedFrames == submitted + 1, "Private frame was not rendered once");
            byte[] retained = ReadPixels(gpu, 128, 128);
            Require(retained[0] == 0 && retained[2] == 255, "Busy compositor changed the completed frame");
        }
        finally { available.Set(); }
        Require((uint)locks.GetValue(gpu.Image)! == 0 && gpu.Pending, "Private GPU write retained a WPF image lock");
        Finish(gpu);
        Require((uint)locks.GetValue(gpu.Image)! == 0, "Completed frame retained an image lock");
        byte[] blue = ReadPixels(gpu, 128, 128);
        Require(blue[0] == 255 && blue[2] == 0, "Pending frame did not recover after the compositor unlocked");

        gpu.Resize(196, 80, 120);
        Require(gpu.Image.PixelWidth == 128 && gpu.Image.PixelHeight == 128, "Resize detached the completed frame");
        gpu.Bind();
        Require(gpu.Image.PixelWidth == 128, "Front-buffer recovery bound an unfinished resized surface");
        Require(gpu.Render(canvas => canvas.Clear(WM.Colors.Lime)), "Resized frame was not submitted");
        Finish(gpu);
        Require(gpu.Image.PixelWidth == 196 && gpu.Image.PixelHeight == 80, "Resized frame was not published");
        int bindings = gpu.BackBufferBindings;
        Require(gpu.Render(canvas => canvas.Clear(WM.Colors.Yellow)), "Stable shared surface could not be reused");
        byte[] stillLime = ReadPixels(gpu, 196, 80);
        Require(stillLime[0] == 0 && stillLime[1] == 255 && stillLime[2] == 0,
            "In-flight private frame changed the previous complete shared surface");
        Finish(gpu);
        Require(gpu.BackBufferBindings == bindings, "Normal frame recreated the WPF interop resource");
        byte[] yellow = ReadPixels(gpu, 196, 80);
        Require(yellow[1] == 255 && yellow[2] == 255, "Second resized frame has wrong pixels");
        Require(gpu.Render(canvas => canvas.Clear(WM.Colors.Red)), "Frame before repeated resize was not submitted");
        gpu.Resize(64, 96, 96);
        gpu.Resize(96, 64, 144);
        Require(gpu.Pending && gpu.Image.PixelWidth == 196, "Repeated resize lost the protected frame");
        Finish(gpu);
        Require(gpu.Image.PixelWidth == 196, "Repeated resize bound an unfinished surface");
        Require(gpu.Render(canvas => canvas.Clear(WM.Colors.Blue)), "Frame after repeated resize was not submitted");
        Finish(gpu);
        Require(gpu.Image.PixelWidth == 96 && gpu.Image.PixelHeight == 64 && !gpu.Lost, "Repeated resize did not recover");
        Require((uint)locks.GetValue(gpu.Image)! == 0, "Repeated resize leaked an image lock");
        Require(gpu.BackBufferBindings == 3, "Shared surface was rebound without a size change");
        Console.WriteLine(JsonSerializer.Serialize(new { SkippedRenderLocks = gpu.SkippedLocks, LeakedLocks = locks.GetValue(gpu.Image),
            gpu.BackBufferBindings, ProtectedFrameRecovered = true, PreviousFrameRetained = true, DpiAndRepeatedResizePassed = true,
            gpu.LastCopyWait }));
    }
}
