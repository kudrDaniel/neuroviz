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
- ✅ **B1a**: `OnsetDetector` переведён на `PercentileConfidence`.
  `confidence` = mid-rank перцентиль текущего флюкса среди последних N
  *событий* (не фреймов). Warm-up (`< confidenceMinHistory` событий) →
  `confidence = 0.5`. См. тест `QuietMaterial_ConfidenceDistributedNotSaturated`.
- ⏳ **B1b**: `NoveltyDetector`, `PitchDetector`, `AzimuthDetector`.
- ⏳ **B1c**: `SpectralFeaturesDetector` (убрать hardcoded `Confidence = 1f`),
  финальный проход по `TUNING.md`.

**Заблокировано на:** ~~5.1 (аллокации в аудио-колбэке)~~ — блокер снят,
`AnalyzerPipeline` уже выносит всю тяжёлую работу из аудио-колбэка на
отдельный поток (видно в `AnalyzerPipeline.OnPcm` — там только
`_queue.Writer.TryWrite(frame)`).

**Обходной путь:** для правила `onset-pulse` больше не нужен —
`confidence` больше не насыщается на C418 Sweden. Для остальных
детекторов — до B1b/B1c применяйте временное удаление правил.
