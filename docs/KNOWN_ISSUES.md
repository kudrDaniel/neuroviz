# Известные проблемы (Known Issues)

Документ фиксирует найденные, но сознательно отложенные проблемы.
Каждая запись содержит симптом, корневую причину, план решения и
зависимости. После закрытия — запись переносится в CHANGELOG с
указанием коммита.

---

### KI-1: Насыщение `Confidence` на разреженном/тихом материале

**Статус:** частично исправлено в B1a (`OnsetDetector`).
Остальные детекторы (`Novelty`, `Pitch`, `Azimuth`, `SpectralFeatures`) —
B1b/B1c.

**Симптом.** На тихом материале с редкими событиями (например, соло-
пианино — C418 «Sweden») почти все события имеют `Confidence ≈ 1.0`.
Визуализатор «сходит с ума». Debug-таблица показывает 1.0 для `onset`,
`novelty`, `pitch_salience`, `spectral_centroid` и `harmonicity`.

**Корневая причина.** У поля `AudioEvent.Confidence` нет определённого
контракта. Каждый детектор считает его сам, относительно своего
локального порога:

- `OnsetDetector` / `NoveltyDetector`: `(x − threshold) / threshold`,
  clamp 0..1. На разреженном материале `std` мал → `threshold` мал →
  любой реальный удар даёт `flux >> threshold` → **clamp в 1.0**.
- `PitchDetector`: `maxHps / (maxHps + 10)`. Чистая нота пианино даёт
  HPS в сотни → **~0.95–1.0 всегда**.
- `SpectralFeaturesDetector`: `Confidence = 1f` **захардкожено** для
  обоих событий (`spectral_centroid`, `harmonicity`).

**Следствие.** Фильтры правил вида `confidence: { min: 0.6 }` полностью
обходятся — каждый onset триггерит `pulse` независимо от силы удара.

**Не однострочный фикс.** Требуется:

1. Определить модель `Confidence` — один из вариантов:
   - **Абсолютная шкала**: `confidence` = сила сигнала относительно
     полной шкалы FFT. Минус: калибровка, зависимость от `fftSize` /
     окна, ломает существующие пороги.
   - **Percentile-based**: `confidence` = насколько событие выделяется
     среди последних N (скользящее окно). Устойчиво к тихому материалу,
     но требует истории на каждый детектор.
   - **Гибрид**: `confidence = signal_strength × novelty_factor`, где
     `signal_strength` из `rms`/`sumMag`, а `novelty_factor` — текущая
     относительная формула.
2. Обновить все пять детекторов.
3. Обновить `TUNING.md`.
4. Добавить тесты на поведение при тихом vs громком и разреженном vs
   плотном материале для каждого детектора.

**Прогресс:**
- ✅ **B1a**: `OnsetDetector` → `PercentileConfidence`.
- ✅ **B1b**: `NoveltyDetector`, `PitchDetector`, `AzimuthDetector` →
  `PercentileConfidence`.
- ✅ **B1c (variant B, в рамках B2)**: `SpectralFeaturesDetector`
  переведён из `IEventDetector` в `IContinuousDetector`. Захардкоженный
  `Confidence = 1f` устранён вместе с событиями — `spectral_centroid`
  и `harmonicity` теперь continuous-метрики (`ContinuousParams`).
  `Confidence` для них больше не существует — правила фильтруют
  напрямую по `params.<metric>` (или, для встроенной визуализации,
  `MainNode` читает значения из `ContinuousParams`).

**Статус KI-1:** закрыт (все 5 детекторов переведены).

**Обходной путь:** не требуется.

---

### KI-2: Continuous params не сохраняются в `.nvproj`

**Статус:** ✅ **закрыт** (schema v3).

**Симптом (был).** После Load project (MVP-A1) и Play в replay-режиме
`ContinuousParams` в `EventStreamFrame` пуст. Визуализатор держит
последние live-значения — визуал «замораживается» на индикаторах
(`spectral_centroid`, `harmonicity`, `band_energy.{low,mid,high}`,
`vad_state`, `speech_music_ratio`, `spectral_flux`), которые в live
двигались. После перезапуска App — вообще нули.

**Решение (schema v3).**
- `EventTimeline [Key(6)] List<ContinuousFrame>` — timestamped samples.
- `ContinuousFrame` — flat `float[8]` позиционно совместимый с
  `ContinuousKeys.Ordered`. Изменение набора ключей требует bump
  (позиционный wire contract).
- `ContinuousKeys` — фиксированный набор 8 ключей (append-only).
- `ContinuousInterpolation.EvaluateAt(frames, pos)` — бинарный поиск
  + линейная интерполяция между соседними frames, clamp по краям.
- Live-play собирает continuous в буфер (`RecordContinuousFrame` в
  `OnTimerTick`, live-ветка) — Save захватывает всё, что играло.
- Re-analyze собирает буфер заново с полным покрытием трека.
- Seek назад → truncation буфера (защита от дублей timestamp).
- Seek вперёд → «дырка», интерполяция сгладит.
- Replay (Imported) → `ContinuousInterpolation` из `_continuousFrames`.

**Ограничения:**
- Разрешение 20 Hz (drain раз в 50 мс; STFT 40 Hz теряет половину
  фреймов). Для визуала достаточно (MainNode сглаживает `kc`/`kb`/`kf`).
- Seek вперёд в live-сессии → дырка в буфере; интерполяция линейно
  усреднит между двумя дальними frames.
- Live-play после Re-analyze дописывает свежие frames; после seek
  назад — truncation срабатывает, хвост отрезается. Это ожидаемо.

---

### KI-3: Re-analyze работает в 1× реального времени

**Симптом.** Нажатие Re-analyze на 4-минутном треке занимает ~4 минуты.

**Корневая причина.** Re-analyze переиспользует обычный playback pipeline
(BASS играет с реальной скоростью, DSP-колбэк кормит `AnalyzerPipeline`).
Это простейший корректный путь для MVP-D. Ускорение playback (BASS
`ATTRIB_FREQ`) даёт 4× скорость, но `AnalyzerPipeline` queue
переполняется (`DropOldest`), теряются кадры — качество анализа
деградирует.

**Не однострочный фикс.** Требуется offline-путь: открыть decode-only
handle, читать PCM блоками, кормить `StftBuffer.Push` напрямую, без
bounded channel. Это то же, что P2-F (offline export) — 1–2 недели
работы.

**Обходной путь.** Re-analyze вызывать редко (только после Load
project / миграции v1 → v2). На 30-секундных тестовых треках —
30 секунд ожидания, приемлемо.
