# NeuroViz — Phase 2

**Статус:** в работе. MVP закрыт (A/B/C/D). Это горизонт, на котором
строится 3D-визуализация, preview в инспекторе и window management.

**Полный план:** `docs/PLAN_PHASE_2.md`. Здесь — карта возможностей
Phase 2 и того, что сознательно отложено в Phase 2.5+ / never.

---

## В scope Phase 2

### P2-C — 3D-сцена

**Цель:** сфера с шипами, частицы, idle-камера, palette. Реагирует
на continuous (`band_energy.*`, `spectral_flux`, `vad_state`,
`speech_music_ratio`) и events (`pulse`, `release`, `stream_boundary`).

**Компоненты (полностью — в PLAN_PHASE_2.md §2.4):**
- Icosphere, displacement по нормали (pulse + band_energy.low).
- `spikiness` uniform от harmonicity или spectral_centroid (O1).
- `GPUParticles3D`: density от band_energy.mid или flux (O2),
  emission-всплески от flux, смещение от pan_x, смена траектории
  от stream_boundary (O3).
- Idle-камера (медленный orbit).
- Palette от shiver/novelty/pitch/band_energy.high.

**Kill switch:** обязательный spike 2–3 дня перед full. Если spike
не проходит по тех/функц/визуальным критериям — обсуждение, возможно
пересмотр.

**Оценка:** spike 2–3 дня, full 3–4 недели.

---

### P2-A — Preview-thumbnail

Godot рендерит кадр 320×180 раз в 200 мс (5 fps) и шлёт по IPC
отдельным типом `PreviewFrame`. Инспектор показывает `Image` в углу.
`ProtocolVersion` 2→3. Клик по thumbnail — **не** фокусирует Godot
(отменено: два процесса, QoL).

**Оценка:** 1 день.

---

### P2-B — Window management

- **Fullscreen on monitor N** — `DisplayServer.WindowSetCurrentScreen(N)`
  + `WINDOW_MODE_FULLSCREEN`, по Control-команде.
- **Always on top** — `WINDOW_FLAG_ALWAYS_ON_TOP`, по Control-команде.
- **Geometry persistence** — `settings.json`, не `.nvproj`.

**Оценка:** 3–4 часа.

---

## Phase 2.5+ (после Phase 2 MVP)

Не в обозримом будущем — отдельные итерации:

### Motif detector (DSP)

Chromagram (12 полутонов) по STFT-кадрам → self-similarity matrix →
пики на диагоналях → событие `motif_repetition`. Требует ~5–10 с
истории, окно SSM. 1–2 недели.

**Замечание:** визуальная часть «припев узнаётся» **уже** решается
через `stream_boundary` в P2-C (смена траектории частиц). Motif как
DSP — дополнительная уникальность, не блокирует базовый визуал.

### Offline export (P2-F) + offline pipeline (KI-3)

Headless-рендер `--fixed-fps 60` + захват `get_viewport().get_texture()`
→ ffmpeg subprocess → MP4 1080p. Требует offline pipeline (анализ без
realtime BASS). KI-3 (быстрый Re-analyze) закрывается тем же pipeline.
1–2 недели.

### Cross-platform (P2-G)

BASS libs для Linux/macOS (`bass.so`, `bass.dylib`), проверка
`NamedPipe` / `MemoryMappedFile` на Unix. Архитектурных блокеров нет —
все API .NET кросс-платформенные. 1 неделя.

### Node-graph editor (P2-E)

Визуальный редактор в стиле TouchDesigner, **view над JSON** (не замена
формата). Отдельный проект: недели масштаба. JSON остаётся единственным
источником правды.

### Heatmap spectrum (P2-I)

Полноценная спектрограмма (time × frequency) в Debug-режиме. Требует
либо хранения полной `Magnitudes` (дорого), либо 128-полосной компактной
версии. 2–3 дня.

---

## Вне плана (никогда)

### P2-H — Multi-track

Принципиально другая задача: сведение ≠ визуализация. Сознательно
не совмещаем.

### Click-to-focus preview (старый D3)

Два процесса, QoL, не критично. Отменено.

---

## Критерий «Phase 2 достигнута»

Полный список — в `PLAN_PHASE_2.md §8`. Краткая сводка:

1. 3D-сцена реагирует на continuous + events.
2. Preview-thumbnail в инспекторе, Reload Rules видно в preview.
3. Fullscreen на monitor N + always-on-top из Settings.
4. Save → Load → сцена играет без live (continuous из `.nvproj` v3).
5. Настройка не требует редактирования `.json` руками.

Когда все пять — `docs/PHASE_2.md` помечается «achieved», следующий
горизонт — Phase 2.5.

---

## Приоритеты (субъективно)

| Фича | Ценность | Стоимость | Приоритет |
|---|---|---|---|
| P2-C 3D-сцена | **Критическая** | Высокая | **Главная цель Phase 2** |
| P2-A Preview | Высокая (tuning) | Низкая | Сразу после spike |
| P2-B Window mgmt | Средняя (QoL) | Очень низкая | Параллельно, 3–4 ч |
| Motif (DSP) | Высокая (уникальность) | Очень высокая | Phase 2.5 |
| Offline export | Высокая | Высокая | Phase 2.5 |
| Cross-platform | Средняя | Средняя | Phase 2.5, по запросу |
| Node-graph | Высокая (UX) | Очень высокая | Отдельный проект someday |
| Heatmap | Низкая | Средняя | Phase 2.5, опционально |

---

## Что уже готово к Phase 2 (фундамент)

- **Continuous-канал** — 8 ключей в `EventStreamFrame.ContinuousParams`,
  `schema v3` в `.nvproj`.
- **IPC**: Control pipe (hello, hello_ack), EventStream pipe, MMF audio clock.
- **Replay continuous** через `ContinuousInterpolation` — Load project =
  полный визуал без live.
- **3D-ready данные**: `band_energy.{low,mid,high}`, `spectral_flux`,
  `vad_state`, `speech_music_ratio`, `spectral_centroid`, `harmonicity` —
  все доступны в `EventStreamFrame` каждый тик.
- **Events**: onset, offset, novelty, pitch_salience, azimuth_estimate,
  stream_boundary. Достаточно для шейдерных «попаданий».
- **Rules**: JSON → params (pulse, shiver, release, stream_glow,
  stream_fade, pan_x, pitch_height). Работают в 3D без изменений.
