using NeuroViz.Core.Events;
using NeuroViz.Core.Rules;
using NeuroViz.Project;
using NeuroViz.Tests.Audio;

namespace NeuroViz.Tests;

public class ProjectLoaderTests
{
    // ─── Load ────────────────────────────────────────────────────────

    [Fact]
    public async Task Load_ValidProject_ReturnsDocument()
    {
        using var tmp = new TempDir();
        var audio = Path.Combine(tmp.Path, "a.wav");
        ToneWavWriter.WriteSine(audio, seconds: 0.2);
        var projectPath = await SaveProjectAsync(tmp.Path, audio);

        var loaded = await new ProjectLoader().LoadAsync(projectPath);

        Assert.Equal("t", loaded.Document.Name);
        Assert.Single(loaded.Document.Timeline.Events);
        Assert.Equal(audio, loaded.AudioPath);
        Assert.Equal(AudioIntegrity.Ok, loaded.Integrity);
    }

    [Fact]
    public async Task Load_MissingFile_ThrowsProjectNotFound()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nv-nope-{Guid.NewGuid():N}.nvproj");
        await Assert.ThrowsAsync<ProjectNotFoundException>(
            () => new ProjectLoader().LoadAsync(path));
    }

    [Fact]
    public async Task Load_CorruptFile_ThrowsProjectCorrupt()
    {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Path, "broken.nvproj");
        await File.WriteAllBytesAsync(path, new byte[] { 1, 2, 3, 4, 5 });

        await Assert.ThrowsAsync<ProjectCorruptException>(
            () => new ProjectLoader().LoadAsync(path));
    }

    [Fact]
    public async Task Load_NewerVersion_ThrowsVersionTooNew()
    {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Path, "future.nvproj");
        var doc = new ProjectDocument
        {
            ProjectVersion = ProjectDocument.CurrentVersion + 1,
            Name = "future",
        };
        ProjectSerializer.Save(doc, path);

        var ex = await Assert.ThrowsAsync<ProjectVersionTooNewException>(
            () => new ProjectLoader().LoadAsync(path));
        Assert.Equal(ProjectDocument.CurrentVersion + 1, ex.FileVersion);
        Assert.Equal(ProjectDocument.CurrentVersion, ex.SupportedVersion);
    }

    // ─── Audio path resolution ──────────────────────────────────────

    [Fact]
    public async Task ResolveAudio_AbsolutePath_FindsFile()
    {
        using var tmp = new TempDir();
        var audio = Path.Combine(tmp.Path, "a.wav");
        ToneWavWriter.WriteSine(audio, seconds: 0.2);
        var projectPath = await SaveProjectAsync(tmp.Path, audio);

        var loaded = await new ProjectLoader().LoadAsync(projectPath);
        Assert.NotNull(loaded.AudioPath);
        Assert.Equal(audio, loaded.AudioPath);
    }

    [Fact]
    public async Task ResolveAudio_FilenameNearProject_FindsFile()
    {
        using var tmp = new TempDir();
        var audio = Path.Combine(tmp.Path, "a.wav");
        ToneWavWriter.WriteSine(audio, seconds: 0.2);

        // В проекте — заведомо неверный абсолютный путь с тем же именем файла.
        var doc = new ProjectDocument
        {
            Name = "t",
            Timeline = new EventTimeline
            {
                AudioPath = "/nonexistent/dir/elsewhere/a.wav",
                DurationSeconds = 0.2,
            },
        };
        var projectPath = Path.Combine(tmp.Path, "p.nvproj");
        ProjectSerializer.Save(doc, projectPath);

        var loaded = await new ProjectLoader().LoadAsync(projectPath);
        Assert.Equal(audio, loaded.AudioPath);
    }

    [Fact]
    public async Task ResolveAudio_RelativePath_FindsFile()
    {
        using var tmp = new TempDir();
        var subDir = Path.Combine(tmp.Path, "audio");
        Directory.CreateDirectory(subDir);
        var audio = Path.Combine(subDir, "a.wav");
        ToneWavWriter.WriteSine(audio, seconds: 0.2);

        var doc = new ProjectDocument
        {
            Name = "t",
            Timeline = new EventTimeline
            {
                AudioPath = "audio/a.wav",
                DurationSeconds = 0.2,
            },
        };
        var projectPath = Path.Combine(tmp.Path, "p.nvproj");
        ProjectSerializer.Save(doc, projectPath);

        var loaded = await new ProjectLoader().LoadAsync(projectPath);
        Assert.Equal(audio, loaded.AudioPath);
    }

    [Fact]
    public async Task ResolveAudio_NotFound_ReturnsNull()
    {
        using var tmp = new TempDir();
        var doc = new ProjectDocument
        {
            Name = "t",
            Timeline = new EventTimeline { AudioPath = "missing.wav" },
        };
        var projectPath = Path.Combine(tmp.Path, "p.nvproj");
        ProjectSerializer.Save(doc, projectPath);

        var loaded = await new ProjectLoader().LoadAsync(projectPath);
        Assert.Null(loaded.AudioPath);
        Assert.Equal(AudioIntegrity.NotChecked, loaded.Integrity);
    }

    // ─── SHA-256 integrity ──────────────────────────────────────────

    [Fact]
    public async Task Hash_Matching_ReturnsOk()
    {
        using var tmp = new TempDir();
        var audio = Path.Combine(tmp.Path, "a.wav");
        ToneWavWriter.WriteSine(audio, seconds: 0.2);
        var projectPath = await SaveProjectAsync(tmp.Path, audio);

        var loaded = await new ProjectLoader().LoadAsync(projectPath);
        Assert.Equal(AudioIntegrity.Ok, loaded.Integrity);
    }

    [Fact]
    public async Task Hash_Mismatch_ReturnsMismatch()
    {
        using var tmp = new TempDir();
        var audio = Path.Combine(tmp.Path, "a.wav");
        ToneWavWriter.WriteSine(audio, seconds: 0.2);
        var projectPath = await SaveProjectAsync(tmp.Path, audio);

        // Меняем содержимое аудио — хеш в проекте становится неактуальным.
        ToneWavWriter.WriteSine(audio, seconds: 0.3);

        var loaded = await new ProjectLoader().LoadAsync(projectPath);
        Assert.Equal(AudioIntegrity.Mismatch, loaded.Integrity);
    }

    [Fact]
    public async Task Hash_Empty_ReturnsNotChecked()
    {
        using var tmp = new TempDir();
        var audio = Path.Combine(tmp.Path, "a.wav");
        ToneWavWriter.WriteSine(audio, seconds: 0.2);

        var doc = new ProjectDocument
        {
            Name = "t",
            Timeline = new EventTimeline
            {
                AudioPath = audio,
                AudioSha256 = "",
                DurationSeconds = 0.2,
            },
        };
        var projectPath = Path.Combine(tmp.Path, "p.nvproj");
        ProjectSerializer.Save(doc, projectPath);

        var loaded = await new ProjectLoader().LoadAsync(projectPath);
        Assert.Equal(AudioIntegrity.NotChecked, loaded.Integrity);
    }

    // ─── Helpers ────────────────────────────────────────────────────

    private static async Task<string> SaveProjectAsync(string dir, string audioPath)
    {
        await using var fs = File.OpenRead(audioPath);
        var hash = await System.Security.Cryptography.SHA256.HashDataAsync(fs);

        var doc = new ProjectDocument
        {
            Name = "t",
            CreatedAt = DateTimeOffset.UtcNow,
            Timeline = new EventTimeline
            {
                AudioPath = audioPath,
                AudioSha256 = Convert.ToHexString(hash),
                DurationSeconds = 0.2,
                Events =
                {
                    new AudioEvent
                    {
                        Type = EventTypes.Onset,
                        TimestampSeconds = 0.1,
                        Confidence = 0.9f,
                    },
                },
            },
            Rules = new RulesDocument(),
        };
        var projectPath = Path.Combine(dir, "p.nvproj");
        ProjectSerializer.Save(doc, projectPath);
        return projectPath;
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }
        public TempDir()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"nv-a1-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}