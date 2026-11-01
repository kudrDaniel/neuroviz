using System.Security.Cryptography;

namespace NeuroViz.Project;

/// <summary>
/// Результат загрузки .nvproj. AudioPath = null означает, что путь
/// из проекта не удалось отрезолвить (файл не найден ни по одному из
/// вариантов). Integrity = NotChecked, если аудио нет или хеш не записан.
/// </summary>
public sealed record LoadedProject(
    ProjectDocument Document,
    string? AudioPath,
    AudioIntegrity Integrity);

public enum AudioIntegrity
{
    /// <summary>Хеш не записан в проекте, или аудио-файл не найден.</summary>
    NotChecked,

    /// <summary>Сохранённый и фактический SHA-256 совпадают.</summary>
    Ok,

    /// <summary>Аудио-файл есть, но SHA-256 не совпадает (изменён с момента анализа).</summary>
    Mismatch,
}

/// <summary>
/// Читает .nvproj, резолвит путь к аудио относительно папки проекта и
/// сверяет SHA-256. Не знает про UI и ViewModel: любое решение «спросить
/// пользователя» принимается выше — если <see cref="LoadedProject.AudioPath"/>
/// null, вызывающий код должен сам предложить диалог.
/// </summary>
public sealed class ProjectLoader
{
    public async Task<LoadedProject> LoadAsync(
        string projectPath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var fullPath = Path.GetFullPath(projectPath);
        if (!File.Exists(fullPath))
            throw new ProjectNotFoundException(fullPath);

        ProjectDocument doc;
        try
        {
            var bytes = await File.ReadAllBytesAsync(fullPath, ct).ConfigureAwait(false);
            doc = ProjectSerializer.Deserialize(bytes);
            if (doc is null)
                throw new InvalidOperationException("Deserialize returned null");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            throw new ProjectCorruptException(fullPath, ex);
        }

        if (doc.ProjectVersion > ProjectDocument.CurrentVersion)
            throw new ProjectVersionTooNewException(doc.ProjectVersion, ProjectDocument.CurrentVersion);

        var projectDir = Path.GetDirectoryName(fullPath) ?? "";
        var audioPath = ResolveAudioPath(doc.Timeline.AudioPath, projectDir);

        var integrity = await CheckAudioAsync(audioPath, doc.Timeline.AudioSha256, ct)
            .ConfigureAwait(false);

        return new LoadedProject(doc, audioPath, integrity);
    }

    /// <summary>
    /// Резолвит сохранённый в проекте путь к аудио:
    /// 1. как есть (абсолютный/относительный от CWD — если файл существует);
    /// 2. имя файла рядом с .nvproj (переезд проект+аудио в другую папку);
    /// 3. как относительный от папки .nvproj.
    /// Возвращённый путь канонизируется через Path.GetFullPath:
    /// Path.Combine не нормализует разделители, а нам нужна одна
    /// форма наружу (для сравнений, settings.json и будущей
    /// кросс-платформенной сериализации).
    /// </summary>
    private static string? ResolveAudioPath(string storedPath, string projectDir)
    {
        if (string.IsNullOrEmpty(storedPath)) return null;

        var found = TryResolveRaw(storedPath, projectDir);
        return found is null ? null : Path.GetFullPath(found);
    }

    private static string? TryResolveRaw(string storedPath, string projectDir)
    {
        if (File.Exists(storedPath)) return storedPath;

        var fileName = Path.GetFileName(storedPath);
        if (!string.IsNullOrEmpty(fileName))
        {
            var nearProject = Path.Combine(projectDir, fileName);
            if (File.Exists(nearProject)) return nearProject;
        }

        var asRelative = Path.Combine(projectDir, storedPath);
        if (File.Exists(asRelative)) return asRelative;

        return null;
    }

    private static async Task<AudioIntegrity> CheckAudioAsync(
        string? audioPath, string storedSha256, CancellationToken ct)
    {
        if (audioPath is null || string.IsNullOrEmpty(storedSha256))
            return AudioIntegrity.NotChecked;

        try
        {
            await using var fs = File.OpenRead(audioPath);
            var hash = await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false);
            var hex = Convert.ToHexString(hash);
            return string.Equals(hex, storedSha256, StringComparison.OrdinalIgnoreCase)
                ? AudioIntegrity.Ok
                : AudioIntegrity.Mismatch;
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            // Не читается (занят, нет прав) — не повод валить load.
            return AudioIntegrity.NotChecked;
        }
    }
}