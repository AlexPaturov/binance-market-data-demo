# MVP Execution Order

## Принятое решение

Порядок доработки portfolio MVP:

1. Авторизация.
2. Тесты.
3. Пайплайн.
4. Остальное.

Причина: авторизация определяет реальные access-сценарии, тесты фиксируют эти сценарии, пайплайн должен запускать уже честный green test set, а документация и cleanup завершают MVP.

## 1. Авторизация

### MUST HAVE

- Принять auth MVP decision: Azure B2C/OIDC with role claims from identity provider.
- Включить authentication/authorization middleware в `DataManager`.
- Закрыть DataManager UI по default policy.
- Ввести роли `Viewer`, `Operator`, `Admin`.
- Закрыть Hangfire Dashboard только для `Admin`.
- Убрать production-использование `AllowAllConnectionsFilter`.
- Проверить вручную сценарии anonymous/viewer/operator/admin.

### DO NOT DO NOW

- Не делать пользовательскую админку.
- Не делать полноценную permission/scopes matrix.
- Не заменять B2C локальным config allowlist.

## 2. Тесты

### MUST HAVE

- Починить или исключить псевдотесты, которые сейчас ломают `dotnet test`.
- Оставить только честный green baseline.
- Добавить минимальные auth tests после реализации авторизации.
- Проверить `dotnet test` локально.

### SHOULD HAVE

- Один Testcontainers integration test на реальный repository/SQL path.
- Один worker orchestration unit test.

## 3. Пайплайн

### MUST HAVE

- Добавить `dotnet build`.
- Добавить `dotnet test`.
- Убрать deploy на `pull_request`.
- Deploy оставить только на `push` в `master` или manual trigger.
- Убрать debug output `.env`.
- Не логировать секреты.

## 4. Остальное

### MUST HAVE

- Актуализировать README под фактический MVP state.
- Зафиксировать schema baseline / demo DB setup.
- Описать demo flow.
- Обновить `docs/mvp/project_resume.md`, если фактическое состояние изменится.

### STOP CONDITION

Проект останавливается после того, как:

- auth закрывает DataManager и Hangfire;
- роли работают воспроизводимо;
- `dotnet test` проходит;
- CI запускает build/test;
- PR не деплоит production;
- README объясняет demo flow;
- MVP-документация соответствует коду.
