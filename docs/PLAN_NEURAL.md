# Neural-визуализатор — план

**Статус:** в работе.
**Ветка:** `feature/neural-visualizer` (от `main` после коммита
`docs: mark Phase 2 achieved`).
**Образец:** `PLAN_PHASE_2.md`.

---

## 1. Цель

> Вторая 3D-сцена, независимая от spike. Двухполушарная
> нейросинаптическая сеть, которая живёт от continuous и events
> из существующего IPC-пайплайна. Визуально — «мозг»: холодная база,
> слоистая структура, тёплые вспышки, волна активации через связи.

Не заменяет spike. Пользователь выбирает сцену в Settings
(2d / spike / neural). Spike остаётся «спокойным» вариантом,
neural — «комплексным».

## 2. Что входит

- Топология двух геоидов с зеркальными слоями.
- Hop-cascade активация от событий.
- 800 узлов/геоид, ~3600 рёбер.
- Палитра cold+тёплый, hue от pitch.
- Частицы-нейротрансмиттеры вдоль рёбер (тени) + фоновый рой.
- Continuous → несколько каналов визуала одновременно.
- stream_boundary → перестройка CC-магистралей.
- Idle-камера + zoom-out на novelty.
- Scene selector в Settings UI.

## 3. Что НЕ входит

| Отказ | Причина |
|---|---|
| Переход на кривые вместо линий | MVP рёбер = `PRIMITIVE_LINES` (Q2). Кривые — follow-up, если линии будут «плоскими». Документируется как open. |
| Замена spike | Две сцены сосуществуют, выборочно. |
| Multi-track / export / motif / cross-platform | Phase 2.5+ — не пересекается. |
| Динамическая топология на каждом кадре | Каскад активирует ноды, но **структура** рёбер меняется только на `stream_boundary` (медленно, ~5 с). Иначе O(N·K) на каждом кадре. |
| Редактирование топологии в UI | Топология — генеративный алгоритм (детерминированный по seed). Не рисуем руками. |

## 4. Зафиксированные решения

### Топология

**Два зеркальных геоида** вдоль оси X, растянутых по X (эллипсоиды).

X: 0.0─────0.3─────0.5─────0.7─────1.0
LowL MidL HighL│HighR MidR LowR
входы ──────────→ CC ←────────── входы

- **Левый геоид:** слои по X — Low (X ∈ [0.05, 0.30]), Mid (0.30, 0.42),
  High (0.42, 0.50). Низкие входы слева, высокие у центра.
- **Правый геоид:** зеркально — Low (0.95, 0.70), Mid (0.70, 0.58),
  High (0.58, 0.50). Низкие входы справа, высокие у центра.
- **CC (corpus callosum):** cross-edges между одноимёнными слоями
  `LowL↔LowR`, `MidL↔MidR`, `HighL↔HighR`. Bundle High-CC плотный
  (короткие линии через центр), Mid-CC средний, Low-CC разреженный
  (длинные линии, огибают «сцену» — N-Q1).
- **`pan_x` ∈ [-1, +1]**: активность смещается от левого (-1) через
  равномерно (0) к правому (+1).

**800 узлов/геоид = 1600 total** (по умолчанию). 4 связи на узел →
~3200 intra-edges + ~400 CC-edges ≈ 3600 рёбер.

**Масштаб:** N.2-fix2 скейлит всю структуру 2× через `SceneRoot`
(-1.03 X offset, scale 2.0). В world units: ~1.8 × 1 × 1 →
~3.6 × 2 × 2 (aspect 1.8:1:1 сохранён). Камера на прежней дистанции
(2.9), смотрит в тот же центр — эффект «структура крупнее», не
«отъехали». Узлы крупнее (mesh scale), рёбра тонкие относительно
узлов (line-width 1px = ограничение `PRIMITIVE_LINES`, если станет
больно — квады в Phase 3).

### Рендер

- **Узлы** — `MultiMeshInstance3D`, один icosphere mesh (детализация
  настраиваемая), 1600 instance'ов с per-instance scale/color.
- **Рёбра** — `ArrayMesh` с `PRIMITIVE_LINES`, vertex color по
  активации endpoints. Один draw-call.
- **Тени:** DirectionalLight3D + ShadowEnabled. Узлы отбрасывают тени
  на соседей и на себя. Рёбра — без теней (line primitive, нет объёма).

### Каскад активации

**Hop-cascade** (Q3 = B + элементы C):

1. **Seed:** на `pulse` выбирается 1–3 узла в Low-слое. Выбор X-позиции
   по `pan_x`: `-1` → 90% левый гемисфер, `+1` → 90% правый, `0` → 50/50.
2. **Hop:** seed активируется сейчас, все его intra-соседи — через
   120 мс, их соседи — через 240 мс, до **4 хопов** (~500 мс).
3. **Направленный bias:** если узел в слое L имеет соседа в слое L+1
   (ближе к центру = выше по спектру), переход к нему приоритезируется
   — визуально волна «поднимается» от входов к CC.
4. **Затухание:** `activation *= exp(-hop * 0.6)` — 4-й хоп ~9% силы.
   Дальше — 0 (edge не идёт).
5. **CC-переход:** каскад **не** переходит через corpus callosum
   автоматически, кроме случая: seed сработал и в левом, и в правом
   (pan_x ≈ 0) — тогда активируются оба CC-конца в High-слое и волна
   «встречается» в центре.

**Реализация:** `Queue<(nodeIdx, activateAtSeconds, intensity)>`,
проверяется в `_Process`. Не физика, чистая event-driven симуляция.
При `pulse` срабатывает только если кулдаун прошёл (default 50 мс —
иначе частый drum'n'bass перегружает каскад).

### Частицы (N.5)

**Ambient** — прямой ребёнок `MainNodeNeural` (НЕ SceneRoot), world
position `(1.0, 0, 0)` — центр структуры. Emission radius 3.0 world.
В SceneRoot halo смещался бы влево на 2 единицы (SceneRoot position
X=-1.03), поэтому ambient отделён от масштабируемой ветки.

**Neurotransmitters** — `MultiMesh.CustomAabb` **обязателен**.
`MultiMeshInstance3D` не имеет `VisibilityAabb` (это свойство
`GpuParticles3D`). Без custom AABB Godot cull'ит весь MultiMesh:
все instance'ы стартуют в (0,0,0) local с крошечным scale, авто-AABB
вырожденный, и при первом рендере cull-решение уже принято —
частицы летают «вне видимости». CustomAabb покрывает области
local X [-0.5, 2.5], YZ [-1, 1].

Цвет — фиолетовый (0.75, 0.35, 1.0), комплементарен беж-тан базе.
Спавнится 2 частицы на hop. CastShadow = Off (200 мелких сфер на
shadow map — расточительно, тени невидимы).

**Взаимодействие с release wave:** частицы — дети SceneRoot, волна
смещает сам SceneRoot (в N.4-fix — per-node, но позиции узлов меняются
внутри SceneRoot). Ambient тоже в SceneRoot → он «плывёт» с волной,
но neurotransmitters — per-frame position из `_nodeBasePos`, который
волной не затронут. Значит волна не тянет за собой частицы в полёте.
Приемлемо: удар release редко совпадает с каскадом. Open question
для N.6, если понадобится синхронизация.

### Палитра (N.2-fix, базовая)

| Элемент | Low | Mid | High |
|---|---|---|---|
| **Узел** | (0.92, 0.88, 0.78) светло-бежевый | (0.70, 0.55, 0.38) тёплый tan | (0.48, 0.34, 0.22) тёмный tan |
| **Intra edge** | (0.72, 0.38, 0.38, a=0.50) | (0.78, 0.35, 0.35, a=0.55) | (0.82, 0.32, 0.32, a=0.60) |
| **CC edge** | (0.80, 0.32, 0.32, a=0.78) | (0.85, 0.28, 0.28, a=0.82) | (0.90, 0.24, 0.24, a=0.85) |

**Фон:** (0.02, 0.02, 0.05) тёмно-синий. **Ambient:** (0.10, 0.07, 0.05) тёплый тусклый.

**Активация (N.4, решено):** base → red (0.3) → orange (0.7) → yellow (1.0),
плюс emissive: `glow = act²`, `EMISSION = albedo * (0.15 + glow*2.0)`.
Quadratic falloff: subtle при act=0.3 (9% glow), поп при act=1.0.

**Отклонения от исходного плана §4:**
- `band_energy.low/mid/high` → **emissive boost**, не scale.
  Scale делал бы «растущие узлы» (выглядит как баг); emissive
  даёт «сеть освещается» — тот же UX, чище.
- `harmonicity` → **brightness modulation** рёбер, не vertex jitter.
  Jitter требует per-frame vertex rebuild (дорого) или shader с
  UV-offsets (сложно). Brightness даёт 80% эффекта за 5% кода.
  Настоящий jitter — open follow-up (N.4-fix или Phase 3).
- `speech_music_ratio` — отложено в N.6 (вместе с vad_state и CC).
- **release**: N.4 делал `SceneRoot.Scale * (1 - release * 0.1)` —
  мгновенное сжатие всей структуры. N.4-fix заменяет на per-node
  волну: узлы смещаются к центру по мере прохождения фронта, а не
  все сразу. `harmonicity` управляет неоднородностью фронта
  (чистый тон → ровный ripple, шум → хаотичная рябь). Узлы — CPU
  `SetInstanceTransform`, рёбра — shader с dist-based arrival.
  Радиусы сжатия те же (~15%), но распределены по времени и
  пространству.

**VAD / speech** — N.6.

### Interaction table (ядро «системы»)

| Параметр | Тип | Узлы | Рёбра | Частицы | Камера |
|---|---|---|---|---|---|
| `band_energy.low` | cont | Low-слой: scale + emissive | CC-Low bundle подсветка | — | — |
| `band_energy.mid` | cont | Mid-слой: scale + emissive | CC-Mid bundle подсветка | Ambient density | — |
| `band_energy.high` | cont | High-слой: scale + emissive | CC-High bundle подсветка | — | — |
| `spectral_flux` | cont | Random flash поверх каскада | — | Burst-emission | — |
| `harmonicity` | cont | — | Coherence: 1 → прямые, 0 → дрожат (noise offset) | — | — |
| `vad_state` | cont | Левый/правый гемисфер: sec. factor × activation | — | — | — |
| `speech_music_ratio` | cont | Профиль: речь = плотность в Low, музыка = равномерно | — | — | — |
| `pulse` | event | Seed каскада (X по pan_x) | Волна по hop | Neurotransmitters | — |
| `release` | event | **Волна сжатия** от центра структуры; каждый узел проседает к центру с sin-пиком за 0.4с; неравномерность фронта (опережение/запоздание) управляется `(1 - harmonicity) * 0.3` | — | — | — |
| `shiver` | event | Яркая вспышка | — | — | Zoom-out ×1.3 |
| `cortex_flash` | event | Быстрый пик белого | — | — | — |
| `pitch_height` | event | Hue shift синий→фиолетовый→красный | Hue shift | — | — |
| `pan_x` | event | Смещение seed'а + гемисфер bias | — | Сдвиг роя | — |
| `stream_boundary` | event | Перестройка CC-bundles | Новые связи (плавно) | Переход trajectory | Orbit speed ×1.5 |

**Принцип:** каждый параметр касается ≥2 слоёв визуала. Это «взаимодействие систем».

### Камера

Idle orbit (как spike): 0.05 rad/s, Y-sway 0.4. Плюс `zoom-out`:
`fov = 75 + shiver * 15` (на novelty камера «отъезжает»,
показать всю сеть). Плавный возврат через `exp(-dt/0.5)`.

### Settings UI

Расширение существующего окна Settings:

| Поле | Тип | Default | Диапазон |
|---|---|---|---|
| `VisualizerScene` | ComboBox: `2d / spike / neural` | `spike` | — |
| `NeuralNodesPerHemisphere` | Slider | 800 | 200–1500 |
| `NeuralEdgesPerNode` | Slider | 4 | 2–6 |
| `NeuralCCDensity` | Slider | 0.3 | 0.1–1.0 |
| `NeuralCameraOrbitSpeed` | Slider | 0.05 | 0.01–0.2 |
| `NeuralHueRange` | Slider | 0.3 | 0.0–1.0 (насколько pitch сдвигает hue) |

`VisualizerScene` — единственное, что появляется в Settings сразу;
остальные neural-специфичные поля появляются только когда выбран
neural (visibility bound to ComboBox value). Spike-поля — когда spike.
2D-полей нет (2D-fallback без настроек).

`NeuralNodesPerHemisphere` применяется при **перезапуске** visualizer'а
(топология строится один раз при загрузке сцены). Остальные —
живые (uniform / field), применяются через `SetVisualConfig`.

### IPC

**Конфиг:** `SetVisualConfigPayload` (из P2-C) расширяется полями
для neural. MessagePack `[Key(N)]`, добавление новых ключей — не
breaking. Или новый тип `SetNeuralConfig` (отдельная команда) —
чище. Решим в N.9.

**Scene selector:** новая Control-команда `SetVisualizerScene`
с payload `{ Scene: "2d" | "spike" | "neural" }`. Godot на этой
команде переключает `MainNode` / `MainNode3D` / `MainNodeNeural`
(через `GetTree().ChangeSceneToFile("res://neural.tscn")` или
in-place replacement). Детали в N.9.

### Replay (Q10)

Через `ContinuousInterpolation`, идентично spike. Каскад при replay:
`pulse` приходит из Events в окне, seed выбирается по `pan_x` из
восстановленных continuous. Полностью воспроизводимо.

## 5. Стадии

### N.1 — Топология (3–4 дня)

**Цель:** структура двух геоидов в памяти, без рендера.

**Содержание:**
- `NeuralTopology` (класс): генерация узлов и рёбер.
- Детерминированный по seed (`Random(42)`, чтобы топология была
  одинаковой между запусками).
- Геометрия: два эллипсоида (растянуты по X), слои по X.
- **Узлы:** 800/геоид, распределение:
  - 30% Low-слой (X ∈ [0.05, 0.30] левого)
  - 40% Mid-слой (0.30, 0.42)
  - 30% High-слой (0.42, 0.50)
  - аналогично для правого зеркально.
- **Intra-edges:** K-nearest по 3D-расстоянию, K = 4.
- **CC-edges (N-Q1 = вариант B):** между Low-узлами левого и Low-
  узлами правого — дуги над/под геоидами. Для Mid — прямые через
  центр. Для High — короткие прямые у самого центра. Density =
  `CCDensity * layerWeight` где `layerWeight`: High=1.0, Mid=0.6,
  Low=0.2.
- Debug-вывод: `GD.Print("[Neural] Nodes: 1600, Intra-edges: 3200,
  CC-edges: ~380")`.

**Готово когда:**
- Топология генерируется, counts совпадают с ожидаемыми.
- Debug-вывод: углы слоёв по X — Low слева, High у центра (левый),
  зеркально (правый).
- Deterministic: два запуска → одинаковые counts и центроиды слоёв.

### N.2 — Рендер (2–3 дня)

**Цель:** сеть видна.

**Содержание:**
- `MultiMeshInstance3D` для узлов: один icosphere (radius 0.02),
  1600 instance'ов, per-instance transform (position + scale).
  Цвет пока единый (базовый холодный).
- `ArrayMesh` для intra-edges: `PRIMITIVE_LINES`, 2 vertices per edge,
  vertex color = endpoint average (пока базовый).
- `ArrayMesh` для CC-edges: отдельный mesh (для будущей анимации
  кривых, N-Q1 = B).
- DirectionalLight3D с ShadowEnabled. Узлы отбрасывают тени.

**Готово когда:**
- Два геоида видны как «мозг», не как «шар с линиями».
- CC-bundles читаются — «мозолистое тело» видно как плотный жгут
  у центра.
- 1600 узлов + 3600 рёбер → стабильные 60 FPS @ 1920×1080.

**Kill switch после N.2:** если сеть не «мозг», а «шум» —
стоп. Проблема в топологии, не в рендере. Возврат к N.1.

### N.3 — Каскад (2–3 дня)

**Цель:** сеть «живёт» — активируется, волна идёт.

**Содержание:**
- `ActivationState[]` — float[1600], каждый узел.
- `Queue<(nodeIdx, activateAtTime, intensity)>` — pending.
- `OnPulse(panX)`: seed 1–3 узла в Low-слое (X по panX), intensity 1.0.
- `Process(delta, now)`: pop pending, activate, add neighbors из
  intra-edges с задержкой 120 мс, intensity *= 0.6.
- Decay: `activation[i] *= exp(-dt/0.4)` каждый кадр.
- Per-frame: per-instance color = base + activation * warm.

**Готово когда:**
- `pulse` (onset) → видна волна от seed к соседям, затухает за ~4
  хопа (~500 мс).
- Частые удары (drum'n'bass) → каскады не сливаются в «кашу»,
  кулдаун 50 мс работает.
- FPS ≥ 60 при 5+ одновременных каскадах.

**Kill switch после N.3:** если каскад визуально не «волна», а
«мельтешение» — стоп. Пересмотр seed-логики / decay / hop-задержки.

### N.4 — Палитра + непрерывные (2 дня)

- Cold base → warm на активации, white на пике.
- `band_energy.low/mid/high` → per-layer scale + emissive
  (Low при bass, Mid при мелодии, High при воздухе).
- `pitch_height` → hue shift.
- `shiver` → глобальная вспышка.
- `cortex_flash` → быстрый пик.
- `release` → сжатие (scale × 0.85 на 400 мс).
- `harmonicity` → coherence: high = прямые линии, low = дрожащие
  (шум по Y-координате вершин).

**Готово когда:** визуально различимы «активный» и «спокойный»
треки; hue-сдвиг на высоких нотах заметен; release виден.

### N.5 — Частицы (3–4 дня)

- Neurotransmitters: `MultiMesh` с 200 instance'ами, летят по рёбрам
  при каскаде. Тени включены.
- Ambient: density от `band_energy.mid`, без теней.
- Bounded pool с DropOldest.

**Готово когда:** каскад виден «как передача сигнала» через частицы;
FPS ≥ 60 при пике (200 частиц); тени частиц на узлах заметны.

### N.6 — CC-bundles + vad/speech (2–3 дня)

- `band_energy.low/mid/high` → CC-Low/Mid/High bundle подсветка.
- `vad_state` → гемисфер модуляция.
- `speech_music_ratio` → распределение размеров узлов.

**Готово когда:** на треке со сменой инструментал/вокал CC-бандлы
реагируют.

### N.7 — stream_boundary (2–3 дня)

- `stream_boundary open` → перестройка CC-топологии (N-Q5).
  Вариант: 2–3 фиксированных CC-конфигурации, плавный переход
  через интерполяцию vertex positions за ~5 с.
- Ambient trajectory cycle (как spike) — тот же паттерн.

**Готово когда:** на треке со сменой секций CC-конфигурация
меняется визуально; переход плавный (не рывок).

### N.8 — Камера (½ дня)

- Idle orbit (0.05 rad/s, Y-sway 0.4) как в spike.
- Zoom-out на novelty: `fov = 75 + shiver * 15`, плавный возврат.

**Готово когда:** камера облетает; shiver → мягкий zoom-out.

### N.9 — Settings UI + IPC scene selector (½ дня)

- `VisualizerScene` ComboBox в Settings.
- Control-команда `SetVisualizerScene`.
- Godot переключает сцену на `ChangeSceneToFile`.
- Neural-поля в Settings (условно видимые).

**Готово когда:** переключение между 2d/spike/neural работает из UI.

### N.10 — Polish (2–3 дня)

- TUNING.md: секция neural.
- README: секция neural + скриншот.
- PLAN_NEURAL.md: финальная отметка.
- Q1/Q2/Q3/Q4/Q5 из N-Q — закрыть или отложить в документе.

## 6. Открытые вопросы

Решаются по ходу. Не блокируют старт.

| # | Вопрос | Когда |
|---|---|---|
| **N-Q1** | CC-Low bridge geometry: прямые сквозь геоиды / дуги поверх / только через верхний полюс? Текущее решение — **вариант B (дуги)**, но визуально не проверено | N.1 → N.2 (по первому рендеру) |
| **N-Q2** | Seed при `pulse`: 1 нода / 2–3 ноды с рандомным весом? | N.3 |
| **N-Q3** | `release` = global сжатие или приглушение активных? | N.4 |
| **N-Q4** | Частицы A: сколько одновременно? Тени для всех или только крупных? | N.5 |
| **N-Q5** | stream_boundary open → сколько CC-конфигураций: 2 / 3 / бесконечно? | N.7 |

## 7. Kill switches

Два обязательных:

1. **После N.2.** Сеть визуально «мозг, не шум». Если нет —
   пересмотр N.1, не продолжать рендер.
2. **После N.3.** Каскад визуально «волна, не мельтешение». Если
   нет — пересмотр seed/decay/hop, не продолжать палитру.

Остальные стадии incremental — отмена = «не доделать», не
«переделать».

## 8. Критерий «Neural достигнут»

Одна связная история:

1. Открыл трек, Settings → scene = neural, Launch.
2. Сеть видна: два геоида, CC-bundles, узлы, линии.
3. `pulse` → каскад: волна от seed по слоям, neurotransmitters
   летят по связям, вспышки warm на фоне cold.
4. `band_energy.low/mid/high` → три слоя дышат одновременно.
5. `pan_x` → активность смещается L↔R.
6. `pitch_height` → hue shift.
7. `shiver` → вспышка + zoom-out.
8. `stream_boundary open` → CC-перестройка.
9. Seek / Load project → всё воспроизводится без live (schema v3).
10. 60 FPS @ 1920×1080 на GTX 1060-классе.

Когда всё десять — N.10 финальный коммит, документ закрыт.

## 9. Порядок коммитов

Один подэтап = 1–2 коммита, каждый — рабочее состояние:

- `feat(visualizer): N.1 — neural topology generation`
- `feat(visualizer): N.2 — render nodes and edges`
- `feat(visualizer): N.3 — hop cascade from pulse`
- `feat(visualizer): N.4 — palette and continuous reactions`
- `feat(visualizer): N.5 — neurotransmitter particles`
- `feat(visualizer): N.6 — CC-bundles and vad/speech`
- `feat(visualizer): N.7 — stream_boundary CC reconfiguration`
- `feat(visualizer): N.8 — idle camera + novelty zoom`
- `feat(app,ipc,visualizer): N.9 — scene selector in Settings`
- `docs: mark neural visualizer done`

## 10. Что НЕ входит (напоминание)

- Замена spike (сосуществуют).
- Переход на кривые вместо линий (follow-up, документируется как
  open в TUNING.md после N.2).
- Динамическая топология на каждом кадре (только на stream_boundary).
- Multi-track, export, motif, cross-platform, node-graph.
- Ручное редактирование топологии (только генератор).
