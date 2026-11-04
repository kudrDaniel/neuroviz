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

**Симптом.** После Load project (MVP-A1) и Play в replay-режиме
`ContinuousParams` в `EventStreamFrame` пуст. Визуализатор держит
последние значения из live-сессии — визуал «замораживается» на
индикаторах (`spectral_centroid`, `harmonicity`), которые в live
двигались.

**Корневая причина.** `ContinuousParams` вычисляются в реальном
времени из PCM; в `EventTimeline` (`.nvproj`) они не сохраняются.
Затронуты `spectral_centroid`, `harmonicity`, `band_energy.{low,mid,high}`.
Схема `EventTimeline` не имеет поля под continuous-историю.

**Не однострочный фикс.** Требуется:
1. Расширить `EventTimeline` — `List<ContinuousSample>` или
   `Dictionary<string, List<(double t, float v)>>`.
2. Инкремент `EventTimeline.SchemaVersion` (1→2) и
   `ProjectDocument.ProjectVersion` (1→2) + миграция.
3. `SaveProjectAsync` пишет живую историю; `LoadProjectAsync`
   восстанавливает.
4. `MainNode` в replay ищет непрерывные значения по timestamp,
   а не только latest.
5. `band_energy.{low,mid,high}` (B3) добавляют 3 новых ключа. Пока
   набор continuous-ключей не финализирован (B3/B4), сохранение
   `EventTimeline` под них — преждевременная работа.

**Заблокировано на:** MVP-C — `vad_state` и `speech_music_ratio`
добавят ещё 2–3 continuous-ключа. Сохранять схему под 5 ключей, чтобы
через неделю переделывать под 7 — расточительно. Сначала определить
полный набор, потом один schema bump.

**Обходной путь до закрытия.** В replay визуал работает от событий
(pulse, shiver, pan, pitch). Continuous-индикаторы заморожены на
последних значениях — визуально это «застывший фон», не баг
логики.
