using Godot;
using System;

namespace NeuroViz.Visualizer.Scripts;

public abstract partial class VisualizerBase2D : Node2D
{
    protected IpcClient Ipc => IpcBootstrapHolder.Client;
    private bool _previewFirstCaptureLogged;

    public override void _Ready()
    {
        _ReadyVisualizer();
    }

    public override void _Process(double delta)
    {
        Ipc.DrainMainThreadActions();

        if (Ipc.PendingScenePath is { } pendingPath)
        {
            Ipc.ClearPendingScene();
            var current = GetTree().CurrentScene?.SceneFilePath;
            if (!string.Equals(current, pendingPath, StringComparison.OrdinalIgnoreCase))
            {
                GetTree().ChangeSceneToFile(pendingPath);
                return;
            }
        }

        _ProcessVisualizer(delta);

        double now = Time.GetTicksMsec() / 1000.0;
        if (Ipc.ShouldCapturePreview(now))
            CapturePreviewAfterDraw();
    }

    protected abstract void _ReadyVisualizer();
    protected abstract void _ProcessVisualizer(double delta);

    private async void CapturePreviewAfterDraw()
    {
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);

        if (!IsInsideTree() || !Ipc.IsPreviewConnected) return;

        var img = GetViewport().GetTexture().GetImage();
        if (img == null || img.IsEmpty()) return;

        if (!_previewFirstCaptureLogged)
        {
            _previewFirstCaptureLogged = true;
            int nonBlack = 0;
            for (int y = 0; y < img.GetHeight(); y += 32)
                for (int x = 0; x < img.GetWidth(); x += 32)
                {
                    var c = img.GetPixel(x, y);
                    if (c.R > 0.02f || c.G > 0.02f || c.B > 0.02f) nonBlack++;
                }
            GD.Print($"[Preview] First capture {img.GetWidth()}x{img.GetHeight()}, non-black: {nonBlack}");
        }

        img.Resize(320, 180, Image.Interpolation.Bilinear);
        Ipc.QueuePreview(img);
        img.Dispose();
    }

    public override void _ExitTree()
    {
        Ipc?.Dispose();
    }
}