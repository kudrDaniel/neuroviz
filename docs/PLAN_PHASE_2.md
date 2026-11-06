# Phase 2 — план до состояния «Phase 2 MVP достигнут»

Документ фиксирует цель, границы, порядок и критерии готовности
Phase 2. По образцу `PLAN_A1_LOAD_PROJECT.md`: каждая стадия имеет
явное «готово когда», kill switch на рискованном шаге, открытые
вопросы с ответом «когда решать».

**MVP-A/B/C/D закрыты.** Это следующий горизонт.

---

## 1. Цель

> Открыл трек → 3D-сцена в Godot реагирует на continuous-параметры
> и события → сохранил проект → через неделю загрузил → сцена
> играет без live-сессии → увидел текущий 3D-кадр в инспекторе
> (preview-thumbnail) → переключил на второй монитор одной настройкой.

Ощущение: «инструмент визуализации», а не «прототип».

## 2. Что входит в Phase 2 MVP

| Блок | Что |
|---|---|
| **P2-C** | 3D-сцена: сфера с шипами, частицы, idle-камера, palette |
| **P2-A** | Preview-thumbnail в инспекторе (320×180 @ 5 fps) |
| **P2-B** | Настройка «вынести на второй монитор fullscreen» + geometry persistence |
| **Polish** | TUNING.md, README, Settings UI (monitor index, preview toggle) |

## 3. Что НЕ входит (и почему)

| Отказ | Причина |
|---|---|
| **Multi-track** (P2-H) | Принципиально другая задача: сведение ≠ визуализация. Сознательно не совмещаем. |
| **Node-graph editor** (P2-E) | Отдельный проект недель масштаба. JSON остаётся источником правды; node-graph может появиться someday как **view** над JSON, не замена. |
| **Export** (P2-F) + **offline pipeline** (KI-3) | Связаны: offline export требует offline анализа. После Phase 2. |
| **Cross-platform** (P2-G) | Someday. Архитектурные зацепки уже есть (`IpcConstants`, `NamedPipe`, `MemoryMappedFile` — все кросс-платформенные API .NET). |
| **Motif detector** (старый P2-D) | Переформулирован: не детектор, а **смена сцены** через `stream_boundary`. Визуальная часть входит в P2-C; DSP-часть (chromagram, SSM) — Phase 2.5+. |
| **Click-to-focus preview** (старый D3) | Два процесса, QoL, не критично. |
| **Heatmap spectrum** (P2-I) | Не в Phase 2. |

## 4. Открытые дизайн-вопросы

Не блокируют начало. Решаются в указанной точке.

| # | Вопрос | Мой склон | Когда решать |
|---|---|---|---|
| **O1** | Острота шипов ← `harmonicity` или `spectral_centroid`? | `harmonicity`: чистый тон → гладко, шум → остро. Семантически чище. | После 2.1 spike, до 2.4 шейдера |
| **O2** | Плотность частиц ← `band_energy.mid` или `spectral_flux`? | `band_energy.mid`: мелодия/вокал → жизнь вокруг. `spectral_flux` отдельно → emission-всплески. | После 2.1 spike, до 2.4 частиц |
| **O3** | `stream_boundary open=0` (закрытие) — что делает с частицами? | Не «возврат в исходное», а новая траектория. 3–4 варианта по циклу. | В 2.4, когда дойдём до частиц |

## 5. Стадии

### 2.1 — P2-C spike

**Цель:** технический de-risk. Отдельная сцена `src/NeuroViz.Visualizer/spike.tscn`. Не трогаем основную `node_2d.tscn` / `MainNode`.

**Содержание:**
- `MeshInstance3D` — icosphere, ~1000 вершин.
- `ShaderMaterial` — один displacement-эффект: `band_energy.low → смещение по нормали`.
- `Camera3D` — статична, смотрит на сферу.
- `MainNode3D` (spike) — читает IPC непрерывно, передаёт `smoothLow` в uniform.
- `DirectionalLight3D` — минимум, чтобы сфера читалась.

**Готово когда** (все три):
1. **Технически:** 1920×1080, стабильно 60 FPS при continuous @ 20 Hz. Замер `Performance.get_monitor(Performance.TIME_FPS)` ≥ 58 среднее за 60 с.
2. **Функционально:** `band_energy.low` двигает displacement плавно, без дрожи. Сглаживание в `MainNode3D` не хуже `kb` в 2D (τ ≈ 83 мс).
3. **Визуально:** выглядит как «заготовка 3D-визуала», не как тестовый куб.

**Kill switch: passed.** Проверено вручную:
- FPS стабильно ≥ 58 (1920×1080, GTX 1060-класс), без просадок.
- `band_energy.low → displacement` плавное, без дрожи (kb = 1-exp(-12·dt)).
- `Cont: 8` в FPS-лейбле — все 8 continuous-ключей приходят.
- Визуально — «заготовка 3D-визуала»: пульсирующая сфера с эмиссией,
  не тестовый куб.

**Оценка:** 2–3 дня.

---

### 2.2 — P2-B (window management)

**Цель:** «вынести визуал на второй монитор fullscreen» — одна настройка, не режим работы. Geometry persistence.

**Содержание:**
- Control-команда `control:set_window_mode` с `{ monitor: int, fullscreen: bool }` → Godot: `DisplayServer.WindowSetCurrentScreen(N)` + `WINDOW_MODE_FULLSCREEN`.
- Control-команда `control:set_always_on_top` с `{ enabled: bool }`.
- `settings.json` (расширение `AppSettings`): `VisualizerMonitorIndex` (int, default 0), `VisualizerAlwaysOnTop` (bool, default false), `VisualizerWindowGeometry` (`x,y,w,h` — сохраняется при закрытии Godot).
- Settings UI: выпадающий список мониторов + чекбокс always-on-top. Появляются в форме D3.

**Готово когда:**
- В Settings UI выбираешь monitor 2 → Launch Visualizer открывает Godot на втором мониторе, fullscreen.
- Чекбокс always-on-top → окно поверх остальных.
- Закрыл Godot → Settings показывают сохранённую geometry при следующем запуске.

**Оценка:** 3–4 часа.

**Урок (manual test):** DisplayServer.* — main-thread-only. Фоновые
чтения Control pipe кладут команды в очередь; `_Process` дренит и
применяет. Эта же дисциплина обязательна для P2-A (рендер
preview-кадра из viewport → текстура → PNG — тоже main-thread-only)
и любых будущих Control-команд.

---

### 2.3 — P2-A (preview-thumbnail)

**Цель:** настраивая JSON-правила, видеть 3D-результат в инспекторе без Alt-Tab.

**Содержание:**
- Новый IPC-тип `PreviewFrame` (PNG/JPEG байты + `FrameSeq`), `IpcConstants.ProtocolVersion` 2 → 3. Старый visualizer без preview не сломается (неизвестный frame → ignored).
- Godot `MainNode`: раз в 200 мс (5 fps) `GetViewport().GetTexture().GetImage()` → `SavePngToBuffer()` → отправить в Control pipe обратно.
- Инспектор `IpcClient`: приём `PreviewFrame`, `Dispatcher.UIThread.Post` → `WriteableBitmap` в углу главного окна (200×120 или 320×180).
- Нагрузка: 320×180 PNG ≈ 5–20 КБ × 5 fps ≈ 100 КБ/с — незаметно.

**Готово когда:**
- В инспекторе видно живой кадр 3D-сцены.
- Латентность ≤ 300 мс (200 мс кадр + сеть + рендер UI).
- При правке JSON-правил → Reload Rules → preview отражает изменения без перезапуска App/Godot.

**Оценка:** 1 день.

**Статус: реализовано.** Preview-thumbnail 320×180 @ 5 fps,
`NeuroViz.Preview` pipe (Godot = server, App = client).
`ProtocolVersion` 3→4. Захват через `GetViewport().GetTexture().GetImage()`
+ `Resize(320, 180)` на main thread; PNG encode + pipe write off-thread.
Rate-limit — `IpcClient.ShouldCapturePreview` (гейт по `IsPreviewConnected`,
не жжём CPU если инспектор не слушает).

**Известная цена:** `GetImage()` — GPU→CPU round-trip на viewport-
разрешении (~8 МБ на 1920×1080 кадр). При 5 fps = ~40 МБ/с аллокаций,
видимый GC churn. Для MVP приемлемо; если профайлер покажет jitter —
Phase 2.5 заменит на `SubViewport` с рендером в 320×180 напрямую.

---

### 2.4 — P2-C full

### Подэтапы 2.4

| Подэтап | Что | Оценка |
|---|---|---|
| **2.4a** | Шипы: simplex-noise displacement + `pulse` event | 3–4 дня |
| **2.4b** | Idle-камера (медленный orbit) | ½ дня |
| **2.4c** | Частицы: `band_energy.mid` density + `spectral_flux` emission | 3–5 дней |
| **2.4d** | Частицы: `pan_x` орбита + `stream_boundary` смена траектории | 3–5 дней |
| **2.4e** | Palette: `shiver`/`novelty`/`pitch`/`band_energy.high` | 2–3 дня |
| **2.4f** | Polish: настройки в `settings.json`, TUNING.md | 2–3 дня |

**Порядок:** 2.4a → 2.4e → 2.4c → 2.4d → 2.4b → 2.4f.
Palette (2.4e) рано — дешёвый, сильно меняет «ощущение», помогает
оценивать остальное. Idle-камера (2.4b) после частиц — с камерой
без частиц скучно, с частицами «живая».

**O1, O2, O3:** решены.
- O1: `harmonicity → spikiness` (semantic: tone=smooth, noise=sharp).
- O2: `band_energy.mid → particle density`; `spectral_flux → emission bursts`.
- O3: `stream_boundary open=0` → возврат к idle-траектории; `open=1` → следующая из 3 вариантов по циклу.

**Цель:** сцена, которую не стыдно показать.

**Содержание (по компонентам):**

| Компонент | Источник параметра | Примечание |
|---|---|---|
| **Сфера** | icosphere, ~5000 вершин (детальнее spike) | — |
| **Шипы: раздувание** | `pulse` (event) + `band_energy.low` (continuous) | pulse — резкий удар, low — дыхание |
| **Шипы: сжатие** | `release` (event) | «выдох» |
| **Шипы: острота** | `harmonicity` **или** `spectral_centroid` (см. O1) | uniform `spikiness` [0..1] |
| **Частицы: плотность** | `band_energy.mid` **или** `spectral_flux` (см. O2) | `GPUParticles3D` |
| **Частицы: emission-всплески** | `spectral_flux` | через `amount_ratio` |
| **Частицы: смещение** | `pan_x` (event) | смещение орбиты |
| **Частицы: траектория** | `stream_boundary open` — смена направления | 3–4 варианта по циклу (см. O3) |
| **Камера** | idle orbit ~0.05 rad/s | пассивно, «сцена жива» |
| **Palette/emissive** | `shiver` / `novelty` / `pitch` / `band_energy.high` | Colors по тем же правилам, что 2D MainNode |

**Персистентность:** параметры сцены (palette, camera speed, particle count) — в `settings.json`, не в `.nvproj`. `.nvproj` остаётся портативным: «что анализировали + правила». Пользователь A открывает проект пользователя B → видит свой визуал на тех же данных.

**Миграция 2D → 3D:** `MainNode.cs` (2D) **не удаляем**. Обе сцены живут параллельно. Запуск через `--path` к конкретной `main_scene`? Или новая настройка `VisualizerScene: "2d" | "3d"` в Settings? Решается в начале 2.4.

**Готово когда** (все):
- Сцена отражает continuous и events без ручной настройки; видны все компоненты выше.
- На треке с явной структурой (куплет/припев/брейк) `stream_boundary` меняет траекторию частиц (визуально заметно).
- `pulse` на удар раздувает шипы заметно, `release` — сжимает.
- 60 FPS на 1920×1080 GTX 1060-класса (проверка на твоём железе).
- Сцена не «сходит с ума» на тихом материале (проверка после B1a confidence-model).
- Idle-камера плавная; при отсутствии звука сцена не замирает, а медленно дышит.

**Оценка:** 3–4 недели. Основные риски: шейдерная математика (spikes + sharpness), GPU-частицы под 60 FPS, отладка визуального баланса. Первая версия почти наверняка «некрасивая» — потребуется 2–3 итерации тюнинга.

**2.4e реализовано:** палитра от `band_energy.high` (brightness),
`pitch_height` (hue), `shiver` (orange), `cortex_flash` (white
emissive). Используются оба действия правила `novelty-shiver` —
JSON-изменений нет. Детали тюнинга — в `TUNING.md §«3D-палитра»`.

**2.4c реализовано:** ambient-частицы (density от `band_energy.mid`
через `AmountRatio`), burst-частицы (`one_shot` `Restart()` на пиках
`spectral_flux` > 0.18 с refractory 200 мс). Тени выключены. Тюнинг
в `MainNode3D.SetupAmbientParticles` / `SetupBurstParticles` /
`_Process`; детали — в `TUNING.md §«3D-частицы»`.

**2.4d реализовано:** `pan_x → смещение эмиттера` (0.5 amplitude),
`stream_boundary → cycle Fast/Reverse/Idle`. Reverse использует
отрицательный OrbitVelocity (если Godot 4.7 не поддерживает —
fallback slow-positive + large-scale, документировано в TUNING.md).
Переход плавный по природе (5 s lifetime → эволюция, не рывок).

**2.4b реализовано:** idle-камера (орбита 0.05 rad/s, Y-покачивание
0.03 rad/s амplituda 0.4, Лиссажу). Привязана к `_visualTime`, не к
audio clock — работает на паузе и переживает seek. Тюнинг в
`UpdateIdleCamera`; детали — в `TUNING.md §«Idle-камера»`.

---

### 2.5 — Polish

- `TUNING.md`: раздел про 3D-параметры, palette, particle tuning.
- `README.md`: скриншот, команда запуска 3D-сцены.
- `docs/PHASE_2.md`: отметить «Phase 2 достигнута», оставшееся → Phase 2.5.
- Settings UI: monitor index + preview toggle + scene selector (2d/3d).
- Возможный коммит: `docs: mark Phase 2 achieved`.

**Оценка:** 2–3 дня.

---

## 6. Порядок и оценка

| Этап | Оценка | Зависит от |
|---|---|---|
| 2.1 P2-C spike | 2–3 дня | — |
| 2.2 P2-B | 3–4 ч | — |
| 2.3 P2-A | 1 день | 2.1 (preview показывает что-то осмысленное) |
| 2.4 P2-C full | 3–4 нед | 2.1 (kill switch пройден) |
| 2.5 Polish | 2–3 дня | 2.4 |

**Итого:** ~4–5 недель инженерных + неопределённость «визуально дожать» (+30–50% по опыту).

Без дедлайна. Темп «в удовольствие» → 8–12 недель календарных.

## 7. Kill switch (единственный)

**Только 2.1 P2-C spike.** Все три критерия §2.1 должны быть пройдены. Если нет — стоп, обсуждение, возможно пересмотр P2-C full.

Остальные этапы не имеют kill switch — они incremental, отмена = «не доделать», не «переделать».

## 8. Критерий «Phase 2 достигнута»

Одна связная история:

1. Пользователь открывает трек, запускает 3D-визуализатор.
2. Сцена реагирует на continuous (band_energy, flux, VAD) и events (pulse, release, stream_boundary).
3. Настраивает JSON-правила → Reload Rules → preview в инспекторе показывает изменения без Alt-Tab.
4. Настраивает monitor 2 + always-on-top в Settings → визуал на втором мониторе.
5. Сохраняет проект, закрывает, через неделю открывает → 3D-сцена играет из `.nvproj` без live-сессии (continuous восстановлены через schema v3).

Когда все пять — `docs/PHASE_2.md` помечается «achieved». Дальше — Phase 2.5 (motif, offline export, cross-platform, node-graph as view, heatmap — всё, что отложено).

## 9. Что не делать в Phase 2 (ещё раз, для памяти)

- Multi-track (никогда в обозримом будущем).
- Node-graph editor (отдельный проект someday).
- Offline pipeline / export / cross-platform (Phase 2.5+).
- Motif detector как DSP (Phase 2.5); смена сцены через stream_boundary — да, в 2.4.
- Click-to-focus preview (отменён).
- Heatmap spectrum (Phase 2.5).

## 10. Порядок коммитов

Мелкие коммиты по паттерну MVP:

- `feat(visualizer): p2-c spike — sphere with band_energy.low displacement`
- `feat(app,visualizer): p2-b — fullscreen on monitor N + always-on-top + geometry persistence`
- `feat(ipc,app,visualizer): p2-a — preview thumbnail 320x180 @ 5fps`
- `feat(visualizer): p2-c full — spikes, particles, idle camera, palette` (возможно серия подкоммитов)
- `docs: mark Phase 2 achieved`

Каждый — рабочее состояние. 2.4 будет длинным; вероятно, разбить на подкоммиты: sphere-shader → particles → stream_boundary-траектории → palette-polish.
