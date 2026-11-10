# NeuroViz — План рефакторинга архитектуры

**Статус:** В работе (ветка `refactor/architecture-cleanup`).
**Цель:** Устранить дублирование кода, разгрузить «Божественные объекты» (`MainNodeNeural`, `MainWindowViewModel`), повысить тестируемость и поддерживаемость проекта без изменения пользовательского функционала.

**Принципы:**
1. Код, относящийся только к одной сцене (Spike, Neural, 2D), остается в ее файлах.
2. Общий код (математика, IPC, UI-менеджеры) выносится в отдельные классы.
3. Никаких изменений в поведении визуализации, правилах (JSON) или формате `.nvproj` на время рефакторинга.

---

## Этап 1: Общий код визуализатора (Устранение дублирования)

### 1.1 `ParamValueRef` -> `Visualizer/Scripts/ParamValueHelper.cs`
*   **Обоснование:** Класс дублируется в `MainNode3D` и `MainNodeNeural`. Изменение логики затухания событий требует правок в двух местах.
*   **Действие:** Сделать статический метод-расширение или базовый класс.

### 1.2 Базовый класс сцены `VisualizerSceneBase.cs`
*   **Обоснование:** Логика захвата превью (через `FramePostDraw`) и обработки `PendingScenePath` (для смены сцен) дублируется во всех трех сценах. Ошибка с черным экраном превью правилась в трех местах.
*   **Действие:** Создать `VisualizerSceneBase : Node`, вынести туда `_ipc`, `_Process` (с проверкой `PendingScenePath`), `CapturePreviewAfterDraw`, `_ExitTree`.

---

## 1.3: `IpcClient` как process-level singleton (Bootstrap Scene)

### Проблема (архитектурный диагноз)

Сейчас `IpcClient` создается внутри каждой сцены (`MainNode`, `MainNode3D`, `MainNodeNeural`) в `_Ready` и уничтожается в `_ExitTree`. При `ChangeSceneToFile`:

1. Старая сцена вызывает `_ipc.Dispose()`. Фоновые задачи (`ReadAsync` на named pipes) не завершаются синхронно — ОС освобождает сокеты с задержкой.
2. Новая сцена создает новый `IpcClient` и пытается подключиться к тем же именам каналов.
3. Старые сокеты еще держат имена → новый клиент получает `All pipe instances are busy` или timeout.
4. Ретрай через `Task.Delay(1000)` маскирует проблему, но не решает: на разных ОС/нагрузках тайминги снова ломаются.

**Корневая причина:** `IpcClient` привязан к жизненному циклу сцены. Смена сцены — событие визуальное, оно не должно влиять на транспортный слой.

### Решение

`IpcClient` живет на уровне процесса Godot. Создается один раз при старте, уничтожается при закрытии. Сцены — только **потребители** (subscribe/unsubscribe к событиям). `ChangeSceneToFile` меняет только `CurrentScene`, IPC-слой не замечает смены.

### Архитектура

```
Godot Process
├── /root (Window)
│   ├── IpcBootstrapHolder         ← persistent (added manually to /root)
│   │   └── IpcClient               ← one per process, lives here
│   │       ├── Control pipe
│   │       ├── EventStream pipe
│   │       ├── AudioClock MMF
│   │       └── Preview pipe
│   │
│   └── CurrentScene               ← changes via ChangeSceneToFile
│       └── MainNodeNeural / MainNode3D / MainNode
│           └── gets IpcClient via IpcBootstrapHolder.Client
│           └── subscribes to VisualConfigReceived etc.
│           └── polls frame via Ipc.GetLatestFrame()
```

**Ключевое:** `IpcBootstrapHolder` — прямой child `Root`, не `CurrentScene`. `ChangeSceneToFile` уничтожает только `CurrentScene`, не `Root` children. Значит holder переживает смену сцены.

### Компоненты

#### 1. `bootstrap.tscn` (main_scene)

Один Node с `Bootstrap.cs`. Эфемерный — существует один кадр.

```
project.godot:
  [application]
  run/main_scene="res://bootstrap.tscn"
```

#### 2. `Bootstrap.cs : Node`

```csharp
public override void _Ready()
{
    // 1. Прочитать конфиг из командной строки (--nv-scene, --nv-nodes, ...)
    NeuralSceneConfig.InitializeFromCommandLine();

    // 2. Создать persistent holder и добавить в Root
    var holder = new IpcBootstrapHolder();
    GetTree().Root.AddChild(holder);

    // 3. Определить целевую сцену
    var target = NeuralSceneConfig.InitialScene; // "neural" / "spike" / "2d"
    var path = target switch
    {
        "2d" => "res://node_2d.tscn",
        "spike" => "res://spike.tscn",
        _ => "res://neural.tscn",
    };

    // 4. Загрузить целевую сцену
    GetTree().ChangeSceneToFile(path);
    // Bootstrap умирает в конце кадра, holder остается жить
}
```

#### 3. `IpcBootstrapHolder.cs : Node`

```csharp
public partial class IpcBootstrapHolder : Node
{
    private static IpcBootstrapHolder? _instance;
    private IpcClient _client = null!;

    public static IpcClient Client =>
        _instance?._client
        ?? throw new InvalidOperationException(
            "IpcBootstrapHolder not initialized. " +
            "Check that bootstrap.tscn is the main scene.");

    public override void _Ready()
    {
        _instance = this;
        _client = new IpcClient();
        _client.Start();
        GD.Print("[Bootstrap] IpcClient started (process-lifetime).");
    }

    public override void _Process(double delta)
    {
        // Единственное место, где обрабатывается PendingScenePath
        if (_client.PendingScenePath is { } path)
        {
            _client.ClearPendingScene();
            var current = GetTree().CurrentScene?.SceneFilePath;
            if (!string.Equals(current, path, StringComparison.OrdinalIgnoreCase))
            {
                GD.Print($"[Bootstrap] Scene switch: {current} → {path}");
                GetTree().ChangeSceneToFile(path);
            }
        }
    }

    public override void _ExitTree()
    {
        // Вызывается один раз — при закрытии приложения
        GD.Print("[Bootstrap] Shutting down IpcClient.");
        _client?.Dispose();
        _instance = null;
    }
}
```

#### 4. `VisualizerBase3D.cs` / `VisualizerBase2D.cs` (изменения)

**Убираем:** создание `IpcClient`, `_ipc.Start()`, `_ipc.Dispose()`.
**Убираем:** проверку `PendingScenePath` (теперь в holder).
**Добавляем:** `protected IpcClient Ipc { get; private set; }` — берет из holder.

```csharp
public abstract partial class VisualizerBase3D : Node3D
{
    protected IpcClient Ipc { get; private set; } = null!;
    private bool _previewFirstCaptureLogged;

    public override void _Ready()
    {
        Ipc = IpcBootstrapHolder.Client;
        _ReadyVisualizer();
    }

    public override void _Process(double delta)
    {
        _Ipc.DrainMainThreadActions();
        _ProcessVisualizer(delta);

        if (Ipc.ShouldCapturePreview(Time.GetTicksMsec() / 1000.0))
            CapturePreviewAfterDraw();
    }

    protected abstract void _ReadyVisualizer();
    protected abstract void _ProcessVisualizer(double delta);

    private async void CapturePreviewAfterDraw() { /* без изменений, использует Ipc */ }

    // НЕТ _ExitTree override для Ipc — сцена не владеет клиентом.
}
```

#### 5. `MainNodeNeural.cs` / `MainNode3D.cs` / `MainNode.cs` (изменения)

**Убираем:** всё, что связано с созданием и dispose `IpcClient`.
**Меняем:** везде `_ipc` → `Ipc` (protected свойство базового класса).
**Оставляем:** `_ExitTree` только если есть подписки на события (отписаться от `VisualConfigReceived`), но **не трогать `Ipc.Dispose()`**.

Пример:
```csharp
// Было:
public override void _ExitTree()
{
    _ipc.VisualConfigReceived -= OnVisualConfigReceived;
    _ipc.Dispose();   // ← удалить
}

// Стало:
public override void _ExitTree()
{
    Ipc.VisualConfigReceived -= OnVisualConfigReceived;
    // Dispose делает IpcBootstrapHolder при закрытии приложения.
}
```

### Что решается по конструкции

| Баг | Было | Стало |
|---|---|---|
| `All pipe instances are busy` при смене сцены | Ретраи + тайминги | Каналы не переподключаются |
| Зависание App при закрытии | `Task.WaitAll` и orphaned tasks | Один Dispose, один канал |
| `PendingScenePath` race | Каждая сцена проверяет | Единственный holder проверяет |
| Preview gap при смене сцены | Возможен | Непрерывный стрим (клиент не прерывается) |
| `maxInstances > 1` как костыль | Применяли | Не нужен, возвращаем `1` |

### Что НЕ меняется

- Внутренности `IpcClient` (все методы, события, каналы) — как есть.
- Логика визуализации (волны, спайки, реструктуризация) — как есть.
- Логика App-стороны (`IpcServer`) — как есть.
- Формат `.nvproj`, IPC-протокол — как есть.
- `NeuralSceneConfig`, `PendingScenePath` — те же свойства, но читаются в другом месте.

### Файлы, которые затрагиваем

**Новые:**
- `src/NeuroViz.Visualizer/bootstrap.tscn`
- `src/NeuroViz.Visualizer/Scripts/Bootstrap.cs`
- `src/NeuroViz.Visualizer/Scripts/IpcBootstrapHolder.cs`

**Изменяемые:**
- `src/NeuroViz.Visualizer/project.godot` — `run/main_scene` → `bootstrap.tscn`
- `src/NeuroViz.Visualizer/Scripts/VisualizerBase3D.cs` — получает Ipc из holder, убирает владение
- `src/NeuroViz.Visualizer/Scripts/VisualizerBase2D.cs` — то же
- `src/NeuroViz.Visualizer/Scripts/MainNodeNeural.cs` — `_ipc` → `Ipc`, убрать `Dispose`
- `src/NeuroViz.Visualizer/Scripts/MainNode3D.cs` — то же
- `src/NeuroViz.Visualizer/Scripts/MainNode.cs` — то же
- `src/NeuroViz.Visualizer/Scripts/NeuralSceneConfig.cs` — добавить `InitialScene` поле, парсить `--nv-scene`

**Откат:**
- `src/NeuroViz.App/Ipc/IpcServer.cs` — `maxInstances` обратно `5` → `1` (не нужен, race исчезает)

### Порядок коммитов

1. **Commit 1:** `refactor(visualizer): bootstrap scene + IpcBootstrapHolder` — новые файлы, `project.godot` правка. Старый `VisualizerBase` пока не тронут — оба механизма (старый и новый) сосуществуют.
2. **Commit 2:** `refactor(visualizer): migrate scenes to IpcBootstrapHolder` — `VisualizerBase3D/2D`, `MainNode*` — убираем `_ipc` ownership, используем `Ipc` из holder.
3. **Commit 3:** `refactor(visualizer): remove legacy IpcClient ownership from scenes` — финальная чистка, удаление `PendingScenePath` checks из базовых классов.

Каждый коммит — рабочее состояние.

### Тестирование

**Обязательные сценарии (ручной прогон):**
1. Launch Visualizer → сцена Neural → переключение на Spike → 2D → Neural. Повторить 20 раз. Ни одной ошибки, ни одного таймаута, ни одного зависания.
2. Закрыть App во время работы любой сцены. Godot завершается чисто (нет висящих процессов в диспетчере задач).
3. Логи: `[Bootstrap] IpcClient started` — **один раз** за весь process lifetime (не на каждой сцене). `[Bootstrap] Scene switch: X → Y` — на каждом переключении. `[Bootstrap] Shutting down IpcClient` — один раз при закрытии.
4. Preview в инспекторе **не прерывается** при смене сцены (виден плавный переход на новый визуал).
5. `--nv-scene=spike` в cmdline при холодном старте: Godot запускается сразу на Spike, не на Neural.
6. Переключение сцен через Settings UI (ComboBox) — работает мгновенно, без ожидания.

**Регрессионные проверки:**
- Wave, cascade, restructure в Neural — работают как раньше.
- Спайки, частицы в Spike — работают.
- 2D сцена — работает.

---

## Этап 2: Разгрузка `MainNodeNeural.cs`

### 2.1 `NeuralMath.cs`
*   **Обоснование:** Функции `HashPhase` и `ComputeContinuousWave` — чистая математика, используются в разных местах (C# и шейдеры).
*   **Действие:** Вынести в статический класс `NeuralMath`.

### 2.2 `NeuralTopologyManager.cs`
*   **Обоснование:** Логика снапшотов (`TakeSnapshot`, `RestoreSnapshot`) и перестройки связей (`RebuildEdgesAfterMovement`) — это графовая логика. Она не должна знать про Godot Nodes.
*   **Действие:** Вынести в отдельный класс, который оперирует структурами данных (списки узлов/ребер).

### 2.3 `NeuralWaveManager.cs`
*   **Обоснование:** Логика наслоения волн (N.10-fix9), расчет `totalPulse` и упаковка в `Vector4` для шейдера.
*   **Действие:** Изолировать расчет волн от рендеринга.

### 2.4 `NeuralParticleSystem.cs`
*   **Обоснование:** Инициализация `GpuParticles3D` и `MultiMesh` для нейротрансмиттеров — это много кода, который можно вынести из основного цикла.
*   **Действие:** Отдельный класс-обертка.

---

## Этап 3: Разгрузка `MainWindowViewModel` (App слой)

### 3.1 `ProjectManager.cs`
*   **Обоснование:** `SaveProjectAsync`, `LoadProjectAsync`, `ReanalyzeAsync` содержат логику работы с файлами и сериализацией. ViewModel не должен этим заниматься.
*   **Действие:** Вынести в отдельный сервис.

### 3.2 `PreviewManager.cs`
*   **Обоснование:** Конвертация `byte[]` в `Bitmap` и управление `PreviewBitmap` смешаны с логикой UI.
*   **Действие:** Вынести в сервис, который ViewModel просто биндит.

### 3.3 `PlaybackManager.cs`
*   **Обоснование:** Обертки над `ManagedBassAudioPlayer`, управление `Position`, `Play/Pause`.
*   **Действие:** Вынести, чтобы ViewModel не знал про BASS напрямую.

---

## Этап 4: Analysis/Core

### 4.1 `SpectralFluxCalculator.cs`
*   **Обоснование:** Дублирование расчета flux между `OnsetDetector` и `SpectralFluxDetector`.
*   **Действие:** Вынести чистую математику в статический класс.

### 4.3 `MathUtils.SmoothTo()`
*   **Обоснование:** Константы `1f - Mathf.Exp(-k * delta)` повторяются в десятках мест.
*   **Действие:** Единая утилита сглаживания.

---

## Порядок работ и коммитов

1. **Branch:** `refactor/architecture-cleanup`
2. **PR 1:** Этап 1 (Visualizer) — самый безопасный, убирает дублирование.
3. **PR 2:** Этап 2 (Neural) — большой, но изолированный внутри одной сцены.
4. **PR 3:** Этап 3 (App) — требует тщательного ручного тестирования UI.
5. **PR 4:** Этап 4 (Analysis) — требует прогона всех тестов детекторов.
