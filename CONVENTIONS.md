# NeuroViz — соглашения проекта

## Git

### Ветки
| Тип | Шаблон | Пример |
|---|---|---|
| Основная | `main` | — |
| Новая функциональность | `feature/<short-name>` | `feature/managedbass-player` |
| Багфикс | `fix/<short-name>` | `fix/rules-json-path` |
| Инфраструктура | `chore/<short-name>` | `chore/vscode-tasks` |
| Эксперимент (может не влиться) | `experiment/<short-name>` | `experiment/godot-ipc` |

- Ветка от `main`, живёт несколько дней, потом merge.
- Перед merge: `git rebase main` (линейная история).
- `develop` не заводим — соло-проект.

### Коммиты (Conventional Commits)
| Тип | Когда |
|---|---|
| `feat:` | новая функциональность |
| `fix:` | багфикс |
| `chore:` | инфраструктура, зависимости, конфиги |
| `docs:` | документация |
| `test:` | тесты |
| `refactor:` | без изменения поведения |
| `build:` | сборка, NuGet, csproj |
| `perf:` | производительность |

Примеры:

feat: add ManagedBass audio player skeleton
fix: correct rules JSON path in tests
chore: initialize repository with .gitignore
test: add round-trip test for ProjectDocument
refactor: rename Project to ProjectDocument

Один коммит = одна логическая единица. Заголовок ≤ 72 символов, повелительное наклонение («add», а не «added»).

### Репозиторий
| Что | Правило |
|---|---|
| `bin/`, `obj/` | игнорируются, никогда не коммитим |
| `.vscode/{tasks,launch,settings,extensions}.json` | коммитим |
| `.vscode/` остальное | игнорируется |
| `samples/rules/*.json` | коммитим (источник правды) |
| `Fixtures/` в `bin/` | копия, не коммитим |
| Line endings | `.sln` → CRLF, всё остальное → LF (`.gitattributes`) |

---

## VS Code

| Действие | Клавиши / путь |
|---|---|
| Собрать solution | `Ctrl+Shift+B` |
| Запустить с дебаггером | `F5` |
| Запустить без дебаггера | `Ctrl+F5` |
| Тесты всего solution | `Ctrl+Shift+P` → `Tasks: Run Task` → `test` |
| Тесты текущего файла | Панель Testing слева / `Ctrl+Shift+P` → `Testing: Run Tests in Current File` |
| Все задачи (build/test/run/clean/restore) | `Ctrl+Shift+P` → `Tasks: Run Task` |
| Перезагрузить окно (после смены csproj) | `Ctrl+Shift+P` → `Developer: Reload Window` |
| Найти символ | `Ctrl+T` |
| Перейти к определению | `F12` |
| Найти все ссылки | `Shift+F12` |

---

## Полезные команды

| Задача | Команда |
|---|---|
| Сборка | `dotnet build` |
| Тесты | `dotnet test` |
| Тесты с фильтром | `dotnet test --filter "FullyQualifiedName~RuleEngine"` |
| Только один проект | `dotnet test tests/NeuroViz.Tests` |
| Очистка | `dotnet clean` |
| Восстановление пакетов | `dotnet restore` |
| Добавить ссылку | `dotnet add <proj> reference <other-proj>` |
| Добавить NuGet | `dotnet add <proj> package <Name>` |
| Список ссылок проекта | `dotnet list <proj> reference` |
| Показать устаревшие пакеты | `dotnet list package --outdated` |

---

## Правила кода (кратко)

- `Nullable` включён везде; `!` — только с комментарием.
- `TreatWarningsAsErrors` — в `NeuroViz.Core`, `NeuroViz.Analysis`, `NeuroViz.Project`.
- Единый `MessagePackOptions.Default` — никаких локальных `new MessagePackSerializerOptions`.
- Аудио-колбэк: без аллокаций (по возможности), без блокировок, без исключений наружу.
- Публичные типы в `NeuroViz.Core` — это API; менять только с повышением `SchemaVersion` / `ProjectVersion`.