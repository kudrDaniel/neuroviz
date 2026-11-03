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
- ✅ **B1a**: `OnsetDetector` → `PercentileConfidence`. `confidence` =
  mid-rank перцентиль `flux` среди последних N onset-событий. Warm-up
  (`< confidenceMinHistory`) → `0.5`. Регрессионный тест
  `QuietMaterial_ConfidenceDistributedNotSaturated`.
- ✅ **B1b**: `NoveltyDetector`, `PitchDetector`, `AzimuthDetector` →
  `PercentileConfidence`.
  - Novelty: перцентиль `normalizedError`. Warm-up 3 события (≈4.5s).
  - Pitch: перцентиль `maxHps`. Убран `maxHps/(maxHps+10)` — не
    насыщается на пианино.
  - Azimuth: перцентиль `|ildNorm|` (семантика сменилась с
    «абсолютная панорама» на «необычность панорамы», `basic.json`
    не затронут — правило использует `param_from_event`).
  - Регрессионные тесты `Confidence_Distributed_NotSaturated` в
    каждом из трёх файлов.
- ⏳ **B1c**: `SpectralFeaturesDetector` (убрать hardcoded
  `Confidence = 1f`); финальный проход по `TUNING.md`.

**Заблокировано на:** — блокер снят, `AnalyzerPipeline` выносит всю
тяжёлую работу из аудио-колбэка на отдельный поток.

**Обходной путь:** — не требуется для onset/novelty/pitch/azimuth.
Остаётся один hardcoded-`Confidence = 1f` в `SpectralFeaturesDetector`;
до B1c правила, фильтрующие по `confidence` на `spectral_centroid` /
`harmonicity`, всё ещё будут проходить всегда. В `basic.json` таких
правил нет (обе используют `param_from_event`).
