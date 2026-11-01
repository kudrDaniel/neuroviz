namespace NeuroViz.Project;

public sealed class ProjectNotFoundException : Exception
{
    public string Path { get; }

    public ProjectNotFoundException(string path)
        : base($"Project file not found: {path}")
        => Path = path;
}

public sealed class ProjectCorruptException : Exception
{
    public string Path { get; }

    public ProjectCorruptException(string path, Exception inner)
        : base($"Project file is corrupted: {path}", inner)
        => Path = path;
}

public sealed class ProjectVersionTooNewException : Exception
{
    public int FileVersion { get; }
    public int SupportedVersion { get; }

    public ProjectVersionTooNewException(int fileVersion, int supportedVersion)
        : base($"Project version {fileVersion} is newer than supported " +
               $"{supportedVersion}. Update NeuroViz.")
    {
        FileVersion = fileVersion;
        SupportedVersion = supportedVersion;
    }
}

/// <summary>
/// Бросается не из <see cref="ProjectLoader"/>, а из ViewModel, когда
/// загрузка проекта требует выбора аудиофайла пользователем.
/// </summary>
public sealed class AudioNotFoundException : Exception
{
    public string StoredPath { get; }

    public AudioNotFoundException(string storedPath)
        : base($"Audio file not found: {storedPath}")
        => StoredPath = storedPath;
}