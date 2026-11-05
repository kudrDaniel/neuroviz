# A1 — Load Project (.nvproj)

Подробный план первого шага MVP-A. Цель — замкнуть цикл
save → load → save без ручной правки C#.

**Оценка:** 1 рабочий день.
**Зависит от:** ничего (базовый шаг).
**Разблокирует:** A2, A3, A4, все последующие тиры.

---

## 1. Состояние до и после

### До
- «Save project…» есть, «Load» нет.
- `ProjectSerializer.Load` **написан**, но не вызывается ниоткуда.
- Пользователь не может вернуться к сохранённой работе, кроме
  повторного открытия аудио и повторного анализа.

### После
- Кнопка **Load project…** открывает `.nvproj`.
- Восстанавливает `Events`, `_eventKeys`, правила, аудио.
- Если аудио потеряно — диалог поиска.
- Если хеш не совпал — предупреждение, но загрузка.
- Play / Seek работают в режиме replay без пересчёта.
- Кнопка Re-analyze (MVP-D) потом позволит перегенерировать события.

---

## 2. Модель: `ProjectLoader`

Отдельный класс, чистый относительно UI. Помещается в
`src/NeuroViz.Project/ProjectLoader.cs` (он ближе к сериализации, чем
к UI).

```csharp
namespace NeuroViz.Project;

public enum AudioIntegrity { Ok, Mismatch, NotChecked }

public sealed record LoadedProject(
    ProjectDocument Document,
    string? AudioPath,
    AudioIntegrity Integrity);

public sealed class ProjectLoader
{
    public async Task<LoadedProject> LoadAsync(
        string projectPath, CancellationToken ct = default);
}
```

### Что делает `LoadAsync`

1. **Прочитать файл.** `File.ReadAllBytesAsync(path, ct)`. Если файла
   нет → `ProjectNotFoundException`.
2. **Десериализовать.** `ProjectSerializer.Deserialize(bytes)`. При
   ошибке → `ProjectCorruptException` с сообщением.
3. **Проверить версию.** `doc.ProjectVersion > ProjectDocument.CurrentVersion`
   → `ProjectVersionTooNewException`. `CurrentVersion` — константа в
   `ProjectDocument`.
4. **Резолвить аудио-путь** (см. ниже). Если null → `AudioPath = null`.
5. **Проверить SHA-256.** Асинхронно. Если `AudioPath` null или
   `AudioSha256` пустой → `NotChecked`. Иначе — `Ok` / `Mismatch`.

### Резолв аудио-пути

```csharp
private static string? ResolveAudioPath(string storedPath, string projectDir)
{
    if (string.IsNullOrEmpty(storedPath)) return null;

    // 1. Абсолютный или относительный — как есть, если существует.
    if (File.Exists(storedPath)) return storedPath;

    // 2. Только имя — рядом с .nvproj.
    var fileName = Path.GetFileName(storedPath);
    if (!string.IsNullOrEmpty(fileName))
    {
        var nearProject = Path.Combine(projectDir, fileName);
        if (File.Exists(nearProject)) return nearProject;
    }

    // 3. Как относительный от .nvproj.
    var asRelative = Path.Combine(projectDir, storedPath);
    if (File.Exists(asRelative)) return asRelative;

    return null;
}
```

Логика зеркалит `ResolveRulesPath` из фикса 6.2 — единый стиль.

### Исключения

```csharp
public sealed class ProjectNotFoundException : Exception
{
    public ProjectNotFoundException(string path) : base(
        $"Project file not found: {path}") { }
}

public sealed class ProjectCorruptException : Exception
{
    public ProjectCorruptException(string path, Exception inner) : base(
        $"Project file is corrupted: {path}", inner) { }
}

public sealed class ProjectVersionTooNewException : Exception
{
    public ProjectVersionTooNewException(int fileVersion, int supported)
        : base($"Project version {fileVersion} is newer than supported " +
               $"{supported}. Update NeuroViz.") { }
}

public sealed class AudioNotFoundException : Exception
{
    public AudioNotFoundException(string storedPath) : base(
        $"Audio file not found: {storedPath}") { }
}
```

`AudioNotFoundException` бросается **в ViewModel**, не в `ProjectLoader`
(loader возвращает `AudioPath = null`, а решение «спросить
пользователя» — это UI-логика).

---

## 3. ViewModel: `LoadProjectAsync`

### Изменения в полях

```csharp
// были readonly:
private RuleEngine _ruleEngine;        // ← убрать readonly
private readonly RulesDocument _rulesDocument;  // ← убрать readonly

// стали mutable:
private RuleEngine _ruleEngine;
private RulesDocument _rulesDocument;

// новый loader:
private readonly ProjectLoader _projectLoader;
```

### Новый enum состояния

```csharp
private enum ProjectSource { None, Live, Imported }
private ProjectSource _projectSource = ProjectSource.None;
```

Зачем: сейчас `Play` автоматически переключается в live, если
позиция < 0.1 с. Для загруженного проекта это неправильно — события
уже есть, пересчитывать не надо.

### `LoadProjectAsync`

```csharp
public async Task LoadProjectAsync(
    string projectPath,
    string? overrideAudioPath = null)
{
    try
    {
        var loaded = await _projectLoader.LoadAsync(projectPath, _lifetimeCts.Token);

        // 1. Аудио: путь из override (пользователь выбрал в диалоге)
        //    или из резолва в проекте.
        var audioPath = overrideAudioPath ?? loaded.AudioPath;
        if (audioPath is null || !File.Exists(audioPath))
            throw new AudioNotFoundException(loaded.Document.Timeline.AudioPath);

        // 2. Integrity-проверка, если аудио не было override.
        if (overrideAudioPath is null && loaded.Integrity == AudioIntegrity.Mismatch)
        {
            // Не падаем, но сообщаем. Loader уже сравнил хеши.
            StatusText = "Warning: audio changed since analysis. " +
                         "Timeline may be misaligned. Use Re-analyze to fix.";
        }

        // 3. Сбросить текущее состояние.
        StopPlayback();
        Events.Clear();
        _eventKeys.Clear();
        _pipeline?.Dispose();
        _pipeline = null;

        // 4. Загрузить аудио в плеер.
        _player.Load(audioPath);
        Duration = _player.Duration;
        CurrentFilePath = audioPath;

        // 5. Восстановить события.
        foreach (var ev in loaded.Document.Timeline.Events)
        {
            var key = (ev.Type, ToTickMs(ev.TimestampSeconds));
            _eventKeys.Add(key);
            Events.Add(EventRow.FromStored(ev));
        }

        // 6. Правила из проекта — если не пустые.
        if (loaded.Document.Rules.Rules.Count > 0)
        {
            _rulesDocument = loaded.Document.Rules;
            _ruleEngine = new RuleEngine(_rulesDocument);
        }
        // Иначе — оставляем текущие (загруженные при старте App).

        // 7. Создать pipeline для возможного Re-analyze (MVP-D),
        //    но НЕ attach'ить: события уже есть.
        _pipeline = CreatePipeline(_player);

        // 8. Состояние.
        _projectSource = ProjectSource.Imported;
        _isLiveAnalysis = false;
        _lastTickPosition = _player.Position;   // = 0

        StatusText = $"Loaded {Events.Count} events from " +
                     $"{Path.GetFileName(projectPath)}";
    }
    catch (OperationCanceledException)
    {
        StatusText = "Load cancelled";
    }
    catch (AudioNotFoundException ex)
    {
        // View ловит и открывает диалог; см. §4.
        StatusText = ex.Message;
        throw;
    }
    catch (Exception ex)
    {
        StatusText = $"Load failed: {ex.Message}";
    }
}
```

### Изменения в `Play` / `SeekTo`

```csharp
[RelayCommand]
private void Play()
{
    if (CurrentFilePath is null) return;

    // Автопереключение в live — ТОЛЬКО для живого проекта.
    // Загруженный проект всегда остаётся в replay.
    if (_projectSource == ProjectSource.Live
        && !_isLiveAnalysis
        && _player.Position < 0.1)
    {
        SwitchToLive();
    }

    _player.Play();
    IsPlaying = true;
}

public void SeekTo(double seconds)
{
    if (CurrentFilePath is null) return;
    _player.Seek(seconds);
    Position = seconds;
    SliderPosition = seconds;

    if (_projectSource == ProjectSource.Live)
    {
        _pipeline?.Reset();
        SwitchToReplay();
    }
    // Для Imported — уже в replay, ничего не делать.
}
```

### Изменения в `LoadFileAsync`

```csharp
// В конце LoadFileAsync, после SwitchToLive():
_projectSource = ProjectSource.Live;
```

### `EventRow.FromStored` — новый helper

Сейчас `EventRow.From(AnalyzedEvent)` требует `SpectralFrame`. Для
загруженных событий кадра нет. Нужен второй фабричный метод:

```csharp
public static EventRow FromStored(AudioEvent ev) => new()
{
    Timestamp = ev.TimestampSeconds,
    Type = ev.Type,
    Confidence = ev.Confidence,
    ParamsDisplay = FormatParams(ev.Params),
    Source = ev,
    DownsampledSpectrum = new float[32],   // debug-спектр недоступен
};
```

`FormatParams` вынести из `EventRow.From` в статический helper —
сейчас там дублирование строки формирования `ParamsDisplay`.

**Открытый вопрос (см. §7):** сохранять ли `DownsampledSpectrum` в
`.nvproj`. Если да — `FromStored` восстановит его.

---

## 4. UI: кнопка и диалоги

### `MainWindow.axaml`

```xml
<Button Content="Open…"           Click="OnOpenClicked" MinWidth="90" />
<Button Content="Load project…"   Click="OnLoadProjectClicked" MinWidth="120" />
<Button Content="Play"            Command="{Binding PlayCommand}" MinWidth="70" />
...
```

### `MainWindow.axaml.cs`

```csharp
private async void OnLoadProjectClicked(object? sender, RoutedEventArgs e)
{
    var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
    {
        Title = "Open NeuroViz project",
        AllowMultiple = false,
        FileTypeFilter = new[]
        {
            new FilePickerFileType("NeuroViz project")
            {
                Patterns = new[] { "*.nvproj" },
            },
        },
    });

    if (files.Count == 0) return;
    var projectPath = files[0].Path.LocalPath;
    await LoadProjectWithAudioResolutionAsync(projectPath);
}

private async Task LoadProjectWithAudioResolutionAsync(
    string projectPath, string? overrideAudioPath = null)
{
    try
    {
        await ViewModel.LoadProjectAsync(projectPath, overrideAudioPath);
    }
    catch (AudioNotFoundException)
    {
        // Аудио не найдено — спросить у пользователя.
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Locate audio file for this project",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Audio")
                {
                    Patterns = new[] { "*.wav", "*.mp3", "*.ogg", "*.flac" },
                },
            },
        });

        if (files.Count == 0)
        {
            ViewModel.StatusText = "Load cancelled: audio file not selected";
            return;
        }

        // Повторная попытка с overrideAudioPath — не бросает, если файл есть.
        await LoadProjectWithAudioResolutionAsync(
            projectPath, files[0].Path.LocalPath);
    }
}
```

Рекурсия безопасна: второй вызов идёт с `overrideAudioPath`, и если
файл существует — `LoadProjectAsync` не бросит `AudioNotFoundException`.

---

## 5. State machine `ProjectSource`

Сводная таблица поведения по состояниям:

| Действие | `None` | `Live` | `Imported` |
|---|---|---|---|
| `Open…` → `LoadFileAsync` | → `Live` | → `Live` | → `Live` (сбрасывает проект) |
| `Load project…` → `LoadProjectAsync` | → `Imported` | → `Imported` | → `Imported` (заменяет) |
| `Play` при pos < 0.1 | — | SwitchToLive | остаётся replay |
| `Play` при pos > 0.1 | — | остаётся replay | остаётся replay |
| `SeekTo` | — | SwitchToReplay | ничего (уже replay) |
| `Stop` | — | → `None` | → `None` |
| `Save project…` | ошибка | сохраняет | сохраняет |

`Stop` → `None` — потому что после Stop состояние «аудио загружено, но
не играет» неоднозначно (это ещё Live-проект с событиями от live-анализа,
или уже Imported?). Для MVP — `Stop` сбрасывает `_projectSource` в
`None`, и следующий `Play` начнёт с чистого `Live` (если позиция 0).

**Изменение в `Stop`:**

```csharp
[RelayCommand]
private void Stop()
{
    StopPlayback();
    Position = 0;
    SliderPosition = 0;
    IsPlaying = false;
    _lastTickPosition = 0;
    _projectSource = ProjectSource.None;
    // _isLiveAnalysis при следующем Play определится заново.
}
```

---

## 6. Тесты

Тестируется **`ProjectLoader`** (чистая логика), а ViewModel-интеграция —
вручную (тяжёлая — требует живого `ManagedBassAudioPlayer`).

### `tests/NeuroViz.Tests/ProjectLoaderTests.cs`

```csharp
public class ProjectLoaderTests
{
    [Fact] public async Task Load_ValidProject_ReturnsDocument();
    [Fact] public async Task Load_MissingFile_ThrowsProjectNotFound();
    [Fact] public async Task Load_CorruptFile_ThrowsProjectCorrupt();
    [Fact] public async Task Load_NewerVersion_ThrowsVersionTooNew();
    [Fact] public async Task ResolveAudio_AbsolutePath_FindsFile();
    [Fact] public async Task ResolveAudio_FilenameNearProject_FindsFile();
    [Fact] public async Task ResolveAudio_RelativePath_FindsFile();
    [Fact] public async Task ResolveAudio_NotFound_ReturnsNull();
    [Fact] public async Task Hash_Matching_ReturnsOk();
    [Fact] public async Task Hash_Mismatch_ReturnsMismatch();
    [Fact] public async Task Hash_Empty_ReturnsNotChecked();
}
```

**Техника:** `ToneWavWriter.WriteSine` создаёт временный wav; сохраняем
`ProjectDocument` с этим путём; загружаем обратно; проверяем.

### `tests/NeuroViz.Tests/ProjectLoaderResilienceTests.cs`

- Проект + аудио в одной папке → `AudioPath` — абсолютный, находится.
- Проект в `dirA`, аудио в `dirB`, в проекте относительный путь →
  резолвится.
- Аудио переименовано → резолв падает, `AudioPath = null`.

### Не в этом коммите, но нужное для регрессии
- `MainWindowViewModel.LoadProjectAsync` с реальным
  `ManagedBassAudioPlayer` — интеграционный тест. Помечаем как `Skip`
  (как существующий `Smoke_PlaysAndEmitsPcm`), добавляем в MVP-D или
  отдельным PR «тестовая инфраструктура».

---

## 7. Открытые вопросы

1. **Сохранять ли `DownsampledSpectrum` в `.nvproj`?**
   - ✅ **Решено в D4 (schema v2):** Да. `AudioEvent.DownsampledSpectrum`
     ([Key(7)] `float[]`, 32 bands, 128 байт/событие). v1 файлы читаются
     (missing key → `Array.Empty<float>()` → `EventRow.FromStored`
     fallback на `new float[32]`, debug-таблица показывает нули).
     Re-analyze дозаполняет спектры для v1-проектов или после изменения
     правил. 10k событий ≈ 1.3 МБ — приемлемо.

2. **`CurrentProjectPath` в ViewModel?**
   - Сейчас `Save project…` всегда открывает диалог. После load
     логично «Save» без диалога на тот же путь, но это требует
     поля `CurrentProjectPath` и второй кнопки **Save** vs
     **Save As**.
   - **Решение для A1:** не добавлять. `Save As` через существующий
     диалог. `CurrentProjectPath` и разделение Save / Save As —
     в MVP-D.

3. **Что делать, если пользователь жмёт `Open…` после `Load project…`?**
   - Сценарий: загрузил проект, потом открыл другой аудио. Текущее
     поведение: `LoadFileAsync` перезаписывает `Events` и переходит
     в `Live`. Данные проекта молча теряются.
   - **Решение для A1:** не менять. Потом — диалог «Save changes?»
     в MVP-D.

4. **Версия схемы проекта при добавлении новых полей.**
   - `ProjectDocument.ProjectVersion` сейчас 1, `EventTimeline.SchemaVersion` 1.
     При добавлении `ContinuousParams` (MVP-B) в `EventStreamFrame` —
     инкремент `IpcConstants.ProtocolVersion`. `EventTimeline` не
     меняется.
   - При добавлении `DownsampledSpectrum` в `AudioEvent` — инкремент
     `AudioEvent.SchemaVersion` + `EventTimeline.SchemaVersion` + миграция.
   - **Правило:** любое поле, читаемое визуализатором (IPC), ломает
     протокол → `ProtocolVersion`. Любое поле в `.nvproj`, ломающее
     десериализацию → `SchemaVersion`. Локальный
     `ProjectVersion` — для UI-логики «слишком новая версия».

---

## 8. Порядок коммитов

Один коммит на фичу, но если удобно — можно разделить:

1. `feat(project): add ProjectLoader with audio path resolution and hash check`
   — сам loader + тесты.
2. `feat(project): add Load project flow to MainWindowViewModel`
   — ViewModel + UI + state machine.
3. `docs(project): plan and behaviour of Load project`

Или один коммит:

```
feat(project): implement Load project (.nvproj)

Add the missing half of the save/load cycle. ProjectLoader reads
a .nvproj, resolves the audio path against the project's directory
(absolute → filename next to project → relative), and verifies the
stored SHA-256 before returning. MainWindowViewModel.LoadProjectAsync
stops playback, clears live state, restores Events / _eventKeys /
rules, loads audio into the player, and switches the new
ProjectSource state machine to Imported. In Imported mode Play no
longer auto-attaches the analyzer, so a loaded project never
triggers live re-analysis behind the user's back.

Audio missing is surfaced as AudioNotFoundException and handled by
the view: file picker → recursive retry with overrideAudioPath.
Corrupted / version-too-new / file-missing project states map to
distinct exceptions with status-bar messages instead of raw
MessagePack / IOException text.

- Add ProjectLoader, LoadedProject, AudioIntegrity in
  NeuroViz.Project.
- Add ProjectNotFoundException, ProjectCorruptException,
  ProjectVersionTooNewException, AudioNotFoundException.
- MainWindowViewModel: LoadProjectAsync, ProjectSource enum,
  ProjectSource guards in Play / SeekTo / Stop, mutable
  _rulesDocument / _ruleEngine (to accept project rules).
- EventRow.FromStored for events without a SpectralFrame; extract
  FormatParams to a shared helper.
- MainWindow: Load project… button + OnLoadProjectClicked with
  audio-location retry.
- ProjectLoaderTests covering load, path resolution, hash check.

Out of scope (MVP-D): CurrentProjectPath / Save (no dialog),
"Save changes?" prompt on Open while a project is loaded,
persisting DownsampledSpectrum in .nvproj.

Closes MVP-A1.
```
