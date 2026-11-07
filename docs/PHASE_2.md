# NeuroViz — Phase 2

**Статус: achieved.** MVP (A/B/C/D) и Phase 2 (P2-A/B/C) закрыты.
3D-визуализатор, preview-thumbnail в инспекторе, window management
и schema v3 с continuous-персистентностью — в master.

**Дальше — Phase 2.5+:** motif (DSP), offline export, cross-platform,
node-graph editor, heatmap. Ничего из этого не блокирует базовый
сценарий «открыл трек → увидел 3D → сохранил → загрузил → увидел
без live».

---

## Реализовано в Phase 2

### P2-C — 3D-сцена

**Сцена:** `MainNode3D` / `spike.tscn`.

- Icosphere с simplex-noise шипами (два октава, анимированный sampling).
- `harmonicity` → spikiness (тоника/шум → гладко/остро).
- `band_energy.low` → базовое «дыхание».
- `pulse` (onset) → резкий выброс + emissive.
- `release` (offset) → сжатие.
- Палитра: `band_energy.high` (яркость), `pitch_height` (hue),
  `shiver` (оранжевый), `cortex_flash` (белая вспышка).
- Частицы: ambient (density от `band_energy.mid`), burst (разлёт от
  `spectral_flux`), pan (сдвиг от `pan_x`), trajectory (Fast/Reverse/Idle
  от `stream_boundary`).
- Idle-камера (орбита + Y-свей, независима от playback).
- Тени от DirectionalLight + finite-difference normal reconstruction.
- Настройки в Settings UI: camera speed, Y amplitude, particle counts.

### P2-A — Preview-thumbnail

`NeuroViz.Preview` pipe (Godot → App). Godot раз в 200 мс снимает
viewport (post-draw), ресайзит 320×180, кодирует PNG, шлёт в App.
Инспектор показывает `Image` в правом нижнем углу.

### P2-B — Window management

Control-команды `SetWindowMode`, `SetAlwaysOnTop`. Применяются при
Launch из `settings.json`. Fullscreen на monitor N, всегда сверху.

### Schema v3 — continuous в `.nvproj`

`EventTimeline.ContinuousFrames` (8 ключей, `ContinuousFrame` с flat
`float[8]`). Replay интерполирует из сохранённых frames. Сцена играет
из `.nvproj` без live-сессии.

---

## Что уже готово к Phase 2 (фундамент)

- **Continuous-канал** — 8 ключей в `EventStreamFrame.ContinuousParams`,
  schema v3 в `.nvproj`.
- **IPC**: Control (long-lived), EventStream, MMF audio clock, Preview.
- **Replay continuous** через `ContinuousInterpolation` — Load project =
  полный визуал без live.
- **Confidence model** (B1a/b/c) — детекторы не насыщаются на тихом
  материале.
- **Events**: onset, offset, novelty, pitch_salience, azimuth_estimate,
  stream_boundary. Для шейдерных и частичных реакций достаточно.

---

### P2-C — 3D-сцены

**Две независимые 3D-сцены**, выбор через Settings → Visualizer scene.

**Spike (`MainNode3D` / `spike.tscn`) — «спокойная»:**
- Icosphere с simplex-noise шипами, шум анимирован (дрейф по поверхности).
- `harmonicity` → spikiness, `band_energy.low` → displacement,
  `pulse` → attack burst, `release` → compression.
- Палитра от `band_energy.high` (яркость), `pitch_height` (hue),
  `shiver` (оранжевый), `cortex_flash` (белая вспышка).
- Частицы: ambient (density от `band_energy.mid`), burst (от flux),
  pan (сдвиг), trajectory (Fast/Reverse/Idle от `stream_boundary`).
- Idle orbit + novelty zoom-out. Тени, finite-diff normals.

**Neural (`MainNodeNeural` / `neural.tscn`) — «комплексная»:**
- Двухполушарная сеть (1600 узлов, ~3800 рёбер, CC-магистрали).
- Слои по X: Low → Mid → High (спектральная семантика).
- Hop-cascade от `pulse` (волна вспышек от seed через рёбра).
- **Эволюционная перестройка** на `stream_boundary`: часть узлов
  двигается внутри слоёв, связи рвутся и формируются заново.
  Snapshots (B1) обеспечивают детерминированный replay.
- Wave сжатия на `release` (от центра структуры).
- Палитра: cold→warm база (беж→тан), активация red→orange→yellow.
- CC bundles: per-layer brightness от `band_energy`, независимы от
  `harmonicity` (структурные магистрали).
- VAD: `vad_state` → гемисфер bias, `speech_music_ratio` → scale узлов.
- Частицы: нейротрансмиттеры (фиолетовые, по рёбрам на hop),
  ambient halo (density от `band_energy.mid`).
- Idle orbit + novelty zoom-out. Glow (bloom) для «настоящего»
  излучения. Тени, simplex-noise.

**Настройки** (Settings UI):
- Scene selector (`2d` / `spike` / `neural`).
- Spike: camera orbit speed, Y amplitude, particle counts.
- Neural: nodes/hemisphere (200–1500), edges/node (2–6),
  CC density (0.1–1.0), camera orbit speed, camera Y amplitude.
- Топология применяется при Launch/SceneChange; камера — live.

## Phase 2.5+ (после Phase 2)

Не в обозримом будущем — отдельные итерации:

### Motif detector (DSP)

Chromagram → SSM → пики → событие `motif_repetition`. Визуальная
часть «припев узнаётся» **уже** решена через `stream_boundary`
(смена траектории частиц). Motif как DSP — уникальность, не блокер.
1–2 недели.

### Offline export + offline pipeline

Headless `--fixed-fps 60` + `get_viewport().get_texture().get_image()`
→ ffmpeg → MP4 1080p. Требует offline pipeline (KI-3, быстрый
Re-analyze). 1–2 недели.

### Cross-platform

BASS libs для Linux/macOS, проверка NamedPipe/MMF на Unix. 1 неделя.

### Node-graph editor

Визуальный редактор как **view над JSON**. Отдельный проект недель
масштаба. JSON остаётся единственным источником правды.

### Heatmap spectrum

Полноценная спектрограмма в Debug-режиме. 2–3 дня.

### Volumetric fog для neural (Phase 2.5)

Требует `forward_plus` рендерера. Мощный визуальный апгрейд:
эмиссивные узлы и CC-магистрали светятся в 3D-тумане, god-rays,
объёмная глубина. Не блокирует текущую neural-сцену (Glow/bloom
даёт 80% эффекта). Детали — `PLAN_NEURAL.md §11`.

---

## Вне плана (никогда)

- **Multi-track** — принципиально другая задача (сведение ≠
  визуализация). Не совмещаем.
- **Click-to-focus preview** — два процесса, QoL, не критично.

---

## Критерий «Phase 2 достигнута» (выполнен)

Полный список — в `PLAN_PHASE_2.md §8`. Пройдено:

1. ✅ 3D-сцена реагирует на continuous + events.
2. ✅ Preview-thumbnail в инспекторе, Reload Rules виден в preview.
3. ✅ Fullscreen на monitor N + always-on-top из Settings.
4. ✅ Save → Load → сцена играет без live (continuous из `.nvproj` v3).
5. ✅ Настройка не требует редактирования `.json` руками.

---

## Приоритеты (субъективно)

| Фича | Ценность | Стоимость | Приоритет |
|---|---|---|---|
| Motif (DSP) | Высокая (уникальность) | Очень высокая | Phase 2.5 |
| Offline export | Высокая | Высокая | Phase 2.5 |
| Cross-platform | Средняя | Средняя | Phase 2.5, по запросу |
| Node-graph | Высокая (UX) | Очень высокая | Отдельный проект someday |
| Heatmap | Низкая | Средняя | Phase 2.5, опционально |
